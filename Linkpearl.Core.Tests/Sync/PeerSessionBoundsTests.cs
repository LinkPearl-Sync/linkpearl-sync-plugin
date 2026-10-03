using System.Net;
using System.Security.Cryptography;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>
/// Un lien en mémoire dont chaque envoi prend un temps variable avant de
/// partir, comme un envoi relayé qui attend sa socket.
/// </summary>
/// <remarks>
/// C'est ce qui laisse deux envois concurrents d'un même canal se doubler,
/// si rien ne les sérialise.
/// </remarks>
internal sealed class JitterLink : IPeerLink
{
    private readonly Lock _arrival = new();
    private JitterLink? _other;

    public static (JitterLink A, JitterLink B) Pair()
    {
        var a = new JitterLink();
        var b = new JitterLink();
        a._other = b;
        b._other = a;
        return (a, b);
    }

    public bool IsOpen { get; private set; } = true;

    public bool IsRelayed => true;

    public int RoundTripMs => 1;

    public float PacketLossPercent => 0;

    public EndPoint? Remote => null;

    public int PendingOn(byte channel) => 0;

    public event Action<byte, byte[]>? Received;

    public event Action<string>? Closed;

    public async ValueTask SendAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var copy = payload.ToArray();

        // Un délai qui varie d'un envoi à l'autre : le scellement est déjà
        // fait, l'ordre de départ ne l'est pas.
        await Task.Delay(Random.Shared.Next(0, 4), ct).ConfigureAwait(false);

        _other?.Arrive(channel, copy);
    }

    private void Arrive(byte channel, byte[] payload)
    {
        lock (_arrival)
            Received?.Invoke(channel, payload);
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        Closed?.Invoke("fermé");
        return ValueTask.CompletedTask;
    }
}

public sealed class PeerSessionBoundsTests : IDisposable
{
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(3);

    private readonly MovableClock _clock = new();
    private readonly List<IDisposable> _keys = [];

    public void Dispose()
    {
        foreach (var key in _keys)
            key.Dispose();
    }

    private (ECDsa Key, byte[] Public, PeerId Id) Identity()
    {
        var key = CryptoPrimitives.GenerateIdentity();
        _keys.Add(key);
        var point = CryptoPrimitives.ExportPublicPoint(key);
        return (key, point, PeerId.Of(point));
    }

    private static PairRecord Knowing((ECDsa Key, byte[] Public, PeerId Id) other)
        => new()
        {
            Id = other.Id,
            PublicKey = other.Public,
            PairSecret = new byte[32],
            DisplayName = "Pair",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            Trust = PairTrust.Accepted,
            PairedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
        };

    /// <summary>Deux identités, rangées pour que la première initie.</summary>
    private ((ECDsa Key, byte[] Public, PeerId Id) Initiator, (ECDsa Key, byte[] Public, PeerId Id) Responder) Ordered()
    {
        var a = Identity();
        var b = Identity();
        return string.CompareOrdinal(a.Id.ToHex(), b.Id.ToHex()) < 0 ? (a, b) : (b, a);
    }

    private async Task<(PeerSession Initiator, PeerSession Responder)> EstablishedAsync(IPeerLink initiatorLink, IPeerLink responderLink)
    {
        var (initiator, responder) = Ordered();
        using var timeout = new CancellationTokenSource(Quickly);

        var responding = PeerSession.EstablishAsync(
            responderLink, Knowing(initiator), responder.Id, responder.Key, _clock, new SilentLog(), timeout.Token);
        var initiating = PeerSession.EstablishAsync(
            initiatorLink, Knowing(responder), initiator.Id, initiator.Key, _clock, new SilentLog(), timeout.Token);

        var sessions = await Task.WhenAll(initiating, responding);
        Assert.NotNull(sessions[0]);
        Assert.NotNull(sessions[1]);
        return (sessions[0]!, sessions[1]!);
    }

    [Fact]
    public async Task Une_trame_de_handshake_de_mauvaise_taille_ferme_aussitot()
    {
        // Avant, n'importe quelle trame entrait dans la file du handshake, sans
        // borne, et la session attendait quinze secondes pour échouer.
        var (initiator, responder) = Ordered();
        var (otherSide, responderLink) = HeldLink.Pair();

        var responding = PeerSession.EstablishAsync(
            responderLink, Knowing(initiator), responder.Id, responder.Key, _clock, new SilentLog(), default);

        await otherSide.SendAsync(ChannelPlan.ControlChannel, new byte[HandshakeFormat.Message1Length + 1], default);

        Assert.Null(await responding.WaitAsync(Quickly));
        Assert.False(responderLink.IsOpen);
    }

