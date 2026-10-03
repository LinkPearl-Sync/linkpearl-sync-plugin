using System.Net;
using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Core.Transport;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>Un lien en mémoire, pour faire dialoguer deux moteurs sans réseau.</summary>
/// <remarks>
/// La livraison est immédiate et dans l'ordre, ce qui est plus favorable que le
/// transport réel. Ces tests portent sur les décisions du moteur, pas sur le
/// transport : la fragmentation, la fenêtre et la perte sont éprouvées par le
/// harnais, qui fait passer les trames par LiteNetLib pour de bon.
/// </remarks>
internal sealed class MemoryLink : IPeerLink
{
    private MemoryLink? _other;

    public static (MemoryLink A, MemoryLink B) Pair()
    {
        var a = new MemoryLink();
        var b = new MemoryLink();

        a._other = b;
        b._other = a;

        return (a, b);
    }

    public bool IsOpen { get; private set; } = true;

    public bool IsRelayed => false;

    public int RoundTripMs => 12;

    public float PacketLossPercent => 0;

    public int PendingOn(byte channel) => 0;

    public EndPoint? Remote => new IPEndPoint(IPAddress.Loopback, 7777);

    public event Action<byte, byte[]>? Received;

    public event Action<string>? Closed;

    public ValueTask SendAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (IsOpen is false)
            throw new InvalidOperationException("lien fermé");

        _other?.Deliver(channel, payload.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (IsOpen)
        {
            IsOpen = false;
            _other?.Drop();
        }

        return ValueTask.CompletedTask;
    }

    private void Deliver(byte channel, byte[] payload) => Received?.Invoke(channel, payload);

    private void Drop()
    {
        if (IsOpen is false)
            return;

        IsOpen = false;
        Closed?.Invoke("le pair a raccroché");
    }
}

/// <summary>Le point de rencontre : deux appels forment un lien.</summary>
internal sealed class MeetingPoint
{
    private readonly object _gate = new();

    private TaskCompletionSource<IPeerLink>? _waiting;

    public Task<IPeerLink> JoinAsync()
    {
        lock (_gate)
        {
            if (_waiting is { } waiting)
            {
                _waiting = null;

                var (a, b) = MemoryLink.Pair();
                waiting.SetResult(a);

                return Task.FromResult<IPeerLink>(b);
            }

            _waiting = new TaskCompletionSource<IPeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _waiting.Task;
        }
    }
}

internal sealed class MeetingDialer(MeetingPoint point) : IPeerDialer
{
    public async Task<ConnectionAttempt> ConnectAsync(PairRecord pair, CancellationToken ct)
        => new(await point.JoinAsync().WaitAsync(ct), false, null);
}

internal sealed class FailingDialer(bool peerWasAbsent) : IPeerDialer
{
    public int Attempts { get; private set; }

    public Task<ConnectionAttempt> ConnectAsync(PairRecord pair, CancellationToken ct)
    {
        Attempts++;

        return Task.FromResult(new ConnectionAttempt(
            null, peerWasAbsent, peerWasAbsent ? null : "rendez-vous injoignable"));
    }
}

/// <summary>Une tentative qui ne se conclut jamais, comme une annonce en cours au rendez-vous.</summary>
internal sealed class NeverDialer : IPeerDialer
{
    public Task<ConnectionAttempt> ConnectAsync(PairRecord pair, CancellationToken ct)
        => Task.Delay(Timeout.Infinite, ct).ContinueWith(
            _ => new ConnectionAttempt(null, true, null), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
}

/// <summary>Un pair absent, dont l'annonce tient un moment au rendez-vous avant d'abandonner.</summary>
internal sealed class AnnouncingDialer(MovableClock clock, TimeSpan announce) : IPeerDialer
{
    public int Attempts { get; private set; }

    public Task<ConnectionAttempt> ConnectAsync(PairRecord pair, CancellationToken ct)
    {
        Attempts++;
        clock.Advance(announce);

        return Task.FromResult(new ConnectionAttempt(null, true, null));
    }
}

internal sealed class FixedAppearance(CharacterManifest? manifest, PlayerFingerprint? fingerprint) : ILocalAppearance
{
    public CharacterManifest? Manifest { get; set; } = manifest;

    public PlayerFingerprint? Fingerprint { get; set; } = fingerprint;

    public Task<CharacterManifest?> CurrentAsync(CancellationToken ct) => Task.FromResult(Manifest);
}

/// <summary>Une apparence dont CurrentAsync se bloque jusqu'à ce que le test le libère.</summary>
/// <remarks>Sert à faire chevaucher un tic en cours avec un DisposeAsync du moteur.</remarks>
internal sealed class BlockingAppearance(PlayerFingerprint? fingerprint) : ILocalAppearance
{
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlayerFingerprint? Fingerprint { get; } = fingerprint;

    public async Task<CharacterManifest?> CurrentAsync(CancellationToken ct)
    {
        await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return null;
    }
}

/// <summary>Rend un premier manifeste, puis un second à partir du deuxième appel.</summary>
/// <remarks>
/// Simule _current qui change entre l'appel d'AnnounceIfChangedAsync et celui
/// d'EvictIfDueAsync, au milieu d'un même tic : une reconstruction locale
/// tourne sur sa propre tâche, sans rien devoir au moteur.
/// </remarks>
internal sealed class SwappingAppearance(CharacterManifest? first, CharacterManifest? second, PlayerFingerprint? fingerprint) : ILocalAppearance
{
    private int _calls;

