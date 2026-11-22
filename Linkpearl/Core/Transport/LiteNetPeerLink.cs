using System.Net;
using LiteNetLib;

namespace Linkpearl.Core.Transport;

/// <summary>Un lien LiteNetLib vers un pair.</summary>
/// <remarks>
/// Le chiffrement se fait au niveau du message applicatif, par
/// <c>SecureChannel</c>, et non au niveau du datagramme. La spec prévoyait de
/// sceller chaque datagramme par un <c>PacketLayerBase</c>, ce qui n'est pas
/// réalisable : cette couche appartient au <c>NetManager</c> entier et non à un
/// pair, alors que la clé de session est propre à chaque pair et n'existe qu'une
/// fois le handshake terminé. Sceller le datagramme rendrait le handshake
/// circulaire.
///
/// Conséquence assumée : les trames de contrôle de LiteNetLib restent en clair
/// et falsifiables par qui connaît l'adresse. C'est un déni de service, pas une
/// atteinte à la confidentialité : la charge utile reste chiffrée de bout en
/// bout. Le jeton de connexion, dérivé du secret de paire, empêche au moins un
/// inconnu d'ouvrir une session.
/// </remarks>
public sealed class LiteNetPeerLink(NetPeer peer) : IPeerLink
{
    public bool IsOpen => peer.ConnectionState is ConnectionState.Connected;

    public bool IsRelayed => false;

    public int RoundTripMs => peer.Ping * 2;

    public float PacketLossPercent => peer.Statistics.PacketLossPercent;

    public EndPoint? Remote => peer.Address is { } address ? new IPEndPoint(address, peer.Port) : null;

    public int PendingOn(byte channel) => peer.GetPacketsCountInReliableQueue(channel, ordered: true);

    public event Action<byte, byte[]>? Received;

    public event Action<string>? Closed;

    public ValueTask SendAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (IsOpen is false)
            throw new InvalidOperationException("lien fermé");

        // Le pair refuserait le message, et LiteNetLib lèverait de toute façon
        // au découpage : autant le dire dans les mêmes termes que le relais.
        if (payload.Length > PeerLinkFactory.MaxMessageLength)
            throw new ArgumentException($"message de {payload.Length} octets, plafond {PeerLinkFactory.MaxMessageLength}", nameof(payload));

        peer.Send(payload.Span, channel, DeliveryMethod.ReliableOrdered);
        return ValueTask.CompletedTask;
    }

    internal void Deliver(byte channel, byte[] payload) => Received?.Invoke(channel, payload);

    internal void Close(string reason) => Closed?.Invoke(reason);

    public ValueTask DisposeAsync()
    {
        if (IsOpen)
            peer.Disconnect();

        return ValueTask.CompletedTask;
    }
}
