using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkpearl.Core.Safety;

/// <summary>Le coût d'une dérivation, publié avec la liste.</summary>
/// <remarks>
/// Publié et non codé en dur, pour qu'un client d'une version antérieure sache
/// encore vérifier une liste dont on aurait relevé le coût.
/// </remarks>
public readonly record struct BanParameters(int Iterations)
{
    /// <summary>
    /// Assez pour qu'une vérification coûte des dizaines de millisecondes.
    /// </summary>
    /// <remarks>
    /// Le but n'est pas la lenteur : c'est qu'énumérer l'espace des noms de
    /// FFXIV pour fabriquer un annuaire de joueurs utilisant des mods soit hors
    /// de prix, alors que vérifier quelqu'un qu'on a devant soi reste gratuit.
    ///
    /// PBKDF2 et non argon2 : tout ce qui est cryptographique dans ce projet
    /// passe par System.Security.Cryptography, sans dépendance, et le client
    /// doit dériver à l'identique dans un autre dépôt. Le prix est que PBKDF2
    /// se calcule bien sur processeur graphique, là où argon2 y résisterait par
    /// sa consommation mémoire : l'énumération reste chère, moins qu'avec lui.
    /// </remarks>
    public static BanParameters Default { get; } = new(600_000);
}

/// <summary>Une entrée de la liste : une empreinte, un motif, une date.</summary>
/// <remarks>
/// Le motif voyage vers des clients qui l'afficheront, donc il est borné. La
/// date sert à l'opérateur, pas au contrôle.
/// </remarks>
public sealed record BanEntry(byte[] Hash, string Reason, long Since);

/// <summary>
/// La liste de bannissement, telle qu'un service la publie.
/// </summary>
/// <remarks>
/// Elle porte des empreintes lentes de <c>nom@monde</c> et jamais les noms :
/// une liste de noms en clair serait un annuaire de joueurs utilisant des mods,
/// ce qui les exposerait pour une raison étrangère à ce qu'on leur reproche.
/// Elle reste vérifiable par qui a le nom en main, et hors de prix à énumérer.
///
/// Le sel est commun à la liste et non par entrée : sans cela, vérifier un nom
/// demanderait une dérivation par entrée, soit des secondes pour une liste de
/// cinquante.
///
/// <b>Cette liste n'est pas une preuve.</b> Un service de rendez-vous ne voit
/// aucun contenu, donc il ne peut jamais vérifier une accusation : elle relève
/// de la réputation, et celui qui l'applique en répond.
/// </remarks>
public sealed class BanList(byte[] salt, BanParameters parameters, IReadOnlyList<BanEntry> entries)
{
    public const int Version = 1;

    /// <summary>Au-delà, une liste venue du réseau est refusée.</summary>
    public const int MaxEntries = 4096;

    public const int MaxReasonLength = 120;

    public const int MaxIterations = 2_000_000;

    private const int HashLength = 32;
    private const int MinSaltLength = 16;

    public byte[] Salt => salt;

    public BanParameters Parameters => parameters;

    public IReadOnlyList<BanEntry> Entries => entries;

    /// <summary>
    /// L'empreinte d'un personnage sous ce sel.
    /// </summary>
    /// <remarks>
    /// La normalisation est la même que celle de l'empreinte de personnage :
    /// les deux côtés doivent tomber sur la même valeur, et un opérateur qui
    /// recopie un nom à la main met des majuscules où il veut.
    /// </remarks>
    public static byte[] Derive(string name, ushort world, byte[] salt, BanParameters parameters)
    {
        var normalized = $"{name.Trim().ToLowerInvariant()}@{world}";

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized), salt, parameters.Iterations, HashAlgorithmName.SHA256, HashLength);
    }

    /// <summary>Vrai si ce personnage figure dans la liste.</summary>
    /// <remarks>
    /// Coûteux par construction : à n'appeler qu'aux moments rares, à la
    /// réception d'une demande de pairage et avant de poser une apparence.
    /// Jamais pour chaque joueur visible à chaque ronde de détection.
    /// </remarks>
    public bool Contains(string name, ushort world)
    {
        if (entries.Count == 0)
            return false;

        var hash = Derive(name, world, salt, parameters);

        foreach (var entry in entries)
        {
            // Comparaison à temps constant : le temps de réponse ne doit pas
            // dire à quel point une empreinte approchait.
            if (CryptographicOperations.FixedTimeEquals(entry.Hash, hash))
                return true;
        }

        return false;
    }

    public string ToJson(long updated)
    {
        var array = new JsonArray();

        foreach (var entry in entries)
        {
            array.Add(new JsonObject
            {
                ["hash"] = Convert.ToHexStringLower(entry.Hash),
                ["reason"] = entry.Reason,
                ["since"] = entry.Since,
            });
        }

        var document = new JsonObject
        {
            ["version"] = Version,
            ["kdf"] = new JsonObject
            {
                ["algorithm"] = "pbkdf2-sha256",
                ["iterations"] = parameters.Iterations,
            },
            ["salt"] = Convert.ToHexStringLower(salt),
            ["updated"] = updated,
            ["entries"] = array,
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Lit une liste publiée.
    /// </summary>
    /// <remarks>
    /// Elle vient du réseau : tout est borné et rien ne lève. Un service
    /// hostile, ou une version future dont on ne sait rien, ne doit pas faire
    /// tomber le client.
    /// </remarks>
    public static bool TryParse(string? text, out BanList? list, out string? rejection)
    {
        list = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            rejection = "liste vide";
            return false;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject document)
            {
                rejection = "liste malformée";
                return false;
            }

            if (document["version"]?.GetValue<int>() != Version)
            {
                rejection = "version de liste inconnue";
                return false;
            }

            var salt = Convert.FromHexString(document["salt"]?.GetValue<string>() ?? "");

            if (salt.Length < MinSaltLength)
            {
                rejection = $"sel trop court ({salt.Length} octets, minimum {MinSaltLength})";
                return false;
            }

            var iterations = document["kdf"]?["iterations"]?.GetValue<int>() ?? 0;

            // Plafond à un peu plus de trois fois le défaut : le coût se paie par
            // joueur visible et par sel, et un service qui l'imposait à dix
            // millions faisait tourner le processeur du joueur des secondes
            // durant pour chaque passant.
            if (iterations is < 1000 or > MaxIterations)
            {
                rejection = $"coût de dérivation hors bornes ({iterations})";
                return false;
            }

            var read = new List<BanEntry>();

            foreach (var node in document["entries"]?.AsArray() ?? [])
            {
                if (read.Count >= MaxEntries)
                {
                    rejection = $"liste trop longue (plafond {MaxEntries})";
                    return false;
                }

                var hash = Convert.FromHexString(node?["hash"]?.GetValue<string>() ?? "");

                if (hash.Length != HashLength)
                {
                    rejection = "empreinte de taille inattendue";
                    return false;
                }

                // Le motif s'affiche en infobulle : ni caractère de contrôle ni
                // marque de direction, qui retourneraient ou masqueraient le texte.
                var reason = new string((node?["reason"]?.GetValue<string>() ?? "")
                    .Where(c => char.IsControl(c) is false
                             && char.GetUnicodeCategory(c) is not System.Globalization.UnicodeCategory.Format)
                    .ToArray());

                read.Add(new BanEntry(
                    hash,
                    reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason,
                    node?["since"]?.GetValue<long>() ?? 0));
            }

            list = new BanList(salt, new BanParameters(iterations), read);
            rejection = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            rejection = $"liste illisible : {e.Message}";
            return false;
        }
    }
}
