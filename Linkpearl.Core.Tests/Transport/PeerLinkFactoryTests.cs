using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Tests.Sync;
using Linkpearl.Core.Transport;
using Xunit;

namespace Linkpearl.Core.Tests.Transport;

/// <summary>
/// Plusieurs fabriques en boucle locale, sondées par un seul fil comme le
/// ferait le thread du jeu.
/// </summary>
internal sealed class LoopbackFactories : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _polling;

    public LoopbackFactories(int count)
    {
        Factories = Enumerable.Range(0, count).Select(_ => new PeerLinkFactory(2, new SilentLog())).ToArray();

        _polling = Task.Run(async () =>
        {
            while (_stop.IsCancellationRequested is false)
            {
                foreach (var factory in Factories)
                    factory.Poll();

                await Task.Delay(1).ConfigureAwait(false);
            }
        });
    }

    public PeerLinkFactory[] Factories { get; }

    public static IPEndPoint At(PeerLinkFactory factory) => new(IPAddress.Loopback, factory.LocalPort);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _polling;

        foreach (var factory in Factories)
            factory.Dispose();

        _stop.Dispose();
    }
}

public class PeerLinkFactoryTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    /// <summary>Une adresse qui reçoit sans jamais répondre, comme un pair absent.</summary>
    private static Socket Silent(out IPEndPoint at)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        at = (IPEndPoint)socket.LocalEndPoint!;
        return socket;
    }

    [Fact]
    public async Task Une_connexion_ne_sert_que_la_tentative_qui_porte_son_jeton()
    {
        // Le constat de l'audit : la première tentative en attente recevait
        // toute connexion établie. Un pair qui se présentait sous son propre
        // jeton captait celle qu'on destinait à un autre.
        await using var net = new LoopbackFactories(2);
        var (us, intruder) = (net.Factories[0], net.Factories[1]);
        using var absent = Silent(out var absentAt);

        var towardAbsent = us.ConnectAsync([absentAt], "jeton-du-pair-absent", Short, default);
        var towardIntruder = us.ConnectAsync([LoopbackFactories.At(intruder)], "jeton-de-l-intrus", Long, default);
        var fromIntruder = intruder.ConnectAsync([LoopbackFactories.At(us)], "jeton-de-l-intrus", Long, default);

        Assert.NotNull(await fromIntruder);
        Assert.NotNull(await towardIntruder);
        Assert.Null(await towardAbsent);
    }

    [Fact]
    public async Task Un_perçage_simultane_donne_un_seul_lien_partage()
    {
        // Les deux côtés se composent l'un l'autre au même moment, sous le
        // même jeton : chacun doit recevoir le lien, et ce doit être le même.
        await using var net = new LoopbackFactories(2);
        var (alice, bob) = (net.Factories[0], net.Factories[1]);

        var fromAlice = alice.ConnectAsync([LoopbackFactories.At(bob)], "jeton-commun", Long, default);
        var fromBob = bob.ConnectAsync([LoopbackFactories.At(alice)], "jeton-commun", Long, default);

        var aliceLink = await fromAlice;
        var bobLink = await fromBob;

        Assert.NotNull(aliceLink);
        Assert.NotNull(bobLink);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobLink.Received += (_, payload) => received.TrySetResult(payload);

        await aliceLink.SendAsync(0, "coucou"u8.ToArray(), default);

        Assert.Equal("coucou"u8.ToArray(), await received.Task.WaitAsync(Long));
    }

    [Fact]
    public async Task Un_jeton_ne_survit_pas_a_sa_tentative()
    {
        // Le jeton passe en clair. Tant qu'il restait accepté après la fin de
        // la tentative, qui l'avait lu pouvait ouvrir un lien plus tard.
        await using var net = new LoopbackFactories(2);
        var (us, observer) = (net.Factories[0], net.Factories[1]);
        using var absent = Silent(out var absentAt);

        us.Allow("jeton-lu-en-clair", "pair");
        Assert.Null(await us.ConnectAsync([absentAt], "jeton-lu-en-clair", Short, default));

        Assert.Null(await observer.ConnectAsync([LoopbackFactories.At(us)], "jeton-lu-en-clair", Short, default));
    }

    [Fact]
    public async Task Un_jeton_inconnu_n_ouvre_rien()
    {
        await using var net = new LoopbackFactories(2);
        var (us, stranger) = (net.Factories[0], net.Factories[1]);
        using var absent = Silent(out var absentAt);

        var ours = us.ConnectAsync([absentAt], "notre-jeton", Short, default);

        Assert.Null(await stranger.ConnectAsync([LoopbackFactories.At(us)], "un-autre-jeton", Short, default));
        Assert.Null(await ours);
    }

    [Fact]
    public async Task Une_autorisation_sans_tentative_ne_sert_qu_une_fois()
    {
        await using var net = new LoopbackFactories(3);
        var (us, first, second) = (net.Factories[0], net.Factories[1], net.Factories[2]);

        var accepted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        us.Accepted += (label, _) => accepted.TrySetResult(label);
        us.Allow("jeton-passif", "pair attendu");

        Assert.NotNull(await first.ConnectAsync([LoopbackFactories.At(us)], "jeton-passif", Long, default));
        Assert.Equal("pair attendu", await accepted.Task.WaitAsync(Long));

        Assert.Null(await second.ConnectAsync([LoopbackFactories.At(us)], "jeton-passif", Short, default));
    }

    [Fact]
    public async Task Une_connexion_que_personne_n_attend_est_refermee()
    {
        // Autorisée mais sans abonné ni tentative : le lien resterait ouvert
        // sans session. Il doit tomber.
        await using var net = new LoopbackFactories(2);
        var (us, peer) = (net.Factories[0], net.Factories[1]);
        us.Allow("jeton-orphelin", "pair");

        var link = await peer.ConnectAsync([LoopbackFactories.At(us)], "jeton-orphelin", Long, default);

        Assert.NotNull(link);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Closed += _ => closed.TrySetResult();

        if (link.IsOpen)
            await closed.Task.WaitAsync(Long);

        Assert.False(link.IsOpen);
    }
}
