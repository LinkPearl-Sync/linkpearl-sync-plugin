using System.Threading.Channels;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport;
using Xunit;

namespace Linkpearl.Core.Tests.Transport;

/// <summary>Deux bouts de tuyau reliés en mémoire, comme le service les met bout à bout.</summary>
internal sealed class MemoryPipe(Channel<byte[]> outgoing, Channel<byte[]> incoming) : IRelayPipe
{
    /// <summary>Plus grande trame vue passer, pour vérifier la limite du service.</summary>
    public int LargestFrame { get; private set; }

    public static (MemoryPipe A, MemoryPipe B) Pair()
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        return (new MemoryPipe(ab, ba), new MemoryPipe(ba, ab));
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        LargestFrame = Math.Max(LargestFrame, payload.Length);
        await outgoing.Writer.WriteAsync(payload.ToArray(), ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Injecte une trame brute, comme le ferait un pair mal intentionné.</summary>
    public ValueTask InjectAsync(byte[] frame) => outgoing.Writer.WriteAsync(frame);

    public ValueTask DisposeAsync()
    {
        outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public class RelayPeerLinkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static (RelayPeerLink A, RelayPeerLink B, MemoryPipe PipeA) Linked()
    {
        var (a, b) = MemoryPipe.Pair();
        return (new RelayPeerLink(a, null), new RelayPeerLink(b, null), a);
    }

    private static Channel<(byte Channel, byte[] Payload)> Collect(RelayPeerLink link)
    {
        var received = Channel.CreateUnbounded<(byte, byte[])>();
        link.Received += (channel, payload) => received.Writer.TryWrite((channel, payload));
        return received;
    }

    [Fact]
    public async Task Un_message_arrive_entier_sur_son_canal()
    {
        var (a, b, _) = Linked();
        var received = Collect(b);
        _ = Collect(a);

        await a.SendAsync(7, new byte[] { 1, 2, 3 }, CancellationToken.None);

        var (channel, payload) = await received.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal(7, channel);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
        Assert.True(a.IsRelayed);
    }

    [Fact]
    public async Task Un_gros_message_se_fragmente_sous_la_limite_du_service()
    {
        // Le service refuse toute trame de plus de 64 Kio, et un manifeste
        // compressé les dépasse.
        var (a, b, pipeA) = Linked();
        var received = Collect(b);
        _ = Collect(a);

        var big = new byte[300_000];
        Random.Shared.NextBytes(big);

        await a.SendAsync(0, big, CancellationToken.None);

        var (_, payload) = await received.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal(big, payload);
        Assert.True(pipeA.LargestFrame < 64 * 1024);
    }

    [Fact]
    public async Task Des_envois_concurrents_ne_se_melangent_pas()
    {
        // Deux émetteurs sur un même canal : sans un verrou par message, leurs
        // fragments s'entrelaceraient et chaque message serait recollé à
        // l'autre.
        var (a, b, _) = Linked();
        var received = Collect(b);
        _ = Collect(a);

        var messages = Enumerable.Range(0, 20)
            .Select(i => Enumerable.Repeat((byte)i, 100_000).ToArray())
            .ToList();

        await Task.WhenAll(messages.Select(m => a.SendAsync(3, m, CancellationToken.None).AsTask()));

        for (var i = 0; i < messages.Count; i++)
        {
            var (_, payload) = await received.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            Assert.Equal(100_000, payload.Length);
            Assert.All(payload, octet => Assert.Equal(payload[0], octet));
        }
    }

    [Fact]
    public async Task Rien_n_est_lu_avant_le_premier_abonne()
    {
        // Le premier message du handshake part aussitôt le relais ouvert. Lu
        // avant que la session ne s'abonne, il serait levé vers personne.
        var (a, b, _) = Linked();
        _ = Collect(a);

        await a.SendAsync(0, new byte[] { 42 }, CancellationToken.None);
        await Task.Delay(100);

        var received = Collect(b);
        var (_, payload) = await received.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal(new byte[] { 42 }, payload);
    }

    [Fact]
    public async Task Le_temps_d_aller_retour_se_mesure()
    {
        var (a, b, _) = Linked();
        _ = Collect(a);
        _ = Collect(b);

        // La première sonde part au démarrage ; son écho revient en quelques
        // millisecondes en mémoire.
        await Task.Delay(300);

        Assert.True(a.RoundTripMs >= 0);
        Assert.True(a.IsOpen);
        Assert.Equal(0f, a.PacketLossPercent);
    }

    [Fact]
    public async Task Un_message_demesure_ferme_le_lien()
    {
        // Une longueur venant du réseau ne s'alloue jamais sans borne : un pair
        // qui n'envoie jamais de dernier fragment ne remplit pas la mémoire du
        // jeu.
        var (_, b, pipeA) = Linked();
        var closed = new TaskCompletionSource<string>();
        b.Closed += reason => closed.TrySetResult(reason);
        _ = Collect(b);

        var fragment = new byte[2 + RelayPeerLink.FragmentLength];
        var needed = (RelayPeerLink.MaxMessageLength / RelayPeerLink.FragmentLength) + 2;

        for (var i = 0; i < needed && closed.Task.IsCompleted is false; i++)
            await pipeA.InjectAsync(fragment);

        var reason = await closed.Task.WaitAsync(Patience);
        Assert.Contains("plus de", reason);
        Assert.False(b.IsOpen);
    }

    [Fact]
    public async Task Un_type_inconnu_ferme_le_lien()
    {
        var (_, b, pipeA) = Linked();
        var closed = new TaskCompletionSource<string>();
        b.Closed += reason => closed.TrySetResult(reason);
        _ = Collect(b);

        await pipeA.InjectAsync(new byte[] { 0x7f, 0, 1 });

        Assert.Contains("inconnu", await closed.Task.WaitAsync(Patience));
    }

    private static async Task<string> RejectionOf(params byte[][] frames)
    {
        var (_, b, pipeA) = Linked();
        var closed = new TaskCompletionSource<string>();
        b.Closed += reason => closed.TrySetResult(reason);
        _ = Collect(b);

        foreach (var frame in frames)
            await pipeA.InjectAsync(frame);

        var reason = await closed.Task.WaitAsync(Patience);
        Assert.False(b.IsOpen);
        return reason;
    }

    private static byte[] Fragment(bool last, byte channel, int length)
    {
        var frame = new byte[2 + length];
        frame[0] = last ? (byte)0x01 : (byte)0x00;
        frame[1] = channel;
        return frame;
    }

    [Fact]
    public async Task Un_canal_hors_bornes_ferme_le_lien()
    {
        // Le canal est un octet du réseau : sans borne, un pair ouvrait un
        // réassemblage sur chacun des 256.
        Assert.Contains("hors bornes", await RejectionOf(Fragment(false, SecureChannel.MaxChannels, RelayPeerLink.FragmentLength)));
    }

    [Fact]
    public async Task Deux_messages_entrelaces_ferment_le_lien()
    {
        // L'émetteur envoie un message entier sous son verrou : deux
        // réassemblages en cours ne peuvent venir que d'un pair hostile, qui
        // multiplierait ainsi le plafond par le nombre de canaux.
        Assert.Contains("entrelacés", await RejectionOf(
            Fragment(false, 1, RelayPeerLink.FragmentLength),
            Fragment(false, 2, RelayPeerLink.FragmentLength)));
    }

    [Fact]
    public async Task Un_fragment_intermediaire_incomplet_ferme_le_lien()
    {
        // Des fragments d'un octet feraient enfler la liste sans approcher du
        // plafond en octets.
        Assert.Contains("fragment de 1 octets", await RejectionOf(Fragment(false, 1, 1)));
        Assert.Contains("fragment de", await RejectionOf(Fragment(true, 1, RelayPeerLink.FragmentLength + 1)));
    }

    [Fact]
    public async Task Un_message_au_plafond_se_reconstitue()
    {
        var (a, b, _) = Linked();
        var received = Collect(b);

        var payload = new byte[RelayPeerLink.MaxMessageLength];
        Random.Shared.NextBytes(payload);

        await a.SendAsync(3, payload, CancellationToken.None);
        await a.SendAsync(4, new byte[] { 9 }, CancellationToken.None);

        var (channel, message) = await received.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal(3, channel);
        Assert.Equal(payload, message);

        var (next, small) = await received.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal(4, next);
        Assert.Equal(new byte[] { 9 }, small);
    }

    [Fact]
    public async Task La_fermeture_du_tuyau_ferme_le_lien()
    {
        var (a, b, _) = Linked();
        var closed = new TaskCompletionSource<string>();
        b.Closed += reason => closed.TrySetResult(reason);
        _ = Collect(b);
        _ = Collect(a);

        await a.DisposeAsync();

        await closed.Task.WaitAsync(Patience);
        Assert.False(b.IsOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => b.SendAsync(1, new byte[] { 1 }, CancellationToken.None).AsTask());
    }

    [Fact]
    public void Les_deux_pairs_calculent_le_meme_jeton_de_relais()
    {
        // Chacun a son bloc de candidats et celui de l'autre, dans l'ordre
        // inverse : le jeton ne doit pas dépendre de qui calcule.
        var secret = new byte[32];
        Random.Shared.NextBytes(secret);

        byte[] ours = [1, 2, 3, 4];
        byte[] theirs = [9, 8, 7];

        var mine = PeerConnector.RelayTicketFor(secret, ours, theirs);
        var yours = PeerConnector.RelayTicketFor(secret, theirs, ours);

        Assert.Equal(mine, yours);
        Assert.Equal(16, mine.Length);
    }

    [Fact]
    public void Le_jeton_de_relais_depend_du_secret_et_de_l_echange()
    {
        var secret = new byte[32];
        var other = new byte[32];
        other[0] = 1;

        byte[] ours = [1, 2, 3];
        byte[] theirs = [4, 5, 6];

        var reference = PeerConnector.RelayTicketFor(secret, ours, theirs);

        Assert.NotEqual(reference, PeerConnector.RelayTicketFor(other, ours, theirs));
        Assert.NotEqual(reference, PeerConnector.RelayTicketFor(secret, ours, [4, 5, 7]));
    }
}
