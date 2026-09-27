using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Linkpearl.Core.Sync;

/// <summary>
/// Les adresses auxquelles un pair peut être joint.
/// </summary>
/// <remarks>
/// Scellé sous une clé dérivée du secret de paire avant d'être confié au
/// rendez-vous : le serveur transporte ce bloc sans pouvoir le lire, donc sans
/// apprendre les adresses locales d'un réseau domestique.
/// </remarks>
public static class CandidateSet
{
    public const int MaxCandidates = 8;

    public static byte[] Encode(IReadOnlyList<IPEndPoint> candidates)
    {
        var kept = candidates.Take(MaxCandidates).ToList();
        var body = new List<byte> { (byte)kept.Count };

        foreach (var candidate in kept)
        {
            var address = candidate.Address.GetAddressBytes();
            body.Add((byte)address.Length);
            body.AddRange(address);
            body.Add((byte)(candidate.Port >> 8));
            body.Add((byte)candidate.Port);
        }

        return body.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> body, out List<IPEndPoint> candidates, out string? rejection)
        => TryDecode(body, out candidates, out _, out rejection);

    /// <summary>Comme l'autre, et rend le nombre d'octets lus.</summary>
    /// <remarks>
    /// Ce qui suit les adresses est laissé aux extensions : un décodeur d'avant
    /// ne le lisait pas, ce qui permet d'y ajouter sans casser personne.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> body, out List<IPEndPoint> candidates, out int consumed, out string? rejection)
    {
        candidates = [];
        consumed = 0;

        if (body.Length < 1)
        {
            rejection = "bloc de candidats vide";
            return false;
        }

        var count = body[0];

        if (count > MaxCandidates)
        {
            rejection = $"trop de candidats ({count}, plafond {MaxCandidates})";
            return false;
        }

        var offset = 1;

        for (var i = 0; i < count; i++)
        {
            if (body.Length < offset + 1)
            {
                rejection = "bloc de candidats tronqué";
                return false;
            }

            var length = body[offset++];

            if (length is not (4 or 16) || body.Length < offset + length + 2)
            {
                rejection = "adresse de candidat malformée";
                return false;
            }

            var address = new IPAddress(body.Slice(offset, length));
            offset += length;

            var port = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            offset += 2;

            if (port == 0)
            {
                rejection = "port de candidat nul";
                return false;
            }

            candidates.Add(new IPEndPoint(address, port));
        }

        consumed = offset;
        rejection = null;
        return true;
    }

    /// <summary>
    /// Ordonne les candidats du plus prometteur au moins prometteur.
    /// </summary>
    /// <remarks>
    /// IPv6 d'abord : quand les deux pairs en ont une globale, il n'y a pas de
    /// NAT du tout, seulement un pare-feu d'état que le perçage ouvre
    /// trivialement. Puis les adresses privées, qui règlent le cas de deux
    /// joueurs sous le même toit. Puis le reste.
    /// </remarks>
    public static IReadOnlyList<IPEndPoint> InPriorityOrder(IEnumerable<IPEndPoint> candidates)
        => candidates
            .OrderBy(c => c.AddressFamily is AddressFamily.InterNetworkV6 ? 0 : IsPrivate(c.Address) ? 1 : 2)
            .ToList();

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily is not AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();

        return bytes[0] == 10
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
    }
}