    [Fact]
    public async Task Un_deluge_avant_la_fin_du_handshake_ferme_au_lieu_de_s_accumuler()
    {
        // Tout ce qui arrive après le dernier message du handshake et avant
        // notre passage au mode scellé est gardé pour plus tard. Sans borne,
        // un pair qui déverse sans attendre emplissait la mémoire du jeu.
        var (initiator, responder) = Ordered();
        var (initiatorLink, responderLink) = HeldLink.Pair();
        responderLink.DeliverFirst = 1;

        using var timeout = new CancellationTokenSource(Quickly);

        var responding = PeerSession.EstablishAsync(
            responderLink, Knowing(initiator), responder.Id, responder.Key, _clock, new SilentLog(), timeout.Token);
        var initiatorSession = await PeerSession.EstablishAsync(
            initiatorLink, Knowing(responder), initiator.Id, initiator.Key, _clock, new SilentLog(), timeout.Token);

        Assert.NotNull(initiatorSession);

        for (var i = 0; i < 100; i++)
            await initiatorSession.SendAsync(1, MessageKind.Hello, new byte[1024], timeout.Token);

        responderLink.Release();

        Assert.Null(await responding.WaitAsync(Quickly));
        Assert.False(responderLink.IsOpen);
    }

    [Fact]
    public async Task Quelques_trames_avant_la_fin_du_handshake_passent_toujours()
    {
        // La course corrigée plus tôt : ce que le pair envoie dès sa session
        // établie doit survivre à la borne.
        var (initiator, responder) = Ordered();
        var (initiatorLink, responderLink) = HeldLink.Pair();
        responderLink.DeliverFirst = 1;

        using var timeout = new CancellationTokenSource(Quickly);

        var responding = PeerSession.EstablishAsync(
            responderLink, Knowing(initiator), responder.Id, responder.Key, _clock, new SilentLog(), timeout.Token);
        var initiatorSession = await PeerSession.EstablishAsync(
            initiatorLink, Knowing(responder), initiator.Id, initiator.Key, _clock, new SilentLog(), timeout.Token);

        Assert.NotNull(initiatorSession);

        for (var i = 0; i < 10; i++)
            await initiatorSession.SendAsync((byte)(i % 3), MessageKind.Hello, BitConverter.GetBytes(i), timeout.Token);

        responderLink.Release();

        var responderSession = await responding;
        Assert.NotNull(responderSession);

        var received = new List<int>();

        for (var i = 0; i < 10; i++)
            received.Add(BitConverter.ToInt32((await responderSession.Messages.ReadAsync(timeout.Token)).Payload));

        Assert.Equal(Enumerable.Range(0, 10), received.Order());
    }

    [Fact]
    public async Task Une_trame_scellee_illisible_ferme_le_lien()
    {
        // Le transport livre chaque canal dans l'ordre et sans perte : une
        // trame qui ne s'ouvre pas ne vient pas d'un pair honnête. La jeter en
        // silence laissait la session vivante mais amputée.
        var (initiatorLink, responderLink) = HeldLink.Pair();
        var (_, responder) = await EstablishedAsync(initiatorLink, responderLink);

        var forged = new byte[SecureChannel.HeaderLength + 32 + CryptoPrimitives.TagLength];
        forged[2] = 0;
        forged[9] = 1;
        await initiatorLink.SendAsync(ChannelPlan.ControlChannel, forged, default);

        using var read = new CancellationTokenSource(Quickly);
        await Assert.ThrowsAnyAsync<Exception>(async () => await responder.Messages.ReadAsync(read.Token));

        for (var i = 0; i < 100 && responderLink.IsOpen; i++)
            await Task.Delay(10);

        Assert.False(responderLink.IsOpen);
    }

    [Fact]
    public async Task Des_envois_concurrents_d_un_meme_canal_arrivent_tous()
    {
        // Le compteur se prenait au scellement, l'envoi partait hors verrou :
        // deux envois concurrents pouvaient se doubler, et le receveur jetait
        // le plus ancien comme un rejeu.
        var (initiatorLink, responderLink) = JitterLink.Pair();
        var (initiator, responder) = await EstablishedAsync(initiatorLink, responderLink);

        const int Count = 200;

        await Task.WhenAll(Enumerable.Range(0, Count).Select(i => Task.Run(async () =>
            await initiator.SendAsync(1, MessageKind.Hello, BitConverter.GetBytes(i), default))));

        using var read = new CancellationTokenSource(Quickly);
        var received = new HashSet<int>();

        while (received.Count < Count)
        {
            var message = await responder.Messages.ReadAsync(read.Token);
            received.Add(BitConverter.ToInt32(message.Payload));
        }

        Assert.Equal(Count, received.Count);
        Assert.True(responderLink.IsOpen);
    }
}
