using System.Buffers.Binary;
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transfer;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>
/// Le dialogue vu d'un pair qui ne suit pas les règles : on parle ici à une
/// session brute, sans moteur en face, pour envoyer ce qu'un client honnête
/// n'enverrait jamais.
/// </summary>
public sealed class PeerExchangeTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "linkpearl-exchange-" + Guid.NewGuid().ToString("N"));
    private readonly MovableClock _clock = new();
    private readonly List<IDisposable> _keys = [];
    private readonly CancellationTokenSource _life = new();
    private readonly List<IAsyncDisposable> _sessions = [];

    private const string Top = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    private sealed record Setup(
        PeerExchange Exchange, PeerSession Hostile, FileSystemBlobStore Store, BlobHash Ours, BlobHash Foreign,
        FixedAppearance Appearance);

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();

        foreach (var session in _sessions)
            await session.DisposeAsync();

        foreach (var key in _keys)
            key.Dispose();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
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

    private static async Task<BlobHash> PutAsync(FileSystemBlobStore store, byte[] content)
    {
        var hash = BlobHash.OfContent(content);

        await using var writer = await store.BeginWriteAsync(hash, content.Length, default);
        await writer.WriteAsync(content, default);
        Assert.True((await writer.CommitAsync(default)).Accepted);

        return hash;
    }

    /// <summary>
    /// Un échange réel, avec dans son cache un blob de notre apparence et un
    /// blob reçu d'un tiers, et en face une session que le test pilote à la main.
    /// </summary>
    private async Task<Setup> SetupAsync(
        Func<bool>? mayShare = null, CharacterManifest? manifest = null, FileSystemBlobStore? source = null, bool serve = true)
    {
        var a = Identity();
        var b = Identity();
        var (linkA, linkB) = HeldLink.Pair();

        var establishing = PeerSession.EstablishAsync(linkB, Knowing(a), b.Id, b.Key, _clock, new SilentLog(), _life.Token);
        var ours = await PeerSession.EstablishAsync(linkA, Knowing(b), a.Id, a.Key, _clock, new SilentLog(), _life.Token);
        var hostile = await establishing;

        Assert.NotNull(ours);
        Assert.NotNull(hostile);
        _sessions.Add(ours);
        _sessions.Add(hostile);

        var store = source ?? new FileSystemBlobStore(Path.Combine(_root, "cache"), new CacheSettings(), _clock, _ => long.MaxValue);
        var mine = await PutAsync(store, "notre tenue"u8.ToArray());
        var foreign = await PutAsync(store, "la tenue d'un tiers croisé hier"u8.ToArray());

        var appearance = new FixedAppearance(
            manifest ?? new CharacterManifest(
                CharacterManifest.CurrentVersion, [new FileReplacement([Top], mine, "notre tenue"u8.Length)], "", null),
            PlayerFingerprint.Of("alice", 21));

        var limiter = new RateLimiter(_clock, new RateLimiterSettings()) { Bypassed = true };
        var exchange = new PeerExchange(ours, store, appearance, limiter, 4, 16 * 1024, Quotas.Default, new SilentLog(), mayShare: mayShare);

        _ = PumpAsync(ours, exchange);
        if (serve)
            _ = exchange.ServeAsync(_life.Token);

        return new Setup(exchange, hostile, store, mine, foreign, appearance);
    }

    private async Task PumpAsync(PeerSession session, PeerExchange exchange)
    {
        try
        {
            await foreach (var message in session.Messages.ReadAllAsync(_life.Token))
                await exchange.HandleAsync(message, _life.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static byte[] Want(params BlobHash[] hashes)
    {
        var payload = new byte[2 + (hashes.Length * BlobHash.SizeInBytes)];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)hashes.Length);

        for (var i = 0; i < hashes.Length; i++)
            hashes[i].TryWriteTo(payload.AsSpan(2 + (i * BlobHash.SizeInBytes)));

        return payload;
    }

    /// <summary>Ce que la session hostile reçoit pendant le délai donné.</summary>
    private static async Task<List<PeerMessage>> DrainAsync(PeerSession session, int milliseconds = 300)
    {
        var received = new List<PeerMessage>();
        using var window = new CancellationTokenSource(milliseconds);

        try
        {
            while (true)
                received.Add(await session.Messages.ReadAsync(window.Token));
        }
        catch (OperationCanceledException)
        {
        }

        return received;
    }

    private static IEnumerable<BlobHash> StartedBlobs(IEnumerable<PeerMessage> messages)
        => messages.Where(m => m.Kind == MessageKind.BlobStart)
                   .Select(m => { var (hash, _, _, _) = BlobSegments.ReadStart(m.Payload); return hash; });

    private static long ForeignSize(Setup setup)
    {
        Assert.True(setup.Store.TryGetSize(setup.Foreign, out var size));
        return size;
    }

    /// <summary>La présence du pair hostile : son personnage, et le manifeste qu'il dit montrer.</summary>
    private static Task HelloAsync(PeerSession session, BlobHash? announced = null)
    {
        var hello = new byte[PlayerFingerprint.SizeInBytes + BlobHash.SizeInBytes];
        PlayerFingerprint.Of("bob", 21).ToBytes().CopyTo(hello.AsSpan());
        announced?.TryWriteTo(hello.AsSpan(PlayerFingerprint.SizeInBytes));

        return SendAsync(session, MessageKind.Hello, hello);
    }

    private static Task SendAsync(PeerSession session, byte kind, byte[] payload)
        => session.SendAsync(ChannelPlan.ControlChannel, kind, payload, default).AsTask();

    [Fact]
    public async Task Un_blob_du_cache_hors_de_notre_manifeste_n_est_jamais_servi()
    {
        // Servir tout le cache ferait de nous un oracle (« as-tu croisé ce
        // joueur ? ») et la source des mods de nos autres pairs.
        var setup = await SetupAsync();

        await SendAsync(setup.Hostile, MessageKind.BlobWant, Want(setup.Foreign, setup.Ours));
        Assert.Empty(StartedBlobs(await DrainAsync(setup.Hostile)));

        await HelloAsync(setup.Hostile);
        await SendAsync(setup.Hostile, MessageKind.ManifestRequest, []);
        Assert.Contains(await DrainAsync(setup.Hostile), m => m.Kind == MessageKind.ManifestData);

        await SendAsync(setup.Hostile, MessageKind.BlobWant, Want(setup.Foreign, setup.Ours));
        Assert.Equal([setup.Ours], StartedBlobs(await DrainAsync(setup.Hostile)));
    }

    [Fact]
    public async Task Un_meme_blob_redemande_en_boucle_n_est_servi_que_deux_fois()
    {
        var setup = await SetupAsync();

        await HelloAsync(setup.Hostile);
        await SendAsync(setup.Hostile, MessageKind.ManifestRequest, []);
        await DrainAsync(setup.Hostile);

        for (var i = 0; i < 5; i++)
            await SendAsync(setup.Hostile, MessageKind.BlobWant, Want(setup.Ours));

        Assert.Equal(PeerExchange.MaxServesPerBlob, StartedBlobs(await DrainAsync(setup.Hostile)).Count());
    }

    [Fact]
    public async Task Sans_droit_d_envoi_ni_manifeste_ni_blob_ne_partent()
    {
        var setup = await SetupAsync(mayShare: () => false);

        await HelloAsync(setup.Hostile);
        await SendAsync(setup.Hostile, MessageKind.ManifestRequest, []);
        await SendAsync(setup.Hostile, MessageKind.BlobWant, Want(setup.Ours));

        var received = await DrainAsync(setup.Hostile);

        Assert.DoesNotContain(received, m => m.Kind == MessageKind.ManifestData);
        Assert.Empty(StartedBlobs(received));
    }

    [Fact]
    public async Task Un_droit_retire_en_cours_de_session_coupe_le_service()
    {
        var allowed = true;
        var setup = await SetupAsync(mayShare: () => allowed);

        await HelloAsync(setup.Hostile);
        await SendAsync(setup.Hostile, MessageKind.ManifestRequest, []);
        await DrainAsync(setup.Hostile);

        allowed = false;
        await SendAsync(setup.Hostile, MessageKind.BlobWant, Want(setup.Ours));

        Assert.Empty(StartedBlobs(await DrainAsync(setup.Hostile)));
    }

    [Fact]
    public async Task Un_manifeste_non_sollicite_est_ignore()
    {
        // Chacun coûte une décompression, une validation et un plan, et
        // remettait à zéro la réception en cours.
        var setup = await SetupAsync();
        var theirs = new CharacterManifest(
            CharacterManifest.CurrentVersion, [new FileReplacement([Top], setup.Foreign, ForeignSize(setup))], "", null);

        await SendAsync(setup.Hostile, MessageKind.ManifestData, ManifestCodec.Compress(theirs));
        await DrainAsync(setup.Hostile, 100);

        Assert.Null(setup.Exchange.View.Manifest);

        // Annoncé puis demandé, le même manifeste passe.
        await HelloAsync(setup.Hostile, ManifestCodec.HashOf(theirs));
        Assert.Contains(await DrainAsync(setup.Hostile, 200), m => m.Kind == MessageKind.ManifestRequest);

        await SendAsync(setup.Hostile, MessageKind.ManifestData, ManifestCodec.Compress(theirs));
        await DrainAsync(setup.Hostile, 100);

        Assert.NotNull(setup.Exchange.View.Manifest);
        Assert.Equal(ManifestCodec.HashOf(theirs), setup.Exchange.View.ManifestHash);
    }

    [Fact]
    public async Task Une_apparence_plus_lourde_que_la_moitie_du_quota_n_est_pas_telechargee()
    {
        // Épinglée tant qu'elle se reçoit ou s'affiche, elle doit tenir avec
        // la nôtre sous le quota : sinon l'éviction n'a plus rien à retirer.
        var small = new FileSystemBlobStore(
            Path.Combine(_root, "petit"), new CacheSettings { QuotaBytes = 10_000 }, _clock, _ => long.MaxValue);
        var setup = await SetupAsync(source: small);

        var heavy = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [new FileReplacement([Top], BlobHash.OfContent("lourd"u8), 6_000)], "", null);

        await HelloAsync(setup.Hostile, ManifestCodec.HashOf(heavy));
        Assert.Contains(await DrainAsync(setup.Hostile, 200), m => m.Kind == MessageKind.ManifestRequest);

        await SendAsync(setup.Hostile, MessageKind.ManifestData, ManifestCodec.Compress(heavy));

        Assert.DoesNotContain(await DrainAsync(setup.Hostile), m => m.Kind == MessageKind.BlobWant);
        Assert.Null(setup.Exchange.View.Manifest);
    }

    [Fact]
    public async Task Une_demande_arrivee_avant_la_presence_attend_la_presence()
    {
        // Le moteur ouvre la réception avant d'annoncer : un client honnête
        // demande parfois avant de s'être présenté. Rien ne part tant qu'on ne
        // sait pas quel personnage il est, mais la demande n'est pas perdue.
        var setup = await SetupAsync();

        await SendAsync(setup.Hostile, MessageKind.ManifestRequest, []);
        Assert.DoesNotContain(await DrainAsync(setup.Hostile), m => m.Kind == MessageKind.ManifestData);

        await HelloAsync(setup.Hostile);
        Assert.Contains(await DrainAsync(setup.Hostile), m => m.Kind == MessageKind.ManifestData);
    }

    [Fact]
    public async Task Les_demandes_au_dela_de_la_file_tombent()
    {
        // Le service est en retard sur la réception : sans borne, un pair qui
        // inonde ferait grossir la file dans le processus du jeu. Des blobs
        // distincts, pour que le plafond par blob ne masque pas celui de la file.
        var count = PeerExchange.WantQueueCapacity * 4;
        var replacements = new List<FileReplacement>();

        var store = new FileSystemBlobStore(Path.Combine(_root, "source"), new CacheSettings(), _clock, _ => long.MaxValue);

        for (var i = 0; i < count; i++)
        {
            var content = System.Text.Encoding.UTF8.GetBytes($"pièce {i}");
            replacements.Add(new FileReplacement(
                [$"chara/equipment/e{i:D4}/model/c0101e{i:D4}_top.mdl"], await PutAsync(store, content), content.Length));
        }

        var setup = await SetupAsync(
            manifest: new CharacterManifest(CharacterManifest.CurrentVersion, replacements, "", null), source: store, serve: false);

        await HelloAsync(setup.Hostile);
        await SendAsync(setup.Hostile, MessageKind.ManifestRequest, []);
        await DrainAsync(setup.Hostile);

        // Service arrêté : les demandes s'accumulent comme derrière un service en retard.
        foreach (var replacement in replacements)
            await SendAsync(setup.Hostile, MessageKind.BlobWant, Want(replacement.Hash));

        // Le service ne démarre qu'une fois toutes les demandes triées.
        await Task.Delay(300);
        _ = setup.Exchange.ServeAsync(_life.Token);

        var served = StartedBlobs(await DrainAsync(setup.Hostile, 1000)).Count();

        Assert.Equal(PeerExchange.WantQueueCapacity, served);
    }
}
