using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Un aller-retour UDP vers un service, par la réflexion qu'il sert déjà.</summary>
/// <remarks>
/// Aucun service n'a besoin d'être mis à jour pour être mesuré. Une socket
/// éphémère, et non celle du perçage : on veut un temps, pas une adresse.
/// </remarks>
public static class RelayPing
{
    public static async Task<TimeSpan?> PingAsync(
        RelayPlace place, Func<IPAddress, bool> acceptOpen, TimeSpan timeout, CancellationToken ct)
    {
        var address = await ResolveAsync(place, acceptOpen, ct).ConfigureAwait(false);

        if (address is null)
            return null;

        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address.AddressFamily is AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

        var watch = Stopwatch.StartNew();
        var reflected = await RendezvousClient.ReflectAsync(socket, new IPEndPoint(address, place.At.Port), timeout, ct).ConfigureAwait(false);

        return reflected is null ? null : watch.Elapsed;
    }

    private static async Task<IPAddress?> ResolveAsync(RelayPlace place, Func<IPAddress, bool> acceptOpen, CancellationToken ct)
    {
        // Un service du cercle ouvert n'a pas été choisi par l'utilisateur : on
        // ne vise qu'une adresse publique, comme pour l'annonce.
        if (place.Open)
            return await PeerConnector.PublicHostAsync(place.At.Host, Dns.GetHostAddressesAsync, acceptOpen, ct).ConfigureAwait(false) is { } host
                ? IPAddress.Parse(host)
                : null;

        if (IPAddress.TryParse(place.At.Host, out var literal))
            return literal;

        return (await Dns.GetHostAddressesAsync(place.At.Host, ct).ConfigureAwait(false)).FirstOrDefault();
    }
}
