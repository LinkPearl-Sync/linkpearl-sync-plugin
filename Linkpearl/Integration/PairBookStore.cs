using System.Security.Cryptography;
using System.Text.Json;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Integration;

/// <summary>
/// Conserve le carnet de pairs, protégé par DPAPI.
/// </summary>
/// <remarks>
/// Protégé parce qu'il contient les secrets de paire, dont sont dérivés les
/// jetons de rendez-vous. Quelqu'un qui les lirait pourrait observer les
/// présences de ces paires sur le service.
///
/// Un format de transfert explicite plutôt que la sérialisation directe des
/// types du noyau : ceux-ci vont changer, et un carnet illisible après une mise
/// à jour ferait perdre tous les pairages.
/// </remarks>
public sealed class PairBookStore(string path)
{
    private static readonly byte[] Entropy = "linkpearl:pairs:v1"u8.ToArray();

    /// <summary>
    /// La forme sur disque.
    /// </summary>
    /// <remarks>
    /// <c>RendezvousHost</c> est nullable et n'est plus écrit : il n'existe que
    /// pour relire un carnet d'avant la fédération, qui ne portait qu'un hôte.
    /// <c>Rendezvous</c> vient en dernier et vaut null à l'absence, ce que la
    /// désérialisation d'un enregistrement positionnel donne naturellement.
    /// <c>Receive</c> suit la même règle : absent d'un carnet ancien, il vaut
    /// tout accepter, comme un pair qu'on vient d'ajouter. <c>RevokedAt</c>
    /// aussi : un carnet ancien n'a aucun retrait en attente.
    /// <c>PausedByPeer</c> aussi : un carnet ancien ne sait d'aucun pair qu'il
    /// nous a mis en pause.
    /// </remarks>
    private sealed record Dto(
        string Id, string? PublicKey, string PairSecret, string DisplayName, string? RendezvousHost,
        int Trust, int Permissions, int Policy, bool Paused,
        long PairedAt, long? LastSeenAt, string? PinnedFingerprint,
        string[]? Rendezvous = null, int? Receive = null, long? RevokedAt = null, bool PausedByPeer = false);

    private const int ReceiveAnimations = 1;
    private const int ReceiveVfx = 2;
    private const int ReceiveSounds = 4;

    private static int ToBits(TransientCategories receive)
        => (receive.Animations ? ReceiveAnimations : 0)
         | (receive.Vfx ? ReceiveVfx : 0)
         | (receive.Sounds ? ReceiveSounds : 0);

    private static TransientCategories FromBits(int? bits)
        => bits is { } b
            ? new TransientCategories((b & ReceiveAnimations) != 0, (b & ReceiveVfx) != 0, (b & ReceiveSounds) != 0)
            : TransientCategories.All;

    public void Load(PairBook book)
    {
        try
        {
            if (ReadPlain() is not { } plain)
                return;

            var records = JsonSerializer.Deserialize<List<Dto>>(plain) ?? [];

            book.Load(records.Select(Rehydrate).OfType<PairRecord>());
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Carnet illisible : on démarre avec un carnet vide plutôt que
            // d'empêcher le plugin de se charger, et le fichier est écarté,
            // sans quoi le premier pair ajouté l'écraserait.
            CharacterStorage.SetAside(path, "illisible");
        }
    }

    /// <summary>Le carnet déchiffré, tel que la sauvegarde le transporte. Null s'il n'existe pas.</summary>
    public byte[]? ReadPlain()
        => File.Exists(path)
            ? ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)
            : null;

    /// <summary>Vrai si ces octets se relisent comme un carnet.</summary>
    /// <remarks>
    /// Vérifié avant toute restauration : un carnet qui ne se relit pas serait
    /// écarté au chargement suivant, et les pairages avec lui.
    /// </remarks>
    public static bool IsValid(byte[] plain)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Dto>>(plain) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void WritePlain(byte[] plain)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".part";
        File.WriteAllBytes(temporary, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temporary, path, overwrite: true);
    }

    public void Save(PairBook book)
    {
        var dtos = book.All.Select(record => new Dto(
            Convert.ToHexStringLower(record.Id.ToBytes()),
            record.PublicKey is { } key ? Convert.ToHexStringLower(key) : null,
            Convert.ToHexStringLower(record.PairSecret),
            record.DisplayName,
            null,   // l'hôte unique n'est plus écrit, seulement relu
            (int)record.Trust,
            (int)record.Permissions,
            (int)record.Policy,
            record.Paused,
            record.PairedAt.ToUnixTimeSeconds(),
            record.LastSeenAt?.ToUnixTimeSeconds(),
            record.PinnedFingerprint is { } print ? Convert.ToHexStringLower(print.ToBytes()) : null,
            record.Rendezvous.Select(place => place.ToString()).ToArray(),
            ToBits(record.Receive),
            record.RevokedAt?.ToUnixTimeSeconds(),
            record.PausedByPeer));

        WritePlain(JsonSerializer.SerializeToUtf8Bytes(dtos.ToList()));
    }

    private static PairRecord? Rehydrate(Dto dto)
    {
        try
        {
            var rendezvous = ReadPlaces(dto);

            // Un pair sans lieu de rendez-vous est injoignable : le garder
            // afficherait une entrée que rien ne peut jamais joindre, et dont
            // l'utilisateur ne comprendrait pas le silence.
            if (rendezvous.Count == 0)
                return null;

            return new PairRecord
            {
                Id = PeerId.FromBytes(Convert.FromHexString(dto.Id)),
                PublicKey = dto.PublicKey is { } key ? Convert.FromHexString(key) : null,
                PairSecret = Convert.FromHexString(dto.PairSecret),
                DisplayName = dto.DisplayName,
                Rendezvous = rendezvous,
                Trust = (PairTrust)dto.Trust,
                Permissions = (PairPermissions)dto.Permissions,
                Policy = (ConnectionPolicy)dto.Policy,
                Paused = dto.Paused,
                PausedByPeer = dto.PausedByPeer,
                Receive = FromBits(dto.Receive),
                PairedAt = DateTimeOffset.FromUnixTimeSeconds(dto.PairedAt),
                LastSeenAt = dto.LastSeenAt is { } seen ? DateTimeOffset.FromUnixTimeSeconds(seen) : null,
                RevokedAt = dto.RevokedAt is { } revoked ? DateTimeOffset.FromUnixTimeSeconds(revoked) : null,
                PinnedFingerprint = dto.PinnedFingerprint is { } print
                    ? PlayerFingerprint.FromBytes(Convert.FromHexString(print))
                    : null,
            };
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;   // une entrée abîmée ne doit pas emporter tout le carnet
        }
    }

    /// <summary>Les lieux de rendez-vous d'un pair, migration comprise.</summary>
    /// <remarks>
    /// Un carnet d'avant la fédération ne porte qu'un hôte, le port étant alors
    /// une variable globale de la configuration. On ne peut pas le retrouver
    /// ici, donc on prend celui par défaut : c'est celui qu'avaient tous les
    /// réglages d'alors, et l'utilisateur peut corriger dans les réglages.
    /// </remarks>
    private static List<RendezvousAddress> ReadPlaces(Dto dto)
    {
        if (dto.Rendezvous is { Length: > 0 } stored)
        {
            return stored
                .Select(text => RendezvousAddress.TryParse(text, out var place, out _) ? place : (RendezvousAddress?)null)
                .OfType<RendezvousAddress>()
                .ToList();
        }

        return dto.RendezvousHost is { } legacy
            && RendezvousAddress.TryParse(legacy, out var one, out _)
                ? [one]
                : [];
    }
}
