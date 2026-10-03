using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Ui;

namespace Linkpearl.Integration;

/// <summary>Une demande de pairage reçue, en attente de décision.</summary>
/// <param name="Ephemeral">La clé d'accord éphémère de l'autre, reçue avec la demande.</param>
/// <param name="PairingMaterial">
/// Le matériau du secret de paire, une fois l'accord fait. Absent tant que la
/// demande n'a pas été acceptée : c'est l'acceptation qui tire notre éphémère.
/// </param>
public sealed record IncomingRequest(
    PeerId Id, byte[] PublicKey, byte[] PairingNonce, string CharacterName, ushort WorldId,
    DateTimeOffset ReceivedAt, byte[] Ephemeral, byte[]? PairingMaterial = null)
{
    /// <summary>L'empreinte du personnage annoncé, nom et monde : ce qu'on compare aux joueurs visibles.</summary>
    /// <remarks>
    /// Calculée une fois, ici dans l'adaptateur : comparer le nom seul
    /// faisait passer pour « visible à proximité » un homonyme d'un autre monde.
    /// </remarks>
    public PlayerFingerprint Sender { get; } = PlayerFingerprint.Of(DalamudObjectSource.Normalize(CharacterName), WorldId);

    /// <summary>Un repère court pour le journal, qui ne nomme personne.</summary>
    public string Tag => PresenceService.Tag(Id);
}

/// <summary>
/// Présence au rendez-vous, détection des joueurs alentour, demandes de pairage.
/// </summary>
/// <remarks>
/// Une seule connexion, tenue ouverte. C'est elle qui vaut présence : la fermer
/// vaut déclaration d'absence, sans battement de cœur à gérer. C'est aussi elle
/// qui reçoit les demandes, poussées par le serveur plutôt que sondées.
///
/// Ce que le rendez-vous apprend, et c'est assumé : quels noms de personnage
/// sont en ligne. Une adresse de boîte dérive du nom, donc un inconnu ne peut
/// pas reconnaître quelqu'un sans que le serveur le puisse aussi. Voir
/// docs/pairage.md.
/// </remarks>
public sealed class PresenceService : IDisposable
{
    private readonly Configuration _configuration;
    private readonly Func<IdentityKeyPair?> _identity;
    private readonly IPluginLog _log;
    private readonly IClock _clock;

    /// <summary>Les demandes reçues, bornées en nombre et en âge.</summary>
    /// <remarks>
    /// Une liste et non une file : l'utilisateur répond dans l'ordre qu'il
    /// veut, et c'est la demande cliquée qu'il faut retirer, pas la plus ancienne.
    /// </remarks>
    private readonly PairRequestInbox<IncomingRequest> _inbox;

    /// <summary>Nos demandes envoyées, et la surveillance de leurs acceptations.</summary>
    private readonly OutgoingPairRequests _outgoing;

    /// <summary>Les acceptations reçues en réponse à nos propres demandes, pas encore tranchées.</summary>
    /// <remarks>
    /// Tranchées par la boucle du plugin et non par le fil d'écoute : il faut
    /// savoir si l'expéditeur est devant nous, et seul le plugin le sait.
    /// </remarks>
    private readonly ConcurrentQueue<IncomingRequest> _accepted = new();

    /// <summary>
    /// Plafond de la file des acceptations. Seul qui connaît l'aléa d'une de
    /// nos demandes y entre, et la boucle la vide toutes les quinze secondes :
    /// au-delà, c'est une rafale, pas des réponses.
    /// </summary>
    private const int MaxQueuedAcceptances = 32;

    private readonly ConcurrentDictionary<PlayerFingerprint, DateTimeOffset> _detected = new();

    private readonly Dictionary<RendezvousAddress, Session> _sessions = [];
    private readonly Lock _gate = new();

    /// <summary>Dit si une clé ou un personnage est bloqué au carnet, branché par le plugin.</summary>
    private volatile Func<PeerId, PlayerFingerprint, bool>? _blocked;

    /// <summary>
    /// Levé une fois par épisode quand notre boîte personnelle reste tenue
    /// par une autre connexion. Hors du fil du jeu.
    /// </summary>
    public event Action<RendezvousAddress>? MailboxContested;

    /// <summary>Le délai entre deux réclamations d'une boîte qu'une autre connexion tient.</summary>
    /// <remarks>
    /// Une minute : le temps qu'un service voie mourir notre propre connexion
    /// d'avant une coupure réseau, sans le solliciter à chaque ronde.
    /// </remarks>
    private static readonly TimeSpan ClaimRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Combien de temps on croit un service incapable de réclamer, avant de
    /// lui redemander. Chaque essai coûte une connexion, que ce service coupe.
    /// </summary>
    private static readonly TimeSpan ClaimUnsupportedFor = TimeSpan.FromHours(6);

    /// <summary>
    /// Réclamations manquées d'affilée avant d'alerter.
    /// </summary>
    /// <remarks>
    /// Deux, soit une minute environ : après une coupure, le service peut
    /// tenir encore notre propre connexion morte le temps de s'en apercevoir,
    /// et alerter sur un premier refus crierait au loup à chaque hoquet réseau.
    /// </remarks>
    private const int ContestStrikesBeforeAlert = 2;

    /// <summary>Remplacés d'un bloc par le fil de rafraîchissement, lus par les ouvertures.</summary>
    private volatile IReadOnlyList<GroupRecord> _groups = [];

    /// <summary>Par joueur détecté, les groupes dont sa boîte de présence a répondu.</summary>
    private readonly ConcurrentDictionary<PlayerFingerprint, IReadOnlyList<GroupId>> _groupPresence = new();

    /// <summary>Le côté membre de l'admission, attaché une fois par le plugin.</summary>
    private volatile AdmissionHost? _host;

    /// <summary>Le côté candidat de l'admission, attaché une fois par le plugin.</summary>
    private volatile AdmissionCandidate? _candidate;

    /// <summary>Une seule ronde d'ouverture à la fois.</summary>
    /// <remarks>
    /// La boucle de rafraîchissement et une candidature lancée depuis
    /// l'interface ouvrent toutes deux des sessions : deux ouvertures
    /// concurrentes d'une même session en feraient deux connexions, dont la
    /// première, jamais fermée, garderait nos boîtes ouvertes et son écoute
    /// vivante. Jamais disposé : sans poignée d'attente demandée, il ne tient
    /// aucune ressource, et le disposer ferait lever les rondes encore en vol.
    /// </remarks>
    private readonly SemaphoreSlim _opening = new(1, 1);

    /// <summary>Réponses d'admission déposées pour le compte de l'hôte, par minute glissante.</summary>
    /// <remarks>
    /// Le service compte 60 trames par minute et par adresse IP
    /// (RendezvousLimits.AnnouncementsPerMinute), et nos propres interrogations
    /// de présence et réouvertures en consomment déjà. Des demandes forgées en
    /// nombre, chacune avec un aléa neuf, feraient sinon répondre l'hôte jusqu'à
    /// épuiser ce quota et nous faire déconnecter. Vingt laisse la place à
    /// plusieurs candidatures honnêtes simultanées.
    /// </remarks>
    private const int MaxAdmissionAnswersPerMinute = 20;

    /// <summary>Les instants des dernières réponses d'admission, gardés par <see cref="_gate"/>.</summary>
    private readonly Queue<DateTimeOffset> _admissionAnswers = new();

    /// <summary>
    /// Boîtes qu'on s'autorise sur une connexion avant de la refaire à neuf.
    /// </summary>
    /// <remarks>
    /// Le service ne retire jamais une adresse d'une connexion ouverte, et
    /// chaque fenêtre en ajoute : sans remise à zéro, dix groupes épuisent sa
    /// limite de 64 en une heure et demie, et l'ouverture suivante est refusée.
    /// Un peu sous la limite, pour ne jamais la toucher.
    /// </remarks>
    private const int MailboxBudget = 60;

