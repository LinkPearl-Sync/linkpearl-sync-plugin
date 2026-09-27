using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Linkpearl.Core.Transport.Rendezvous;

/// <summary>Un service du cercle ouvert, tel que la liste signée le présente.</summary>
/// <remarks>
/// <see cref="Family"/> est l'empreinte du /24 ou du /48 que l'autorité a vu
/// en le sondant. Le client ne résout rien lui-même, et publier l'empreinte
/// plutôt que l'adresse évite de coller une IP à côté de chaque nom.
///
/// <see cref="Region"/> est le continent que l'autorité déduit de cette même
/// adresse, ou null. Seule la v2 la porte : la v1 l'ignore en écriture et la
/// rend nulle en lecture.
/// </remarks>
public sealed record ConsensusEntry(string Address, string Label, byte[] Family, string? Region = null);

/// <summary>Une signature de la liste : l'identifiant de la clé, puis r‖s.</summary>
public sealed record ConsensusSignature(byte[] KeyId, byte[] Signature);

/// <summary>
/// La liste signée du cercle ouvert.
/// </summary>
/// <remarks>
/// Recopiée littéralement dans le dépôt du service : l'autorité écrit et
/// signe, le plugin lit et vérifie, et les deux doivent voir les mêmes
/// octets. Le format porte une liste de signatures pour qu'ajouter d'autres
/// autorités ne soit plus tard qu'un seuil à exiger.
///
///     liste     = version(4) || emise(8) || expire(8) || nombre(2) || entree*
///     entree    = longueur(1) || adresse || longueur(1) || libelle || famille(8)
///     signature = identifiant_cle(8) || r||s(64)
///     document  = liste || nombre_signatures(1) || signature*
///
///     liste_v2  = "linkpearl:consensus:v2" || version(4) || emise(8) || expire(8) || nombre(2) || entree_v2*
///     entree_v2 = longueur(1) || adresse || longueur(1) || libelle || famille(8) || region(2)
///
/// Le préfixe de la v2 fait partie des octets signés : une signature de l'une
/// ne vaut jamais pour l'autre. La lecture reconnaît la v2 à ce préfixe, qu'une
/// v1 ne peut pas porter : sa version et sa date d'émission tomberaient des
/// milliards d'années dans le futur.
/// </remarks>
public sealed record ServiceConsensus(uint Version, long Issued, long Expires, IReadOnlyList<ConsensusEntry> Entries)
{
    public const int MaxEntries = 1024;
    public const int MaxSignatures = 8;
    public const int FamilySize = 8;
    public const int KeyIdSize = 8;
    public const int SignatureSize = 64;
    public const int PublicPointSize = 65;

    /// <summary>Durée de validité d'une liste émise.</summary>
    /// <remarks>
    /// Une autorité tombée laisse aux clients une semaine avant qu'ils
    /// retombent sur l'ancrage : assez pour la relever, pas assez pour qu'une
    /// liste volée serve indéfiniment.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public const int RegionSize = 2;

    private const int HeaderSize = 4 + 8 + 8 + 2;

    private static ReadOnlySpan<byte> FamilyInfo => "linkpearl:family:v1"u8;

    private static ReadOnlySpan<byte> V2Prefix => "linkpearl:consensus:v2"u8;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public byte[] SignedPortion() => Write(withRegions: false);

    public byte[] SignedPortionV2() => Write(withRegions: true);

    private byte[] Write(bool withRegions)
    {
        if (Entries.Count > MaxEntries)
            throw new ArgumentException($"{Entries.Count} entrées, plafond {MaxEntries}");

        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];

        if (withRegions)
            stream.Write(V2Prefix);

