using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Un service par lequel une paire peut relayer.</summary>
/// <remarks>
/// <see cref="Open"/> dit qu'il vient de la liste signée : on ne le joint alors
/// qu'à une adresse publique, comme pour l'annonce.
/// </remarks>
public sealed record RelayPlace(RendezvousAddress At, string? Region, bool Open)
{
    public ulong Fingerprint => FingerprintOf(At);

    /// <summary>Les huit premiers octets de SHA-256 de l'adresse canonique.</summary>
    /// <remarks>
    /// Ce que les deux pairs se disent pour désigner un service : compact, et
    /// identique des deux côtés quelle que soit la graphie de l'adresse.
    /// </remarks>
    public static ulong FingerprintOf(RendezvousAddress at)
        => BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(ServiceConsensus.Canonical(at))));
}

/// <summary>
/// Les services où une paire a le droit de relayer.
/// </summary>
/// <remarks>
/// Le hasard du secret de paire décide qui est éligible, la latence ne fait
/// que départager. Choisir librement le plus proche de toute la liste
/// laisserait un opérateur qui pose des serveurs partout aspirer les relais
/// de régions entières.
///
/// Une région ne compte qu'avec deux familles au moins : un service seul
/// dans la sienne y prendrait sinon tous les relais.
/// </remarks>
public static class RelayPlacement
{
    public const int MinimumFamilies = 2;

    private static readonly IComparer<byte[]> Descending =
        Comparer<byte[]>.Create((one, other) => other.AsSpan().SequenceCompareTo(one));

    public static IReadOnlyList<RelayPlace> Eligible(
        ReadOnlySpan<byte> pairSecret, IReadOnlyList<ConsensusEntry> entries, IReadOnlyList<RendezvousAddress> anchors)
    {
        var places = new List<RelayPlace>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        void Add(RendezvousAddress at, string? region, bool open)
        {
            if (taken.Add(ServiceConsensus.Canonical(at)))
                places.Add(new RelayPlace(at, region, open));
        }

        var listed = new List<(RendezvousAddress At, ConsensusEntry Entry, byte[] Score)>();

        foreach (var entry in entries)
            if (RendezvousAddress.TryParse(entry.Address, out var at, out _))
                listed.Add((at, entry, ServicePlacement.Score(pairSecret, ServiceConsensus.Canonical(at))));

        var regionOf = listed
            .GroupBy(item => ServiceConsensus.Canonical(item.At), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Entry.Region, StringComparer.Ordinal);

        foreach (var at in ServicePlacement.Choose(pairSecret, entries))
            Add(at, regionOf.GetValueOrDefault(ServiceConsensus.Canonical(at)), open: true);

        foreach (var region in listed
                     .Where(item => item.Entry.Region is not null)
                     .GroupBy(item => item.Entry.Region!, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var families = region.Select(item => Convert.ToHexString(item.Entry.Family)).Distinct(StringComparer.Ordinal).Count();

            if (families < MinimumFamilies)
                continue;

            var best = region.OrderBy(item => item.Score, Descending).First();
            Add(best.At, region.Key, open: true);
        }

        foreach (var at in anchors)
            Add(at, null, open: false);

        return places;
    }
}