    /// <summary>
    /// Ce que nous tenons ouvert auprès d'un service.
    /// </summary>
    /// <remarks>
    /// Une par service activé. La connexion elle-même vaut présence : la fermer
    /// déclare l'absence, sans battement de cœur à gérer. Chaque session a son
    /// propre compte à rebours de reprise, pour qu'un service en panne ne
    /// retarde pas les autres.
    /// </remarks>
    private sealed class Session
    {
        public required RendezvousAddress At { get; init; }

        public RendezvousClient? Client { get; set; }

        public CancellationTokenSource? Life { get; set; }

        public PlayerFingerprint? OpenedFor { get; set; }

        /// <summary>La fenêtre sous laquelle les boîtes ont été ouvertes.</summary>
        public long OpenedWindow { get; set; }

        /// <summary>Adresses tenues par cette connexion, qui ne fait qu'en accumuler.</summary>
        public int OpenedCount { get; set; }

        /// <summary>
        /// Les groupes dont cette connexion tient des boîtes de présence, et
        /// les codes dont elle tient des boîtes d'admission (voir <see cref="Signature"/>).
        /// </summary>
        /// <remarks>
        /// Un ensemble et non une empreinte : il faut savoir si un groupe ou
        /// un code a disparu, pas seulement si quelque chose a changé.
        /// </remarks>
        public IReadOnlySet<string> OpenedKeys { get; set; } = new HashSet<string>();

        public string? Failure { get; set; }

        public DateTimeOffset NextAttempt { get; set; }

        /// <summary>Un service de nos réglages, qui sont aussi ceux du Public.</summary>
        /// <remarks>
        /// Les seuls où l'on tient sa boîte personnelle, où l'on détecte les
        /// joueurs alentour et où l'on dépose des demandes de pairage. Un
        /// service connu par la seule politique d'un groupe a été choisi par
        /// un modérateur, pas par nous : il ne reçoit que les boîtes de ce
        /// groupe, sans quoi il apprendrait notre nom et ceux de nos voisins.
        /// </remarks>
        public bool Settings { get; set; }

        /// <summary>Le service d'une candidature en cours, où le groupe répond dans notre boîte personnelle.</summary>
        public bool Candidacy { get; set; }

        /// <summary>Tient notre boîte personnelle : réglages ou candidature.</summary>
        public bool Personal => Settings || Candidacy;

        /// <summary>Vrai si ce service a accepté la réclamation sur cette connexion.</summary>
        public bool Exclusive { get; set; }

        /// <summary>Jusqu'à quand on croit ce service d'avant la réclamation.</summary>
        public DateTimeOffset ClaimUnsupportedUntil { get; set; }

        /// <summary>Nos propres boîtes qu'une autre connexion tient.</summary>
        public IReadOnlyList<byte[]> Contested { get; set; } = [];

        /// <summary>Les adresses de notre boîte personnelle à la dernière réclamation, en hexadécimal.</summary>
        public IReadOnlySet<string> PersonalAddresses { get; set; } = new HashSet<string>();

        /// <summary>Réclamations d'affilée où notre boîte personnelle est restée à un autre.</summary>
        public int ContestStrikes { get; set; }

        /// <summary>L'alerte de l'épisode en cours est partie.</summary>
        public bool ContestReported { get; set; }

        public DateTimeOffset NextClaim { get; set; }
    }

    /// <summary>Ce qu'une connexion doit tenir.</summary>
    /// <param name="Own">
    /// Nos propres boîtes, dérivées de notre empreinte : la personnelle et
    /// celles de présence de nos groupes. Réclamées, donc à nous seuls.
    /// </param>
    /// <param name="Shared">
    /// Les boîtes d'admission, que tiennent ensemble tous les modérateurs d'un
    /// groupe : ouvertes sans exclusivité, sans quoi le premier en ligne
    /// fermerait la porte aux autres.
    /// </param>
    private sealed record MailboxPlan(
        List<byte[]> Own, List<byte[]> Shared, IReadOnlySet<string> Personal, HashSet<string> Keys)
    {
        public int Count => Own.Count + Shared.Count;
    }

    /// <remarks>
    /// L'identité est demandée à chaque usage et non prise une fois : elle
    /// appartient au personnage connecté, donc elle apparaît à la connexion,
    /// change au changement de personnage, et n'existe pas à l'écran-titre.
    /// </remarks>
    public PresenceService(Configuration configuration, Func<IdentityKeyPair?> identity, IClock clock, IPluginLog log)
    {
        _configuration = configuration;
        _identity = identity;
        _clock = clock;
        _log = log;
        _inbox = new PairRequestInbox<IncomingRequest>(clock);
        _outgoing = new OutgoingPairRequests(clock);
    }

    /// <summary>
    /// Un repère court pour le journal, qui ne nomme personne.
    /// </summary>
    /// <remarks>
    /// Les premiers caractères de l'identifiant de clé : assez pour suivre un
    /// même pair d'une ligne à l'autre, rien qui mène à un nom de personnage.
    /// </remarks>
    public static string Tag(PeerId id) => $"pair {id.ToHex()[..8]}";

    /// <summary>Ce qu'on répond tant qu'aucun personnage n'est connecté.</summary>
    private const string NoCharacter =
        "connexion à un personnage requise : chaque personnage possède sa propre identité Linkpearl.";

    /// <summary>Vrai dès qu'un seul service répond.</summary>
    /// <remarks>
    /// Un seul suffit à être vu et à voir : exiger que tous répondent ferait
    /// dépendre l'affichage du plus mal en point.
    /// </remarks>
    public bool Connected => ConnectedCount > 0;

    public int ConnectedCount
    {
        get
        {
            lock (_gate)
                return _sessions.Values.Count(session => session.Client is not null);
        }
    }

    public int ConfiguredCount => _configuration.ActiveRendezvous.Count;

