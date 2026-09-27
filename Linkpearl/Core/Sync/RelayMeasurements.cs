using System.Buffers.Binary;

namespace Linkpearl.Core.Sync;

/// <summary>Le RTT d'un pair vers un service, désigné par son empreinte.</summary>
public readonly record struct RelayMeasurement(ulong Service, ushort RttMs)
{
    public const ushort Unreachable = 0xFFFF;
    public const ushort Ceiling = 0xFFFE;

    public static RelayMeasurement Of(ulong service, TimeSpan? rtt)
        => new(service, rtt is { } value ? (ushort)Math.Min(Math.Ceiling(value.TotalMilliseconds), Ceiling) : Unreachable);
}

/// <summary>
/// Les mesures glissées après les adresses, dans le bloc scellé.
/// </summary>
/// <remarks>
///     extension = etiquette(1) || longueur(2) || contenu
///     0x01      : nombre(1) || (service(8) || rtt_ms(2))*
///
/// Scellées avec les adresses : le rendez-vous ne voit ni mesure, ni position.
/// Une étiquette inconnue se saute grâce à sa longueur, ce qui laisse la place
/// à une extension future. Une extension malformée ne fait jamais échouer la
/// connexion : sans mesures lisibles, on relaie comme avant, par le service
/// d'appariement.
/// </remarks>
public static class RelayMeasurements
{
    public const byte Tag = 0x01;
    public const int MaxMeasurements = 16;

    /// <summary>Le RTT synthétique d'un service hors de la région d'un pair en relais seul.</summary>
    public const ushort Far = 1000;

    private const int EntrySize = 8 + 2;

    public static byte[] Encode(IReadOnlyList<RelayMeasurement> measurements)
    {
        var kept = measurements.Take(MaxMeasurements).ToList();
        var length = 1 + kept.Count * EntrySize;
        var block = new byte[3 + length];

        block[0] = Tag;
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(1), (ushort)length);
        block[3] = (byte)kept.Count;

        for (var i = 0; i < kept.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(4 + i * EntrySize), kept[i].Service);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(12 + i * EntrySize), kept[i].RttMs);
        }

        return block;
    }

    public static IReadOnlyList<RelayMeasurement>? TryRead(ReadOnlySpan<byte> extensions)
    {
        List<RelayMeasurement>? found = null;
        var offset = 0;

        while (offset < extensions.Length)
        {
            if (extensions.Length - offset < 3)
                return null;

            var tag = extensions[offset];
            var length = BinaryPrimitives.ReadUInt16BigEndian(extensions[(offset + 1)..]);
            offset += 3;

            if (extensions.Length - offset < length)
                return null;

            var content = extensions.Slice(offset, length);
            offset += length;

            if (tag != Tag)
                continue;

            // Deux blocs de mesures : on ne saurait lequel croire, et les deux
            // pairs pourraient ne pas choisir le même.
            if (found is not null || content.Length < 1)
                return null;

            var count = content[0];

            if (count > MaxMeasurements || content.Length != 1 + count * EntrySize)
                return null;

            found = new List<RelayMeasurement>(count);

            for (var i = 0; i < count; i++)
                found.Add(new RelayMeasurement(
                    BinaryPrimitives.ReadUInt64BigEndian(content[(1 + i * EntrySize)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(content[(9 + i * EntrySize)..])));
        }

        return found;
    }

    /// <summary>
    /// Ce qu'un pair en relais seul envoie à la place de ses RTT.
    /// </summary>
    /// <remarks>
    /// Ce mode existe pour cacher son adresse au pair. Des RTT vers plusieurs
    /// continents la laisseraient trianguler ; des zéros pour sa région et
    /// <see cref="Far"/> ailleurs ne lui apprennent que le continent.
    /// </remarks>
    public static IReadOnlyList<RelayMeasurement> Synthetic(IReadOnlyList<RelayPlace> eligible, IReadOnlyList<RelayMeasurement> measured)
    {
        var regionOf = eligible
            .GroupBy(place => place.Fingerprint)
            .ToDictionary(group => group.Key, group => group.First().Region);

        var nearest = measured
            .Where(m => m.RttMs != RelayMeasurement.Unreachable && regionOf.GetValueOrDefault(m.Service) is not null)
            .OrderBy(m => m.RttMs)
            .ThenBy(m => m.Service)
            .Select(m => regionOf[m.Service])
            .FirstOrDefault();

        return [.. measured.Select(m => new RelayMeasurement(
            m.Service,
            m.RttMs == RelayMeasurement.Unreachable ? RelayMeasurement.Unreachable
            : nearest is not null && regionOf.GetValueOrDefault(m.Service) == nearest ? (ushort)0
            : Far))];
    }
}
