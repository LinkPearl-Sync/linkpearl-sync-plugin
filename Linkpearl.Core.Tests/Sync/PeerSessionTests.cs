using System.Net;
using System.Text;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>
/// Un lien en mémoire qui peut retenir ce qu'il reçoit, puis le livrer d'un bloc.
/// </summary>
/// <remarks>
/// C'est ce qui rend déterministe la course de fin de handshake : les deux
/// trames livrées l'une après l'autre sans rendre la main, comme deux paquets
/// arrivés dans la même image.
/// </remarks>
internal sealed class HeldLink : IPeerLink
{
    private HeldLink? _other;
    private readonly Queue<(byte Channel, byte[] Payload)> _held = new();

    /// <summary>
    /// Ce qui arrive avant que quiconque ne se soit abonné. Deux sessions
    /// construites l'une après l'autre par le même appelant peuvent envoyer
    /// leur premier message avant que l'autre bout n'existe encore : sans
    /// cette file, ce message serait perdu en silence, au lieu d'attendre
    /// l'abonnement comme le ferait un vrai socket qui n'a encore livré à
    /// personne.
    /// </summary>
    private readonly Queue<(byte Channel, byte[] Payload)> _beforeSubscribed = new();

    private int _delivered;
    private Action<byte, byte[]>? _received;

    /// <summary>Au-delà de ce nombre de trames reçues, les suivantes sont retenues.</summary>
    public int DeliverFirst { get; set; } = int.MaxValue;

    public static (HeldLink A, HeldLink B) Pair()
    {
        var a = new HeldLink();
        var b = new HeldLink();
        a._other = b;
        b._other = a;
        return (a, b);
    }

    public bool IsOpen { get; private set; } = true;

    public bool IsRelayed => false;

    public int RoundTripMs => 1;

    public float PacketLossPercent => 0;

    /// <summary>Ce que la file d'envoi de chaque canal dit contenir, pour simuler un pair qui n'accuse pas.</summary>
    public int Pending { get; set; }

    public int PendingOn(byte channel) => Pending;

    public EndPoint? Remote => new IPEndPoint(IPAddress.Loopback, 7777);

    public event Action<byte, byte[]>? Received
    {
        add
        {
            _received += value;

            while (_beforeSubscribed.TryDequeue(out var frame))
                Arrive(frame.Channel, frame.Payload);
        }
        remove => _received -= value;
    }

    public event Action<string>? Closed;

    public ValueTask SendAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        _other?.Arrive(channel, payload.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Livre d'un seul tenant tout ce qui a été retenu.</summary>
    public void Release()
    {
        while (_held.TryDequeue(out var frame))
            _received?.Invoke(frame.Channel, frame.Payload);
    }

    private void Arrive(byte channel, byte[] payload)
    {
        if (_received is null)
        {
            _beforeSubscribed.Enqueue((channel, payload));
            return;
        }

        if (_delivered++ < DeliverFirst)
            _received?.Invoke(channel, payload);
        else
            _held.Enqueue((channel, payload));
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        Closed?.Invoke("fermé");
        return ValueTask.CompletedTask;
    }
}

public sealed class PeerSessionTests : IDisposable
{
    private readonly MovableClock _clock = new();
    private readonly List<IDisposable> _keys = [];

    public void Dispose()
    {
        foreach (var key in _keys)
            key.Dispose();
    }

    private (System.Security.Cryptography.ECDsa Key, byte[] Public, PeerId Id) Identity()
    {
        var key = CryptoPrimitives.GenerateIdentity();
        _keys.Add(key);
        var point = CryptoPrimitives.ExportPublicPoint(key);
        return (key, point, PeerId.Of(point));
    }

    private static PairRecord Knowing((System.Security.Cryptography.ECDsa Key, byte[] Public, PeerId Id) other, byte[] secret)
        => new()
        {
            Id = other.Id,
            PublicKey = other.Public,
            PairSecret = secret,
            DisplayName = "Pair",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            Trust = PairTrust.Accepted,
            PairedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public async Task Une_trame_scellee_arrivee_juste_apres_le_handshake_n_est_pas_perdue()
    {
        // Vu dans les tests du moteur sous charge : l'initiateur termine le
        // handshake dès son dernier message envoyé et envoie aussitôt son
        // premier message scellé. Si celui-ci arrive chez le répondeur avant
        // qu'il n'ait vérifié ce dernier message, il était rangé avec les trames
        // du handshake, que plus personne ne lit. Le répondeur ne recevait alors
        // jamais l'identité de l'autre, et n'appliquait rien.
        var a = Identity();
        var b = Identity();
        var (initiator, responder) = string.CompareOrdinal(a.Id.ToHex(), b.Id.ToHex()) < 0 ? (a, b) : (b, a);
        var secret = new byte[32];

        var (initiatorLink, responderLink) = HeldLink.Pair();

        // Le répondeur reçoit le premier message du handshake ; le dernier, et
        // tout ce qui suit, attendent.
        responderLink.DeliverFirst = 1;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var responding = PeerSession.EstablishAsync(
            responderLink, Knowing(initiator, secret), responder.Id, responder.Key, _clock, new SilentLog(), timeout.Token);

        var initiatorSession = await PeerSession.EstablishAsync(
            initiatorLink, Knowing(responder, secret), initiator.Id, initiator.Key, _clock, new SilentLog(), timeout.Token);

        Assert.NotNull(initiatorSession);

        await initiatorSession.SendAsync(ChannelPlan.ControlChannel, MessageKind.Hello, "salut"u8.ToArray(), timeout.Token);

        responderLink.Release();

        var responderSession = await responding;
        Assert.NotNull(responderSession);

        using var read = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var message = await responderSession.Messages.ReadAsync(read.Token);

        Assert.Equal(MessageKind.Hello, message.Kind);
        Assert.Equal("salut", Encoding.UTF8.GetString(message.Payload));
    }
}