    /// <summary>La panne du premier service qui en signale une, s'il y en a.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_gate)
                return _sessions.Values.FirstOrDefault(session => session.Failure is not null)?.Failure;
        }
    }

    /// <summary>La panne dite au joueur, dans sa langue.</summary>
    /// <remarks>
    /// Le message d'une <see cref="SocketException"/> est celui de Windows, dans
    /// la langue du système et sans le nom en cause : « Le nom demandé est
    /// valide mais aucune donnée du type requise n'a été trouvée » ne dit à
    /// personne que c'est son DNS qui ne connaît pas le service.
    /// </remarks>
    private static string Describe(Exception e) => e switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }
            => "adresse du service introuvable",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused }
            => "connexion refusée par le service",
        SocketException { SocketErrorCode: SocketError.TimedOut }
            => "le service ne répond pas",
        _ => "erreur de connexion au service",
    };

    /// <summary>Les empreintes reconnues comme utilisant le plugin, avec leur fraîcheur.</summary>
    public IReadOnlyDictionary<PlayerFingerprint, DateTimeOffset> Detected => _detected;

    /// <summary>Le nombre de demandes en attente, sans copier la liste.</summary>
    public int RequestCount => _inbox.Count;

    public IReadOnlyList<IncomingRequest> PeekRequests() => _inbox.Peek();

    /// <summary>Retire une demande à laquelle l'utilisateur vient de répondre.</summary>
    public void Forget(IncomingRequest request) => _inbox.Remove(request);

    /// <summary>Oublie toutes les demandes, reçues et envoyées, au changement de personnage.</summary>
    /// <remarks>
    /// Une demande est adressée à la boîte d'un personnage : la montrer au
    /// suivant lui ferait accepter, sous son nom, ce qu'on a proposé à un autre.
    /// Et une acceptation de ce qu'avait demandé le précédent rangerait le pair
    /// dans le carnet du suivant.
    /// </remarks>
    public void ForgetRequests()
    {
        _inbox.Clear();
        _outgoing.Clear();
        _accepted.Clear();
    }

    /// <summary>Vrai si une demande à ce joueur attend encore sa réponse.</summary>
    public bool HasPendingRequestTo(PlayerFingerprint target) => _outgoing.IsPending(target);

    /// <summary>Branche la question « est-il bloqué au carnet ? ». Appelé une fois par le plugin.</summary>
    /// <remarks>Interrogée sur le fil d'écoute : elle ne doit jamais lever, et ne rien attendre.</remarks>
    public void SetBlockedCheck(Func<PeerId, PlayerFingerprint, bool> blocked) => _blocked = blocked;

    /// <summary>
    /// Tranche les acceptations reçues depuis le dernier appel.
    /// </summary>
    /// <param name="visible">Ce joueur, nom et monde, est-il devant nous à cet instant ?</param>
    /// <returns>
    /// Chaque acceptation avec son verdict. Celle qui conclut porte le
    /// matériau du secret de paire, que le carnet attend.
    /// </returns>
    public IReadOnlyList<(IncomingRequest Request, AcceptanceVerdict Verdict)> SettleAcceptances(
        Func<PlayerFingerprint, bool> visible)
    {
        var settled = new List<(IncomingRequest, AcceptanceVerdict)>();

        while (_accepted.TryDequeue(out var request))
        {
            var verdict = _outgoing.Settle(
                request.Sender, request.Id, request.PairingNonce, request.Ephemeral, visible(request.Sender));

            if (verdict is AcceptanceVerdict.Concluded concluded)
                settled.Add((request with { PairingMaterial = concluded.PairingMaterial }, verdict));
            else
                settled.Add((request, verdict));
        }

        return settled;
    }

    /// <summary>Les services où notre boîte personnelle reste tenue par une autre connexion.</summary>
    public IReadOnlyList<RendezvousAddress> ContestedServices
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values
                    .Where(session => session.Client is not null && session.ContestStrikes >= ContestStrikesBeforeAlert)
                    .Select(session => session.At)];
            }
        }
    }

    public void SetGroups(IReadOnlyList<GroupRecord> groups) => _groups = groups;

    /// <summary>Les listes des services, attachées une fois par le plugin.</summary>
    private volatile ServiceBanBook? _bans;

    public void SetServiceBans(ServiceBanBook bans) => _bans = bans;

    /// <summary>Branche l'admission. Appelé une fois par le plugin, avant la première ronde.</summary>
    public void Attach(AdmissionHost host, AdmissionCandidate candidate)
    {
        _host = host;
        _candidate = candidate;
    }

    public IReadOnlyList<GroupId> GroupsOf(PlayerFingerprint member)
        => _groupPresence.TryGetValue(member, out var groups) ? groups : [];

    /// <summary>
    /// Les adresses qu'une connexion doit tenir, sous la fenêtre courante et la suivante.
    /// </summary>
    /// <remarks>
    /// Un service de nos réglages tient tout : boîte personnelle, présence de
    /// chaque groupe, admission de chaque code qu'on peut admettre. Un service
    /// connu par la seule politique d'un groupe ne tient que les boîtes des
    /// groupes qui le nomment, et la boîte personnelle seulement le temps
    /// d'une candidature qu'il porte.
    ///
    /// Les clés distinguent un jeu de boîtes d'un autre, pour savoir s'il faut
    /// rouvrir. Préfixées, parce que groupes, codes d'admission et boîte
    /// personnelle vivent dans le même ensemble : un code renouvelé, un droit
    /// d'admettre perdu ou une candidature finie doit fermer la connexion
    /// exactement comme un groupe quitté, sans quoi l'ancienne boîte resterait
    /// ouverte et continuerait de recevoir.
    /// </remarks>
    private MailboxPlan PlanFor(
        Session session, PlayerFingerprint fingerprint, IReadOnlyList<GroupRecord> groups, IReadOnlyList<byte[]> codes)
    {
        var now = _clock.UtcNow;

        bool Names(GroupRecord group) => session.Settings || group.Rendezvous.Contains(session.At);

        var ours = groups.Where(Names).ToList();
        var admitting = session.Settings
            ? codes
            : [.. codes.Where(code => groups.Any(group =>
                  Names(group) && group.Policy?.Code is { } own && own.AsSpan().SequenceEqual(code)))];

        var personal = session.Personal
            ? MailboxAddress.Around(fingerprint, now).Select(address => address.ToBytes()).ToList()
            : [];

        List<byte[]> own =
        [
            .. personal,
            .. ours.SelectMany(group => GroupDerivation.PresenceAround(group.Secret, fingerprint, now))
                .Select(address => address.ToBytes()),
        ];

        List<byte[]> shared =
        [
            .. admitting.SelectMany(code => GroupDerivation.AdmissionAround(code, now)).Select(address => address.ToBytes()),
        ];

        HashSet<string> keys =
        [
            .. ours.Select(group => $"g:{group.Id}"),
            .. admitting.Select(code => $"c:{Convert.ToHexStringLower(code)}"),
        ];

        if (session.Personal)
            keys.Add("p");

        return new MailboxPlan(own, shared, personal.Select(Convert.ToHexStringLower).ToHashSet(), keys);
    }

    /// <summary>Les codes dont on ouvre la boîte d'admission.</summary>
    /// <remarks>
    /// Une exception du carnet ne doit pas priver de présence : sans codes, on
    /// n'admet personne pendant une ronde, ce qui ne coûte qu'un redépôt au candidat.
    /// </remarks>
    private IReadOnlyList<byte[]> AdmissionCodes()
    {
        try
        {
            return _host?.AdmissionCodes ?? [];
        }
        catch (Exception e)
        {
            _log.Warning(e, "Lecture des codes d'admission en échec.");
            return [];
        }
    }

    /// <summary>
    /// Ouvre nos boîtes, ou les rouvre si le personnage a changé.
    /// </summary>
    /// <remarks>
    /// Le changement de personnage compte : les boîtes dérivent du nom, donc
    /// celles de l'ancien doivent se fermer et celles du nouveau s'ouvrir.
    /// </remarks>
    public async Task EnsureOpenAsync(PlayerFingerprint fingerprint, CancellationToken ct)
    {
        await _opening.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await EnsureOpenLockedAsync(fingerprint, ct).ConfigureAwait(false);
        }
        finally
        {
            _opening.Release();
        }

        if (_configuration.Discoverable)
            await RedepositCandidacyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Redépose la demande de la candidature en cours, si c'est le moment.</summary>
    /// <remarks>
    /// Après l'ouverture, pour que la session vers le service du code existe
    /// déjà. Les boîtes ne gardent rien : c'est ce redépôt qui fait voir la
    /// demande au membre qui se connecte après coup, et qui rattrape une
    /// preuve perdue en faisant renvoyer le même défi.
    /// </remarks>
    private async Task RedepositCandidacyAsync(CancellationToken ct)
    {
        if (_candidate is not { } candidate)
            return;

        byte[]? again;
        InvitationTicket? code;
        RendezvousAddress? service;

        try
        {
            code = candidate.Code;
            service = candidate.Service;
            again = candidate.DueRedeposit();
        }
        catch (Exception e)
        {
            _log.Warning(e, "Redépôt de candidature en échec.");
            return;
        }

        if (again is null || code is not { } ticket || service is not { } at)
            return;

        var (delivered, _) = await DepositOnAsync(
            [at], GroupDerivation.AdmissionAddress(ticket.ToBytes(), _clock.UtcNow).ToBytes(), again, ct)
            .ConfigureAwait(false);

        if (delivered == 0)
            _log.Debug("Redépôt de candidature : aucun service joignable, nouvel essai à la prochaine échéance.");
    }

    private async Task EnsureOpenLockedAsync(PlayerFingerprint fingerprint, CancellationToken ct)
    {
        if (_configuration.Discoverable is false)
        {
            CloseAll();
            return;
        }

        Reconcile();

        foreach (var session in Snapshot())
        {
            // Le changement de personnage compte : les boîtes dérivent du nom,
            // donc celles de l'ancien doivent se fermer et celles du nouveau
            // s'ouvrir. La contestation de l'ancien ne regarde pas le nouveau.
            if (session.Client is not null && session.OpenedFor != fingerprint)
            {
                Close(session);
                ForgetContest(session);
            }

            var keys = PlanFor(session, fingerprint, _groups, AdmissionCodes()).Keys;

            // Quitter un groupe, ou perdre un code d'admission, coupe tout. Le service n'a pas d'opération pour
            // fermer une boîte, et rouvrir sur la connexion en place ne fait
            // qu'en ajouter : la présence du groupe quitté resterait visible de
            // ses membres jusqu'à la prochaine reconnexion, des heures plus tard
            // peut-être. On ferme donc la connexion, et la même ronde la rouvre
            // à neuf, avec les seuls groupes restants.
            if (session.Client is not null && session.OpenedKeys.IsSubsetOf(keys) is false)
                Close(session);

            // Les adresses tournent toutes les trente minutes, et changent aussi
            // quand on rejoint un groupe.
            if (session.Client is { } open
                && (session.OpenedWindow != MailboxAddress.IndexAt(_clock.UtcNow)
                    || session.OpenedKeys.SetEquals(keys) is false))
                await ReopenAsync(session, open, fingerprint, ct).ConfigureAwait(false);

            // Une boîte à nous qu'une autre connexion tenait : on la redemande,
            // elle a pu se libérer.
            if (session.Client is { } held && session.Contested.Count > 0 && _clock.UtcNow >= session.NextClaim)
                await ReclaimAsync(session, held, ct).ConfigureAwait(false);

            if (session.Client is not null || _clock.UtcNow < session.NextAttempt)
                continue;

            await OpenAsync(session, fingerprint, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Aligne les sessions sur la liste des services activés.</summary>
    private void Reconcile()
    {
        var configured = _configuration.ActiveRendezvous.Select(entry => entry.Address).ToHashSet();

        // Les services d'un groupe comptent même s'ils ne sont pas dans nos
        // réglages : c'est là que ses membres nous cherchent.
        var active = configured.Concat(_groups.SelectMany(group => group.Rendezvous)).ToHashSet();

        // Le service d'une candidature en cours compte aussi : c'est là que
        // nous déposons la demande, et, n'étant pas encore membres, rien
        // d'autre ne nous y relie. Les réponses arrivent dans notre boîte
        // personnelle, que cette même session tient ouverte.
        // Sans NeedsPassword : cet état attend le joueur, pas le réseau, et une
        // nouvelle candidature rouvrira la session.
        RendezvousAddress? candidacy = _candidate is { State: CandidacyState.Waiting or CandidacyState.Proving, Service: { } code }
            ? code
            : null;

        if (candidacy is { } service)
            active.Add(service);

        lock (_gate)
        {
            foreach (var (at, session) in _sessions.ToList())
            {
                if (active.Contains(at))
                    continue;

                // Retiré des réglages : on ferme, sinon l'utilisateur resterait
                // annoncé sur un service qu'il croit avoir quitté.
                CloseLocked(session);
                _sessions.Remove(at);
            }

            foreach (var at in active)
                if (_sessions.ContainsKey(at) is false)
                    _sessions[at] = new Session { At = at, NextAttempt = _clock.UtcNow };

            // Recalculé à chaque ronde : un service ajouté aux réglages, ou une
            // candidature finie, change ce que la connexion doit tenir, et la
            // comparaison des clés de PlanFor fait le reste.
            foreach (var (at, session) in _sessions)
            {
                session.Settings = configured.Contains(at);
                session.Candidacy = candidacy == at;
            }
        }
    }

    private async Task OpenAsync(Session session, PlayerFingerprint fingerprint, CancellationToken ct)
    {
        var groups = _groups;
        var codes = AdmissionCodes();
        var plan = PlanFor(session, fingerprint, groups, codes);

        // Le service refuse au-delà de RendezvousLimits.MaxMailboxesPerSession
        // (64) : se connecter pour se faire refuser, puis recommencer à chaque
        // ronde, ne mènerait nulle part. On le dit plutôt, sans rien envoyer.
        if (plan.Count > MailboxBudget)
        {
            lock (_gate)
            {
                session.Failure = "trop de groupes sur ce service";
                session.NextAttempt = _clock.UtcNow + TimeSpan.FromSeconds(30);
            }

            _log.Warning($"{plan.Count} boîtes à ouvrir sur {session.At}, au-delà de {MailboxBudget} : ouverture refusée.");
            return;
        }

        RendezvousClient? client = null;

        try
        {
            var window = MailboxAddress.IndexAt(_clock.UtcNow);
            client = await ConnectAsync(session, ct).ConfigureAwait(false);

            var exclusive = false;
            IReadOnlyList<byte[]> contested = [];

            if (plan.Own.Count > 0 && _clock.UtcNow >= session.ClaimUnsupportedUntil)
            {
                switch (await ClaimAsync(client, plan.Own, ct).ConfigureAwait(false))
                {
                    case MailboxClaimOutcome.Held held:
                        exclusive = true;
                        contested = Unheld(plan.Own, held.Mine);
                        break;

                    // Un service d'avant : il a coupé après son refus. On
                    // revient sans réclamer, et on le retient quelques heures
                    // pour ne pas payer une connexion de plus à chaque ronde.
                    case MailboxClaimOutcome.NotSupported:
                        await client.DisposeAsync().ConfigureAwait(false);
                        client = null;

                        lock (_gate)
                            session.ClaimUnsupportedUntil = _clock.UtcNow + ClaimUnsupportedFor;

                        _log.Information(
                            $"{session.At} ne connaît pas la réclamation exclusive des boîtes : ouverture partagée, "
                          + "un autre client peut y lire nos demandes de pairage.");

                        client = await ConnectAsync(session, ct).ConfigureAwait(false);
                        break;

                    case MailboxClaimOutcome.Failed failed:
                        throw new InvalidOperationException($"réclamation refusée : {failed.Reason}");
                }
            }

            // Sans exclusivité, nos propres boîtes s'ouvrent comme avant : mieux
            // vaut une boîte partagée que pas de boîte du tout.
            List<byte[]> opened = exclusive ? plan.Shared : [.. plan.Own, .. plan.Shared];

            if (opened.Count > 0)
                await client.OpenMailboxesAsync(opened, ct).ConfigureAwait(false);

            var life = new CancellationTokenSource();

            lock (_gate)
            {
                session.Client = client;
                session.Life = life;
                session.OpenedFor = fingerprint;
                session.OpenedWindow = window;
                session.Failure = null;
                session.OpenedCount = plan.Count;
                session.OpenedKeys = plan.Keys;
                session.Exclusive = exclusive;
                session.PersonalAddresses = plan.Personal;

                // Sans réclamation, rien à redemander : un service d'avant ne
                // dit pas qui d'autre tient nos boîtes.
                if (exclusive is false)
                {
                    session.Contested = [];
                    session.ContestStrikes = 0;
                }
            }

            var listening = client;
            client = null;
            _ = Task.Run(() => ListenAsync(session, listening, life.Token), life.Token);

            if (exclusive)
                RecordContest(session, contested);
        }
        catch (Exception e)
        {
            // Une connexion jamais confiée à la session se ferme ici, sans quoi
            // elle garderait réclamées des boîtes que personne n'écoute.
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);

            lock (_gate)
            {
                session.Failure = e is InvalidOperationException ? e.Message : Describe(e);

                // Trente secondes avant de réessayer : un service éteint ne doit
                // pas être sollicité à chaque ronde de détection.
                session.NextAttempt = _clock.UtcNow + TimeSpan.FromSeconds(30);
            }

            _log.Warning(e, $"Ouverture des boîtes en échec sur {session.At}.");
        }
    }

    /// <summary>Une connexion neuve, déjà branchée sur les dépôts reçus.</summary>
    private async Task<RendezvousClient> ConnectAsync(Session session, CancellationToken ct)
    {
        var client = new RendezvousClient();

        try
        {
            await client.ConnectAsync(session.At.Host, session.At.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        client.Delivered += OnDelivered;
        return client;
    }

    /// <summary>Réclame, avec un délai de garde.</summary>
    /// <remarks>
    /// Le même que l'interrogation de présence, pour la même raison : une
    /// réponse perdue bloquerait toute la boucle de rafraîchissement.
    /// </remarks>
    private static async Task<MailboxClaimOutcome> ClaimAsync(
        RendezvousClient client, IReadOnlyList<byte[]> addresses, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            return await client.ClaimMailboxesAsync(addresses, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
        {
            return new MailboxClaimOutcome.Failed("le service ne répond pas à la réclamation");
        }
    }

    /// <summary>Les adresses dont le bit est baissé : une autre connexion les tient.</summary>
    private static List<byte[]> Unheld(IReadOnlyList<byte[]> asked, bool[] mine)
        => [.. asked.Where((_, i) => i >= mine.Length || mine[i] is false)];

    /// <summary>
    /// Retient ce qu'une réclamation a laissé à d'autres, et alerte si notre
    /// boîte personnelle y reste.
    /// </summary>
    /// <remarks>
    /// Seule la boîte personnelle alerte : c'est elle qui reçoit les demandes
    /// de pairage, et son adresse dérive d'un nom que tout le monde voit. Une
    /// boîte de présence de groupe ne se calcule qu'avec le secret du groupe :
    /// la voir prise est dit au journal, et réclamé de nouveau comme les autres.
    /// </remarks>
    private void RecordContest(Session session, IReadOnlyList<byte[]> contested)
    {
        bool alert;

        lock (_gate)
        {
            session.Contested = contested;
            session.NextClaim = _clock.UtcNow + ClaimRetry;

            var personal = contested.Any(address => session.PersonalAddresses.Contains(Convert.ToHexStringLower(address)));

            if (personal)
            {
                session.ContestStrikes++;
            }
            else
            {
                session.ContestStrikes = 0;
                session.ContestReported = false;
            }

            alert = session.ContestStrikes >= ContestStrikesBeforeAlert && session.ContestReported is false;

            if (alert)
                session.ContestReported = true;
        }

        if (contested.Count > 0)
            _log.Warning($"{contested.Count} de nos boîtes sur {session.At} sont tenues par une autre connexion : nouvel essai dans une minute.");

        if (alert)
            MailboxContested?.Invoke(session.At);
    }

    /// <summary>Oublie la contestation, au changement de personnage.</summary>
    private void ForgetContest(Session session)
    {
        lock (_gate)
        {
            session.Contested = [];
            session.ContestStrikes = 0;
            session.ContestReported = false;
        }
    }

    /// <summary>Redemande, sur la connexion en place, les boîtes qu'une autre tenait.</summary>
    private async Task ReclaimAsync(Session session, RendezvousClient client, CancellationToken ct)
    {
        var contested = session.Contested;

        // Une réclamation compte peut-être pour le service comme une boîte de
        // plus : au-delà du budget, on repart d'une connexion neuve, qui
        // réclamera tout de nouveau.
        if (session.OpenedCount + contested.Count > MailboxBudget)
        {
            Close(session);
            return;
        }

        switch (await ClaimAsync(client, contested, ct).ConfigureAwait(false))
        {
            case MailboxClaimOutcome.Held held:
                lock (_gate)
                    session.OpenedCount += contested.Count;

                RecordContest(session, Unheld(contested, held.Mine));
                break;

            default:
                // Refus, silence ou coupure : la connexion n'est plus sûre, et la
                // même ronde la rouvrira proprement.
                _log.Warning($"Nouvelle réclamation en échec sur {session.At}.");
                Close(session);
                break;
        }
    }

    /// <summary>
    /// Rouvre les boîtes sous la fenêtre courante, sur la connexion en place.
    /// </summary>
    /// <remarks>
    /// Sans reconnexion : le service garde les anciennes adresses liées à cette
    /// session jusqu'à sa fermeture, et en ajouter n'en retire aucune. Celui
    /// qui nous cherchait sous l'ancienne nous trouve donc encore, et celui qui
    /// arrive nous trouve sous la nouvelle.
    /// </remarks>
    private async Task ReopenAsync(
        Session session, RendezvousClient client, PlayerFingerprint fingerprint, CancellationToken ct)
    {
        var window = MailboxAddress.IndexAt(_clock.UtcNow);
        var plan = PlanFor(session, fingerprint, _groups, AdmissionCodes());

        // Au-delà du budget, on repart d'une connexion neuve, rouverte dans la
        // même ronde : la fermeture ne repousse pas la prochaine tentative.
        if (session.OpenedCount + plan.Count > MailboxBudget)
        {
            Close(session);
            return;
        }

        try
        {
            IReadOnlyList<byte[]>? contested = null;

            if (session.Exclusive && plan.Own.Count > 0)
            {
                // Une boîte déjà à nous le reste : la réclamer de nouveau
                // répond « à nous », et seules les nouvelles adresses comptent.
                switch (await ClaimAsync(client, plan.Own, ct).ConfigureAwait(false))
                {
                    case MailboxClaimOutcome.Held held:
                        contested = Unheld(plan.Own, held.Mine);
                        break;

                    case MailboxClaimOutcome.Failed failed:
                        throw new InvalidOperationException($"réclamation refusée : {failed.Reason}");

                    default:
                        throw new InvalidOperationException("le service ne connaît plus la réclamation");
                }

                if (plan.Shared.Count > 0)
                    await client.OpenMailboxesAsync(plan.Shared, ct).ConfigureAwait(false);
            }
            else
            {
                await client.OpenMailboxesAsync([.. plan.Own, .. plan.Shared], ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                session.OpenedWindow = window;
                session.OpenedCount += plan.Count;
                session.PersonalAddresses = plan.Personal;

                // L'union et non les seules clés courantes : cette connexion
                // garde les boîtes déjà ouvertes. Si un groupe a été quitté ou
                // un code perdu entre la vérification et ici, la ronde suivante
                // le verra manquer et fermera la connexion.
                session.OpenedKeys = new HashSet<string>(session.OpenedKeys.Union(plan.Keys));
            }

            if (contested is not null)
                RecordContest(session, contested);
        }
        catch (Exception e)
        {
            // La connexion est peut-être morte sans qu'on l'ait vu : on la
            // ferme, et la même ronde la rouvrira proprement.
            _log.Warning(e, $"Réouverture des boîtes en échec sur {session.At}.");
            Close(session);
        }
    }

    /// <summary>
    /// Demande aux services de nos réglages lesquels de ces joueurs utilisent le plugin.
    /// </summary>
    /// <remarks>
    /// Un joueur est détecté dès qu'un seul service reconnaît sa boîte, et
    /// l'union se fait sur ceux qui répondent. Exiger l'accord de tous rendrait
    /// la détection dépendante du plus mal en point ; et le silence d'un service
    /// n'est pas une réponse négative, donc il ne retire personne.
    ///
    /// Jamais un service connu par la seule politique d'un groupe : chaque
    /// interrogation lui apprendrait qui se tient autour de nous, et c'est un
    /// modérateur qui l'a choisi, pas nous. Personne n'y tient d'ailleurs sa
    /// boîte personnelle.
    /// </remarks>
    public async Task RefreshDetectionAsync(IReadOnlyList<NearbyPlayer> nearby, CancellationToken ct)
    {
        if (nearby.Count == 0)
            return;

        var connected = Snapshot().Where(session => session.Client is not null).ToList();
        var settings = connected.Where(session => session.Settings).ToList();

        if (settings.Count == 0)
            return;

        var seenSomewhere = new HashSet<PlayerFingerprint>();
        var answered = false;

        foreach (var session in settings)
        {
            // Le serveur plafonne les interrogations : on découpe plutôt que de
            // se faire refuser.
            foreach (var batch in nearby.Chunk(RendezvousWire.MaxQueriedAddresses))
            {
                var addresses = batch
                    .Select(player => MailboxAddress.Of(player.Fingerprint, _clock.UtcNow).ToBytes())
                    .ToList();

                try
                {
                    // Un délai de garde, parce qu'une réponse perdue bloquait
                    // toute la boucle de rafraîchissement : plus de détection,
                    // plus de réouverture de boîte, et rien qui le dise.
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));

                    var present = await session.Client!
                        .QueryPresenceAsync(addresses, deadline.Token).ConfigureAwait(false);

                    if (present is null)
                        break;

                    answered = true;

                    for (var i = 0; i < batch.Length && i < present.Length; i++)
                        if (present[i])
                            seenSomewhere.Add(batch[i].Fingerprint);
                }
                catch (Exception e)
                {
                    lock (_gate)
                        session.Failure = Describe(e);

                    _log.Warning(e, $"Interrogation de présence en échec sur {session.At}.");
                    break;
                }
            }
        }

        // Rien de retiré tant que personne n'a répondu : perdre tout le monde
        // parce que le réseau a hoqueté ferait clignoter la liste.
        if (answered is false)
            return;

        foreach (var player in nearby)
        {
            if (seenSomewhere.Contains(player.Fingerprint))
                _detected[player.Fingerprint] = _clock.UtcNow;
            else
                _detected.TryRemove(player.Fingerprint, out _);
        }

        await RefreshGroupPresenceAsync(nearby, connected, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Demande, pour chaque joueur détecté, s'il est membre de l'un de nos groupes.
    /// </summary>
    /// <remarks>
    /// Seulement les joueurs détectés : un passant sans le plugin n'a pas de
    /// boîte de groupe, et l'interroger ne ferait que coûter au service. Un
    /// joueur qui a coupé la détection n'est donc pas trouvé par ses groupes,
    /// ce que l'interface devra dire.
    /// </remarks>
    private async Task RefreshGroupPresenceAsync(
        IReadOnlyList<NearbyPlayer> nearby, List<Session> connected, CancellationToken ct)
    {
        var groups = _groups;
        var detected = nearby.Where(player => _detected.ContainsKey(player.Fingerprint)).ToList();

        if (groups.Count == 0 || detected.Count == 0)
        {
            _groupPresence.Clear();
            return;
        }

        var questions = detected
            .SelectMany(player => groups.Select(group => (
                player.Fingerprint,
                Group: group.Id,
                group.Rendezvous,
                Address: GroupDerivation.PresenceAddress(group.Secret, player.Fingerprint, _clock.UtcNow).ToBytes())))
            .ToList();

        var found = new HashSet<(PlayerFingerprint Member, GroupId Group)>();
        var answered = false;

        foreach (var session in connected)
        {
            // Un service de groupe n'entend parler que de ses groupes : les
            // adresses des autres lui apprendraient au moins combien nous en avons.
            var asked = session.Settings
                ? questions
                : [.. questions.Where(question => question.Rendezvous.Contains(session.At))];

            foreach (var batch in asked.Chunk(RendezvousWire.MaxQueriedAddresses))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));

                    var present = await session.Client!
                        .QueryPresenceAsync([.. batch.Select(question => question.Address)], deadline.Token)
                        .ConfigureAwait(false);

                    if (present is null)
                        break;

                    answered = true;

                    for (var i = 0; i < batch.Length && i < present.Length; i++)
                        if (present[i])
                            found.Add((batch[i].Fingerprint, batch[i].Group));
                }
                catch (Exception e)
                {
                    _log.Warning(e, $"Interrogation des groupes en échec sur {session.At}.");
                    break;
                }
            }
        }

        // Même règle que la détection : sans réponse, on ne retire personne.
        if (answered is false)
            return;

        _groupPresence.Clear();

        foreach (var member in found.GroupBy(pair => pair.Member))
            _groupPresence[member.Key] = [.. member.Select(pair => pair.Group)];
    }

    /// <summary>Dépose une demande de pairage dans la boîte d'un joueur.</summary>
    public async Task<string> RequestPairAsync(NearbyPlayer target, NearbyPlayer self, CancellationToken ct)
    {
        if (ConnectedToSettings is false)
            return "aucun service Linkpearl disponible.";

        if (_identity() is not { } identity)
            return NoCharacter;

        var nonce = RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength);
        var ephemeral = CryptoPrimitives.GenerateEphemeral();

        var message = new PairRequestMessage(
            IsAccept: false, identity.PublicKey, nonce, CryptoPrimitives.ExportPublicPoint(ephemeral),
            self.Name, self.WorldId);

        var address = MailboxAddress.Of(target.Fingerprint, _clock.UtcNow).ToBytes();
        var (delivered, failure) = await DepositEverywhereAsync(address, message.Encode(), ct).ConfigureAwait(false);

        if (delivered == 0)
        {
            ephemeral.Dispose();
            return $"envoi impossible : {failure}";
        }

        // Gardé en mémoire seulement, jusqu'à la réponse : c'est la moitié
        // privée de l'accord qui donnera le secret de paire. Une nouvelle
        // demande à la même personne remplace l'ancienne, dont la réponse ne
        // pourra plus conclure.
        _outgoing.Add(target.Fingerprint, nonce, ephemeral);
        return $"demande envoyée à {target.Name}.";
    }

    /// <summary>Vrai si au moins un service de nos réglages répond.</summary>
    private bool ConnectedToSettings
    {
        get
        {
            lock (_gate)
                return _sessions.Values.Any(session => session.Client is not null && session.Settings);
        }
    }

    /// <summary>
    /// Dépose sur tous les services de nos réglages à la fois.
    /// </summary>
    /// <remarks>
    /// Nous ignorons lequel la cible utilise : sa boîte vit chez le service
    /// qu'elle a choisi, pas chez nous. Déposer partout est donc la seule façon
    /// de l'atteindre, et le destinataire dédoublonne à la réception.
    ///
    /// Partout, mais jamais chez un service connu par la seule politique d'un
    /// groupe : une demande de pairage porte notre nom, notre clé et le nom de
    /// la cible, que ce service n'a pas à apprendre.
    /// </remarks>
    private async Task<(int Delivered, string? Failure)> DepositEverywhereAsync(
        byte[] address, byte[] payload, CancellationToken ct)
    {
        var delivered = 0;
        string? failure = "aucun service des réglages n'est disponible";

        foreach (var session in Snapshot())
        {
            if (session.Client is null || session.Settings is false)
                continue;

            try
            {
                await session.Client.DepositAsync(address, payload, ct).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception e)
            {
                failure = Describe(e);
                _log.Warning(e, $"Dépôt en échec sur {session.At}.");
            }
        }

        return (delivered, failure);
    }

    /// <summary>
    /// Dépose sur les seuls services désignés.
    /// </summary>
    /// <remarks>
    /// L'admission sait où déposer : le service du code côté candidat, ceux
    /// du groupe côté membre. Déposer partout ferait apprendre le code, ou le
    /// nom du candidat, à des services qui n'ont rien à voir avec le groupe.
    /// </remarks>
    private async Task<(int Delivered, string? Failure)> DepositOnAsync(
        IReadOnlyList<RendezvousAddress> via, byte[] address, byte[] payload, CancellationToken ct)
    {
        // Le service ferme la connexion sur un dépôt trop long : mieux vaut
        // perdre ce seul message que la présence entière.
        if (payload.Length > RendezvousWire.MaxDepositLength)
        {
            _log.Warning($"Dépôt de {payload.Length} octets refusé, au-delà de {RendezvousWire.MaxDepositLength}.");
            return (0, "message trop long pour le service");
        }

        var delivered = 0;
        var failure = "aucun service du groupe n'est disponible";

        foreach (var session in Snapshot())
        {
            if (session.Client is not { } client || via.Contains(session.At) is false)
                continue;

            try
            {
                await client.DepositAsync(address, payload, ct).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception e)
            {
                failure = Describe(e);
                _log.Warning(e, $"Dépôt d'admission en échec sur {session.At}.");
            }
        }

        return (delivered, delivered > 0 ? null : failure);
    }

    /// <summary>Dépose la réponse d'un membre dans la boîte personnelle du candidat.</summary>
    /// <remarks>Utilisé par l'interface pour Accepter et Refuser, et par le fil d'écoute pour les défis.</remarks>
    public async Task<string> AnswerAsync(AdmissionOutbound outbound, CancellationToken ct)
    {
        var candidate = PlayerFingerprint.Of(DalamudObjectSource.Normalize(outbound.CharacterName), outbound.WorldId);
        var address = MailboxAddress.Of(candidate, _clock.UtcNow).ToBytes();

        var (delivered, failure) = await DepositOnAsync(outbound.Via, address, outbound.Payload, ct).ConfigureAwait(false);

        return delivered == 0 ? $"réponse impossible : {failure}" : "Réponse envoyée.";
    }

    /// <summary>Demande à rejoindre un groupe par son code.</summary>
    /// <returns>Le message à dire au joueur.</returns>
    public async Task<string> JoinGroupAsync(
        InvitationTicket code, RendezvousAddress service, string password, NearbyPlayer self, CancellationToken ct)
    {
        // Le groupe répond dans notre boîte personnelle, que seule la
        // détection tient ouverte : sans elle, la demande partirait et la
        // réponse ne trouverait personne.
        if (_configuration.Discoverable is false)
            return "Activer la visibilité dans les réglages pour recevoir la réponse du groupe.";

        if (_identity() is not { } identity)
            return NoCharacter;

        if (_candidate is not { } candidate)
            return "L'entrée dans le groupe n'est pas encore prête. Réessayer dans un instant.";

        var request = candidate.Start(code, service, password, identity.PublicKey, self.Name, self.WorldId);

        // Ouvre au besoin une session vers le service du code, que Reconcile
        // ajoute maintenant que la candidature attend.
        await EnsureOpenAsync(self.Fingerprint, ct).ConfigureAwait(false);

        var (delivered, failure) = await DepositOnAsync(
            [service], GroupDerivation.AdmissionAddress(code.ToBytes(), _clock.UtcNow).ToBytes(), request, ct)
            .ConfigureAwait(false);

        // La candidature reste en attente : le redépôt de chaque minute
        // réessaiera, et le service sera peut-être revenu d'ici là.
        if (delivered == 0)
            return $"Demande non envoyée ({failure}). Nouvel essai chaque minute.";

        // Rien de plus : à ce stade on ne connaît ni le mode d'admission ni qui
        // répondra, et une réponse arrive souvent dans la seconde.
        return "Demande envoyée au groupe.";
    }

    /// <summary>
    /// Accepte une demande reçue et renvoie notre identité au demandeur.
    /// </summary>
    /// <returns>
    /// Le message pour l'utilisateur, et la demande complétée du matériau de
    /// pairage, que le carnet attend. Absente si rien n'a pu être accordé.
    /// </returns>
    public async Task<(string Message, IncomingRequest? Agreed)> AcceptAsync(
        IncomingRequest request, NearbyPlayer self, CancellationToken ct)
    {
        if (ConnectedToSettings is false)
            return ("aucun service Linkpearl disponible.", null);

        if (_identity() is not { } identity)
            return (NoCharacter, null);

        using var ephemeral = CryptoPrimitives.GenerateEphemeral();
        var agreed = request with
        {
            PairingMaterial = PairRequestMessage.AgreeOnPairing(ephemeral, request.Ephemeral, request.PairingNonce),
        };

        var reply = new PairRequestMessage(
            IsAccept: true, identity.PublicKey, request.PairingNonce, CryptoPrimitives.ExportPublicPoint(ephemeral),
            self.Name, self.WorldId);

        try
        {
            var (delivered, failure) = await DepositEverywhereAsync(
                MailboxAddress.Of(request.Sender, _clock.UtcNow).ToBytes(), reply.Encode(), ct)
                .ConfigureAwait(false);

            // Accordé même si la réponse n'est pas partie, comme avant : le
            // demandeur, lui, ne conclura pas, et il redemandera.
            if (delivered == 0)
                return ($"réponse impossible : {failure}", agreed);

            return ($"{Glyphs.Safe(request.CharacterName)} accepté.", agreed);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Réponse d'acceptation en échec.");
            return ("Réponse impossible. Vérifier la connexion aux services Linkpearl, puis réessayer.", agreed);
        }
    }

    /// <summary>
    /// Aiguille un dépôt reçu selon son premier octet.
    /// </summary>
    /// <remarks>
    /// Tourne sur le fil d'écoute : une exception ici tuerait l'écoute, donc
    /// la présence, pour une seule trame hostile. D'où la garde englobante.
    /// </remarks>
    private void OnDelivered(byte[] payload)
    {
        try
        {
            switch (payload)
            {
                // Les types de PairRequestMessage, versions 1 et 2.
                case [>= 0x01 and <= 0x04, ..]:
                    OnPairMessage(payload);
                    break;

                case [var kind, ..] when AdmissionKind.IsAdmission(kind):
                    OnAdmissionMessage(payload);
                    break;

                default:
                    _log.Debug($"Dépôt de type inconnu reçu ({payload.Length} octets).");
                    break;
            }
        }
        catch (Exception e)
        {
            _log.Warning(e, "Dépôt reçu impossible à traiter.");
        }
    }

    /// <summary>Traite un message d'admission. Aucun nom de personnage au journal.</summary>
    private void OnAdmissionMessage(byte[] payload)
    {
        if (AdmissionCodec.TryDecode(payload, out var message, out var why) is false)
        {
            _log.Debug($"Message d'admission illisible : {why}");
            return;
        }

        var host = _host;
        var candidate = _candidate;

        switch (message)
        {
            case AdmissionRequest request when host is not null:
                // L'adaptateur, et lui seul, tire l'empreinte du nom : l'hôte
                // s'en sert pour les bannissements par personnage.
                var fingerprint = PlayerFingerprint.Of(DalamudObjectSource.Normalize(request.CharacterName), request.WorldId);
                SendAnswers(host.OnRequest(request, fingerprint));
                break;

            case AdmissionProof proof when host is not null:
                SendAnswers(host.OnProof(proof));
                break;

            case AdmissionChallenge challenge when candidate is not null:
                OnChallenge(candidate, challenge);
                break;

            case AdmissionWelcome welcome when candidate is not null:
                if (candidate.OnWelcome(welcome))
                    _log.Information("Admission : bienvenue reçue.");

                break;

            case AdmissionRefusal refusal when candidate is not null:
                candidate.OnRefusal(refusal);
                break;
        }
    }

    /// <summary>Répond à un défi par la preuve, déposée dans la boîte d'admission du code.</summary>
    private void OnChallenge(AdmissionCandidate candidate, AdmissionChallenge challenge)
    {
        var code = candidate.Code;
        var service = candidate.Service;

        if (candidate.OnChallenge(challenge) is not { } proof)
        {
            if (candidate.State is CandidacyState.NeedsPassword)
                _log.Information("Admission : le groupe demande un mot de passe.");

            return;
        }

        if (code is not { } ticket || service is not { } at)
            return;

        var address = GroupDerivation.AdmissionAddress(ticket.ToBytes(), _clock.UtcNow).ToBytes();
        Detach(() => DepositOnAsync([at], address, proof, CancellationToken.None), "Envoi de la demande d'entrée");
    }

    /// <summary>Dépose les réponses de l'hôte, hors du fil d'écoute, dans la limite du plafond.</summary>
    private void SendAnswers(IReadOnlyList<AdmissionOutbound> answers)
    {
        foreach (var answer in answers)
        {
            if (TakeAnswerSlot() is false)
            {
                _log.Debug("Réponse d'admission jetée : plafond par minute atteint.");
                continue;
            }

            Detach(() => AnswerAsync(answer, CancellationToken.None), "Réponse à la demande d'entrée");
        }
    }

    /// <summary>Réserve une place parmi les réponses de la dernière minute.</summary>
    private bool TakeAnswerSlot()
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            while (_admissionAnswers.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
                _admissionAnswers.Dequeue();

            if (_admissionAnswers.Count >= MaxAdmissionAnswersPerMinute)
                return false;

            _admissionAnswers.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Lance un dépôt sans bloquer le fil d'écoute.
    /// </summary>
    /// <remarks>
    /// Attendre ici retiendrait les trames suivantes, dont la réponse d'une
    /// interrogation de présence en cours, le temps d'un aller-retour réseau.
    /// </remarks>
    private void Detach(Func<Task> deposit, string what)
        => _ = Task.Run(async () =>
        {
            try
            {
                await deposit().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Warning(e, $"{what} en échec.");
            }
        });

    /// <summary>Range une demande de pairage reçue, ou met une acceptation de côté. Aucun nom au journal.</summary>
    private void OnPairMessage(byte[] payload)
    {
        // Au niveau de détail seulement : n'importe qui peut déposer, et un
        // avertissement par dépôt hostile remplirait le journal.
        if (PairRequestMessage.TryDecode(payload, out var message, out var why) is false)
        {
            _log.Debug($"Demande illisible reçue : {why}");
            return;
        }

        var id = PeerId.Of(message!.PublicKey);

        if (_identity() is not { } identity)
            return;   // déconnecté entre-temps : plus personne à qui remettre ceci

        if (id == identity.Id)
            return;   // notre propre écho, sans intérêt

        var request = new IncomingRequest(
            id, message.PublicKey, message.PairingNonce, message.CharacterName, message.WorldId, _clock.UtcNow,
            message.Ephemeral);

        var sender = request.Sender;

        // Une acceptation n'est pas une demande : c'est l'autre qui dit oui à
        // ce que nous avons proposé. Elle n'entre dans la file que si elle
        // porte l'aléa d'une demande à nous, et c'est la boucle du plugin qui
        // tranche, parce qu'il faut savoir si l'expéditeur est devant nous.
        // Une acceptation sans demande, souvent après un rechargement qui a
        // perdu notre moitié de l'accord, ne peut rien conclure : en faire une
        // demande ferait pairer deux carnets aux secrets différents.
        if (message.IsAccept)
        {
            if (_outgoing.Concerns(sender, message.PairingNonce) is false)
            {
                _log.Debug($"Acceptation sans demande en attente ({request.Tag}) : ignorée.");
                return;
            }

            if (_inbox.Witness(id, message.PairingNonce) is false)
                return;

            if (_accepted.Count >= MaxQueuedAcceptances)
            {
                _log.Warning($"Acceptation jetée ({request.Tag}) : trop d'acceptations en attente.");
                return;
            }

            _accepted.Enqueue(request);
            return;
        }

        // Un expéditeur qui dépose sur plusieurs services ne doit produire
        // qu'une seule invite : la même demande nous arrive alors par autant de
        // chemins que nous partageons de services avec lui.
        if (_inbox.Witness(id, message.PairingNonce) is false)
            return;

        // Bloqué au carnet : on l'a écarté, il n'a plus à nous solliciter.
        if (_blocked?.Invoke(id, sender) is true)
        {
            _log.Information($"Demande de pairage ignorée ({request.Tag}) : expéditeur bloqué au carnet.");
            return;
        }

        // Listé par un service actif : la demande est ignorée, comme le veut la
        // spec. Screen dérive ce qui manque, dans son budget ; au-delà, le
        // verdict reste en attente et la demande est ignorée aussi.
        if (_bans?.Screen(sender, (salt, parameters) => BanList.Derive(message.CharacterName, message.WorldId, salt, parameters))
                .Verdict is BanVerdict.Listed or BanVerdict.Pending)
        {
            _log.Information("Demande de pairage ignorée : expéditeur listé par un service, ou vérification impossible pour l'instant.");
            return;
        }

        switch (_inbox.Offer(id, sender, request))
        {
            case InboxOutcome.Full:
                _log.Information(
                    $"Demande de pairage jetée ({request.Tag}) : déjà {PairRequestInbox<IncomingRequest>.Capacity} en attente.");
                break;

            case InboxOutcome.Replaced:
                _log.Information($"Demande de pairage reçue ({request.Tag}), à la place de la précédente du même expéditeur.");
                break;

            default:
                _log.Information($"Demande de pairage reçue ({request.Tag}).");
                break;
        }
    }

    private async Task ListenAsync(Session session, RendezvousClient client, CancellationToken ct)
    {
        try
        {
            await client.ListenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (ct.IsCancellationRequested is false)
        {
            lock (_gate)
                session.Failure = Describe(e);

            _log.Warning(e, $"Écoute interrompue sur {session.At}.");
        }
        finally
        {
            if (ct.IsCancellationRequested is false)
                Close(session);
        }
    }

    private List<Session> Snapshot()
    {
        lock (_gate)
            return [.. _sessions.Values];
    }

    private void Close(Session session)
    {
        lock (_gate)
            CloseLocked(session);
    }

    private void CloseLocked(Session session)
    {
        session.Life?.Cancel();
        session.Life?.Dispose();
        session.Life = null;

        if (session.Client is not null)
        {
            session.Client.Delivered -= OnDelivered;
            _ = session.Client.DisposeAsync();
            session.Client = null;
        }

        session.OpenedFor = null;
    }

    private void CloseAll()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
                CloseLocked(session);

            _sessions.Clear();
        }

        // Plus personne n'est joignable : garder les détections ferait croire
        // que des joueurs utilisent le plugin alors que plus rien ne le dit.
        _detected.Clear();
    }

    public void Dispose()
    {
        CloseAll();
        _outgoing.Dispose();
    }
}