    public PlayerFingerprint? Fingerprint { get; } = fingerprint;

    public Task<CharacterManifest?> CurrentAsync(CancellationToken ct)
        => Task.FromResult(Interlocked.Increment(ref _calls) == 1 ? first : second);
}

internal sealed class RecordingApplicator : IRemoteApplicator
{
    public List<(PeerId Peer, GameObjectRef Target, CharacterManifest Manifest)> Applied { get; } = [];

    public List<PeerId> Removed { get; } = [];

    public bool Ready { get; set; } = true;

    /// <summary>Faux pour simuler un personnage qui ne finit pas de se charger à temps.</summary>
    public bool ExtrasSucceed { get; set; } = true;

    /// <summary>Tant qu'elle n'est pas levée, une application reste en cours, comme une cible qui charge.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<bool> ApplyAsync(PeerId peer, GameObjectRef target, CharacterManifest manifest, CancellationToken ct)
    {
        Applied.Add((peer, target, manifest));

        if (Hold is { } hold)
            await hold.Task.WaitAsync(ct);

        return ExtrasSucceed;
    }

    public List<(PeerId Peer, CharacterExtras Extras, ExtrasChange Change)> ExtrasApplied { get; } = [];

    public Task<bool> ApplyExtrasAsync(PeerId peer, GameObjectRef target, CharacterExtras extras, ExtrasChange change, CancellationToken ct)
    {
        if (ExtrasSucceed)
            ExtrasApplied.Add((peer, extras, change));

        return Task.FromResult(ExtrasSucceed);
    }

    public Task RemoveAsync(PeerId peer, CancellationToken ct)
    {
        Removed.Add(peer);
        return Task.CompletedTask;
    }

    public bool CanApply(out string reason)
    {
        reason = Ready ? string.Empty : "chargement d'écran";
        return Ready;
    }
}

internal sealed class SilentLog : ILogSink
{
    public List<string> Warnings { get; } = [];

    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warning(string message, Exception? exception = null) => Warnings.Add(message);
}

/// <summary>
/// Le moteur, éprouvé de bout en bout contre un autre moteur.
/// </summary>
/// <remarks>
/// Deux instances qui se parlent par un lien en mémoire, chacune avec son
/// carnet, son identité et son cache. Le handshake, le manifeste et le
/// transfert sont les vrais : seul le transport est remplacé. C'est le seul
/// montage qui prouve que les pièces s'emboîtent, et c'est aussi la base du
/// mode « faux pair » du harnais.
/// </remarks>
public sealed class SyncEngineTests : IDisposable
{
    private static readonly PlayerFingerprint AlicePrint = PlayerFingerprint.Of("alice", 21);
    private static readonly PlayerFingerprint BobPrint = PlayerFingerprint.Of("bob", 21);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "linkpearl-moteur-" + Guid.NewGuid().ToString("N"));

    private readonly MovableClock _clock = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Un_pair_visible_recoit_l_apparence_de_l_autre()
    {
        await using var world = await TwoEnginesAsync();

        var settled = await world.SettleAsync(
            () => world.BobApplicator.Applied.Count > 0,
            [],
            [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)]);

        Assert.True(settled, "l'apparence n'a jamais été posée : " + world.Describe());

        var applied = Assert.Single(world.BobApplicator.Applied);

        Assert.Equal(world.AliceId, applied.Peer);
        Assert.Equal(4, applied.Target.ObjectIndex);

