using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Linkpearl.Core.Safety;
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
    public async Task Un_tiers_qui_connait_le_jeton_ne_prend_pas_la_tentative()
    {
        // Le jeton passe en clair, et tout membre d'un groupe (ou n'importe
        // qui pour le Public) peut le dériver. Présenté depuis une adresse que
        // la tentative n'a pas composée, il ne doit pas la servir.
        await using var net = new LoopbackFactories(2);
        var (us, intruder) = (net.Factories[0], net.Factories[1]);
        using var absent = Silent(out var peerAt);

        var ours = us.ConnectAsync([peerAt], "jeton-de-la-paire", Short, default);
        await Task.Delay(100);
        var theirs = await intruder.ConnectAsync([LoopbackFactories.At(us)], "jeton-de-la-paire", Short, default);

        Assert.Null(await ours);
        Assert.Null(theirs);
    }

    /// <summary>Une réponse de réflexion telle que LiteNetLib la livre : son en-tête, puis la réponse du service.</summary>
    private static byte[] Reflected(IPEndPoint seen)
    {
        var address = seen.Address.GetAddressBytes();
        return [0x09, 0x08, (byte)address.Length, .. address, (byte)(seen.Port >> 8), (byte)seen.Port];
    }

    /// <summary>Un faux rendez-vous qui répond à la première requête reçue par <paramref name="answer"/>.</summary>
    private static async Task AnswerOnceAsync(Socket rendezvous, IPEndPoint answer)
    {
        var buffer = new byte[64];
        var received = await rendezvous.ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0));
        await rendezvous.SendToAsync(Reflected(answer), received.RemoteEndPoint);
    }

    [Fact]
    public async Task La_reflexion_n_ecoute_que_le_rendez_vous_interroge()
    {
        await using var net = new LoopbackFactories(1);
        var us = net.Factories[0];
        using var rendezvous = Silent(out var rendezvousAt);
        using var intruder = Silent(out _);

        // Un tiers qui connaît notre socket inonde de fausses réponses : un
        // port nul ferait refuser tout notre bloc de candidats par le pair.
        using var stop = new CancellationTokenSource();
        var forging = Task.Run(async () =>
        {
            while (stop.IsCancellationRequested is false)
            {
                await intruder.SendToAsync(Reflected(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 0)), LoopbackFactories.At(us));
                await Task.Delay(20);
            }
        });

        var answering = Task.Run(async () =>
        {
            await Task.Delay(400);
            await AnswerOnceAsync(rendezvous, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000));
        });

        var seen = await us.ReflectAsync(rendezvousAt, Long, default);
        await stop.CancelAsync();
        await forging;
        await answering;

        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000), seen);
    }

    [Fact]
    public async Task Une_reflexion_inutilisable_est_ignoree()
    {
        await using var net = new LoopbackFactories(1);
        var us = net.Factories[0];
        using var rendezvous = Silent(out var rendezvousAt);

        var answering = AnswerOnceAsync(rendezvous, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 0));
        var seen = await us.ReflectAsync(rendezvousAt, Short, default);
        await answering;

        Assert.Null(seen);
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

    /// <summary>Un pair muni du jeton, mais pas de notre code : il envoie ce qu'il veut.</summary>
    private static async Task<(NetManager Manager, NetPeer Peer)> RawPeerAsync(PeerLinkFactory us, string token)
    {
        var manager = new NetManager(new EventBasedNetListener()) { ChannelsCount = 2 };
        manager.Start();
        var peer = manager.Connect(LoopbackFactories.At(us), token);

        for (var waited = 0; peer.ConnectionState is not ConnectionState.Connected && waited < 5000; waited += 20)
            await Task.Delay(20);

        Assert.Equal(ConnectionState.Connected, peer.ConnectionState);
        return (manager, peer);
    }

    [Fact]
    public async Task Un_message_direct_au_dela_du_plafond_n_est_jamais_remis()
    {
        // Le relais bornait déjà un message reconstitué, le lien direct non :
        // LiteNetLib y acceptait 65 535 fragments par message, en préallouant
        // sur la foi du nombre annoncé, et la fabrique copiait le tout avant
        // la moindre vérification de la session.
        await using var net = new LoopbackFactories(1);
        var us = net.Factories[0];
        var sizes = new ConcurrentQueue<int>();
        us.Accepted += (_, link) => link.Received += (_, payload) => sizes.Enqueue(payload.Length);
        us.Allow("jeton-du-pair", "pair");

        var (manager, peer) = await RawPeerAsync(us, "jeton-du-pair");

        try
        {
            peer.Send(new byte[PeerLinkFactory.MaxMessageLength + 4096], 0, DeliveryMethod.ReliableOrdered);
            await Task.Delay(2000);

            Assert.DoesNotContain(sizes, size => size > PeerLinkFactory.MaxMessageLength);
        }
        finally
        {
            manager.Stop();
        }
    }

    [Fact]
    public async Task Un_message_au_plafond_passe_encore_en_direct()
    {
        await using var net = new LoopbackFactories(2);
        var (us, them) = (net.Factories[0], net.Factories[1]);
        var arrived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        us.Accepted += (_, link) => link.Received += (_, payload) => arrived.TrySetResult(payload.Length);
        us.Allow("jeton-du-pair", "pair");

        var link = await them.ConnectAsync([LoopbackFactories.At(us)], "jeton-du-pair", Long, default);
        Assert.NotNull(link);

        await link.SendAsync(0, new byte[PeerLinkFactory.MaxMessageLength], default);

        Assert.Equal(PeerLinkFactory.MaxMessageLength, await arrived.Task.WaitAsync(Long));
    }

    [Fact]
    public void Le_plafond_direct_laisse_passer_le_plus_gros_manifeste_accepte()
        // Le manifeste compressé est le plus gros message légitime ; son
        // enveloppe chiffrée ajoute quelques dizaines d'octets.
        => Assert.True(PeerLinkFactory.MaxMessageLength >= Quotas.Default.MaxManifestCompressedBytes + 1024);
}
