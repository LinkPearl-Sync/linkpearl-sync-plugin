using System.Collections.Concurrent;
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport;

namespace Linkpearl.Core.Sync;

/// <summary>Ce qui sait joindre un pair.</summary>
/// <remarks>
/// Abstrait pour que le moteur se teste sans réseau ni rendez-vous.
/// <see cref="PeerConnector"/> en est la seule implémentation réelle.
/// </remarks>
public interface IPeerDialer
{
    Task<ConnectionAttempt> ConnectAsync(PairRecord pair, CancellationToken ct);
}

/// <summary>Réglages du moteur.</summary>
public sealed record SyncEngineSettings
{
    public static SyncEngineSettings Default { get; } = new();

    /// <summary>Attente avant la première reprise après un échec.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Plafond de l'attente entre deux tentatives.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Attente quand le pair n'est simplement pas en ligne.
    /// </summary>
    /// <remarks>
    /// Fixe, et non doublée à chaque fois : l'absence n'est pas une panne. Un
    /// ami hors ligne toute la journée serait sinon réessayé une fois par heure
    /// au moment où il se connecte enfin.
    /// </remarks>
    public TimeSpan AbsentBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Durée au-delà de laquelle une session qui tombe n'est pas un échec.
    /// </summary>
    /// <remarks>
    /// Une session qui a tenu et qui se ferme, c'est un pair qui nous met en
    /// pause, se déconnecte ou recharge son plugin : on se réannonce aussitôt,
    /// pour qu'il nous retrouve dès son retour. Une session qui tombe à peine
    /// établie ressemble à un lien qui clignote, et reprend le délai croissant.
    /// </remarks>
    public TimeSpan StableSession { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Faux pour débrayer le limiteur d'envoi, voir <see cref="RateLimiter.Bypassed"/>.</summary>
    public bool LimitUpload { get; init; } = true;

    /// <summary>
    /// Tronçons servis de front, un par canal.
    /// </summary>
    /// <remarks>
    /// Mesuré par le faux pair sur une apparence réelle de 405 Mo, avec des
    /// tronçons de 4 Mio, des accusés à la milliseconde et un relais qui
    /// retarde les paquets :
    ///
    /// | canaux | 0 ms      | 20 ms     | 60 ms     |
    /// |--------|-----------|-----------|-----------|
    /// | 8      | 57,5 Mo/s | 14,0 Mo/s |           |
    /// | 16     |           | 21,0 Mo/s | 9,6 Mo/s  |
    /// | 32     | 53,8 Mo/s | 33,0 Mo/s | 17,5 Mo/s |
    /// | 48     |           | 37,0 Mo/s | 3,6 Mo/s  |
    ///
    /// Entre deux foyers, c'est la fenêtre fiable de chaque canal qui décide,
    /// et le débit suit le nombre de canaux. Jusqu'à trente-deux, et pas
    /// au-delà : à quarante-huit, un passage à 60 ms s'est effondré.
    ///
    /// Les anciennes mesures, qui plafonnaient vers seize canaux et
    /// s'effondraient à vingt-quatre, venaient d'un /tmp saturé par le banc
    /// lui-même, pas du transport.
    /// </remarks>
    public int DataChannels { get; init; } = 32;

    public int BlockSize { get; init; } = 16 * 1024;

    public RateLimiterSettings Limiter { get; init; } = new();

    /// <summary>Les animations, VFX et sons acceptés de tous, avant le réglage de chaque pair.</summary>
    public TransientCategories Receive { get; init; } = TransientCategories.All;

    /// <summary>
    /// Attente d'un pair prévenu de son retrait, avant de réessayer plus tard.
    /// </summary>
    /// <remarks>
    /// Un pair à jour raccroche aussitôt l'avis reçu. Celui qui reste en ligne
    /// sans rien dire a un client qui ignore l'avis : on referme, et l'attente
    /// croissante espace les tentatives jusqu'à sa mise à jour.
    /// </remarks>
    public TimeSpan RevocationPatience { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Intervalle entre deux vérifications du quota.
    /// </summary>
    /// <remarks>
    /// Vérifier le quota, c'est sommer la taille de chaque blob : des dizaines de
    /// milliers à chaque seconde, pour un dépassement qui se rattrape très bien
    /// trente secondes plus tard.
    /// </remarks>
    public TimeSpan EvictionInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Par où passe une session, et à quelle latence.</summary>
public readonly record struct PeerRoute(bool Relayed, int RoundTripMs);

/// <summary>L'état d'un pair, tel que l'interface l'affiche.</summary>
/// <param name="Route">Absent tant qu'aucune session n'est ouverte.</param>
/// <param name="Phase">Ce que la liste des pairs doit en dire ; calculée par le moteur, voir <see cref="PeerPhases"/>.</param>
public sealed record PeerStatus(
    PeerId Peer,
    string DisplayName,
    PeerSessionState State,
    PeerView View,
    bool Applied,
    bool FingerprintDisputed,
    string? LastFailure,
    DateTimeOffset? NextAttempt,
    PeerRoute? Route = null,
    GroupId? Group = null,
    PeerPhase Phase = PeerPhase.Searching);

/// <summary>
/// Le moteur : il fait vivre une session par pair et décide quoi poser à l'écran.
/// </summary>
/// <remarks>
/// Tout passe par <see cref="TickAsync"/>, appelé à cadence régulière par
/// l'hôte. Rien ici ne dort ni ne boucle de son côté : c'est ce qui rend le
/// moteur observable pas à pas dans un test, avec une horloge que l'on avance à
/// la main.
///
/// Ce qui est long ne se fait jamais dans le tic. Joindre un pair prend une
/// quinzaine de secondes, servir une apparence en prend des centaines, et un
/// redessin passe par le jeu : ces trois-là tournent sur leurs propres tâches,
/// et le tic ne fait qu'en ramasser le résultat. Un pair lent n'empêche donc
/// jamais un autre d'avancer.
/// </remarks>
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly PairBook _book;
    private readonly IPeerDialer _dialer;
    private readonly ILocalAppearance _local;
    private readonly IRemoteApplicator _applicator;
    private readonly IBlobStore _store;
    private readonly PeerId _ourId;
    private readonly ECDsa _identity;
    private readonly IClock _clock;
    private readonly ILogSink _log;
    private readonly SyncEngineSettings _settings;

    /// <summary>Réglable à chaud : relu par chaque session au tic suivant.</summary>
    private volatile bool _uploadLimited;
    private readonly Quotas _quotas;

    /// <summary>Réglable à chaud depuis l'interface, lu par le tic et par les sessions.</summary>
    private readonly Lock _receiveGate = new();
    private TransientCategories _globalReceive;

    private readonly IGroupGate? _groups;
    private readonly IGroupPolicies? _policies;

    /// <summary>Les listes des services : rien ne se pose sur un personnage listé.</summary>
    private readonly IServiceBans? _bans;

    /// <summary>
    /// Les pairs de groupe voulus, remplacés d'un bloc par le fil de rafraîchissement.
    /// </summary>
    /// <remarks>
    /// Une liste immuable échangée par référence : le tic la lit une fois et
    /// travaille sur sa copie, sans verrou et sans voir une liste à moitié écrite.
    /// </remarks>
    private volatile IReadOnlyList<PairRecord> _groupPeers = [];

    private readonly Dictionary<PeerId, Runtime> _runtimes = [];
    private readonly CancellationTokenSource _life = new();

    /// <summary>Les demandes de réapplication, servies au prochain tic.</summary>
    /// <remarks>
    /// Une file plutôt qu'un accès direct : l'interface et le menu du jeu
    /// appellent depuis un autre fil que celui du moteur, et les structures du
    /// moteur ne sont pas faites pour deux fils.
    /// </remarks>
    private readonly ConcurrentQueue<(PeerId? Id, PlayerFingerprint? Fingerprint)> _reapply = new();

    private CharacterManifest? _announcedManifest;
    private PlayerFingerprint? _announcedFingerprint;

    /// <summary>
    /// Un jeton plutôt qu'un booléen : DisposeAsync doit pouvoir attendre le
    /// tic en cours avant de démonter les runtimes, sans quoi un démontage
    /// pendant un tic laisserait des mods temporaires pointer sur un dossier
    /// supprimé. Jamais relâché après cette attente : plus aucun tic n'entre.
    /// </summary>
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private DateTimeOffset _lastEvictionCheck = DateTimeOffset.MinValue;

    public SyncEngine(
        PairBook book, IPeerDialer dialer, ILocalAppearance local, IRemoteApplicator applicator,
        IBlobStore store, PeerId ourId, ECDsa identity, IClock clock, ILogSink log,
        SyncEngineSettings? settings = null, Quotas? quotas = null, IGroupGate? groups = null, IGroupPolicies? policies = null,
        IServiceBans? bans = null)
    {
        _bans = bans;
        _book = book;
        _dialer = dialer;
        _local = local;
        _applicator = applicator;
        _store = store;
        _ourId = ourId;
        _identity = identity;
        _clock = clock;
        _log = log;
        _settings = settings ?? SyncEngineSettings.Default;
        _globalReceive = _settings.Receive;
        _uploadLimited = _settings.LimitUpload;
        _quotas = quotas ?? Quotas.Default;
        _groups = groups;
        _policies = policies;
    }

    /// <summary>
    /// Le pair nous a retirés de son carnet, et vient d'être retiré du nôtre.
    /// </summary>
    /// <remarks>
    /// Levé depuis le tic, sur le fil du moteur. L'hôte enregistre le carnet
    /// et le dit à l'utilisateur, qui sinon verrait une ligne disparaître.
    /// </remarks>
    public event Action<PairRecord>? PairEnded;

    /// <summary>Un pair retiré a reçu l'avis, et son entrée a quitté le carnet.</summary>
    public event Action<PairRecord>? RevocationDelivered;

    /// <summary>Le moteur a changé le carnet de lui-même : l'hôte l'enregistre.</summary>
    /// <remarks>Levé depuis le tic, sur le fil du moteur, comme <see cref="PairEnded"/>.</remarks>
    public event Action? BookChanged;

    public IReadOnlyList<PeerStatus> Statuses =>
        _runtimes.Where(entry => entry.Value.Revoked is false).Select(entry => new PeerStatus(
            entry.Key,
            entry.Value.Pair.DisplayName,
            entry.Value.Session?.State ?? PeerSessionState.Disconnected,
            entry.Value.Exchange?.View ?? EmptyView,
            entry.Value.AppliedOn is not null,
            entry.Value.Disputed,
            entry.Value.LastFailure,
            entry.Value.Session is null ? entry.Value.NextAttempt : null,
            entry.Value.Session?.Link is { } link ? new PeerRoute(link.IsRelayed, link.RoundTripMs) : null,
            entry.Value.Pair.Group?.Group,
            PeerPhases.Of(
                entry.Value.Dial is not null, entry.Value.Session is not null, _clock.UtcNow,
                entry.Value.NextAttempt, entry.Value.LastFailure, entry.Value.WasAbsent, entry.Value.Pair.PausedByPeer,
                entry.Value.Exchange?.View ?? EmptyView, entry.Value.AppliedOn is not null)))
        .ToList();

    private static PeerView EmptyView { get; } = new(null, null, null, 0, 0, false);

    /// <summary>
    /// Un pas du moteur.
    /// </summary>
    /// <param name="visible">Les joueurs actuellement dans notre champ.</param>
    /// <param name="ct">Annulation de ce pas, distincte de la vie du moteur.</param>
    /// <remarks>
    /// Les appels ne se chevauchent pas : un tic qui arrive pendant qu'un autre
    /// tourne est abandonné plutôt que mis en file. Deux passes simultanées
    /// pourraient lancer deux fois la même application, et une cadence en retard
    /// se rattrape toute seule au tic suivant.
    /// </remarks>
    public async Task TickAsync(IReadOnlyList<VisiblePlayer> visible, CancellationToken ct)
    {
        if (_life.IsCancellationRequested || _tickGate.Wait(0) is false)
            return;

        try
        {
            await ReconcileBookAsync(ct).ConfigureAwait(false);
            FollowPauses();
            await FollowEndingsAsync(ct).ConfigureAwait(false);
            await GiveUpUnansweredNoticesAsync(ct).ConfigureAwait(false);
            await ServeReapplyAsync(ct).ConfigureAwait(false);
            await FollowReceiveChangesAsync(ct).ConfigureAwait(false);
            await AdoptFinishedDialsAsync(ct).ConfigureAwait(false);
            await ExchangePoliciesAsync(ct).ConfigureAwait(false);
            await DropDeadSessionsAsync().ConfigureAwait(false);
            StartDueDials();
            ObserveLinks();
            await AnnounceIfChangedAsync(ct).ConfigureAwait(false);
            ReconcileVisibility(visible);
            await EvictIfDueAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>
    /// Ramène le cache sous son quota, sans toucher à ce qui sert.
    /// </summary>
    /// <remarks>
    /// Dans le tic, et non ailleurs : c'est le seul fil qui peut lire les
    /// runtimes sans course. Notre apparence est demandée à la source plutôt
    /// qu'au dernier manifeste annoncé, qui n'existe pas tant qu'aucun pair
    /// n'est joint.
    /// </remarks>
    private async Task EvictIfDueAsync(CancellationToken ct)
    {
        if (_clock.UtcNow - _lastEvictionCheck < _settings.EvictionInterval)
            return;

        _lastEvictionCheck = _clock.UtcNow;

        if (_store.NeedsEviction is false)
            return;

        var ours = await _local.CurrentAsync(ct).ConfigureAwait(false);

        // Notre apparence peut changer entre cet appel et celui qui a
        // annoncé _announcedManifest : un pair peut encore être en train
        // d'en télécharger les blobs, épinglés ici pour ne pas les lui
        // couper sous le pied.
        var pinned = PinnedBlobs.Of(
            _runtimes.Values
                .SelectMany(runtime => new[] { runtime.AppliedValue, runtime.Exchange?.View.Manifest })
                .Append(ours)
                .Append(_announcedManifest));

        var before = _store.TotalBytes;
        await _store.EvictToAsync(_store.EvictionTarget, pinned, ct).ConfigureAwait(false);

        var freed = before - _store.TotalBytes;

        if (freed > 0)
            _log.Info($"cache au-delà du quota : {freed / (1024 * 1024)} Mo libérés.");
    }

    /// <summary>Aligne les runtimes sur le carnet : un pair actif, un runtime.</summary>
    /// <summary>Redemande et repose l'apparence de ce pair.</summary>
    public void Reapply(PeerId id) => _reapply.Enqueue((id, null));

    /// <summary>Embraye ou débraye le limiteur d'envoi, sessions ouvertes comprises.</summary>
    public void SetUploadLimited(bool limited) => _uploadLimited = limited;

    /// <summary>Les membres de groupe à joindre, tels que le planificateur les voit.</summary>
    public void SetGroupPeers(IReadOnlyList<PairRecord> peers) => _groupPeers = peers;

    /// <summary>
    /// Vrai si un avis de retrait de ce pair doit le retirer.
    /// </summary>
    /// <remarks>
    /// Un pair de groupe n'est pas dans notre carnet : son avis ne peut viser
    /// qu'une paire qu'il croit avoir avec nous, et qui n'existe pas ici. Le
    /// suivre dirait « a mis fin au pairage » à propos d'un groupe.
    /// </remarks>
    internal static bool EndsOnUnpair(PairRecord pair) => pair.Group is null;

    /// <summary>
    /// Vrai si un message de groupe reçu sur cette session doit être traité.
    /// </summary>
    /// <remarks>
    /// Une paire directe n'a pas de politique de groupe à échanger : un message
    /// de ce genre venu de là serait un pair hostile ou bogué qui prétend
    /// parler pour un groupe auquel elle n'appartient pas.
    /// </remarks>
    internal static bool CarriesGroupMessages(PairRecord pair) => pair.Group is not null;

    /// <summary>Change ce qu'on accepte de tous ; les apparences posées suivent au tic suivant.</summary>
    public void SetGlobalReceive(TransientCategories receive)
    {
        lock (_receiveGate)
            _globalReceive = receive;
    }

    private TransientCategories GlobalReceive
    {
        get
        {
            lock (_receiveGate)
                return _globalReceive;
        }
    }

    private TransientCategories EffectiveReceive(Runtime runtime) => GlobalReceive.And(runtime.Pair.Receive);

    /// <summary>Même chose, pour le personnage visible qui porte cette empreinte.</summary>
    public void Reapply(PlayerFingerprint fingerprint) => _reapply.Enqueue((null, fingerprint));

    private async Task ServeReapplyAsync(CancellationToken ct)
    {
        while (_reapply.TryDequeue(out var request))
        {
            var runtime = _runtimes.Values.FirstOrDefault(candidate =>
                request.Id is { } id ? candidate.Pair.Id == id
                                     : candidate.Exchange?.View.Fingerprint == request.Fingerprint
                                       || candidate.Pair.PinnedFingerprint == request.Fingerprint);

            if (runtime is null)
                continue;

            // Oublier ce qu'on a posé suffit à reposer au prochain passage. Et on
            // redemande le manifeste, pour que ce soit bien le dernier.
            runtime.AppliedManifest = null;
            runtime.AppliedValue = null;
            runtime.AppliedOn = null;

            if (runtime.Exchange is { } exchange && runtime.Session is not null)
            {
                try
                {
                    await exchange.RefreshAsync(ct).ConfigureAwait(false);
                    _log.Info($"{runtime.Pair.DisplayName} : réapplication demandée.");
                }
                catch (Exception e)
                {
                    _log.Warning($"{runtime.Pair.DisplayName} : redemande du manifeste en échec.", e);
                }
            }
        }
    }

    /// <summary>Redemande le manifeste d'un pair dont le réglage de réception a changé.</summary>
    /// <remarks>
    /// Le manifeste revient filtré selon le nouveau réglage. S'il diffère de ce
    /// qui est posé, il est reposé dès que ses blobs sont là, comme n'importe
    /// quel changement d'apparence ; sinon rien ne bouge. Ce qui est posé reste
    /// en place d'ici là : l'effacer ferait clignoter le pair pour rien.
    /// </remarks>
    private async Task FollowReceiveChangesAsync(CancellationToken ct)
    {
        foreach (var runtime in _runtimes.Values)
        {
            var effective = EffectiveReceive(runtime);

            if (effective == runtime.Receive)
                continue;

            runtime.Receive = effective;

            if (runtime.Exchange is not { } exchange || runtime.Session is null)
                continue;

            try
            {
                await exchange.RefreshAsync(ct).ConfigureAwait(false);
                _log.Info($"{runtime.Pair.DisplayName} : réception changée ({effective}), manifeste redemandé.");
            }
            catch (Exception e)
            {
                _log.Warning($"{runtime.Pair.DisplayName} : redemande du manifeste en échec.", e);
            }
        }
    }

    private async Task ReconcileBookAsync(CancellationToken ct)
    {
        // Un pair retiré se joint encore, le temps de le lui dire. Les membres
        // de groupe viennent en plus, sans jamais masquer une entrée du carnet.
        var active = _book.Active.Concat(_book.Revoked).ToDictionary(pair => pair.Id);

        foreach (var member in _groupPeers)
            active.TryAdd(member.Id, member);

        foreach (var (id, runtime) in _runtimes.ToList())
        {
            if (active.TryGetValue(id, out var pair) && pair.Trust == runtime.Pair.Trust)
            {
                runtime.Pair = pair;
                continue;
            }

            // Mis en pause, bloqué, supprimé ou retiré : on débranche et on
            // efface ce qu'on avait posé. Laisser l'apparence en place ferait
            // de la mise en pause un bouton sans effet visible. Un pair retiré
            // revient au tour suivant, sous une session qui ne sert qu'à le
            // prévenir : celle-ci portait une apparence, qui doit cesser.
            // Mis en pause pendant une session : le dire avant de raccrocher,
            // sans quoi l'autre nous croit parti du jeu. Pas un blocage, qui ne
            // doit rien révéler, et jamais en cherchant le pair exprès.
            if (runtime.Session is { } open && _book.Find(id) is { Paused: true, Trust: PairTrust.Accepted })
                await SendPauseAsync(runtime, open, ct).ConfigureAwait(false);

            _log.Info($"{runtime.Pair.DisplayName} : pair retiré des actifs, session fermée.");
            await TearDownAsync(id, runtime, ct).ConfigureAwait(false);
            _runtimes.Remove(id);
        }

        foreach (var (id, pair) in active)
        {
            if (_runtimes.ContainsKey(id))
                continue;

            // Joignable tout de suite : une reprise de plugin ne doit pas coûter
            // une attente à l'utilisateur.
            _runtimes[id] = new Runtime { Pair = pair, NextAttempt = _clock.UtcNow };
        }
    }

    /// <summary>Reporte au carnet les avis de pause reçus depuis le tic précédent.</summary>
    private void FollowPauses()
    {
        foreach (var (id, runtime) in _runtimes)
        {
            if (runtime.PausedByPeerNotice is false)
                continue;

            runtime.PausedByPeerNotice = false;
            _book.SetPausedByPeer(id, true);
            runtime.Pair = _book.Find(id) ?? runtime.Pair;

            _log.Info($"{runtime.Pair.DisplayName} : nous a mis en pause.");
            BookChanged?.Invoke();
        }
    }

    /// <summary>
    /// Tire les conséquences d'un avis de retrait reçu.
    /// </summary>
    /// <remarks>
    /// L'avis vient d'une session chiffrée, liée à la clé du carnet : seul ce
    /// pair a pu l'envoyer, et le rendez-vous n'y peut rien. Le retrait est
    /// donc appliqué sans demander, comme l'autre l'a décidé sans nous.
    ///
    /// Reçu d'un pair que nous avions nous-mêmes retiré, c'est que les deux ont
    /// rompu en même temps : il n'y a plus personne à prévenir.
    /// </remarks>
    private async Task FollowEndingsAsync(CancellationToken ct)
    {
        foreach (var (id, runtime) in _runtimes.ToList())
        {
            if (runtime.EndedByPeer is false)
                continue;

            await TearDownAsync(id, runtime, ct).ConfigureAwait(false);
            _runtimes.Remove(id);
            _book.Remove(id);

            if (runtime.Revoked)
            {
                RevocationDelivered?.Invoke(runtime.Pair);
                continue;
            }

            _log.Info($"{runtime.Pair.DisplayName} : pairage rompu par le pair.");
            PairEnded?.Invoke(runtime.Pair);
        }
    }

    /// <summary>Referme la session d'un pair prévenu qui ne raccroche pas.</summary>
    private async Task GiveUpUnansweredNoticesAsync(CancellationToken ct)
    {
        foreach (var (id, runtime) in _runtimes)
        {
            if (runtime is not { Revoked: true, Session: not null, NoticeSentAt: { } sent })
                continue;

            if (_clock.UtcNow - sent < _settings.RevocationPatience)
                continue;

            _log.Info($"{runtime.Pair.DisplayName} : avis de retrait resté sans réponse.");
            await TearDownAsync(id, runtime, ct).ConfigureAwait(false);
            Retry(runtime, peerWasAbsent: false, "avis de retrait sans réponse");
        }
    }

    private async Task AdoptFinishedDialsAsync(CancellationToken ct)
    {
        foreach (var (id, runtime) in _runtimes)
        {
            if (runtime.Dial is not { IsCompleted: true } dial)
                continue;

            runtime.Dial = null;

            DialResult result;

            try
            {
                result = await dial.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception e)
            {
                _log.Warning($"{runtime.Pair.DisplayName} : tentative en échec.", e);
                result = new DialResult(null, false, e.Message);
            }

            if (result.Session is { } session)
                await AdoptSessionAsync(id, runtime, session, ct).ConfigureAwait(false);
            else
                Retry(runtime, result.PeerWasAbsent, result.Failure);
        }
    }

    private async Task AdoptSessionAsync(PeerId id, Runtime runtime, PeerSession session, CancellationToken ct)
    {
        if (runtime.Revoked)
        {
            await NotifyRevokedAsync(runtime, session, ct).ConfigureAwait(false);
            return;
        }

        var limiter = new RateLimiter(_clock, _settings.Limiter);

        var exchange = new PeerExchange(
            session, _store, _local, limiter, _settings.DataChannels, _settings.BlockSize, _quotas, _log,
            () => runtime.Receive);

        runtime.Receive = EffectiveReceive(runtime);

        runtime.Session = session;
        runtime.SessionSince = _clock.UtcNow;
        runtime.Exchange = exchange;
        runtime.Limiter = limiter;
        limiter.Bypassed = _uploadLimited is false;
        runtime.Failures = 0;
        runtime.LastFailure = null;
        runtime.WasAbsent = false;
        runtime.Life = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);

        // Deux tâches et non une : le service des blobs d'un pair dure des
        // minutes, et s'il tenait la même boucle que la réception, les trames
        // qu'il nous envoie pendant ce temps s'entasseraient en mémoire dans le
        // processus du jeu. Huit cents mégaoctets par personne, dans les deux
        // sens en même temps.
        runtime.Pump = PumpAsync(runtime, exchange, session, runtime.Life.Token);
        runtime.Serve = exchange.ServeAsync(runtime.Life.Token);

        _book.Seen(id);

        // Une session rouverte, c'est la reprise vue d'ici : la pause qu'il
        // nous avait dite est finie.
        if (runtime.Pair.PausedByPeer)
        {
            _book.SetPausedByPeer(id, false);
            runtime.Pair = _book.Find(id) ?? runtime.Pair;
            BookChanged?.Invoke();
        }

        try
        {
            await exchange.HelloAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Warning($"{runtime.Pair.DisplayName} : présence non annoncée.", e);
        }
    }

    /// <summary>
    /// Échange les politiques de groupe sur chaque session de groupe.
    /// </summary>
    /// <remarks>
    /// Chaque côté envoie la sienne à l'ouverture, garde la plus récente des
    /// deux, et renvoie la sienne s'il reçoit plus ancien : deux membres qui se
    /// croisent repartent avec la même.
    /// </remarks>
    private async Task ExchangePoliciesAsync(CancellationToken ct)
    {
        if (_policies is null)
            return;

        foreach (var runtime in _runtimes.Values)
        {
            if (runtime is not { Pair.Group: { } origin, Session: { } session })
                continue;

            while (runtime.InboundPolicies.TryDequeue(out var payload))
            {
                if (payload.Length <= GroupId.SizeInBytes
                    || GroupId.FromBytes(payload.AsSpan(0, GroupId.SizeInBytes)) != origin.Group)
                    continue;

                if (_policies.OfferPolicy(origin.Group, payload.AsSpan(GroupId.SizeInBytes)) is PolicyOffer.Stale)
                    runtime.SentPolicy = null;
            }

            if (_policies.CurrentPolicy(origin.Group) is not { } current
                || runtime.SentPolicy is { } sent && sent.AsSpan().SequenceEqual(current))
                continue;

            try
            {
                byte[] message = [.. origin.Group.ToBytes(), .. current];
                await session.SendAsync(ChannelPlan.ControlChannel, MessageKind.GroupPolicy, message, ct).ConfigureAwait(false);
                runtime.SentPolicy = current;
            }
            catch (Exception e)
            {
                _log.Warning($"{runtime.Pair.DisplayName} : politique de groupe non envoyée.", e);
            }
        }
    }

    /// <summary>
    /// Ouvre une session qui ne sert qu'à dire au pair qu'on l'a retiré.
    /// </summary>
    /// <remarks>
    /// Ni présence ni apparence : il n'a plus à nous voir. Ce qu'il nous
    /// envoie est lu et jeté, sauf son propre avis s'il nous retire aussi.
    /// </remarks>
    private async Task NotifyRevokedAsync(Runtime runtime, PeerSession session, CancellationToken ct)
    {
        runtime.Session = session;
        runtime.SessionSince = _clock.UtcNow;
        runtime.Failures = 0;
        runtime.LastFailure = null;
        runtime.Life = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        runtime.Pump = PumpAsync(runtime, null, session, runtime.Life.Token);

        try
        {
            await session.SendAsync(ChannelPlan.ControlChannel, MessageKind.Unpair, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);

            runtime.NoticeSentAt = _clock.UtcNow;
            _log.Info($"{runtime.Pair.DisplayName} : avis de retrait envoyé.");
        }
        catch (Exception e)
        {
            _log.Warning($"{runtime.Pair.DisplayName} : avis de retrait non envoyé.", e);
        }
    }

    /// <summary>
    /// Dit au pair qu'on le met en pause, puis laisse partir la trame.
    /// </summary>
    /// <remarks>
    /// La fermeture suit aussitôt, et LiteNetLib peut jeter une trame fiable
    /// encore en file quand la connexion tombe : on attend que le canal de
    /// contrôle se vide, une seconde au plus. Au mieux : perdu, l'avis laisse
    /// l'autre nous voir absent, comme avant qu'il existe.
    /// </remarks>
    private async Task SendPauseAsync(Runtime runtime, PeerSession session, CancellationToken ct)
    {
        try
        {
            await session.SendAsync(ChannelPlan.ControlChannel, MessageKind.Pause, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);

            for (var waited = 0; waited < 20 && session.Link.PendingOn(ChannelPlan.ControlChannel) > 0; waited++)
                await Task.Delay(50, ct).ConfigureAwait(false);

            _log.Info($"{runtime.Pair.DisplayName} : avis de pause envoyé.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning($"{runtime.Pair.DisplayName} : avis de pause non envoyé.", e);
        }
    }

    private async Task DropDeadSessionsAsync()
    {
        foreach (var (id, runtime) in _runtimes.ToList())
        {
            if (runtime.Session is not { State: PeerSessionState.Disconnected })
                continue;

            if (runtime is { Revoked: true, NoticeSentAt: not null })
            {
                // Il a raccroché après l'avis : c'est sa façon d'en accuser
                // réception, et l'entrée gardée pour lui n'a plus d'objet.
                await TearDownAsync(id, runtime, CancellationToken.None).ConfigureAwait(false);
                _runtimes.Remove(id);
                _book.Remove(id);

                _log.Info($"{runtime.Pair.DisplayName} : avis de retrait remis.");
                RevocationDelivered?.Invoke(runtime.Pair);
                continue;
            }

            _log.Info($"{runtime.Pair.DisplayName} : session tombée.");

            var stable = _clock.UtcNow - runtime.SessionSince >= _settings.StableSession;

            await TearDownAsync(id, runtime, CancellationToken.None).ConfigureAwait(false);

            if (stable)
            {
                // Mesuré en jeu : à cinq secondes d'attente ici, une pause suivie
                // d'une reprise coûtait une dizaine de secondes, l'autre n'étant
                // pas encore revenu au rendez-vous quand on l'y cherchait.
                runtime.Failures = 0;
                runtime.LastFailure = null;
                runtime.WasAbsent = false;
                runtime.NextAttempt = _clock.UtcNow;
            }
            else
            {
                Retry(runtime, peerWasAbsent: false, "lien perdu");
            }
        }
    }

    private void StartDueDials()
    {
        foreach (var runtime in _runtimes.Values)
        {
            if (runtime.Session is not null || runtime.Dial is not null)
                continue;

            if (_clock.UtcNow < runtime.NextAttempt)
                continue;

            runtime.DialStartedAt = _clock.UtcNow;
            runtime.Dial = DialAsync(runtime.Pair, _life.Token);
        }
    }

    private async Task<DialResult> DialAsync(PairRecord pair, CancellationToken ct)
    {
        var attempt = await _dialer.ConnectAsync(pair, ct).ConfigureAwait(false);

        if (attempt.Link is null)
            return new DialResult(null, attempt.PeerWasAbsent, attempt.Failure);

        // En cas de refus, la session a déjà refermé le lien : rien à libérer ici.
        var session = await PeerSession
            .EstablishAsync(attempt.Link, pair, _ourId, _identity, _clock, _log, ct, _groups)
            .ConfigureAwait(false);

        return session is null
            ? new DialResult(null, false, "session refusée")
            : new DialResult(session, false, null);
    }

    /// <summary>Donne au limiteur ce que le lien observe, pour qu'il puisse céder.</summary>
    /// <remarks>
    /// Sans cette mesure, le limiteur monterait jusqu'à son plafond et n'en
    /// redescendrait jamais : c'est elle, et elle seule, qui ferme la boucle
    /// entre la congestion réelle et le débit que l'on s'autorise.
    /// </remarks>
    private void ObserveLinks()
    {
        foreach (var runtime in _runtimes.Values)
        {
            if (runtime is not { Session: { } session, Limiter: { } limiter })
                continue;

            limiter.Bypassed = _uploadLimited is false;
            limiter.Observe(session.Link.PacketLossPercent, session.Link.RoundTripMs);
        }
    }

    /// <summary>Réannonce notre apparence aux pairs connectés quand elle a changé.</summary>
    /// <remarks>
    /// Rien n'est envoyé tant que rien ne bouge : la session ouverte vaut
    /// présence, et un battement de cœur applicatif serait du trafic pur.
    /// </remarks>
    private async Task AnnounceIfChangedAsync(CancellationToken ct)
    {
        var manifest = await _local.CurrentAsync(ct).ConfigureAwait(false);
        var fingerprint = _local.Fingerprint;

        // Comparaison par référence : le manifeste n'est reconstruit que
        // lorsqu'un changement a été détecté, donc la même instance signifie la
        // même apparence, sans rehacher des centaines de mégaoctets.
        if (ReferenceEquals(manifest, _announcedManifest) && fingerprint == _announcedFingerprint)
            return;

        _announcedManifest = manifest;
        _announcedFingerprint = fingerprint;

        foreach (var runtime in _runtimes.Values)
        {
            if (runtime.Exchange is not { } exchange || runtime.Session is null)
                continue;

            try
            {
                await exchange.HelloAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Warning($"{runtime.Pair.DisplayName} : réannonce en échec.", e);
            }
        }
    }

    /// <summary>Pose ce qui doit l'être, retire ce qui ne doit plus l'être.</summary>
    private void ReconcileVisibility(IReadOnlyList<VisiblePlayer> visible)
    {
        var announced = new Dictionary<PeerId, PlayerFingerprint>();

        foreach (var (id, runtime) in _runtimes)
        {
            if (runtime.Exchange?.View.Fingerprint is not { } fingerprint || fingerprint == default)
                continue;

            if (runtime.Pair.Permissions.HasFlag(PairPermissions.ReceiveAppearance) is false)
                continue;

            // Listé par un service actif : rien de posé, paire directe
            // comprise. Absent d'« announced », il sort du champ pour la suite,
            // et ce qui était posé se retire par le chemin ordinaire.
            if (_bans?.Status(fingerprint).Verdict is BanVerdict.Listed)
                continue;

            if (runtime.Pair.PinnedFingerprint is { } pinned)
            {
                // Le pair annonce un autre personnage que celui auprès duquel on
                // s'est pairé. Ce peut être son alternatif comme ce peut être la
                // revendication du personnage d'un tiers, sur qui ses fichiers
                // s'appliqueraient chez nous. Dans le doute on ne pose rien, et
                // l'interface a de quoi le dire.
                if (pinned != fingerprint)
                {
                    if (runtime.Disputed is false)
                        _log.Warning($"{runtime.Pair.DisplayName} : empreinte de personnage inattendue, rien n'est posé.");

                    runtime.Disputed = true;
                    continue;
                }
            }
            else
            {
                _book.PinFingerprint(id, fingerprint);
                runtime.Pair = _book.Find(id) ?? runtime.Pair;
            }

            runtime.Disputed = false;
            announced[id] = fingerprint;
        }

        var matched = VisibilityMatcher.Match(visible, announced)
            .ToDictionary(match => match.Peer, match => match.Object);

        foreach (var (id, runtime) in _runtimes)
        {
            if (runtime.Busy)
                continue;

            var inSight = matched.TryGetValue(id, out var target);

            if (inSight && runtime.Exchange is { View: { Ready: true, Manifest: { } manifest } })
            {
                var hash = ManifestCodec.HashOf(manifest);

                if (runtime.AppliedOn == target && runtime.AppliedManifest == hash)
                    continue;

                if (_applicator.CanApply(out var why) is false)
                {
                    _log.Debug($"{runtime.Pair.DisplayName} : application différée, {why}");
                    continue;
                }

                // Même objet, mêmes fichiers : seuls les plugins voisins ont
                // quelque chose à reposer, et un redessin ferait clignoter le
                // personnage entier pour un titre qui change.
                if (runtime.AppliedOn == target
                    && runtime.AppliedValue is { } before
                    && ExtrasDiff.OnlyExtrasDiffer(before, manifest))
                {
                    var change = ExtrasDiff.Between(before.ExtrasOrNone, manifest.ExtrasOrNone);
                    runtime.Work = ApplyExtrasAsync(id, runtime, target, manifest, change, hash);
                }
                else
                {
                    runtime.Work = ApplyAsync(id, runtime, target, manifest, hash);
                }
            }
            else if (runtime.AppliedOn is not null && inSight is false)
            {
                runtime.Work = RemoveAsync(id, runtime);
            }
        }
    }

    private async Task ApplyAsync(
        PeerId id, Runtime runtime, GameObjectRef target, CharacterManifest manifest, BlobHash hash)
    {
        try
        {
            var extrasPosed = await _applicator.ApplyAsync(id, target, manifest, SessionToken(runtime)).ConfigureAwait(false);

            runtime.AppliedOn = target;
            RecordApplied(runtime, manifest, hash, extrasPosed);
            runtime.Session?.MarkApplied();
            _book.Seen(id);

            _log.Info($"{runtime.Pair.DisplayName} : apparence posée.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            runtime.LastFailure = $"application en échec : {e.Message}";
            _log.Warning($"{runtime.Pair.DisplayName} : application en échec.", e);
        }
    }

    /// <summary>
    /// Note ce qui est réellement à l'écran.
    /// </summary>
    /// <remarks>
    /// Des extras qui n'ont pas pu être posés (personnage trop long à charger)
    /// sont notés absents : au tic suivant, le manifeste diffère de ce qui est
    /// posé par ses seuls extras, et ils sont retentés sans redessin. Les croire
    /// posés laissait le pair sans ses proportions jusqu'à sa réapparition.
    /// </remarks>
    private static void RecordApplied(Runtime runtime, CharacterManifest manifest, BlobHash hash, bool extrasPosed)
    {
        var onScreen = extrasPosed ? manifest : manifest with { Extras = null };

        runtime.AppliedValue = onScreen;
        runtime.AppliedManifest = extrasPosed ? hash : ManifestCodec.HashOf(onScreen);
    }

    private async Task ApplyExtrasAsync(
        PeerId id, Runtime runtime, GameObjectRef target, CharacterManifest manifest, ExtrasChange change, BlobHash hash)
    {
        try
        {
            var extrasPosed = await _applicator.ApplyExtrasAsync(id, target, manifest.ExtrasOrNone, change, SessionToken(runtime)).ConfigureAwait(false);

            RecordApplied(runtime, manifest, hash, extrasPosed);

            _log.Info($"{runtime.Pair.DisplayName} : extras reposés sans redessin.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            runtime.LastFailure = $"extras en échec : {e.Message}";
            _log.Warning($"{runtime.Pair.DisplayName} : extras en échec.", e);
        }
    }

    private async Task RemoveAsync(PeerId id, Runtime runtime)
    {
        try
        {
            await _applicator.RemoveAsync(id, _life.Token).ConfigureAwait(false);

            runtime.AppliedOn = null;
            runtime.AppliedManifest = null;
            runtime.AppliedValue = null;

            // La session reste ouverte et le cache reste plein : le pair va
            // revenir, et tout refaire coûterait un transfert complet.
            runtime.Session?.MarkOutOfSight();

            _log.Info($"{runtime.Pair.DisplayName} : hors du champ, apparence retirée.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            runtime.LastFailure = $"retrait en échec : {e.Message}";
            _log.Warning($"{runtime.Pair.DisplayName} : retrait en échec.", e);
        }
    }

    /// <param name="exchange">Null pour la session d'un pair retiré, dont on ne lit que l'avis.</param>
    private async Task PumpAsync(Runtime runtime, PeerExchange? exchange, PeerSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var message in session.Messages.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Ramassé au tic suivant : c'est lui qui touche au carnet et à
                // l'écran, pas ce fil-ci.
                if (message.Kind == MessageKind.Unpair)
                {
                    if (EndsOnUnpair(runtime.Pair))
                        runtime.EndedByPeer = true;

                    continue;
                }

                // Ramassé au tic suivant, comme l'avis de retrait. Ignoré d'un
                // membre de groupe : on ne met pas en pause un membre, on le
                // bloque, et un blocage ne se dit pas.
                if (message.Kind == MessageKind.Pause)
                {
                    if (runtime.Pair.Group is null)
                        runtime.PausedByPeerNotice = true;

                    continue;
                }

                // Ramassé au tic suivant, comme l'avis de retrait : c'est le tic
                // qui touche au carnet de groupes, pas ce fil-ci.
                if (message.Kind == MessageKind.GroupPolicy)
                {
                    if (CarriesGroupMessages(runtime.Pair))
                    {
                        // Bornée à quatre : un pair qui inonde ne fait grossir
                        // aucune mémoire, le tic suivant rééchange la plus récente.
                        if (runtime.InboundPolicies.Count < 4)
                            runtime.InboundPolicies.Enqueue(message.Payload);
                    }

                    continue;
                }

                if (exchange is not null)
                    await exchange.HandleAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Warning($"{runtime.Pair.DisplayName} : dialogue interrompu.", e);
        }
    }

    /// <summary>Referme tout ce qui concerne un pair, y compris ce qui est à l'écran.</summary>
    /// <summary>
    /// Le jeton d'une application : celui de la session, pas celui du moteur.
    /// </summary>
    /// <remarks>
    /// Une application peut attendre jusqu'à dix secondes qu'un personnage
    /// finisse de se charger. Une pause dans cette fenêtre doit l'interrompre,
    /// et c'est la session qu'une pause ferme.
    /// </remarks>
    private CancellationToken SessionToken(Runtime runtime) => runtime.Life?.Token ?? _life.Token;

    private async Task TearDownAsync(PeerId id, Runtime runtime, CancellationToken ct)
    {
        // Relevé avant d'annuler, et non après : l'annulation exécute sur-le-champ
        // la suite de l'application, qui aurait l'air achevée au moment de
        // vérifier. Vu par le test de pause.
        var interrupted = runtime.Work is { IsCompleted: false };

        runtime.Life?.Cancel();

        // Une application en cours est annulée par ce qui précède, puis
        // attendue : sans cela, elle finissait après le démontage, sur un pair
        // oublié, et ce qu'elle posait restait jusqu'au déchargement du plugin.
        // Elle a pu poser une partie avant l'annulation, d'où le retrait même
        // si rien n'est noté comme posé.
        if (interrupted)
        {
            try
            {
                await runtime.Work!.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Déjà journalisé par la tâche elle-même.
            }
        }

        if (runtime.AppliedOn is not null || interrupted)
        {
            try
            {
                await _applicator.RemoveAsync(id, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Warning($"{runtime.Pair.DisplayName} : retrait en échec à la fermeture.", e);
            }

            runtime.AppliedOn = null;
            runtime.AppliedManifest = null;
            runtime.AppliedValue = null;
        }

        if (runtime.Exchange is { } exchange)
            await exchange.DisposeAsync().ConfigureAwait(false);

        if (runtime.Session is { } session)
            await session.DisposeAsync().ConfigureAwait(false);

        runtime.Life?.Dispose();
        runtime.Life = null;
        runtime.NoticeSentAt = null;
        runtime.Exchange = null;
        runtime.Session = null;
        runtime.Limiter = null;
        runtime.Pump = null;
        runtime.Serve = null;
        runtime.SentPolicy = null;
        runtime.InboundPolicies.Clear();
    }

    private void Retry(Runtime runtime, bool peerWasAbsent, string? failure)
    {
        runtime.LastFailure = peerWasAbsent ? null : failure;
        runtime.WasAbsent = peerWasAbsent;

        if (peerWasAbsent)
        {
            // Depuis le début de la tentative, et non sa fin : l'annonce tient
            // vingt-cinq secondes, et compter depuis sa fin ferait un cycle de
            // cinquante-cinq où l'on n'est présent que vingt-cinq. C'est le
            // recouvrement des annonces des deux côtés qui les apparie.
            var next = runtime.DialStartedAt + _settings.AbsentBackoff;
            runtime.NextAttempt = next > _clock.UtcNow ? next : _clock.UtcNow;
            return;
        }

        // Doublement, pour qu'un rendez-vous en panne ou un pair injoignable ne
        // se traduise pas par une tentative toutes les cinq secondes jusqu'au
        // soir.
        runtime.Failures++;

        var shift = Math.Min(runtime.Failures - 1, 20);
        var ticks = Math.Min(_settings.MaxBackoff.Ticks, _settings.FirstBackoff.Ticks * (1L << shift));

        runtime.NextAttempt = _clock.UtcNow + TimeSpan.FromTicks(ticks);

        if (failure is not null)
            _log.Debug($"{runtime.Pair.DisplayName} : {failure}, reprise dans {TimeSpan.FromTicks(ticks).TotalSeconds:0} s.");
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);

        // Un tic déjà entré doit finir avant qu'on démonte : sans cela, il
        // pourrait encore lire _runtimes pendant qu'on le vide, ou poser un
        // mod temporaire pointant sur un cache qu'on vient de fermer. Jamais
        // relâché ensuite : les tics suivants voient _life annulée et
        // repartent avant même de solliciter ce sémaphore.
        await _tickGate.WaitAsync().ConfigureAwait(false);

        foreach (var (id, runtime) in _runtimes)
            await TearDownAsync(id, runtime, CancellationToken.None).ConfigureAwait(false);

        _runtimes.Clear();
        _tickGate.Dispose();
        _life.Dispose();
    }

    private sealed record DialResult(PeerSession? Session, bool PeerWasAbsent, string? Failure);

    /// <summary>Ce que le moteur tient pour un pair, entre deux tics.</summary>
    private sealed class Runtime
    {
        public required PairRecord Pair { get; set; }

        public CancellationTokenSource? Life { get; set; }

        public PeerSession? Session { get; set; }

        public PeerExchange? Exchange { get; set; }

        public RateLimiter? Limiter { get; set; }

        public Task<DialResult>? Dial { get; set; }

        public Task? Pump { get; set; }

        public Task? Serve { get; set; }

        /// <summary>L'application ou le retrait en cours, jamais deux à la fois.</summary>
        public Task? Work { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset NextAttempt { get; set; }

        public DateTimeOffset DialStartedAt { get; set; }

        public DateTimeOffset SessionSince { get; set; }

        public string? LastFailure { get; set; }

        /// <summary>Le pair n'était pas au rendez-vous au dernier essai : l'interface le dit absent.</summary>
        public bool WasAbsent { get; set; }

        public GameObjectRef? AppliedOn { get; set; }

        public BlobHash? AppliedManifest { get; set; }

        /// <summary>Le manifeste posé, pour savoir ce qu'un nouveau change.</summary>
        public CharacterManifest? AppliedValue { get; set; }

        public bool Disputed { get; set; }

        /// <summary>Vrai pour un pair retiré, qu'on ne joint plus que pour le prévenir.</summary>
        public bool Revoked => Pair.Trust is PairTrust.Revoked;

        /// <summary>Quand l'avis de retrait est parti sur la session en cours.</summary>
        public DateTimeOffset? NoticeSentAt { get; set; }

        /// <summary>Écrit par la pompe de la session, lu par le tic.</summary>
        public volatile bool EndedByPeer;

        /// <summary>Un avis de pause est arrivé, que le tic suivant reporte au carnet.</summary>
        public volatile bool PausedByPeerNotice;

        /// <summary>
        /// Ce qu'on accepte de ce pair, global et pair confondus.
        /// </summary>
        /// <remarks>
        /// Écrit par le tic, lu par la session à chaque manifeste reçu. L'écriture
        /// précède toujours la redemande du manifeste, et la réponse ne peut
        /// arriver qu'après : la session lit donc la valeur à jour.
        /// </remarks>
        public TransientCategories Receive { get; set; } = TransientCategories.All;

        public bool Busy => Work is { IsCompleted: false };

        /// <summary>Les charges de <see cref="MessageKind.GroupPolicy"/> reçues, en attente du tic.</summary>
        public ConcurrentQueue<byte[]> InboundPolicies { get; } = new();

        /// <summary>La dernière politique de groupe envoyée sur cette session.</summary>
        public byte[]? SentPolicy { get; set; }
    }
}