        BinaryPrimitives.WriteUInt32BigEndian(number, Version);
        stream.Write(number[..4]);
        BinaryPrimitives.WriteInt64BigEndian(number, Issued);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, Expires);
        stream.Write(number);
        BinaryPrimitives.WriteUInt16BigEndian(number, (ushort)Entries.Count);
        stream.Write(number[..2]);

        foreach (var entry in Entries)
        {
            WriteText(stream, entry.Address, nameof(entry.Address));
            WriteText(stream, entry.Label, nameof(entry.Label));

            if (entry.Family.Length != FamilySize)
                throw new ArgumentException($"famille de {entry.Family.Length} octets, {FamilySize} attendus");

            stream.Write(entry.Family);

            if (withRegions)
                WriteRegion(stream, entry.Region);
        }

        return stream.ToArray();
    }

    private static void WriteRegion(MemoryStream stream, string? region)
    {
        if (region is null)
        {
            stream.WriteByte(0);
            stream.WriteByte(0);
            return;
        }

        if (region.Length != RegionSize || region.Any(letter => letter is < 'A' or > 'Z'))
            throw new ArgumentException($"région « {region} » : deux lettres majuscules attendues");

        stream.WriteByte((byte)region[0]);
        stream.WriteByte((byte)region[1]);
    }

    public static byte[] Assemble(ServiceConsensus list, IReadOnlyList<ConsensusSignature> signatures)
        => Assemble(list.SignedPortion(), signatures);

    public static byte[] AssembleV2(ServiceConsensus list, IReadOnlyList<ConsensusSignature> signatures)
        => Assemble(list.SignedPortionV2(), signatures);

    private static byte[] Assemble(byte[] signed, IReadOnlyList<ConsensusSignature> signatures)
    {
        if (signatures.Count is < 1 or > MaxSignatures)
            throw new ArgumentException($"{signatures.Count} signatures, de 1 à {MaxSignatures}", nameof(signatures));

        using var stream = new MemoryStream();
        stream.Write(signed);
        stream.WriteByte((byte)signatures.Count);

        foreach (var signature in signatures)
        {
            if (signature.KeyId.Length != KeyIdSize || signature.Signature.Length != SignatureSize)
                throw new ArgumentException("signature de taille inattendue", nameof(signatures));

            stream.Write(signature.KeyId);
            stream.Write(signature.Signature);
        }

        return stream.ToArray();
    }

    public static byte[] Sign(ServiceConsensus list, ECDsa key) => Sign(list.SignedPortion(), key);

    public static byte[] SignV2(ServiceConsensus list, ECDsa key) => Sign(list.SignedPortionV2(), key);

    private static byte[] Sign(byte[] signed, ECDsa key)
    {
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Assemble(signed, [new ConsensusSignature(KeyId(PublicPoint(key)), signature)]);
    }

    public static byte[] PublicPoint(ECDsa key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        return [0x04, .. point.X!, .. point.Y!];
    }

    /// <summary>Les huit premiers octets de SHA-256 de la clé compressée.</summary>
    /// <remarks>Il sert à retrouver la clé, jamais à lui faire confiance.</remarks>
    public static byte[] KeyId(ReadOnlySpan<byte> publicPoint)
    {
        if (publicPoint.Length != PublicPointSize || publicPoint[0] != 0x04)
            throw new ArgumentException("point public de 65 octets attendu", nameof(publicPoint));

        Span<byte> compressed = stackalloc byte[33];
        compressed[0] = (byte)(0x02 | (publicPoint[^1] & 1));
        publicPoint[1..33].CopyTo(compressed[1..]);

        return SHA256.HashData(compressed)[..KeyIdSize];
    }

    public static byte[] Family(IPAddress address)
    {
        // Une IPv4 vue à travers une socket IPv6 reste la même machine : sans
        // cette conversion, un même hôte aurait deux familles selon la pile.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        var prefix = address.AddressFamily == AddressFamily.InterNetwork ? bytes.AsSpan(0, 3) : bytes.AsSpan(0, 6);

        var message = new byte[FamilyInfo.Length + prefix.Length];
        FamilyInfo.CopyTo(message);
        prefix.CopyTo(message.AsSpan(FamilyInfo.Length));

        return SHA256.HashData(message)[..FamilySize];
    }

    /// <summary>Vrai pour une adresse joignable depuis Internet, et seulement elle.</summary>
    /// <remarks>
    /// Partagé par la sonde de l'autorité et par le client : un service du
    /// cercle ouvert choisit ce que son nom résout, et sans ce filtre il ferait
    /// viser le réseau local de ceux qui l'emploient, ou celui de l'autorité.
    /// </remarks>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return (bytes[0] is 0 or 10 or 127 or >= 224
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)) is false;

        // En IPv6, seul l'unicast global (2000::/3) est joignable d'Internet ;
        // tout le reste est local, lien local, multicast ou réservé.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xE0) == 0x20
            && address.IsIPv6Multicast is false;
    }

    /// <summary>L'écriture unique d'une adresse, pour que deux graphies d'un même service se confondent.</summary>
    public static string Canonical(RendezvousAddress address) => $"{address.Host.ToLowerInvariant()}:{address.Port}";

    public static bool TryParse(
        ReadOnlySpan<byte> document, out ServiceConsensus? list,
        out IReadOnlyList<ConsensusSignature> signatures, out int signedLength, out string? rejection)
    {
        list = null;
        signatures = [];
        signedLength = 0;

        var v2 = document.StartsWith(V2Prefix);
        var start = v2 ? V2Prefix.Length : 0;

        if (document.Length < start + HeaderSize + 1)
        {
            rejection = "liste signée tronquée";
            return false;
        }

        var header = document[start..];
        var version = BinaryPrimitives.ReadUInt32BigEndian(header);
        var issued = BinaryPrimitives.ReadInt64BigEndian(header[4..]);
        var expires = BinaryPrimitives.ReadInt64BigEndian(header[12..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(header[20..]);

        if (count > MaxEntries)
        {
            rejection = $"{count} entrées, plafond {MaxEntries}";
            return false;
        }

        if (expires <= issued)
        {
            rejection = "expiration antérieure à l'émission";
            return false;
        }

        var offset = start + HeaderSize;
        var entries = new List<ConsensusEntry>(count);

        for (var i = 0; i < count; i++)
        {
            if (TryReadText(document, ref offset, out var address) is false
                || address.Length > RendezvousWire.MaxDirectoryAddressLength
                || RendezvousAddress.TryParse(address, out _, out _) is false)
            {
                rejection = $"adresse {i} illisible";
                return false;
            }

            if (TryReadText(document, ref offset, out var label) is false
                || label.Length > RendezvousWire.MaxDirectoryLabelLength)
            {
                rejection = $"libellé {i} illisible";
                return false;
            }

            if (offset + FamilySize > document.Length)
            {
                rejection = $"famille {i} tronquée";
                return false;
            }

            var family = document.Slice(offset, FamilySize).ToArray();
            offset += FamilySize;

            string? region = null;

            if (v2)
            {
                if (offset + RegionSize > document.Length)
                {
                    rejection = $"région {i} tronquée";
                    return false;
                }

                var raw = document.Slice(offset, RegionSize);
                offset += RegionSize;

                if (raw[0] != 0 || raw[1] != 0)
                {
                    if (raw[0] is < (byte)'A' or > (byte)'Z' || raw[1] is < (byte)'A' or > (byte)'Z')
                    {
                        rejection = $"région {i} illisible";
                        return false;
                    }

                    region = string.Concat((char)raw[0], (char)raw[1]);
                }
            }

            entries.Add(new ConsensusEntry(address, label, family, region));
        }

        if (offset >= document.Length)
        {
            rejection = "aucune signature";
            return false;
        }

        signedLength = offset;
        var signatureCount = document[offset++];
        var signatureSize = KeyIdSize + SignatureSize;

        if (signatureCount is < 1 or > MaxSignatures || offset + signatureCount * signatureSize != document.Length)
        {
            rejection = "bloc de signatures malformé";
            return false;
        }

        var read = new List<ConsensusSignature>(signatureCount);

        for (var i = 0; i < signatureCount; i++, offset += signatureSize)
            read.Add(new ConsensusSignature(
                document.Slice(offset, KeyIdSize).ToArray(),
                document.Slice(offset + KeyIdSize, SignatureSize).ToArray()));

        list = new ServiceConsensus(version, issued, expires, entries);
        signatures = read;
        rejection = null;
        return true;
    }

    public static bool TryVerify(
        ReadOnlySpan<byte> document, IReadOnlyList<byte[]> trustedPoints, long now,
        out ServiceConsensus? list, out string? rejection)
    {
        list = null;

        if (TryParse(document, out var parsed, out var signatures, out var signedLength, out rejection) is false)
            return false;

        var signed = document[..signedLength];

        foreach (var point in trustedPoints)
        {
            if (point.Length != PublicPointSize || point[0] != 0x04)
                continue;

            var id = KeyId(point);

            foreach (var signature in signatures)
            {
                if (id.AsSpan().SequenceEqual(signature.KeyId) is false || Verifies(point, signed, signature.Signature) is false)
                    continue;

                if (now >= parsed!.Expires)
                {
                    rejection = "liste expirée";
                    return false;
                }

                list = parsed;
                rejection = null;
                return true;
            }
        }

        rejection = "aucune signature d'une clé connue";
        return false;
    }

    private static bool Verifies(byte[] point, ReadOnlySpan<byte> message, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..] },
            });

            return key.VerifyData(
                message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // Un point hors de la courbe n'est pas une clé : il ne vérifie rien.
            return false;
        }
    }

    private static void WriteText(MemoryStream stream, string text, string what)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        if (bytes.Length > byte.MaxValue)
            throw new ArgumentException($"{what} de {bytes.Length} octets, au plus {byte.MaxValue}");

        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

    private static bool TryReadText(ReadOnlySpan<byte> document, ref int offset, out string text)
    {
        text = string.Empty;

        if (offset >= document.Length)
            return false;

        var length = document[offset];

        if (offset + 1 + length > document.Length)
            return false;

        try
        {
            text = StrictUtf8.GetString(document.Slice(offset + 1, length));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        offset += 1 + length;
        return true;
    }
}
