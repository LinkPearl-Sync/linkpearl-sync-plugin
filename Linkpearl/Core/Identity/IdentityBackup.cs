using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Crypto;

namespace Linkpearl.Core.Identity;

/// <summary>Ce qu'une sauvegarde porte d'un personnage : sa clé et son carnet, en clair.</summary>
/// <param name="Folder">Le dossier du personnage, soit son empreinte (<see cref="CharacterFolder"/>).</param>
/// <param name="Identity">La clé privée, au format PKCS#8.</param>
/// <param name="Pairs">Le carnet, dans sa forme sur disque déchiffrée.</param>
/// <param name="Groups">
/// Les groupes, dans leur forme sur disque déchiffrée. Nul quand le personnage
/// n'en a aucun, ou que la sauvegarde vient d'une version antérieure qui ne
/// portait pas ce champ.
/// </param>
public sealed record BackupEntry(string Folder, byte[] Identity, byte[] Pairs, byte[]? Groups = null);

/// <summary>Le résultat d'une lecture : des entrées, ou la raison du refus.</summary>
public sealed record BackupReadResult(IReadOnlyList<BackupEntry> Entries, string? Failure, bool NeedsPassword)
{
    public static BackupReadResult Refused(string failure, bool needsPassword = false)
        => new([], failure, needsPassword);
}

/// <summary>
/// Le fichier qui permet de retrouver ses personnages après une réinstallation.
/// </summary>
/// <remarks>
/// Sur disque, l'identité est protégée par DPAPI, donc liée au compte Windows :
/// copier le dossier de configuration ne survit pas à une réinstallation du
/// système. Ce fichier-ci ne dépend de rien d'autre que de son mot de passe.
///
/// Le mot de passe est facultatif, par choix de l'utilisateur. Sans lui, le
/// fichier vaut l'identité entière : qui le récupère se fait passer pour vous
/// auprès de vos pairs. Un contrôle SHA-256 attrape alors la corruption, pas la
/// falsification, ce qui suffit puisque rien n'y est secret vis-à-vis de qui
/// peut l'écrire.
///
/// Il se partage, donc il se lit comme un message venu du réseau : un seul
/// champ hors des règles et le fichier entier est refusé. Les noms de dossier
/// surtout, qui deviendront des chemins.
///
/// Disposition :
/// <code>
/// "LPBK" | version (1) | mode (1)
/// mode 0 : charge | SHA-256(charge)
/// mode 1 : itérations (4, LE) | sel (16) | nonce (12) | AES-GCM(charge), l'en-tête en données associées
/// charge : nombre (2) | { dossier (16) | taille clé (2) | clé | taille carnet (4) | carnet
///                         [version 2 seulement : taille groupes (4, LE) | groupes] }*
/// </code>
/// Version 1 n'a pas de champ groupes : relue, chaque entrée porte
/// <c>Groups = null</c>. Écrite, une sauvegarde est toujours en version 2.
/// </remarks>
public static class IdentityBackup
{
    private static ReadOnlySpan<byte> Magic => "LPBK"u8;

    private const byte FirstVersion = 1;
    private const byte Version = 2;
    private const byte ModePlain = 0;
    private const byte ModeProtected = 1;

    public const int PlainHeaderLength = 6;
    public const int IterationsOffset = PlainHeaderLength;

    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int ProtectedHeaderLength = PlainHeaderLength + sizeof(int) + SaltLength + NonceLength;
    private const int DigestLength = 32;

    /// <summary>
    /// PBKDF2-SHA256 à six cent mille itérations, la recommandation OWASP de 2023.
    /// </summary>
    /// <remarks>
    /// Une demi-seconde environ à la sauvegarde et à la restauration, deux
    /// gestes rares. Le plancher et le plafond ne servent qu'à la lecture : un
    /// fichier fabriqué ne doit ni affaiblir la dérivation ni geler le jeu.
    /// </remarks>
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int MaxIterations = 10_000_000;

    /// <summary>Plus qu'aucun joueur n'a de personnages.</summary>
    private const int MaxEntries = 64;

    /// <summary>Une clé PKCS#8 sur P-256 pèse environ 140 octets.</summary>
    private const int MaxIdentityLength = 1024;

    /// <summary>Quatre Mio de carnet, soit des milliers de pairs.</summary>
    private const int MaxPairsLength = 4 * 1024 * 1024;

    /// <summary>Même borne que le carnet : des milliers de groupes tiendraient largement dedans.</summary>
    private const int MaxGroupsLength = 4 * 1024 * 1024;

    private const int MaxFileLength = 16 * 1024 * 1024;