        // Le blob a bien traversé : c'est la chaîne entière, du manifeste au cache.
        Assert.True(world.BobStore.TryGetSize(world.Blob, out var size));
        Assert.Equal(world.BlobSize, size);
    }

    [Fact]
    public async Task L_empreinte_du_pair_est_epinglee_a_la_premiere_rencontre()
    {
        await using var world = await TwoEnginesAsync();

        var settled = await world.SettleAsync(
            () => world.BobApplicator.Applied.Count > 0,
            [],
            [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)]);

        Assert.True(settled, "l'apparence n'a jamais été posée : " + world.Describe());
        Assert.Equal(AlicePrint, world.BobBook.Find(world.AliceId)!.PinnedFingerprint);
    }

    [Fact]
    public async Task Un_pair_qui_sort_du_champ_voit_son_apparence_retiree()
    {
        await using var world = await TwoEnginesAsync();

        var applied = await world.SettleAsync(
            () => world.BobApplicator.Applied.Count > 0,
            [],
            [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)]);

        Assert.True(applied, "l'apparence n'a jamais été posée");

        var removed = await world.SettleAsync(
            () => world.BobApplicator.Removed.Count > 0, [], []);

        Assert.True(removed, "l'apparence n'a jamais été retirée");
        Assert.Equal(world.AliceId, Assert.Single(world.BobApplicator.Removed));

        // La session reste ouverte : le pair va revenir, et tout refaire
        // coûterait un transfert complet.
        Assert.Equal(PeerSessionState.Connected, world.BobStatus().State);
    }

    [Fact]
    public async Task Une_apparence_deja_posee_n_est_pas_reposee_a_chaque_tic()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        for (var i = 0; i < 10; i++)
            await world.TickAsync([], sees);

        // Un redessin coûte un clignotement à l'écran : le refaire sans raison
        // se verrait immédiatement.
        Assert.Single(world.BobApplicator.Applied);
    }

    [Fact]
    public async Task Un_pair_mis_en_pause_est_debranche_et_son_apparence_effacee()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        world.BobBook.SetPaused(world.AliceId, true);
        await world.TickAsync([], sees);

        // Sans cela, la mise en pause serait un bouton sans effet visible.
        Assert.Equal(world.AliceId, Assert.Single(world.BobApplicator.Removed));
        Assert.Empty(world.Bob.Statuses);
    }

    [Fact]
    public async Task Une_empreinte_autre_que_celle_epinglee_n_applique_rien()
    {
        // Un pair pourrait revendiquer le personnage d'un tiers et nous faire
        // poser ses fichiers dessus, visible chez nous seuls.
        await using var world = await TwoEnginesAsync(pinOnBob: PlayerFingerprint.Of("quelqu-un-d-autre", 21));

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        var disputed = await world.SettleAsync(() => world.BobStatus().FingerprintDisputed, [], sees);

        Assert.True(disputed, "l'empreinte inattendue n'a pas été signalée");
        Assert.Empty(world.BobApplicator.Applied);
    }

    [Fact]
    public async Task L_extinction_du_moteur_efface_ce_qui_etait_pose()
    {
        var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        await world.DisposeAsync();

        // Désactiver le plugin ne doit rien changer à l'apparence du joueur.
        Assert.Equal(world.AliceId, Assert.Single(world.BobApplicator.Removed));
    }

    [Fact]
    public async Task Les_tentatives_s_espacent_apres_un_echec()
    {
        var dialer = new FailingDialer(peerWasAbsent: false);
        await using var engine = Solitary(dialer);

        await engine.TickAsync([], default);
        Assert.Equal(1, dialer.Attempts);

        // Le tic suivant ramasse l'échec et pose l'attente.
        await engine.TickAsync([], default);
        await engine.TickAsync([], default);
        Assert.Equal(1, dialer.Attempts);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await engine.TickAsync([], default);
        Assert.Equal(2, dialer.Attempts);

        // Doublement : cinq secondes ne suffisent plus.
        await engine.TickAsync([], default);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await engine.TickAsync([], default);
        Assert.Equal(2, dialer.Attempts);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await engine.TickAsync([], default);
        Assert.Equal(3, dialer.Attempts);
    }

    [Fact]
    public async Task Un_pair_hors_ligne_ne_fait_pas_monter_l_attente()
    {
        // L'absence n'est pas une panne : un ami hors ligne toute la journée
        // serait sinon réessayé une fois par heure au moment où il se connecte.
        var dialer = new FailingDialer(peerWasAbsent: true);
        await using var engine = Solitary(dialer);

        for (var round = 1; round <= 3; round++)
        {
            await engine.TickAsync([], default);
            await engine.TickAsync([], default);

            Assert.Equal(round, dialer.Attempts);

            _clock.Advance(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task Un_pair_recherche_ne_se_dit_pas_hors_ligne()
    {
        // Vu en jeu le 26 septembre : pendant les vingt-cinq secondes d'annonce
        // au rendez-vous, un pair repris s'affichait « hors ligne ».
        await using var engine = Solitary(new NeverDialer());

        await engine.TickAsync([], default);

        Assert.Equal(PeerPhase.Searching, engine.Statuses.Single().Phase);
    }

    [Theory]
    [InlineData(true, PeerPhase.Absent)]
    [InlineData(false, PeerPhase.Failing)]
    public async Task Une_tentative_vaine_dit_si_le_pair_etait_absent_ou_en_echec(bool absent, PeerPhase expected)
    {
        await using var engine = Solitary(new FailingDialer(peerWasAbsent: absent));

        await engine.TickAsync([], default);
        await engine.TickAsync([], default);

        Assert.Equal(expected, engine.Statuses.Single().Phase);
    }

    [Fact]
    public async Task L_attente_d_un_absent_se_compte_depuis_le_debut_de_l_annonce()
    {
        // L'annonce est tenue vingt-cinq secondes sur un cycle de trente. Si
        // l'attente partait de la fin de l'annonce, le cycle durerait
        // cinquante-cinq secondes, et une reprise attendrait jusqu'à trente.
        var dialer = new AnnouncingDialer(_clock, TimeSpan.FromSeconds(25));
        await using var engine = Solitary(dialer);

        await engine.TickAsync([], default);
        await engine.TickAsync([], default);
        Assert.Equal(1, dialer.Attempts);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await engine.TickAsync([], default);

        Assert.Equal(2, dialer.Attempts);
    }

    [Fact]
    public async Task Une_pause_se_dit_et_la_reprise_l_efface()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        var saved = 0;
        world.Bob.BookChanged += () => saved++;

        world.AliceBook.SetPaused(world.BobId, true);

        Assert.True(
            await world.SettleAsync(() => world.BobStatus().Phase is PeerPhase.PausedByPeer, [], sees),
            "la pause n'a jamais été dite : " + world.Describe());

        Assert.True(world.BobBook.Find(world.AliceId)!.PausedByPeer);
        Assert.True(saved > 0, "le carnet marqué n'a pas été donné à enregistrer");

        world.AliceBook.SetPaused(world.BobId, false);

        Assert.True(
            await world.SettleAsync(() => world.BobBook.Find(world.AliceId)!.PausedByPeer is false, [], sees),
            "la reprise n'a pas effacé la marque : " + world.Describe());

        Assert.NotEqual(PeerPhase.PausedByPeer, world.BobStatus().Phase);
    }

    [Fact]
    public async Task Sans_session_ouverte_la_pause_ne_dit_rien()
    {
        // Alice met Bob en pause avant qu'ils ne se soient jamais joints : rien
        // ne doit l'obliger à le chercher pour le lui dire.
        await using var world = await TwoEnginesAsync();

        // Alice tourne seule : elle cherche Bob, qui ne répond pas. C'est
        // pendant cette recherche, sans session, que tombe la pause.
        for (var i = 0; i < 5; i++)
        {
            world.Clock.Advance(TimeSpan.FromMilliseconds(50));
            await world.Alice.TickAsync([], default);
        }

        Assert.Contains(world.Alice.Statuses, status => status.Peer == world.BobId);

        world.AliceBook.SetPaused(world.BobId, true);

        for (var i = 0; i < 40; i++)
            await world.TickAsync([], []);

        Assert.False(world.BobBook.Find(world.AliceId)!.PausedByPeer);
    }

    [Fact]
    public async Task Un_blocage_ne_se_dit_pas()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        world.AliceBook.Block(world.BobId);

        for (var i = 0; i < 40; i++)
            await world.TickAsync([], sees);

        Assert.False(world.BobBook.Find(world.AliceId)!.PausedByPeer);
    }
    [Fact]
    public async Task Une_session_qui_tombe_se_rejoint_sans_attendre()
    {
        // Mettre en pause puis reprendre : l'autre côté voit tomber la session
        // et doit se réannoncer aussitôt, sans quoi la reprise attend son délai.
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        // Une session qui a tenu : ce n'est pas un lien qui clignote.
        world.Clock.Advance(TimeSpan.FromSeconds(30));

        world.AliceBook.SetPaused(world.BobId, true);
        await world.TickAsync([], sees);
        await world.TickAsync([], sees);

        world.AliceBook.SetPaused(world.BobId, false);

        // Deux secondes d'horloge au plus, bien moins que le premier délai.
        var back = false;

        for (var i = 0; i < 40 && back is false; i++)
        {
            await world.TickAsync([], sees);
            back = world.Bob.Statuses.Single().State is not (PeerSessionState.Disconnected
                or PeerSessionState.Connecting or PeerSessionState.Handshaking);
            await Task.Delay(10);
        }

        Assert.True(back, "la session n'est pas revenue sans délai : " + world.Describe());
    }

    [Fact]
    public async Task Un_changement_d_extras_seul_se_pose_sans_application_complete()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        world.AliceAppearance.Manifest = world.AliceAppearance.Manifest! with
        {
            Extras = CharacterExtras.None with { Honorific = "{\"Title\":\"le Voyageur\"}" },
        };

        Assert.True(await world.SettleAsync(() => world.BobApplicator.ExtrasApplied.Count > 0, [], sees),
            "les extras n'ont jamais été posés : " + world.Describe());

        Assert.Single(world.BobApplicator.Applied);
        Assert.True(world.BobApplicator.ExtrasApplied[0].Change.Honorific);
        Assert.False(world.BobApplicator.ExtrasApplied[0].Change.CustomizePlus);
    }

    [Fact]
    public async Task Une_pause_pendant_une_application_retire_ce_qui_se_posait()
    {
        // Relevé en relecture : l'application attend désormais que le personnage
        // soit chargé, jusqu'à dix secondes. Une pause dans cette fenêtre ne
        // retirait rien, et l'apparence restait jusqu'au déchargement du plugin.
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        world.BobApplicator.Hold = new TaskCompletionSource();

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'application n'a jamais commencé : " + world.Describe());

        world.BobBook.SetPaused(world.AliceId, true);

        for (var i = 0; i < 5 && world.BobApplicator.Removed.Count == 0; i++)
        {
            await world.TickAsync([], sees);
            await Task.Delay(10);
        }

        Assert.Equal(world.AliceId, Assert.Single(world.BobApplicator.Removed));
    }

    [Fact]
    public async Task Des_extras_non_poses_a_temps_sont_retentes_seuls()
    {
        // Relevé en relecture : un personnage qui met plus de dix secondes à se
        // charger n'avait pas ses extras, et le moteur les croyait posés.
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        world.AliceAppearance.Manifest = world.AliceAppearance.Manifest! with
        {
            Extras = CharacterExtras.None with { Heels = "{\"DefaultOffset\":0.1}" },
        };
        world.BobApplicator.ExtrasSucceed = false;

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        world.BobApplicator.ExtrasSucceed = true;

        Assert.True(await world.SettleAsync(() => world.BobApplicator.ExtrasApplied.Count > 0, [], sees),
            "les extras n'ont jamais été retentés : " + world.Describe());

        Assert.Single(world.BobApplicator.Applied);
        Assert.True(world.BobApplicator.ExtrasApplied[0].Change.Heels);
    }

    [Fact]
    public async Task Des_fichiers_changes_redemandent_une_application_complete()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees));

        world.AliceAppearance.Manifest = world.AliceAppearance.Manifest! with
        {
            MetaManipulations = Linkpearl.Core.Tests.Safety.Gzipped.Base64(1, "autre"),
            Extras = CharacterExtras.None with { Honorific = "{\"Title\":\"b\"}" },
        };

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 1, [], sees),
            "la nouvelle apparence n'a pas été reposée : " + world.Describe());
        Assert.Empty(world.BobApplicator.ExtrasApplied);
    }

    [Fact]
    public async Task Rien_n_est_pose_tant_que_le_jeu_n_est_pas_pret()
    {
        await using var world = await TwoEnginesAsync();

        world.BobApplicator.Ready = false;

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        var ready = await world.SettleAsync(() => world.BobStatus().View.Ready, [], sees);

        Assert.True(ready, "les blobs ne sont jamais arrivés");
        Assert.Empty(world.BobApplicator.Applied);

        world.BobApplicator.Ready = true;

        var applied = await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees);

        Assert.True(applied, "l'application n'a pas repris une fois le jeu prêt");
    }

    [Fact]
    public async Task Des_animations_bloquees_pour_un_pair_ne_sont_ni_demandees_ni_posees()
    {
        await using var world = await TwoEnginesAsync(withAnimation: true);

        world.BobBook.SetReceive(world.AliceId, TransientCategories.All with { Animations = false });

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        var first = Assert.Single(world.BobApplicator.Applied).Manifest;

        Assert.DoesNotContain(IdlePath, first.Replacements.SelectMany(r => r.GamePaths));
        Assert.True(world.BobStore.TryGetSize(world.Blob, out _));
        Assert.False(world.BobStore.TryGetSize(world.AnimationBlob!.Value, out _), "l'animation bloquée a été téléchargée");

        // Débloquer suffit : l'animation est demandée puis posée, sans rien
        // attendre d'Alice, qui n'a rien changé.
        world.BobBook.SetReceive(world.AliceId, TransientCategories.All);

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 1, [], sees),
            "le déblocage n'a pas reposé l'apparence : " + world.Describe());

        Assert.Contains(IdlePath, world.BobApplicator.Applied[^1].Manifest.Replacements.SelectMany(r => r.GamePaths));
        Assert.True(world.BobStore.TryGetSize(world.AnimationBlob!.Value, out _));
    }

    [Fact]
    public async Task Le_blocage_global_s_ajoute_a_celui_du_pair()
    {
        await using var world = await TwoEnginesAsync(withAnimation: true);

        world.Bob.SetGlobalReceive(TransientCategories.All with { Animations = false });

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        Assert.DoesNotContain(IdlePath, world.BobApplicator.Applied[^1].Manifest.Replacements.SelectMany(r => r.GamePaths));

        world.Bob.SetGlobalReceive(TransientCategories.All);

        Assert.True(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 1, [], sees),
            "le déblocage global n'a pas reposé l'apparence : " + world.Describe());

        Assert.Contains(IdlePath, world.BobApplicator.Applied[^1].Manifest.Replacements.SelectMany(r => r.GamePaths));
    }

    [Fact]
    public async Task Un_pair_retire_l_apprend_et_nous_retire_de_son_carnet()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        var ended = new List<PeerId>();
        var delivered = new List<PeerId>();
        world.Bob.PairEnded += pair => ended.Add(pair.Id);
        world.Alice.RevocationDelivered += pair => delivered.Add(pair.Id);

        world.AliceBook.Revoke(world.BobId);

        Assert.True(
            await world.SettleAsync(() => delivered.Count > 0, [], sees),
            "l'avis de retrait n'a jamais été remis : " + world.Describe());

        // Sans cela, Bob verrait encore Alice comme pairée, et sa ligne
        // resterait « hors ligne » pour toujours sans qu'il sache pourquoi.
        Assert.Equal(world.AliceId, Assert.Single(ended));
        Assert.Null(world.BobBook.Find(world.AliceId));
        Assert.Null(world.AliceBook.Find(world.BobId));
        Assert.Contains(world.AliceId, world.BobApplicator.Removed);
        Assert.Empty(world.Bob.Statuses);
        Assert.Empty(world.Alice.Statuses);
    }

    [Fact]
    public async Task Un_pair_absent_au_retrait_l_apprend_en_revenant_sans_rien_recevoir()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        var delivered = new List<PeerId>();
        world.Alice.RevocationDelivered += pair => delivered.Add(pair.Id);

        // Retiré avant toute rencontre : la session qui s'ouvre ensuite ne sert
        // qu'à le prévenir, jamais à lui envoyer une apparence.
        world.AliceBook.Revoke(world.BobId);

        Assert.True(
            await world.SettleAsync(() => delivered.Count > 0, [], sees),
            "l'avis de retrait n'a jamais été remis : " + world.Describe());

        Assert.Null(world.BobBook.Find(world.AliceId));
        Assert.Empty(world.BobApplicator.Applied);
    }

    /// <summary>Un moteur seul, qui n'a personne à joindre. Pour les tentatives.</summary>
    private SyncEngine Solitary(IPeerDialer dialer)
    {
        var identity = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(identity);

        var theirKey = NewPublicKey();
        var book = new PairBook(_clock);

        book.Load([Accepted(PeerId.Of(theirKey), theirKey)]);

        return new SyncEngine(
            book, dialer, new FixedAppearance(null, BobPrint), new RecordingApplicator(),
            Store("solitaire"), PeerId.Of(CryptoPrimitives.ExportPublicPoint(identity)), identity,
            _clock, new SilentLog());
    }

    private byte[] NewPublicKey()
    {
        var key = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(key);

        return CryptoPrimitives.ExportPublicPoint(key);
    }

    private static PairRecord Accepted(PeerId id, byte[] publicKey, PlayerFingerprint? pinned = null)
        => new()
        {
            Id = id,
            PublicKey = publicKey,
            PairSecret = new byte[32],
            DisplayName = "Pair",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            Trust = PairTrust.Accepted,
            PairedAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            PinnedFingerprint = pinned,
        };

    private FileSystemBlobStore Store(string name)
        => new(Path.Combine(_root, name), new CacheSettings(), _clock, _ => long.MaxValue);

    private static async Task<BlobHash> PutAsync(FileSystemBlobStore store, byte[] content)
    {
        var hash = BlobHash.OfContent(content);

        await using var writer = await store.BeginWriteAsync(hash, content.Length, default);
        await writer.WriteAsync(content, default);
        Assert.True((await writer.CommitAsync(default)).Accepted);

        return hash;
    }

    [Fact]
    public async Task Au_dela_du_quota_le_cache_evince_ce_qui_n_est_pas_a_l_ecran()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        var stale = await PutAsync(world.BobStore, Encoding.UTF8.GetBytes(new string('x', 200)));
        world.BobStore.SetQuota(world.BlobSize + 10);
        world.Clock.Advance(TimeSpan.FromMinutes(1));

        await world.TickAsync([], sees);

        Assert.False(world.BobStore.TryGetSize(stale, out _));
        Assert.True(world.BobStore.TryGetSize(world.Blob, out _));
    }

    [Fact]
    public async Task Le_cache_garde_toujours_notre_propre_apparence()
    {
        var store = Store("seul");
        var oursContent = Encoding.UTF8.GetBytes("notre tenue, en tout petit");
        var ours = await PutAsync(store, oursContent);
        var stale = await PutAsync(store, Encoding.UTF8.GetBytes(new string('y', 200)));

        var manifest = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [new FileReplacement(["chara/equipment/e0001/model/c0101e0001_top.mdl"], ours, oursContent.Length)],
            string.Empty, null);

        var identity = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(identity);

        await using var engine = new SyncEngine(
            new PairBook(_clock), new FailingDialer(peerWasAbsent: true), new FixedAppearance(manifest, AlicePrint),
            new RecordingApplicator(), store, PeerId.Of(CryptoPrimitives.ExportPublicPoint(identity)), identity,
            _clock, new SilentLog());

        store.SetQuota(oursContent.Length + 10);
        await engine.TickAsync([], default);

        Assert.True(store.TryGetSize(ours, out _));
        Assert.False(store.TryGetSize(stale, out _));
    }

    [Fact]
    public async Task L_eviction_epargne_aussi_le_dernier_manifeste_annonce()
    {
        var store = Store("annonce");

        var contentV1 = Encoding.UTF8.GetBytes(new string('v', 100));
        var hashV1 = await PutAsync(store, contentV1);
        var manifestV1 = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [new FileReplacement(["chara/equipment/e0001/model/c0101e0001_top.mdl"], hashV1, contentV1.Length)],
            string.Empty, null);

        _clock.Advance(TimeSpan.FromHours(1));
        var stale = await PutAsync(store, Encoding.UTF8.GetBytes(new string('s', 100)));

        // Le premier appel à CurrentAsync (AnnounceIfChangedAsync) rend
        // encore manifestV1 ; le second (EvictIfDueAsync), plus rien : c'est
        // ce qui peut arriver si une reconstruction locale change _current
        // entre les deux, au milieu du même tic.
        var appearance = new SwappingAppearance(manifestV1, null, AlicePrint);

        var identity = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(identity);

        await using var engine = new SyncEngine(
            new PairBook(_clock), new FailingDialer(peerWasAbsent: true), appearance,
            new RecordingApplicator(), store, PeerId.Of(CryptoPrimitives.ExportPublicPoint(identity)), identity,
            _clock, new SilentLog());

        store.SetQuota(contentV1.Length + 10);
        await engine.TickAsync([], default);

        // hashV1 est le plus ancien : sans l'épingle sur le dernier manifeste
        // annoncé, c'est lui qui partirait le premier.
        Assert.True(store.TryGetSize(hashV1, out _));
        Assert.False(store.TryGetSize(stale, out _));
    }

    [Fact]
    public async Task L_eviction_n_est_verifiee_qu_a_intervalle()
    {
        var store = Store("cadence");
        var identity = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(identity);

        await using var engine = new SyncEngine(
            new PairBook(_clock), new FailingDialer(peerWasAbsent: true), new FixedAppearance(null, AlicePrint),
            new RecordingApplicator(), store, PeerId.Of(CryptoPrimitives.ExportPublicPoint(identity)), identity,
            _clock, new SilentLog());

        await engine.TickAsync([], default);

        var stale = await PutAsync(store, Encoding.UTF8.GetBytes(new string('z', 200)));
        store.SetQuota(10);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await engine.TickAsync([], default);
        Assert.True(store.TryGetSize(stale, out _));

        _clock.Advance(TimeSpan.FromSeconds(30));
        await engine.TickAsync([], default);
        Assert.False(store.TryGetSize(stale, out _));
    }

    [Fact]
    public async Task DisposeAsync_attend_le_tic_en_cours_et_un_tic_apres_ne_fait_rien()
    {
        // Un démontage pendant un tic laisserait des mods temporaires pointer
        // sur un dossier supprimé : DisposeAsync doit attendre le tic en vol.
        var appearance = new BlockingAppearance(BobPrint);
        var identity = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(identity);

        var engine = new SyncEngine(
            new PairBook(_clock), new FailingDialer(peerWasAbsent: true), appearance,
            new RecordingApplicator(), Store("porte"), PeerId.Of(CryptoPrimitives.ExportPublicPoint(identity)), identity,
            _clock, new SilentLog());

        var tick = engine.TickAsync([], default);
        await Task.Delay(50);

        var dispose = engine.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted, "DisposeAsync n'a pas attendu le tic en cours.");

        // Entré pendant que Dispose attend le tic en vol : la vie est déjà
        // annulée, ce tic-là ne doit rien faire et revenir tout de suite.
        var afterDispose = engine.TickAsync([], default);
        await afterDispose.WaitAsync(TimeSpan.FromSeconds(2));

        appearance.Gate.SetResult();
        await tick;
        await dispose;
    }

    private const string IdlePath = "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap";

    private sealed class SwitchableBans : IServiceBans
    {
        public volatile bool Listed;

        public ServiceBanStatus Status(PlayerFingerprint player)
            => Listed
                ? new ServiceBanStatus(BanVerdict.Listed, new ServiceBan(new RendezvousAddress("rdv.exemple.ch", 47900), "triche"))
                : ServiceBanStatus.Clear;
    }

    [Fact]
    public async Task Une_paire_listee_perd_son_apparence()
    {
        var bans = new SwitchableBans();
        await using var world = await TwoEnginesAsync(bobBans: bans);

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        bans.Listed = true;

        // Listée par un service actif, même une paire directe perd son apparence.
        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Removed.Count > 0, [], sees),
            "l'apparence est restée");
    }

    [Fact]
    public async Task Rien_n_est_pose_sur_un_liste()
    {
        var bans = new SwitchableBans { Listed = true };
        await using var world = await TwoEnginesAsync(bobBans: bans);

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.False(await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees, rounds: 300));
    }

    [Fact]
    public async Task Un_personnage_liste_ne_recoit_plus_notre_apparence()
    {
        // Couper un seul sens laissait un banni collecter les mods de qui le
        // croisait : Alice, qui voit Bob listé, ne lui envoie plus rien.
        var bans = new SwitchableBans { Listed = true };
        await using var world = await TwoEnginesAsync(aliceBans: bans);

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        // La session tient et la présence passe : seul l'envoi est coupé.
        Assert.True(
            await world.SettleAsync(() => world.Bob.Statuses.SingleOrDefault()?.View.Fingerprint == AlicePrint, [], sees),
            "la session ne s'est jamais ouverte : " + world.Describe());

        Assert.False(await world.SettleAsync(() => world.BobStatus().View.Manifest is not null, [], sees, rounds: 200));
        Assert.Empty(world.BobApplicator.Applied);
    }

    [Fact]
    public async Task L_interface_lit_les_etats_pendant_que_le_tic_les_change()
    {
        // L'interface lit depuis le thread du jeu, le tic écrit depuis le pool.
        // Énumérer le dictionnaire des runtimes pendant qu'un pair entre ou
        // sort levait « collection modifiée » au milieu d'un dessin.
        await using var world = await TwoEnginesAsync();
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (stop.IsCancellationRequested is false)
                _ = world.Bob.Statuses.Count;
        });

        for (var i = 0; i < 100; i++)
        {
            // Pause et reprise : le runtime d'Alice sort du dictionnaire et y revient.
            world.BobBook.SetPaused(world.AliceId, i % 2 == 0);
            await world.TickAsync([], []);
        }

        await stop.CancelAsync();
        await reader;
    }

    [Fact]
    public async Task Sans_permission_d_envoi_notre_apparence_ne_part_pas()
    {
        await using var world = await TwoEnginesAsync();
        world.AliceBook.SetPermissions(world.BobId, PairPermissions.ReceiveAppearance);

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.Bob.Statuses.SingleOrDefault()?.View.Fingerprint == AlicePrint, [], sees),
            "la session ne s'est jamais ouverte : " + world.Describe());

        Assert.False(await world.SettleAsync(() => world.BobStatus().View.Manifest is not null, [], sees, rounds: 200));
        Assert.Empty(world.BobApplicator.Applied);
    }

    private async Task<TwoEngines> TwoEnginesAsync(PlayerFingerprint? pinOnBob = null, bool withAnimation = false, IServiceBans? bobBans = null, IServiceBans? aliceBans = null)
    {
        var alice = CryptoPrimitives.GenerateIdentity();
        var bob = CryptoPrimitives.GenerateIdentity();
        _disposables.Add(alice);
        _disposables.Add(bob);

        var aliceKey = CryptoPrimitives.ExportPublicPoint(alice);
        var bobKey = CryptoPrimitives.ExportPublicPoint(bob);
        var aliceId = PeerId.Of(aliceKey);
        var bobId = PeerId.Of(bobKey);

        var aliceStore = Store("alice");
        var bobStore = Store("bob");

        var content = Encoding.UTF8.GetBytes("un modèle de tenue, en tout petit");
        var hash = BlobHash.OfContent(content);

        await using (var writer = await aliceStore.BeginWriteAsync(hash, content.Length, default))
        {
            await writer.WriteAsync(content, default);
            await writer.CommitAsync(default);
        }

        List<FileReplacement> replacements =
            [new FileReplacement(["chara/equipment/e0001/model/c0101e0001_top.mdl"], hash, content.Length)];

        BlobHash? animation = null;

        if (withAnimation)
        {
            var idle = Encoding.UTF8.GetBytes("une idle assise, en tout petit");
            animation = BlobHash.OfContent(idle);

            await using (var writer = await aliceStore.BeginWriteAsync(animation.Value, idle.Length, default))
            {
                await writer.WriteAsync(idle, default);
                await writer.CommitAsync(default);
            }

            replacements.Add(new FileReplacement([IdlePath], animation.Value, idle.Length));
        }

        var manifest = new CharacterManifest(CharacterManifest.CurrentVersion, replacements, string.Empty, null);

        var aliceBook = new PairBook(_clock);
        aliceBook.Load([Accepted(bobId, bobKey)]);

        var bobBook = new PairBook(_clock);
        bobBook.Load([Accepted(aliceId, aliceKey, pinOnBob)]);

        var point = new MeetingPoint();
        var bobApplicator = new RecordingApplicator();
        var aliceLog = new SilentLog();
        var bobLog = new SilentLog();

        var aliceAppearance = new FixedAppearance(manifest, AlicePrint);

        var aliceEngine = new SyncEngine(
            aliceBook, new MeetingDialer(point), aliceAppearance,
            new RecordingApplicator(), aliceStore, aliceId, alice, _clock, aliceLog, bans: aliceBans);

        var bobEngine = new SyncEngine(
            bobBook, new MeetingDialer(point), new FixedAppearance(null, BobPrint), bobApplicator,
            bobStore, bobId, bob, _clock, bobLog, bans: bobBans);

        return new TwoEngines(
            aliceEngine, bobEngine, bobBook, bobApplicator, bobStore, aliceId, hash, content.Length,
            aliceLog, bobLog, _clock, aliceBook, bobId, aliceAppearance, animation);
    }

    private sealed record TwoEngines(
        SyncEngine Alice, SyncEngine Bob, PairBook BobBook, RecordingApplicator BobApplicator,
        FileSystemBlobStore BobStore, PeerId AliceId, BlobHash Blob, long BlobSize,
        SilentLog AliceLog, SilentLog BobLog, MovableClock Clock, PairBook AliceBook, PeerId BobId,
        FixedAppearance AliceAppearance, BlobHash? AnimationBlob)
        : IAsyncDisposable
    {
        public PeerStatus BobStatus() => Bob.Statuses.Single();

        /// <summary>De quoi diagnostiquer un test qui n'aboutit pas.</summary>
        public string Describe()
            => $"chez Bob {string.Join(" | ", Bob.Statuses)}, chez Alice {string.Join(" | ", Alice.Statuses)}, "
             + $"avertissements Bob [{string.Join(" ; ", BobLog.Warnings)}], "
             + $"avertissements Alice [{string.Join(" ; ", AliceLog.Warnings)}]";

        public async Task TickAsync(IReadOnlyList<VisiblePlayer> aliceSees, IReadOnlyList<VisiblePlayer> bobSees)
        {
            // Le temps avance, sinon rien ne se transfère : le limiteur de débit
            // remplit son seau à jetons depuis l'horloge injectée, et une horloge
            // figée ne lui accorde jamais un octet.
            Clock.Advance(TimeSpan.FromMilliseconds(50));

            await Alice.TickAsync(aliceSees, default);
            await Bob.TickAsync(bobSees, default);
        }

        /// <summary>Fait tourner les deux moteurs jusqu'à ce que la condition tienne.</summary>
        public async Task<bool> SettleAsync(
            Func<bool> done, IReadOnlyList<VisiblePlayer> aliceSees, IReadOnlyList<VisiblePlayer> bobSees, int rounds = 2000)
        {
            for (var i = 0; i < rounds; i++)
            {
                await TickAsync(aliceSees, bobSees);

                if (done())
                    return true;

                await Task.Delay(10);
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            await Alice.DisposeAsync();
            await Bob.DisposeAsync();
        }
    }
}
