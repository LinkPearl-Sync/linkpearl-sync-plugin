using System.Security.Cryptography;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transfer;
using Xunit;

namespace Linkpearl.Core.Tests.Transfer;

/// <summary>
/// Le récepteur est l'endroit où atterrit tout ce qu'un pair envoie. Ces tests
/// portent surtout sur ce qu'il refuse.
/// </summary>
public sealed class BlobTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "linkpearl-xfer-" + Guid.NewGuid().ToString("N"));

    private sealed class Clock : Linkpearl.Core.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    }

    private FileSystemBlobStore NewStore()
        => new(Path.Combine(_root, Guid.NewGuid().ToString("N")), new CacheSettings(), new Clock(), _ => long.MaxValue);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static byte[] Content(int size)
    {
        var content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    /// <summary>L'annonce d'un tronçon, telle que l'émetteur l'écrit.</summary>
    private static byte[] Start(BlobHash hash, long size, long offset, long length)
    {
        var payload = new byte[BlobSegments.StartLength];
        BlobSegments.WriteStart(payload, hash, size, offset, length);
        return payload;
    }

    private static byte[] End(BlobHash hash)
    {
        var payload = new byte[BlobHash.SizeInBytes];
        hash.TryWriteTo(payload);
        return payload;
    }

    private static byte[] Chunk(ReadOnlySpan<byte> data)
    {
        var payload = new byte[4 + data.Length];
        data.CopyTo(payload.AsSpan(4));
        return payload;
    }

    /// <summary>Ce que le plan demande : chaque blob avec la taille du manifeste.</summary>
    internal static Dictionary<BlobHash, long> Want(params (BlobHash Hash, long Size)[] wanted)
        => wanted.ToDictionary(w => w.Hash, w => w.Size);

    [Fact]
    public async Task Une_annonce_qui_ne_porte_pas_la_taille_du_manifeste_est_refusee()
    {
        // Le manifeste a été jugé sur cette taille, plafond total compris : un
        // pair qui annonce plus en route ferait réserver ce que la validation
        // n'a jamais accepté.
        var hash = BlobHash.OfContent("x"u8);
        await using var receiver = new BlobReceiver(NewStore(), Quotas.Default, BlobTransferTests.Want((hash, 1000)));

        var outcome = await receiver.HandleAsync(1, MessageKind.BlobStart, Start(hash, 100_000, 0, 100_000), default);

        Assert.False(outcome.Accepted);
        Assert.Contains("manifeste", outcome.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FileSystemBlobStore> StoreWith(byte[] content)
    {
        var store = NewStore();
        var hash = BlobHash.OfContent(content);

        await using var writer = await store.BeginWriteAsync(hash, content.Length, default);
        await writer.WriteAsync(content, default);
        await writer.CommitAsync(default);

        return store;
    }

    [Fact]
    public async Task Un_blob_traverse_l_emetteur_et_le_recepteur_a_l_identique()
    {
        var content = Content(200_000);
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = NewStore();

        var sender = new BlobSender(source, blockSize: 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((hash, content.Length)));

        await foreach (var frame in sender.FramesFor(hash, default))
        {
            var outcome = await receiver.HandleAsync(1, frame.Kind, frame.Payload, default);
            Assert.True(outcome.Accepted, outcome.Rejection);
        }

        Assert.True(destination.TryGetSize(hash, out var size));
        Assert.Equal(content.Length, size);

        await using var stream = await destination.OpenReadAsync(hash, default);
        Assert.Equal(hash, await BlobHash.OfStreamAsync(stream, default));
    }

    [Fact]
    public async Task Un_blob_vide_traverse_aussi()
    {
        var content = Array.Empty<byte>();
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = NewStore();

        var sender = new BlobSender(source, 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((hash, content.Length)));

        await foreach (var frame in sender.FramesFor(hash, default))
            Assert.True((await receiver.HandleAsync(1, frame.Kind, frame.Payload, default)).Accepted);

        Assert.True(destination.TryGetSize(hash, out _));
    }

    [Fact]
    public async Task Un_blob_qu_on_n_a_pas_demande_est_refuse()
    {
        // Un pair ne décide pas de ce qu'on stocke : seul ce que le plan a
        // demandé est accepté.
        var content = Content(1000);
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = NewStore();

        var sender = new BlobSender(source, 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want());

        var first = await sender.FramesFor(hash, default).FirstAsync();
        var outcome = await receiver.HandleAsync(1, first.Kind, first.Payload, default);

        Assert.False(outcome.Accepted);
        Assert.Contains("demandé", outcome.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Un_bloc_sans_annonce_prealable_est_refuse()
    {
        var destination = NewStore();
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want());

        var outcome = await receiver.HandleAsync(1, MessageKind.BlobChunk, new byte[100], default);

        Assert.False(outcome.Accepted);
        Assert.NotNull(outcome.Rejection);
    }

    [Fact]
    public async Task Un_blob_plus_gros_que_le_plafond_est_refuse_des_l_annonce()
    {
        var hash = BlobHash.OfContent("x"u8);
        var destination = NewStore();
        var quotas = Quotas.Default with { MaxBlobBytes = 1000 };

        await using var receiver = new BlobReceiver(destination, quotas, BlobTransferTests.Want((hash, 5000)));

        var outcome = await receiver.HandleAsync(1, MessageKind.BlobStart, Start(hash, 5000, 0, 5000), default);

        Assert.False(outcome.Accepted);
        Assert.Contains("plafond", outcome.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Un_envoi_qui_depasse_la_taille_annoncee_est_coupe()
    {
        // Sans ce contrôle, un pair pourrait annoncer un octet et en envoyer
        // autant qu'il veut, jusqu'à remplir le disque.
        var content = Content(50_000);
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = NewStore();

        var sender = new BlobSender(source, 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((hash, content.Length)));

        var frames = new List<OutgoingFrame>();
        await foreach (var frame in sender.FramesFor(hash, default))
            frames.Add(frame);

        // On rejoue un bloc de données : le total dépasse alors l'annonce.
        var chunk = frames.First(f => f.Kind == MessageKind.BlobChunk);
        await receiver.HandleAsync(1, frames[0].Kind, frames[0].Payload, default);

        ReceiveOutcome? refus = null;
        for (var i = 0; i < 10 && refus is null; i++)
        {
            var outcome = await receiver.HandleAsync(1, chunk.Kind, chunk.Payload, default);
            if (outcome.Accepted is false)
                refus = outcome;
        }

        Assert.NotNull(refus);
        Assert.Contains("annoncée", refus!.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Un_contenu_qui_ne_correspond_pas_a_l_empreinte_annoncee_est_refuse_a_la_cloture()
    {
        var annonce = BlobHash.OfContent("ce qui est annoncé"u8);
        var envoye = "ce qui est envoyé"u8.ToArray();

        var destination = NewStore();
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((annonce, envoye.Length)));

        var start = Start(annonce, envoye.Length, 0, envoye.Length);

        Assert.True((await receiver.HandleAsync(1, MessageKind.BlobStart, start, default)).Accepted);
        Assert.True((await receiver.HandleAsync(1, MessageKind.BlobChunk, Chunk(envoye), default)).Accepted);

        var outcome = await receiver.HandleAsync(1, MessageKind.BlobEnd, End(annonce), default);

        Assert.False(outcome.Accepted);
        Assert.False(destination.TryGetSize(annonce, out _));
    }

    [Fact]
    public async Task Une_annonce_pendant_un_transfert_en_cours_est_refusee()
    {
        // Un transfert entrant à la fois par pair : sinon un pair ouvre mille
        // écritures et fait exploser la mémoire et les descripteurs.
        var content = Content(50_000);
        var hash = BlobHash.OfContent(content);
        var other = Content(50_000);
        var otherHash = BlobHash.OfContent(other);

        var destination = NewStore();
        await using var receiver = new BlobReceiver(
            destination, Quotas.Default, Want((hash, content.Length), (otherHash, other.Length)));

        var start = Start(hash, content.Length, 0, content.Length);

        Assert.True((await receiver.HandleAsync(1, MessageKind.BlobStart, start, default)).Accepted);
        var outcome = await receiver.HandleAsync(1, MessageKind.BlobStart, start, default);

        Assert.False(outcome.Accepted);
        Assert.Contains("cours", outcome.Rejection!, StringComparison.OrdinalIgnoreCase);

        // Mais sur un autre canal, un second blob est parfaitement légitime :
        // c'est de là que vient tout le parallélisme du transfert.
        var second = Start(otherHash, other.Length, 0, other.Length);
        Assert.True((await receiver.HandleAsync(2, MessageKind.BlobStart, second, default)).Accepted);
    }

    [Theory]
    [InlineData(MessageKind.BlobStart, 10)]
    [InlineData(MessageKind.BlobChunk, 2)]
    [InlineData(MessageKind.BlobEnd, 5)]
    public async Task Un_message_tronque_est_refuse_sans_lever(byte kind, int length)
    {
        var destination = NewStore();
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want());

        var outcome = await receiver.HandleAsync(1, kind, new byte[length], default);

        Assert.False(outcome.Accepted);
        Assert.NotNull(outcome.Rejection);
    }

    [Fact]
    public async Task Un_type_de_message_inconnu_est_refuse()
    {
        var destination = NewStore();
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want());

        Assert.False((await receiver.HandleAsync(1, 0xFE, new byte[10], default)).Accepted);
    }

    [Fact]
    public async Task Un_blob_deja_en_cache_n_est_pas_reecrit()
    {
        var content = Content(5_000);
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = await StoreWith(content);

        var sender = new BlobSender(source, 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((hash, content.Length)));

        var start = await sender.FramesFor(hash, default).FirstAsync();
        var outcome = await receiver.HandleAsync(1, start.Kind, start.Payload, default);

        Assert.True(outcome.Accepted);
        Assert.True(outcome.AlreadyPresent);
    }
}

/// <summary>
/// Les gros blobs, découpés en tronçons répartis sur plusieurs canaux.
/// </summary>
/// <remarks>
/// Mesuré au faux pair, à 20 ms de latence et seize canaux : les 364 premiers
/// mégaoctets arrivaient en 18 s, puis un seul blob de 85 Mo en prenait 20 à
/// lui seul, sur un canal plafonné à 1,9 Mo/s. Le découpage est ce qui supprime
/// cette traîne ; ces tests portent sur ce qu'un pair pourrait en abuser.
/// </remarks>
public sealed class BlobSegmentTests : IDisposable
{
    private const int Segment = BlobSegments.SegmentSize;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "linkpearl-segments-" + Guid.NewGuid().ToString("N"));

    private sealed class Clock : Linkpearl.Core.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private FileSystemBlobStore NewStore()
        => new(Path.Combine(_root, Guid.NewGuid().ToString("N")), new CacheSettings(), new Clock(), _ => long.MaxValue);

    private static byte[] Content(int size)
    {
        var content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    private async Task<FileSystemBlobStore> StoreWith(byte[] content)
    {
        var store = NewStore();

        await using var writer = await store.BeginWriteAsync(BlobHash.OfContent(content), content.Length, default);
        await writer.WriteAsync(content, default);
        await writer.CommitAsync(default);

        return store;
    }

    private static byte[] Start(BlobHash hash, long size, long offset, long length)
    {
        var payload = new byte[BlobSegments.StartLength];
        BlobSegments.WriteStart(payload, hash, size, offset, length);
        return payload;
    }

    [Fact]
    public void Un_petit_blob_tient_en_un_seul_troncon_et_un_gros_en_plusieurs()
    {
        Assert.Equal([(0L, 1000L)], BlobSegments.Of(1000));
        Assert.Equal([(0L, 0L)], BlobSegments.Of(0));

        var segments = BlobSegments.Of((2L * Segment) + 10);

        Assert.Equal([(0L, (long)Segment), (Segment, Segment), (2L * Segment, 10L)], segments);
    }

    [Fact]
    public async Task Un_gros_blob_arrive_entier_par_plusieurs_canaux_entrelaces()
    {
        var content = Content((2 * Segment) + 12_345);
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = NewStore();

        var sender = new BlobSender(source, 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((hash, content.Length)));

        // Chaque tronçon sur son canal, et les trames des trois entrelacées,
        // le dernier tronçon en tête : l'ordre d'arrivée entre canaux n'est
        // jamais garanti.
        var streams = new List<(byte Channel, List<OutgoingFrame> Frames)>();
        byte channel = 1;

        foreach (var (offset, length) in BlobSegments.Of(content.Length).Reverse())
        {
            var frames = new List<OutgoingFrame>();
            await foreach (var frame in sender.FramesFor(hash, offset, length, default))
                frames.Add(frame);

            streams.Add((channel++, frames));
        }

        var completed = 0;

        for (var i = 0; streams.Any(s => i < s.Frames.Count); i++)
        {
            foreach (var (on, frames) in streams)
            {
                if (i >= frames.Count)
                    continue;

                var outcome = await receiver.HandleAsync(on, frames[i].Kind, frames[i].Payload, default);
                Assert.True(outcome.Accepted, outcome.Rejection);

                if (outcome.BlobCompleted)
                    completed++;
            }
        }

        Assert.Equal(1, completed);

        await using var stream = await destination.OpenReadAsync(hash, default);
        Assert.Equal(hash, await BlobHash.OfStreamAsync(stream, default));
    }

    [Fact]
    public async Task Un_troncon_non_aligne_est_refuse()
    {
        // Des tronçons qui se chevauchent laisseraient un pair écrire deux fois
        // la même zone et masquer ce qu'il y a mis la première fois.
        var hash = BlobHash.OfContent("x"u8);
        await using var receiver = new BlobReceiver(NewStore(), Quotas.Default, BlobTransferTests.Want((hash, 3L * Segment)));

        var outcome = await receiver.HandleAsync(
            1, MessageKind.BlobStart, Start(hash, 3L * Segment, 1000, Segment), default);

        Assert.False(outcome.Accepted);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(Segment + 1)]
    public async Task Un_troncon_de_longueur_inattendue_est_refuse(long length)
    {
        var hash = BlobHash.OfContent("x"u8);
        await using var receiver = new BlobReceiver(NewStore(), Quotas.Default, BlobTransferTests.Want((hash, 3L * Segment)));

        var outcome = await receiver.HandleAsync(
            1, MessageKind.BlobStart, Start(hash, 3L * Segment, 0, length), default);

        Assert.False(outcome.Accepted);
    }

    [Fact]
    public async Task Le_meme_troncon_sur_deux_canaux_est_refuse()
    {
        var hash = BlobHash.OfContent("x"u8);
        await using var receiver = new BlobReceiver(NewStore(), Quotas.Default, BlobTransferTests.Want((hash, 3L * Segment)));

        var start = Start(hash, 3L * Segment, Segment, Segment);

        Assert.True((await receiver.HandleAsync(1, MessageKind.BlobStart, start, default)).Accepted);
        Assert.False((await receiver.HandleAsync(2, MessageKind.BlobStart, start, default)).Accepted);
    }

    [Fact]
    public async Task Deux_tailles_differentes_pour_un_meme_blob_sont_refusees()
    {
        var hash = BlobHash.OfContent("x"u8);
        await using var receiver = new BlobReceiver(NewStore(), Quotas.Default, BlobTransferTests.Want((hash, 3L * Segment)));

        Assert.True((await receiver.HandleAsync(
            1, MessageKind.BlobStart, Start(hash, 3L * Segment, 0, Segment), default)).Accepted);

        var outcome = await receiver.HandleAsync(
            2, MessageKind.BlobStart, Start(hash, 2L * Segment, Segment, Segment), default);

        Assert.False(outcome.Accepted);
    }

    [Fact]
    public async Task Une_annonce_a_l_ancien_format_dit_de_mettre_a_jour()
    {
        var hash = BlobHash.OfContent("x"u8);
        await using var receiver = new BlobReceiver(NewStore(), Quotas.Default, BlobTransferTests.Want((hash, 1)));

        var old = new byte[BlobHash.SizeInBytes + 8];
        hash.TryWriteTo(old);

        var outcome = await receiver.HandleAsync(1, MessageKind.BlobStart, old, default);

        Assert.False(outcome.Accepted);
        Assert.Contains("jour", outcome.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Un_troncon_falsifie_fait_refuser_le_blob_entier()
    {
        var content = Content(Segment + 5_000);
        var hash = BlobHash.OfContent(content);

        var source = await StoreWith(content);
        var destination = NewStore();

        var sender = new BlobSender(source, 16 * 1024);
        await using var receiver = new BlobReceiver(destination, Quotas.Default, BlobTransferTests.Want((hash, content.Length)));

        ReceiveOutcome? last = null;
        byte channel = 1;
        var tampered = false;

        foreach (var (offset, length) in BlobSegments.Of(content.Length))
        {
            await foreach (var frame in sender.FramesFor(hash, offset, length, default))
            {
                var payload = frame.Payload;

                // Un octet changé dans le premier bloc du second tronçon.
                if (offset > 0 && frame.Kind == MessageKind.BlobChunk && tampered is false)
                {
                    payload = (byte[])payload.Clone();
                    payload[10] ^= 0xFF;
                    tampered = true;
                }

                last = await receiver.HandleAsync(channel, frame.Kind, payload, default);
            }

            channel++;
        }

        Assert.False(last!.Accepted);
        Assert.False(destination.TryGetSize(hash, out _));
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async Task<T> FirstAsync<T>(this IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
            return item;

        throw new InvalidOperationException("séquence vide");
    }
}