    public static byte[] Write(IReadOnlyList<BackupEntry> entries, string? password, int iterations = DefaultIterations)
    {
        if (entries.Count is 0 or > MaxEntries)
            throw new ArgumentException($"une sauvegarde porte de 1 à {MaxEntries} personnages.", nameof(entries));

        if (Validate(entries) is { } problem)
            throw new ArgumentException(problem, nameof(entries));

        var payload = Encode(entries);

        if (string.IsNullOrEmpty(password))
        {
            var plain = new byte[PlainHeaderLength + payload.Length + DigestLength];
            WriteHeader(plain, ModePlain);
            payload.CopyTo(plain, PlainHeaderLength);
            SHA256.HashData(payload, plain.AsSpan(PlainHeaderLength + payload.Length));
            return plain;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);

        var header = new byte[ProtectedHeaderLength];
        WriteHeader(header, ModeProtected);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(IterationsOffset), iterations);
        RandomNumberGenerator.Fill(header.AsSpan(IterationsOffset + sizeof(int), SaltLength + NonceLength));

        var key = DeriveKey(password, header, iterations);

        try
        {
            var sealedPayload = CryptoPrimitives.Seal(key, Nonce(header), payload, header);
            return [.. header, .. sealedPayload];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Vrai si le fichier demande un mot de passe. Faux aussi pour ce qui n'est pas une sauvegarde.</summary>
    public static bool IsProtected(ReadOnlySpan<byte> file)
        => HasHeader(file) && file[PlainHeaderLength - 1] == ModeProtected;

    public static BackupReadResult Read(ReadOnlySpan<byte> file, string? password)
    {
        if (file.Length > MaxFileLength)
            return BackupReadResult.Refused("fichier trop volumineux pour une sauvegarde Linkpearl.");

        if (HasHeader(file) is false)
            return BackupReadResult.Refused("ce fichier n'est pas une sauvegarde Linkpearl.");

        if (file[4] is not (FirstVersion or Version))
            return BackupReadResult.Refused("cette sauvegarde vient d'une version plus récente de Linkpearl.");

        var version = file[4];

        return file[5] switch
        {
            ModePlain => ReadPlain(file, version),
            ModeProtected => ReadProtected(file, version, password),
            _ => BackupReadResult.Refused("ce fichier n'est pas une sauvegarde Linkpearl."),
        };
    }

    private static BackupReadResult ReadPlain(ReadOnlySpan<byte> file, byte version)
    {
        if (file.Length < PlainHeaderLength + DigestLength)
            return BackupReadResult.Refused("sauvegarde incomplète.");

        var payload = file[PlainHeaderLength..^DigestLength];

        Span<byte> digest = stackalloc byte[DigestLength];
        SHA256.HashData(payload, digest);

        if (CryptographicOperations.FixedTimeEquals(digest, file[^DigestLength..]) is false)
            return BackupReadResult.Refused("sauvegarde endommagée : contenu invalide.");

        return Decode(payload, version);
    }

    private static BackupReadResult ReadProtected(ReadOnlySpan<byte> file, byte version, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return BackupReadResult.Refused("cette sauvegarde est protégée par un mot de passe.", needsPassword: true);

        if (file.Length < ProtectedHeaderLength)
            return BackupReadResult.Refused("sauvegarde incomplète.");

        var header = file[..ProtectedHeaderLength];
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header[IterationsOffset..]);

        if (iterations is < MinIterations or > MaxIterations)
            return BackupReadResult.Refused("sauvegarde invalide : protection incorrecte.");

        var key = DeriveKey(password, header, iterations);

        try
        {
            if (CryptoPrimitives.TryOpen(key, Nonce(header), file[ProtectedHeaderLength..], header, out var payload) is false)
            {
                // Un mauvais mot de passe et un fichier altéré sont
                // indiscernables, et c'est voulu : le dire aiderait qui essaie.
                return BackupReadResult.Refused("mot de passe incorrect ou sauvegarde endommagée.", needsPassword: true);
            }

            try
            {
                return Decode(payload, version);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Encode(IReadOnlyList<BackupEntry> entries)
    {
        var length = sizeof(ushort) + entries.Sum(entry =>
            CharacterFolder.Length + sizeof(ushort) + entry.Identity.Length + sizeof(int) + entry.Pairs.Length
            + sizeof(int) + (entry.Groups?.Length ?? 0));

        var buffer = new byte[length];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)entries.Count);
        span = span[sizeof(ushort)..];

        foreach (var entry in entries)
        {
            Encoding.ASCII.GetBytes(entry.Folder, span);
            span = span[CharacterFolder.Length..];

            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)entry.Identity.Length);
            entry.Identity.CopyTo(span[sizeof(ushort)..]);
            span = span[(sizeof(ushort) + entry.Identity.Length)..];

            BinaryPrimitives.WriteInt32LittleEndian(span, entry.Pairs.Length);
            entry.Pairs.CopyTo(span[sizeof(int)..]);
            span = span[(sizeof(int) + entry.Pairs.Length)..];

            var groupsLength = entry.Groups?.Length ?? 0;
            BinaryPrimitives.WriteInt32LittleEndian(span, groupsLength);
            entry.Groups?.CopyTo(span[sizeof(int)..]);
            span = span[(sizeof(int) + groupsLength)..];
        }

        return buffer;
    }

    private static BackupReadResult Decode(ReadOnlySpan<byte> payload, byte version)
    {
        const string Broken = "sauvegarde invalide : contenu illisible.";

        if (payload.Length < sizeof(ushort))
            return BackupReadResult.Refused(Broken);

        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        payload = payload[sizeof(ushort)..];

        if (count is 0 or > MaxEntries)
            return BackupReadResult.Refused(Broken);

        var entries = new List<BackupEntry>(count);

        for (var i = 0; i < count; i++)
        {
            if (payload.Length < CharacterFolder.Length + sizeof(ushort))
                return BackupReadResult.Refused(Broken);

            var folder = Encoding.ASCII.GetString(payload[..CharacterFolder.Length]);
            payload = payload[CharacterFolder.Length..];

            int identityLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            payload = payload[sizeof(ushort)..];

            if (identityLength is 0 or > MaxIdentityLength || payload.Length < identityLength + sizeof(int))
                return BackupReadResult.Refused(Broken);

            var identity = payload[..identityLength].ToArray();
            payload = payload[identityLength..];

            var pairsLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
            payload = payload[sizeof(int)..];

            if (pairsLength is < 0 or > MaxPairsLength || payload.Length < pairsLength)
                return BackupReadResult.Refused(Broken);

            var pairs = payload[..pairsLength].ToArray();
            payload = payload[pairsLength..];

            byte[]? groups = null;

            if (version == Version)
            {
                if (payload.Length < sizeof(int))
                    return BackupReadResult.Refused(Broken);

                var groupsLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
                payload = payload[sizeof(int)..];

                if (groupsLength is < 0 or > MaxGroupsLength || payload.Length < groupsLength)
                    return BackupReadResult.Refused(Broken);

                groups = groupsLength is 0 ? null : payload[..groupsLength].ToArray();
                payload = payload[groupsLength..];
            }

            entries.Add(new BackupEntry(folder, identity, pairs, groups));
        }

        if (payload.IsEmpty is false)
            return BackupReadResult.Refused(Broken);

        // Tout ou rien : restaurer une partie des personnages laisserait croire
        // à une sauvegarde complète.
        if (Validate(entries) is { } problem)
            return BackupReadResult.Refused($"sauvegarde refusée : {problem}");

        return new BackupReadResult(entries, null, false);
    }

    private static string? Validate(IReadOnlyList<BackupEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (IsFolderName(entry.Folder) is false)
                return "un nom de personnage n'a pas la forme attendue.";

            if (seen.Add(entry.Folder) is false)
                return "un personnage apparaît deux fois.";

            if (entry.Identity.Length is 0 or > MaxIdentityLength)
                return "une identité est invalide.";

            if (entry.Pairs.Length > MaxPairsLength)
                return "un carnet est trop volumineux.";

            if (entry.Groups is { Length: > MaxGroupsLength })
                return "une liste de groupes est trop volumineuse.";
        }

        return null;
    }

    /// <summary>
    /// Seize chiffres hexadécimaux minuscules, exactement ce que produit
    /// <see cref="CharacterFolder.Name"/>.
    /// </summary>
    /// <remarks>
    /// Une liste blanche et non la recherche de « .. » ou de séparateurs : le
    /// nom devient un chemin, et seule une forme entièrement connue garantit
    /// qu'il ne sortira pas du dossier des personnages.
    /// </remarks>
    public static bool IsFolderName(string folder)
        => folder.Length == CharacterFolder.Length
        && folder.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasHeader(ReadOnlySpan<byte> file)
        => file.Length >= PlainHeaderLength && file[..Magic.Length].SequenceEqual(Magic);

    private static void WriteHeader(Span<byte> destination, byte mode)
    {
        Magic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = mode;
    }

    private static ReadOnlySpan<byte> Salt(ReadOnlySpan<byte> header)
        => header.Slice(IterationsOffset + sizeof(int), SaltLength);

    private static ReadOnlySpan<byte> Nonce(ReadOnlySpan<byte> header)
        => header.Slice(IterationsOffset + sizeof(int) + SaltLength, NonceLength);

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> header, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)),
            Salt(header), iterations, HashAlgorithmName.SHA256, 32);
}
