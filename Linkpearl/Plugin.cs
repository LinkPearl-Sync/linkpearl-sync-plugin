using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Integration;
using Linkpearl.Integration.Extras;
using Linkpearl.Ui;
using Linkpearl.Ui.Onboarding;
using Linkpearl.Ui.Pages;
using System.Reflection;
using System.Security.Cryptography;

namespace Linkpearl;

/// <summary>
/// Point d'entrée du plugin.
/// </summary>
/// <remarks>
/// Le plugin n'est qu'une coquille autour de <c>Core</c> : il fournit les
/// adaptateurs Dalamud et l'interface, et ne porte aucune logique. Tout ce qui
/// décide se teste sous Linux, sans le jeu.
/// </remarks>
public sealed class Plugin : IDalamudPlugin
{
    /// <summary>
    /// Ouvre la fenêtre, rien d'autre : tout le reste passe par l'interface.
    /// </summary>
    /// <remarks>
    /// Le jeu intercepte « /linkpearl » avant Dalamud : c'est une commande de
    /// chat native. Toute commande choisie ici doit être vérifiée en jeu.
    /// </remarks>
    private const string Command = "/lpearl";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager         Commands        { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IChatGui                Chat            { get; private set; } = null!;
    [PluginService] internal static IObjectTable            Objects         { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static IPlayerState            PlayerState     { get; private set; } = null!;
    [PluginService] internal static IContextMenu            ContextMenu     { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IDtrBar                 DtrBar          { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;
    [PluginService] internal static INamePlateGui           NamePlates      { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static INotificationManager    Notifications   { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        Textures        { get; private set; } = null!;

    private readonly PenumbraIpc _penumbra;
    private readonly GlamourerIpc _glamourer;
    private readonly SelfLoop _selfLoop;

    /// <summary>
    /// Décide quand le cache existe. L'apparence locale et l'applicateur
    /// reçoivent son magasin commutable, le moteur n'existe que cache ouvert.
    /// </summary>
    private readonly CacheKeeper _cacheKeeper;

    /// <summary>Compte les tics, pour sonder le dossier du cache toutes les cinq secondes.</summary>
    private int _syncTicks;

    private readonly PeerLinkFactory _links;
    private readonly LocalAppearance _appearance;
    private readonly RemoteApplicator _applicator;
    /// <summary>
    /// Le moteur n'existe qu'une fois un personnage connecté.
    /// </summary>
    /// <remarks>
    /// Il porte la clé d'identité, qui appartient au personnage : la construire
    /// à l'écran-titre reviendrait à en inventer une pour personne, que le
    /// premier connecté hériterait.
    /// </remarks>
    private SyncEngine? _engine;

    private ulong _character;

    /// <summary>Vrai pendant qu'une restauration réécrit les dossiers des personnages.</summary>
    /// <remarks>
    /// Le suivi du personnage se tait pendant ce temps : recharger le carnet au
    /// milieu, ou pire le réécrire, mélangerait l'ancien et le restauré.
    /// </remarks>
    private volatile bool _restoring;

    private readonly BackupState _backupState = new();
    private readonly string _root;
    private readonly string _legacyRoot;
    private readonly PairingService _pairing;
    private readonly PresenceService _presence;
    private readonly GroupBook _groups;
    private readonly GroupDialPlanner _groupPlanner;

    /// <summary>Les listes de bannissement des services actifs, appliquées par empreinte.</summary>
    private readonly ServiceBanBook _serviceBans;

    /// <summary>Télécharge ces listes, à la connexion puis toutes les heures.</summary>
    private readonly ServiceBanFetcher _banFetcher;

    /// <summary>La liste signée du cercle ouvert, et le droit d'y passer.</summary>
    private readonly OpenCircle _openCircle;

    /// <summary>La redemande à l'autorité, toutes les six heures.</summary>
    private readonly ConsensusFetcher _consensusFetcher;
    private readonly RelayLatencies _relayLatencies;
    private readonly RelayLatencyLoop _relayLoop;
    private DateTimeOffset _lastRelayTargets = DateTimeOffset.MinValue;

    /// <summary>Dérive, sur le pool, ce qu'il faut pour vérifier les joueurs visibles.</summary>
    private readonly ServiceBanScreening _banScreening;

    /// <summary>Les services actifs vus à la ronde précédente, pour retélécharger quand ils changent.</summary>
    private string _lastServices = string.Empty;

    /// <summary>L'interrupteur « Réseau ouvert » au tick précédent, pour relire la liste quand on le rallume.</summary>
    private bool _lastOpenCircle = true;

    /// <summary>Le côté membre de l'admission : défis, validations en attente.</summary>
    private readonly AdmissionHost _admissionHost;

    /// <summary>Notre candidature en cours, une seule à la fois.</summary>
    private readonly AdmissionCandidate _candidate;

    /// <summary>Les gestes de la page Groupes, que le plugin exécute.</summary>
    private readonly GroupActions _groupActions;
    // Volatile : écrit par le thread du jeu au changement de personnage, lu par
    // le fil du handshake qui épingle un membre de groupe.
    private volatile GroupStore? _groupStore;
    private readonly DalamudObjectSource _objectSource;
    private readonly PluginState _state = new();
    private readonly DiscoveryState _discovery = new();

    /// <summary>
    /// Regroupe les signaux de changement d'apparence en une reconstruction.
    /// </summary>
    /// <remarks>
    /// Un changement de tenue produit une dizaine de redessins en quelques
    /// centaines de millisecondes. Le plafond existe pour que quelqu'un qui
    /// bricole son apparence dix minutes finisse quand même par être annoncé.
    /// </remarks>
    private readonly Debouncer _appearanceChanged =
        new(new SystemClock(), TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(5));
    private readonly WindowSystem _windows = new("Linkpearl");
    private readonly MainWindow _window;
    /// <summary>
    /// Assignée plus loin dans le constructeur, après <see cref="_window"/> :
    /// initialisée à <c>null!</c> parce qu'une fermeture capturée plus haut
    /// (le bouchon de <see cref="MainWindow"/> qui la rouvre) la référence
    /// avant cette affectation, sans jamais l'appeler avant elle.
    /// </summary>
    private readonly OnboardingWindow _onboarding = null!;

    /// <summary>L'état des dépendances, relevé par IPC hors du dessin.</summary>
    private volatile bool _penumbraReady;
    private volatile bool _glamourerReady;
    private long _prerequisitesDueAt;

    private readonly StatusBarEntry _statusBar;
    private readonly TransferOverlay _overlay;

    /// <summary>La fenêtre qui rejoint ou crée un groupe, ouverte depuis la page Groupes.</summary>
    private readonly GroupEntryWindow _groupEntry;
    private readonly NameplateGlyphs _nameplates;
    private readonly ExtrasIpc _extras;
    private readonly TransientCapture _transients;
    private long _statusBarDueAt;
    private long _nameplatesDueAt;
    private readonly Configuration _configuration;
    private readonly SystemClock _clock = new();
    private SyncEngineSettings _engineSettings = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Les empreintes visibles à la ronde précédente.</summary>
    /// <remarks>
    /// La détection n'interroge les services que lorsque cet ensemble change :
    /// redemander toutes les quinze secondes pour les mêmes personnes est du
    /// trafic pur, multiplié par le nombre de services.
    /// </remarks>
    private HashSet<Linkpearl.Core.Abstractions.PlayerFingerprint> _lastVisible = [];

    /// <summary>Quand la dernière interrogation a eu lieu.</summary>
    private DateTimeOffset _lastDetection = DateTimeOffset.MinValue;

    public Plugin()
    {
        // Tout au début, avant GetPluginConfig() : Dalamud range configuration
        // et identités sous l'InternalName, donc encore sous l'ancien nom tant
        // que ceci n'a pas couru une première fois après le renommage.
        AdoptLegacyConfiguration();

        var penumbra  = _penumbra  = new PenumbraIpc(PluginInterface);
        var glamourer = _glamourer = new GlamourerIpc(PluginInterface);
        _selfLoop = new SelfLoop(penumbra, glamourer, Log);

        _configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Un réglage d'avant la fédération devient une liste d'une entrée. Rien
        // n'est demandé à l'utilisateur, et rien n'est écrasé s'il a déjà choisi.
        _configuration.MigrateIfNeeded();

        // Le dossier que Dalamud attribue au plugin, et non un chemin de notre
        // invention : c'est là qu'une sauvegarde, une désinstallation ou un
        // utilisateur curieux iront chercher ce qui appartient à Linkpearl.
        var root = _root = PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(root);

        // Le cache reste ailleurs : plusieurs gigaoctets qui se régénèrent n'ont
        // rien à faire dans un profil itinérant. C'est aussi l'emplacement
        // d'avant, donc les caches déjà constitués restent utilisables.
        _legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linkpearl");

        // Le témoin des collections posées suit le plugin dans son dossier.
        // Il est repris de l'ancien emplacement, sans quoi les collections
        // laissées par la session d'avant ne seraient plus retirables.
        AdoptLeftoverMarker();

        var clock = _clock;
        _pairing = new PairingService(_configuration, clock, Log);
        _presence = new PresenceService(_configuration, () => _pairing.Identity, clock, Log);
        _objectSource = new DalamudObjectSource(Objects, ClientState, Framework);

        _groups = new GroupBook(clock);
        _groupPlanner = new GroupDialPlanner(clock);

        // Les deux côtés de l'admission vivent aussi longtemps que le plugin :
        // ils ne portent pas l'identité, ils la demandent au moment d'agir, et
        // un changement de personnage vide le carnet qu'ils consultent.
        _serviceBans = new ServiceBanBook(clock);
        _banFetcher = new ServiceBanFetcher(_configuration, _serviceBans, Log);
        _openCircle = new OpenCircle(ConsensusKeys.Trusted, clock) { Enabled = _configuration.OpenCircle };
        _consensusFetcher = new ConsensusFetcher(_openCircle, Path.Combine(root, "consensus.bin"), Log);
        _relayLatencies = new RelayLatencies(
            clock, (place, timeout, ct) => RelayPing.PingAsync(place, ServiceConsensus.IsPublicAddress, timeout, ct));
        _relayLoop = new RelayLatencyLoop(_relayLatencies, Log);
        _banScreening = new ServiceBanScreening(_serviceBans, Log);
        _presence.SetServiceBans(_serviceBans);

        // Un candidat listé par un service actif n'est pas admis. La dérivation
        // se fait à la demande, dans le budget du livre : voir ServiceBanBook.
        _admissionHost = new AdmissionHost(
            _groups, () => _pairing.Identity?.PublicKey, clock,
            refuses: (request, print) => _serviceBans.Screen(
                    print, (salt, parameters) => BanList.Derive(request.CharacterName, request.WorldId, salt, parameters))
                .Verdict is not BanVerdict.Clear);
        _candidate = new AdmissionCandidate(clock);
        _presence.Attach(_admissionHost, _candidate);
        _groupActions = BuildGroupActions();

        // Un épinglage arrive d'un handshake, hors du thread du jeu : l'écriture
        // se fait là où il arrive, le stockage se protège seul. Un disque plein
        // ou un droit refusé ne doit pas faire échouer le handshake qui a
        // épinglé : l'épinglage reste en mémoire, et le prochain changement
        // retentera l'écriture.
        _groups.Changed += () =>
        {
            try
            {
                _groupStore?.Save(_groups);
            }
            catch (Exception e)
            {
                // Le type seul : le message d'une erreur d'entrée-sortie porte
                // le chemin complet du profil, qui n'a rien à faire au journal.
                Log.Warning($"Enregistrement des groupes en échec ({e.GetType().Name}).");
            }
        };

        _groups.PolicyAdopted += OnPolicyAdopted;

        // Le moteur et ce qu'il lui faut. Une seule socket pour tous les pairs :
        // c'est son adresse publique que le rendez-vous rend, donc elle seule
        // qui aura percé le NAT.
        var engineSettings = new SyncEngineSettings
        {
            LimitUpload = _configuration.LimitUpload,
            Receive = new TransientCategories(
                _configuration.ReceiveAnimations, _configuration.ReceiveVfx, _configuration.ReceiveSounds),
            Limiter = new RateLimiterSettings
            {
                CeilingBytesPerSecond = _configuration.UploadCeilingBytesPerSecond,
            },
        };

        _cacheKeeper = new CacheKeeper(
            _configuration,
            Path.Combine(_legacyRoot, "cache"),
            clock,
            path => new DriveInfo(Path.GetPathRoot(path) ?? "/").AvailableFreeSpace,
            new PluginLogSink(Log, "cache"));

        _links = new PeerLinkFactory(engineSettings.DataChannels + 1, new PluginLogSink(Log, "transport"));
        _extras = new ExtrasIpc(PluginInterface, Objects, Log);
        _transients = new TransientCapture(penumbra, Framework, Objects, clock, Log);
        _appearance = new LocalAppearance(
            penumbra, glamourer, Framework, Objects, _extras, _transients, MoodlesKey, _cacheKeeper.Store, Log);

        // Une animation moddée jouée pour la première fois : elle rejoint
        // l'apparence annoncée, par le même anti-rebond.
        _transients.Discovered += _appearanceChanged.Signal;

        // Tout changement de mod affectant le personnage produit un redessin, et
        // Glamourer signale chaque changement d'état. Les deux sont levés
        // depuis le thread du jeu : on ne fait que signaler, la reconstruction
        // part de la boucle de synchronisation.
        penumbra.SettingsChanged += _appearanceChanged.Signal;

        penumbra.Redrawn += index =>
        {
            if (index == 0)
                _appearanceChanged.Signal();
        };

        glamourer.Changed += address =>
        {
            if (address == Objects.LocalPlayer?.Address)
                _appearanceChanged.Signal();
        };

        // Les plugins voisins signalent eux-mêmes nos changements : titre,
        // statuts, proportions. La rafale passe par le même anti-rebond.
        _extras.Changed += _appearanceChanged.Signal;

        // Honorific, Moodles ou PetNicknames qui redémarrent ont oublié ce
        // que nous avions posé chez eux : on repose chaque pair affiché.
        _extras.Ready += () =>
        {
            foreach (var status in _engine?.Statuses ?? [])
            {
                if (status.Applied)
                    _engine?.Reapply(status.Peer);
            }
        };

        _applicator = new RemoteApplicator(
            penumbra, glamourer, _extras, Framework, Objects, ClientState, Condition, _cacheKeeper.Store, Quotas.Default, root, Log);

        _engineSettings = engineSettings;

        // Le personnage est inconnu au chargement : le plugin démarre à
        // l'écran-titre. Tout ce qui porte l'identité attend donc la connexion.
        Framework.Update += FollowCharacter;

        // PollEvents depuis le thread du jeu, jamais avec UnsyncedEvents : les
        // trames reçues remontent ainsi sur le fil qui a le droit de toucher au
        // jeu, et la cadence est celle d'une image.
        Framework.Update += PollLinks;

        // Les polices avant la fenêtre : l'atlas est construit en tâche de
        // fond, et la fenêtre retombe sur celle de Dalamud tant qu'il ne l'est
        // pas, sans jamais rester vide.
        Fonts.Build(PluginInterface);
        Brand.Initialize(Textures.GetFromManifestResource(Assembly.GetExecutingAssembly(), "Images.logo.png"));

        _groupEntry = new GroupEntryWindow(_candidate, _groupActions);

        _window = new MainWindow(
            _pairing, _presence, _state, _configuration,
            () => _engine?.Statuses ?? [],
            _discovery,
            at => RunSafely(() => DiscoverAsync(at)),
            player => RunSafely(() => RequestPairAsync(player)),
            Accept,
            Decline,
            (id, paused) => _pairing.SetPaused(id, paused),
            id => _engine?.Reapply(id),
            id => _pairing.Remove(id),
            SetUploadLimited,
            () => GlobalReceive,
            SetGlobalReceive,
            SetPairReceive,
            _backupState,
            (path, password) => RunSafely(() => BackupAsync(path, password)),
            (path, password) => RunSafely(() => RestoreAsync(path, password)),
            _cacheKeeper,
            () => _onboarding.Show(),
            _groups,
            _candidate,
            _groupActions,
            () => _admissionHost.Pending,
            _groupEntry,
            _serviceBans,
            _openCircle);

        // Clic droit sur un personnage appairé : réappliquer, comme le font
        // les autres outils de synchronisation. C'est le geste que les joueurs
        // connaissent déjà.
        ContextMenu.OnMenuOpened += OnMenuOpened;

        _windows.AddWindow(_window);
        _windows.AddWindow(_groupEntry);
        _windows.AddWindow(new RequestToasts(
            _presence, _state, () => _window.ShowsRequests, _window.OpenRequests, Accept, Decline,
            () => _admissionHost.Pending, _groupActions.Approve, _groupActions.Decline));

        _onboarding = new OnboardingWindow(
            Textures.GetFromManifestResource(Assembly.GetExecutingAssembly(), "Images.banner.png"),
            new CacheChooser(_cacheKeeper),
            () => _penumbraReady,
            () => _glamourerReady,
            started: _window.OpenNearby,
            closed: OnOnboardingClosed);
        _windows.AddWindow(_onboarding);
        Framework.Update += UpdatePrerequisites;

        _statusBar = new StatusBarEntry(DtrBar, Open);
        Framework.Update += UpdateStatusBar;

        _overlay = new TransferOverlay(GameGui, Objects);
        Framework.Update += UpdateOverlay;
        PluginInterface.UiBuilder.Draw += _overlay.Draw;

        _nameplates = new NameplateGlyphs(NamePlates);
        Framework.Update += UpdateNameplates;

        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;

        _ = Task.Run(() => RefreshLoopAsync(_shutdown.Token), _shutdown.Token);
        _banFetcher.Start();
        _consensusFetcher.Start();
        _relayLoop.Start();
        _ = Task.Run(() => SyncLoopAsync(_shutdown.Token), _shutdown.Token);

        Commands.AddHandler(Command, new CommandInfo((_, _) => Open())
        {
            HelpMessage = "Ouvrir la fenêtre de Linkpearl.",
        });

        Log.Information($"Chargé. Penumbra : {Describe(penumbra.TryGetVersion())}, Glamourer : {Describe(glamourer.TryGetVersion())}.");

        // Rattrapage d'une session précédente : un rechargement ou un plantage a
        // pu laisser une collection affectée au personnage, que plus rien en
        // mémoire ne sait retirer.
        if (_selfLoop.HasLeftovers())
        {
            Framework.RunOnFrameworkThread(_selfLoop.Revert);
            Log.Information("Une collection d'une session précédente a été retirée.");
        }

        Framework.RunOnFrameworkThread(() =>
        {
            var left = _applicator.CleanLeftovers();

            if (left > 0)
                Log.Information($"{left} collection(s) de pair d'une session précédente ont été retirées.");
        });

        if (_configuration.OnboardingSeen is false)
            _onboarding.Show();

        // En dernier : l'ouverture peut lever Lost, qui ouvre la fenêtre, et la
        // fenêtre doit exister. La mesure de l'ancien cache suit l'ouverture,
        // voir OnCacheOpened.
        _cacheKeeper.Opened += OnCacheOpened;
        _cacheKeeper.Lost += OnCacheLost;
        _cacheKeeper.Start();
    }

    /// <summary>
    /// Ajoute au menu d'un personnage ce que Linkpearl peut faire avec lui :
    /// réappliquer s'il est pairé, le pairage s'il peut l'être.
    /// </summary>
    /// <remarks>
    /// Rien pour les autres : proposer l'entrée à tout le monde inviterait à
    /// cliquer pour rien, et dirait à qui regarde par-dessus l'épaule que le
    /// plugin est là.
    /// </remarks>
    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetObject: IPlayerCharacter player })
            return;

        var name = player.Name.TextValue;

        if (string.IsNullOrWhiteSpace(name))
            return;

        var world = (ushort)player.HomeWorld.RowId;
        var fingerprint = PlayerFingerprint.Of(DalamudObjectSource.Normalize(name), world);

        // L'empreinte annoncée compte autant que l'épinglée : un pair ajouté
        // avant que l'épinglage se fasse à l'acceptation n'a que la première.
        var paired = _pairing.Book.Listed.Any(pair => pair.PinnedFingerprint == fingerprint)
                  || (_engine?.Statuses ?? []).Any(status => status.View.Fingerprint == fingerprint);

        if (paired)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = "Linkpearl : réappliquer",
                PrefixChar = 'L',
                PrefixColor = 541,
                OnClicked = _ => _engine?.Reapply(fingerprint),
            });

            return;
        }

        if (PairingMenuItem(name, world, fingerprint) is { } item)
            args.AddMenuItem(item);
    }

    /// <summary>
    /// L'entrée de pairage, pour un joueur que la page « Autour de vous »
    /// listerait comme « à pairer ».
    /// </summary>
    /// <remarks>
    /// Si c'est lui qui a demandé, l'entrée accepte : lui renvoyer une demande
    /// croiserait la sienne au lieu de conclure.
    /// </remarks>
    private MenuItem? PairingMenuItem(string name, ushort world, PlayerFingerprint fingerprint)
    {
        var incoming = _presence.RequestCount == 0
            ? null
            : _presence.PeekRequests().FirstOrDefault(request =>
                  request.WorldId == world
               && string.Equals(request.CharacterName, name, StringComparison.OrdinalIgnoreCase));

        if (incoming is not null)
        {
            return new MenuItem
            {
                Name = "Linkpearl : accepter le pairage",
                PrefixChar = 'L',
                PrefixColor = 541,
                OnClicked = _ => Accept(incoming),
            };
        }

        // Seulement un joueur qui se signale : une demande déposée dans la
        // boîte de quelqu'un qui n'utilise pas le plugin n'arriverait jamais.
        if (_presence.Detected.ContainsKey(fingerprint) is false)
            return null;

        // L'instantané fournit le NearbyPlayer qu'attend la demande ; un joueur
        // tout juste arrivé n'y est pas encore, et l'entrée attend le suivant.
        if (_state.Nearby.FirstOrDefault(nearby => nearby.Fingerprint == fingerprint) is not { } target)
            return null;

        var sent = _presence.PendingOutgoing.ContainsKey(fingerprint);

        return new MenuItem
        {
            Name = sent ? "Linkpearl : renvoyer la demande de pairage" : "Linkpearl : demander le pairage",
            PrefixChar = 'L',
            PrefixColor = 541,
            OnClicked = _ => RunSafely(() => RequestPairAsync(target)),
        };
    }

    /// <summary>
    /// Reprend la configuration et les identités d'avant le renommage en
    /// LinkpearlSync, si elles sont manifestement à nous.
    /// </summary>
    /// <remarks>
    /// pluginConfigs/Linkpearl.json et pluginConfigs/Linkpearl/ sont aussi les
    /// emplacements du plugin officiel Linkpearl (NotNite) : LegacyConfigAdoption
    /// vérifie que c'est bien à nous avant de reprendre quoi que ce soit, et ne
    /// remplace ni n'efface jamais rien. Une reprise ratée ne doit pas empêcher
    /// le plugin de charger, d'où le journal qui ne garde que le type
    /// d'exception : IOException embarque souvent le chemin complet, qui porte
    /// le nom du compte Windows.
    /// </remarks>
    private void AdoptLegacyConfiguration()
    {
        try
        {
            var newConfigFile = PluginInterface.ConfigFile.FullName;
            var legacyConfigFile = Path.Combine(
                Path.GetDirectoryName(newConfigFile) ?? "", "Linkpearl.json");

            if (LegacyConfigAdoption.TryAdoptConfigFile(legacyConfigFile, newConfigFile))
                Log.Information("configuration d'avant le renommage reprise.");

            var newConfigDir = Path.TrimEndingDirectorySeparator(PluginInterface.ConfigDirectory.FullName);
            var legacyConfigDir = Path.Combine(Path.GetDirectoryName(newConfigDir) ?? "", "Linkpearl");

            if (LegacyConfigAdoption.TryAdoptDirectory(legacyConfigDir, newConfigDir))
                Log.Information("identités d'avant le renommage reprises.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"reprise d'avant le renommage impossible ({e.GetType().Name}).");
        }
    }

    /// <summary>Reprend le témoin des collections de l'emplacement précédent.</summary>
    private void AdoptLeftoverMarker()
    {
        const string marker = "remote-collections.txt";

        var from = Path.Combine(_legacyRoot, marker);
        var to = Path.Combine(_root, marker);

        try
        {
            if (File.Exists(from) && File.Exists(to) is false)
                File.Move(from, to);
        }
        catch (IOException e)
        {
            Log.Warning(e, "Reprise du témoin de collections impossible.");
        }
    }

    /// <summary>
    /// Suit le personnage connecté, et attache l'identité qui lui appartient.
    /// </summary>
    /// <remarks>
    /// Sur le changement de l'identifiant plutôt que sur les événements de
    /// connexion : le même test couvre l'arrivée à l'écran de sélection, le
    /// départ, et le passage d'un personnage à un autre sans déconnexion, qui
    /// ne lève pas les mêmes événements.
    /// </remarks>
    private void FollowCharacter(IFramework framework)
    {
        // L'identifiant de contenu du personnage connecté, que Dalamud expose
        // ici depuis qu'il a séparé l'état du joueur de celui du client.
        if (_restoring)
            return;

        var current = ClientState.IsLoggedIn ? PlayerState.ContentId : 0;

        if (current == _character)
            return;

        _character = current;

        try
        {
            ReleaseCharacter();

            if (current is not 0)
                TakeCharacter(current);
        }
        catch (Exception e)
        {
            // Une exception ici remonterait dans la boucle du jeu.
            Log.Error(e, "Changement de personnage en échec.");
            Report("Impossible de charger l'identité de ce personnage. Consulter le journal.");
        }
    }

    private void TakeCharacter(ulong contentId)
    {
        var root = CharacterStorage.Prepare(_root, contentId, _legacyRoot, message => Log.Information(message));

        _pairing.Bind(root);

        _groupStore = new GroupStore(Path.Combine(root, "groups.json"));
        _groupStore.Load(_groups);

        _transients.Attach(root);
        StartEngineIfReady();
    }

    /// <summary>
    /// Construit le moteur si tout ce qu'il lui faut est là : un personnage et
    /// un cache ouvert.
    /// </summary>
    /// <remarks>
    /// Appelé à la connexion et à l'ouverture du cache, dans n'importe quel
    /// ordre : le second appel est celui qui construit. Sur le thread du jeu.
    /// </remarks>
    private void StartEngineIfReady()
    {
        if (_engine is not null || _character is 0 || _pairing.Id is null
            || _cacheKeeper.State is not CacheGateState.Open)
            return;

        _engine = new SyncEngine(
            _pairing.Book,
            new PeerConnector(
                _links,
                new RendezvousEndpoint(_configuration.RendezvousHost, _configuration.RendezvousPort),
                _clock,
                new PluginLogSink(Log, "moteur"),
                circle: _openCircle,
                latencies: _relayLatencies),
            _appearance, _applicator, _cacheKeeper.Store, _pairing.Id!.Value, _pairing.Identity!.Key, _clock,
            new PluginLogSink(Log, "moteur"), _engineSettings, groups: _groups, policies: _groups, bans: _serviceBans);

        // Le moteur a déjà retiré l'entrée du carnet : il reste à l'écrire, et
        // à dire pourquoi une ligne vient de disparaître de la liste.
        _engine.PairEnded += pair =>
        {
            _pairing.Save();
            Report($"{pair.DisplayName} a mis fin au pairage.");
        };
        _engine.RevocationDelivered += _ => _pairing.Save();
        _engine.BookChanged += () => _pairing.Save();

        var pairs = _pairing.Book.Listed.Count;

        Log.Information(pairs is 0
            ? $"identité de ce personnage : {_pairing.Id}. Aucun pair pour l'instant."
            : $"identité de ce personnage : {_pairing.Id}. {pairs} pair(s) au carnet.");
    }

    private void ReleaseCharacter()
    {
        // Le moteur d'abord : il partage le même PairBook, et la boucle de
        // synchronisation doit voir _engine à null avant que le carnet ne
        // soit vidé par Unbind.
        StopEngine();
        _pairing.Unbind();

        _groupStore = null;
        _groups.Clear();
        _presence.SetGroups([]);

        _presence.ForgetRequests();

        // Une candidature ou une admission du personnage précédent qui
        // aboutirait maintenant rangerait le secret obtenu par l'un dans le
        // carnet de l'autre : c'est exactement ce qui relierait les deux.
        _candidate.Cancel();
        _admissionHost.Reset();

        // Sans quoi un bannissement adopté avant la prochaine ronde jugerait
        // le nouveau personnage sur l'empreinte de l'ancien.
        _state.Self = null;

        _transients.Attach(null);
    }

    /// <summary>
    /// Arrête le moteur, qui retire des pairs ce qu'il leur a posé.
    /// </summary>
    /// <remarks>
    /// Hors du thread du jeu : le retrait passe par RunOnFrameworkThread, et
    /// l'attendre depuis ce thread-là se bloquerait sur soi-même.
    /// </remarks>
    private void StopEngine()
    {
        var engine = _engine;
        _engine = null;

        if (engine is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Arrêt du moteur en échec.");
            }
        });
    }

    /// <summary>Le cache est ouvert : le moteur peut naître, et notre apparence y entrer.</summary>
    /// <remarks>
    /// Peut être levé depuis le pool de threads (un dossier rechoisi) : le
    /// moteur se construit sur le thread du jeu. L'apparence est recapturée
    /// parce que ses fichiers ne sont pas dans un cache neuf.
    /// </remarks>
    private void OnCacheOpened()
    {
        _appearanceChanged.Signal();
        Framework.RunOnFrameworkThread(StartEngineIfReady);

        // L'ancien cache se mesure en parcourant tout l'arbre : hors du
        // chargement. Après l'ouverture et non au constructeur : Choose
        // pendant la présentation peut fixer PreviousCacheDirectory, et la
        // mesure doit porter sur ce que l'ouverture vient de retenir.
        RunSafely(() => Task.Run(_cacheKeeper.MeasurePrevious));
    }

    /// <summary>
    /// Le dossier du cache a disparu : tout s'arrête, et on le dit.
    /// </summary>
    /// <remarks>
    /// Arrêter le moteur rend chaque pair à son apparence par défaut : ses mods
    /// temporaires pointent sur des fichiers qui n'existent plus.
    /// </remarks>
    private void OnCacheLost()
    {
        Framework.RunOnFrameworkThread(() =>
        {
            StopEngine();
            _window.IsOpen = true;

            Notifications.AddNotification(new Notification
            {
                Title = "Linkpearl",
                Content = "Le dossier du cache est introuvable. Synchronisation arrêtée "
                        + "jusqu'au choix d'un autre dossier.",
                Type = NotificationType.Error,
            });
        });
    }

    /// <summary>
    /// La présentation se ferme. La première fois, le cache peut enfin naître,
    /// dans le dossier choisi.
    /// </summary>
    /// <remarks>
    /// Sur le pool de threads : ouvrir le cache relit son index, voire tout
    /// son arbre.
    /// </remarks>
    private void OnOnboardingClosed()
    {
        if (_configuration.OnboardingSeen)
            return;

        RunSafely(() => Task.Run(_cacheKeeper.FinishOnboarding));
    }

    /// <summary>
    /// Relève Penumbra et Glamourer toutes les deux secondes, seulement tant
    /// que la présentation est ouverte.
    /// </summary>
    /// <remarks>
    /// Le dessin ne doit rien interroger : un appel IPC à chaque image, pour
    /// une réponse qui ne change qu'au chargement d'un plugin, serait du gâchis.
    /// </remarks>
    private void UpdatePrerequisites(IFramework framework)
    {
        if (_onboarding.IsOpen is false)
            return;

        var now = Environment.TickCount64;

        if (now < _prerequisitesDueAt)
            return;

        _prerequisitesDueAt = now + 2000;
        _penumbraReady = _penumbra.TryGetVersion() is not null;
        _glamourerReady = _glamourer.TryGetVersion() is not null;
    }

    private static string Describe((int Major, int Minor)? version)
        => version is { } v ? $"{v.Major}.{v.Minor}" : "absent";

    private void Open() => _window.IsOpen = true;

    /// <summary>
    /// Tient à jour ce que l'interface affiche.
    /// </summary>
    /// <remarks>
    /// En tâche de fond, à intervalle lâche : la détection interroge le
    /// rendez-vous, et le faire à chaque image en ferait un service de sondage.
    /// </remarks>
    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            try
            {
                if (_objectSource.IsLoggedIn)
                {
                    var self = await _objectSource.LocalAsync(ct).ConfigureAwait(false);
                    _state.Self = self;

                    if (self is not null)
                    {
                        // Avant SetGroups : le groupe tout juste rejoint ouvre
                        // ses boîtes dès cette ronde, et non quinze secondes
                        // plus tard.
                        TakeJoinedGroup();

                        // Les services du Public sont ceux de la configuration :
                        // ils suivent un service ajouté ou retiré sans rien réactiver.
                        _groups.SetPublicServices(ActiveServices());

                        _presence.SetGroups(_groups.All);
                        await _presence.EnsureOpenAsync(self.Fingerprint, ct).ConfigureAwait(false);

                        // L'autre a dit oui à notre demande : le pair entre au
                        // carnet sans rien redemander à celui qui a invité.
                        while (_presence.TryTakeAcceptance(out var accepted) && accepted is not null)
                            Report($"{accepted.CharacterName} a accepté : {_pairing.AddFromRequest(accepted)}");

                        _state.Nearby = await _objectSource.SnapshotAsync(ct).ConfigureAwait(false);

                        // Mesuré : 345 Mo par mois et par joueur à trois
                        // secondes, contre 20 à quinze et seulement quand le
                        // champ change. La fédération multiplie encore ce coût
                        // par le nombre de services, ce qui fait de cette
                        // cadence une nécessité et non un confort.
                        var visible = _state.Nearby.Select(player => player.Fingerprint).ToHashSet();

                        // Le changement de champ n'est pas un critère suffisant.
                        // Un « non » n'est vrai qu'à l'instant où il est donné :
                        // celui qui vient d'arriver n'a pas encore ouvert sa
                        // boîte, et sans ce rattrapage sa réponse négative tient
                        // pour toujours, alors que les deux sont là, connectés,
                        // et que rien ne bouge plus. C'est exactement ainsi que
                        // deux personnages côte à côte ne se sont jamais vus.
                        var stale = _clock.UtcNow - _lastDetection > TimeSpan.FromSeconds(60);

                        if (visible.SetEquals(_lastVisible) is false || stale)
                        {
                            _lastVisible = visible;
                            _lastDetection = _clock.UtcNow;

                            await _presence.RefreshDetectionAsync(_state.Nearby, ct).ConfigureAwait(false);
                        }

                        // Un service ajouté ou retiré : sa liste ne doit pas
                        // attendre l'heure pour compter, ou cesser de compter.
                        var services = string.Join('|', _configuration.ActiveRendezvous.Select(entry => entry.Address));

                        if (services != _lastServices)
                        {
                            _lastServices = services;
                            _banFetcher.RefreshSoon();
                        }

                        // Rallumé, le réseau ouvert repart d'une liste fraîche : celle
                        // gardée depuis le dernier démarrage peut dater de six heures.
                        var openCircle = _configuration.OpenCircle;

                        if (openCircle && _lastOpenCircle is false)
                            _consensusFetcher.RefreshSoon();

                        _lastOpenCircle = openCircle;

                        // Les services où chaque paire pourrait relayer, recalculés
                        // ici parce que c'est cette boucle qui lit déjà le carnet.
                        if (_clock.UtcNow - _lastRelayTargets >= RelayLatencyLoop.TargetsEvery)
                        {
                            _lastRelayTargets = _clock.UtcNow;
                            _relayLoop.Offer([.. _pairing.Book.Listed
                                .SelectMany(pair => RelayPlacement.Eligible(pair.PairSecret, _openCircle.RelayEntriesFor(pair), pair.Rendezvous))
                                .DistinctBy(place => place.Fingerprint)]);
                        }

                        // Les joueurs à vérifier : ceux qui ont le plugin, et les
                        // paires du carnet, dont l'apparence se pose sans détection.
                        var pinned = _pairing.Book.All
                            .Select(pair => pair.PinnedFingerprint)
                            .OfType<PlayerFingerprint>()
                            .ToHashSet();

                        _banScreening.Schedule([.. _state.Nearby.Where(player =>
                            _presence.Detected.ContainsKey(player.Fingerprint) || pinned.Contains(player.Fingerprint))]);

                        // À chaque ronde et non seulement quand le champ change :
                        // un membre sorti du champ doit finir par partir, et
                        // c'est le passage du temps qui l'y conduit.
                        var sightings = _state.Nearby
                            .SelectMany(player => _presence.GroupsOf(player.Fingerprint)
                                .Select(group => new GroupSighting(group, player.Fingerprint, player.Name)))
                            .ToList();

                        var directly = _pairing.Book.All
                            .Select(pair => pair.PinnedFingerprint)
                            .OfType<PlayerFingerprint>();

                        _engine?.SetGroupPeers(_groupPlanner.Plan(self.Fingerprint, sightings, _groups.All, directly, _serviceBans));
                    }
                }
                else
                {
                    _state.Nearby = [];
                    _lastVisible.Clear();
                }
            }
            catch (Exception e) when (ct.IsCancellationRequested is false)
            {
                Log.Warning(e, "Rafraîchissement en échec.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Demande à un service la liste de ceux qu'il connaît.
    /// </summary>
    /// <remarks>
    /// Le résultat n'est qu'une proposition : rien n'entre dans la liste de
    /// l'utilisateur sans qu'il coche une case. C'est ce qui empêche un annuaire
    /// de devenir une autorité.
    /// </remarks>
    private async Task DiscoverAsync(RendezvousAddress at)
    {
        _discovery.Reset();
        _discovery.From = at;
        _discovery.Running = true;

        try
        {
            await using var client = new RendezvousClient();
            await client.ConnectAsync(at.Host, at.Port, _shutdown.Token).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));

            var offered = await client.QueryDirectoryAsync(deadline.Token).ConfigureAwait(false);

            _discovery.Offered = offered ?? [];

            if (offered is null)
                _discovery.Failure = "ce service ne partage aucune liste de services.";
        }
        catch (Exception e)
        {
            Log.Warning($"Consultation de l'annuaire en échec ({e.GetType().Name}).");
            _discovery.Failure = "Impossible de consulter ce service. Vérifier son adresse et réessayer.";
        }
        finally
        {
            _discovery.Running = false;
        }
    }

    private void PollLinks(IFramework framework) => _links.Poll();

    /// <summary>
    /// Tient à jour le compte de la barre de statut.
    /// </summary>
    /// <remarks>
    /// Une fois par seconde : l'instantané des joueurs proches ne change pas
    /// plus vite, et la barre est un nœud du jeu qu'on ne touche que d'ici.
    /// </remarks>
    private void UpdateStatusBar(IFramework framework)
    {
        var now = Environment.TickCount64;

        if (now < _statusBarDueAt)
            return;

        _statusBarDueAt = now + 1000;
        _statusBar.Update(
            _state.Nearby, _pairing.Book.Listed, _presence.RequestCount + _admissionHost.Pending.Count,
            _cacheKeeper.State is CacheGateState.Missing);

        RemindBackup();
    }

    /// <summary>
    /// Rappelle, une fois, qu'une sauvegarde existe.
    /// </summary>
    /// <remarks>
    /// Au premier pair et pas avant : c'est à ce moment qu'il y a quelque chose
    /// à perdre, et une réinstallation de Windows obligerait sinon à refaire
    /// chaque pairage.
    /// </remarks>
    private void RemindBackup()
    {
        if (_configuration.BackupReminded || _pairing.Book.Listed.Count == 0)
            return;

        _configuration.BackupReminded = true;
        _configuration.Save();

        Report("Sauvegarder les personnages dans Réglages, « Sauvegarde » : "
             + "sans sauvegarde, une réinstallation de Windows oblige à refaire tous les pairages.");
    }

    private string CharactersRoot => Path.Combine(_root, "characters");

    /// <summary>
    /// La clé qui renomme nos GUID Moodles, propre au personnage connecté.
    /// </summary>
    /// <remarks>
    /// Dérivée de l'identité du personnage : deux personnages d'une même
    /// personne donnent des GUID qu'on ne peut pas relier.
    /// </remarks>
    private byte[]? MoodlesKey()
        => _pairing.Identity is { } identity
            ? MoodlesSanitizer.KeyFor(identity.Key.ExportParameters(includePrivateParameters: true).D!)
            : null;

    /// <summary>
    /// Place les badges de transfert, à chaque image : le personnage bouge, la
    /// caméra aussi, et un badge en retard d'une seconde flotterait à côté.
    /// </summary>
    private void UpdateOverlay(IFramework framework)
        => _overlay.Update(
            _configuration.ShowTransferBadges, _engine?.Statuses ?? [], _state.Nearby, _pairing.Book);

    /// <summary>
    /// Recalcule les glyphes des plaques de nom, quatre fois par seconde.
    /// </summary>
    /// <remarks>
    /// Pas à chaque image comme les badges : la plaque suit le personnage
    /// d'elle-même, et ce qu'elle affiche ne change qu'au rythme de l'instantané
    /// des joueurs visibles et des états du moteur.
    /// </remarks>
    private void UpdateNameplates(IFramework framework)
    {
        var now = Environment.TickCount64;

        if (now < _nameplatesDueAt)
            return;

        _nameplatesDueAt = now + 250;
        _nameplates.Update(
            _configuration.ShowNameplateGlyphs, _state.Nearby, _pairing.Book.Listed, _engine?.Statuses ?? [],
            [.. _presence.Detected.Keys],
            _presence.RequestCount == 0 ? [] : _presence.PeekRequests());
    }

    private TransientCategories GlobalReceive => _engineSettings.Receive;

    private void SetGlobalReceive(TransientCategories receive)
    {
        _configuration.ReceiveAnimations = receive.Animations;
        _configuration.ReceiveVfx = receive.Vfx;
        _configuration.ReceiveSounds = receive.Sounds;
        _configuration.Save();

        // Le moteur du personnage suivant naîtra avec le même réglage.
        _engineSettings = _engineSettings with { Receive = receive };
        _engine?.SetGlobalReceive(receive);
    }

    private void SetPairReceive(PeerId id, TransientCategories receive)
    {
        // Le moteur voit le changement au tic suivant et redemande le
        // manifeste : rien d'autre à faire ici.
        if (_pairing.SetReceive(id, receive) is { Length: > 0 } failure)
            Log.Warning($"Réglage de réception refusé : {failure}");
    }

    private void SetUploadLimited(bool limited)
    {
        _configuration.LimitUpload = limited;
        _configuration.Save();

        // Le moteur du personnage suivant naîtra avec le même réglage.
        _engineSettings = _engineSettings with { LimitUpload = limited };
        _engine?.SetUploadLimited(limited);
    }

    private async Task BackupAsync(string destination, string? password)
    {
        _backupState.Running = true;

        try
        {
            var (entries, unreadable) = await Task.Run(() => IdentityBackupService.Collect(CharactersRoot))
                .ConfigureAwait(false);

            if (entries.Count == 0)
            {
                Tell("Aucun personnage à sauvegarder : se connecter d'abord avec chaque personnage.", failed: true);
                return;
            }

            // PBKDF2 prend une demi-seconde : hors du thread du jeu.
            var content = await Task.Run(() => IdentityBackup.Write(entries, password)).ConfigureAwait(false);
            IdentityBackupService.WriteFile(destination, content);

            _configuration.BackupReminded = true;
            _configuration.BackedUp = true;
            _configuration.Save();

            var saved = entries.Count == 1 ? "1 personnage sauvegardé" : $"{entries.Count} personnages sauvegardés";
            var skipped = unreadable switch
            {
                0 => "",
                1 => ", 1 personnage illisible laissé de côté",
                _ => $", {unreadable} personnages illisibles laissés de côté",
            };

            // Le nom du fichier seulement : un chemin complet porte le nom du
            // compte Windows.
            Tell($"{saved}{skipped}, dans {Path.GetFileName(destination)}.", failed: unreadable > 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Écriture de la sauvegarde en échec ({e.GetType().Name}).");
            Tell("Sauvegarde impossible. Vérifier l'emplacement et les droits d'écriture.", failed: true);
        }
        finally
        {
            _backupState.Running = false;
        }
    }

    private async Task RestoreAsync(string source, string? password)
    {
        _backupState.Running = true;

        try
        {
            var file = new FileInfo(source);

            if (file.Exists is false || file.Length > 16 * 1024 * 1024)
            {
                Tell("Ce fichier n'est pas une sauvegarde Linkpearl.", failed: true);
                return;
            }

            var content = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
            var read = await Task.Run(() => IdentityBackup.Read(content, password)).ConfigureAwait(false);

            if (read.Failure is { } failure)
            {
                _backupState.AwaitingPassword = read.NeedsPassword ? source : null;

                // La première demande de mot de passe n'est pas un échec : le
                // fichier est simplement protégé.
                Tell(password is null && read.NeedsPassword ? null : failure, failed: true);
                return;
            }

            // Lâcher le personnage avant d'écrire : un carnet encore chargé
            // réécrirait l'ancien par-dessus le restauré.
            await Framework.RunOnFrameworkThread(() =>
            {
                _restoring = true;
                ReleaseCharacter();
            }).ConfigureAwait(false);

            (bool Restored, string Message) outcome;

            try
            {
                outcome = await Task.Run(() => IdentityBackupService.Restore(CharactersRoot, read.Entries))
                    .ConfigureAwait(false);
            }
            finally
            {
                // Zéro force le suivi à reprendre le personnage connecté à
                // l'image suivante, avec l'identité qu'on vient de poser.
                await Framework.RunOnFrameworkThread(() =>
                {
                    _character = 0;
                    _restoring = false;
                }).ConfigureAwait(false);
            }

            _backupState.AwaitingPassword = null;

            if (outcome.Restored)
            {
                _configuration.BackupReminded = true;
                _configuration.BackedUp = true;
                _configuration.Save();
            }

            Tell(outcome.Message, failed: outcome.Restored is false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Lecture de la sauvegarde en échec ({e.GetType().Name}).");
            Tell("Lecture impossible. Vérifier que le fichier existe et reste accessible.", failed: true);
        }
        finally
        {
            _backupState.Running = false;
        }
    }

    private void Tell(string? message, bool failed)
    {
        _backupState.Message = message;
        _backupState.Failed = failed;
    }

    /// <summary>
    /// Fait avancer le moteur.
    /// </summary>
    /// <remarks>
    /// Une seconde, et non une image : le tic ne transfère rien lui-même, les
    /// sessions vivent sur leurs propres tâches. Il décide seulement de joindre,
    /// de poser et de retirer, et une seconde de retard sur ces trois-là ne se
    /// voit pas. Le faire à chaque image ferait tourner pour rien une boucle qui
    /// parcourt tous les pairs.
    ///
    /// Il ne prend pas d'instantané de l'ObjectTable : il réutilise celui que la
    /// boucle d'interface a déjà pris, car chaque instantané part sur le thread
    /// du jeu.
    /// </remarks>
    private async Task SyncLoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            try
            {
                // Rien ne touche au cache tant qu'il n'est pas ouvert : la capture
                // de notre apparence y écrit. Le signal reste en attente, et la
                // capture part dès l'ouverture.
                if (_cacheKeeper.State is CacheGateState.Open)
                {
                    _appearance.Follow(_state.Self?.Fingerprint);

                    // L'anti-rebond n'est consommé qu'une fois le calme revenu, ou
                    // au plafond : c'est ce qui évite de rehacher pendant qu'on
                    // essaie dix tenues d'affilée.
                    if (_appearanceChanged.TryConsume())
                        _appearance.Rebuild();
                }
                else
                {
                    // Le cache est fermé (présentation en attente, ou dossier
                    // perdu) : on oublie ce qu'on suivait. Sans cela, un
                    // personnage inchangé à la réouverture verrait Follow(fp)
                    // rester muet, et le moteur neuf annoncerait un manifeste
                    // dont les blobs ne sont plus dans le nouveau cache.
                    _appearance.Forget();
                }

                // Le dossier du cache peut être supprimé pendant qu'on joue.
                if (++_syncTicks % 5 == 0)
                    _cacheKeeper.Check();

                var visible = _state.Nearby
                    .Select(player => new VisiblePlayer(player.Object, player.Fingerprint))
                    .ToList();

                // Sur le chemin de la perte, StopEngine ne fait que mettre
                // l'arrêt en file sur le thread du jeu : sans cette condition,
                // ce même tic pourrait encore appeler TickAsync pendant que
                // DisposeAsync du moteur tourne.
                if (_engine is { } engine && _cacheKeeper.State is CacheGateState.Open)
                    await engine.TickAsync(visible, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (ct.IsCancellationRequested is false)
            {
                Log.Warning(e, "Tic du moteur en échec.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    private async Task RequestPairAsync(NearbyPlayer target)
    {
        if (_state.Self is not { } self)
        {
            Report("Personnage introuvable.");
            return;
        }

        Report(await _presence.RequestPairAsync(target, self, _shutdown.Token).ConfigureAwait(false));
    }

    private void Accept(IncomingRequest request)
    {
        // Retirée avant tout await : la réponse part en tâche de fond, et un
        // second clic à l'image suivante accepterait sinon deux fois.
        _presence.Forget(request);
        RunSafely(() => AcceptAsync(request));
    }

    private async Task AcceptAsync(IncomingRequest request)
    {
        if (_state.Self is not { } self)
        {
            Report("Personnage introuvable.");
            return;
        }

        var (message, agreed) = await _presence.AcceptAsync(request, self, _shutdown.Token).ConfigureAwait(false);

        if (agreed is not null)
            Report(_pairing.AddFromRequest(agreed));

        Report(message);
    }

    private void Decline(IncomingRequest request) => _presence.Forget(request);

    /// <summary>Les gestes de la page Groupes.</summary>
    private GroupActions BuildGroupActions() => new()
    {
        Create = CreateGroup,
        Join = JoinGroup,
        CancelJoin = _candidate.Cancel,
        Leave = LeaveGroup,
        Forget = ForgetGroup,
        Edit = EditGroup,
        Approve = pending => AnswerAdmission(_admissionHost.Approve(pending.Nonce)),
        Decline = pending => AnswerAdmission(_admissionHost.Decline(pending.Nonce)),
        SetPaused = _groups.SetPaused,
        SetReceive = _groups.SetReceive,
        SetPublic = enabled => _groups.SetPublic(enabled, ActiveServices()),
        PublicWarningSeen = () => _configuration.PublicWarningSeen,
        AcknowledgePublicWarning = () =>
        {
            _configuration.PublicWarningSeen = true;
            _configuration.Save();
        },
        Block = _groups.Block,
        Unblock = _groups.Unblock,
        SetDefaultReceive = _groups.SetDefaultReceive,
        OurIdentityKey = () => _pairing.Identity?.PublicKey,
    };

    /// <summary>Les services actifs de la configuration : ceux du Public.</summary>
    private List<RendezvousAddress> ActiveServices()
        => [.. _configuration.ActiveRendezvous.Select(entry => entry.Address)];

    /// <summary>Crée un groupe dont nous sommes le propriétaire, et en donne le code.</summary>
    /// <returns>Vrai si le groupe existe désormais, pour que sa fenêtre se ferme.</returns>
    private bool CreateGroup(string name, string password)
    {
        if (_pairing.Identity is not { } identity)
        {
            Report("Personnage introuvable.");
            return false;
        }

        if (_configuration.ActiveRendezvous.FirstOrDefault() is not { } service)
        {
            Report("Activer d'abord un service Linkpearl dans les réglages.");
            return false;
        }

        CreatedGroup created;

        try
        {
            created = GroupGovernance.Create(name, password, service.Address, identity.PublicKey, _clock.UtcNow);
        }
        catch (ArgumentException)
        {
            Report("Création impossible : le nom doit contenir de 1 à 32 lettres, chiffres ou tirets, sans espace.");
            return false;
        }
        catch (InvalidOperationException e)
        {
            Log.Warning($"Création de groupe en échec ({e.GetType().Name}).");
            Report("Création impossible. Réessayer. Si le problème persiste, consulter le journal.");
            return false;
        }

        if (_groups.TryAdd(created.Record, out var refusal) is false)
        {
            Report($"création impossible : {refusal}.");
            return false;
        }

        Report($"Groupe {created.Record.Name} créé. Son code : {InvitationTicketText.Encode(created.Code, service.Address)}");
        return true;
    }

    /// <summary>Demande à rejoindre un groupe par le code collé.</summary>
    /// <remarks>
    /// Seuls les blancs sont retirés, pas les tirets : le ticket se débarrasse
    /// lui-même des siens, et un nom d'hôte après l'arobase peut en porter.
    /// </remarks>
    private void JoinGroup(string text, string password)
    {
        var cleaned = string.Concat(text.Where(character => char.IsWhiteSpace(character) is false));

        if (InvitationTicketText.TryParse(cleaned, out var code, out var at, out var why) is false)
        {
            Report($"code illisible : {why}.");
            return;
        }

        if ((at ?? _configuration.ActiveRendezvous.FirstOrDefault()?.Address) is not { } service)
        {
            Report("Activer d'abord un service Linkpearl dans les réglages.");
            return;
        }

        if (_state.Self is not { } self)
        {
            Report("Personnage introuvable.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Report(await _presence.JoinGroupAsync(code, service, password, self, _shutdown.Token)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // L'arrêt du plugin pendant l'attente de l'ouverture des
                // boîtes : il n'y a plus personne à qui le dire.
            }
            catch (Exception e)
            {
                // Le type seul : le texte du code, donc de quoi entrer dans le
                // groupe, n'a rien à faire au journal.
                Log.Warning($"Demande d'admission en échec ({e.GetType().Name}).");
                Report("Demande impossible. Vérifier la connexion aux services Linkpearl, puis réessayer.");
            }
        }, _shutdown.Token);
    }

    /// <summary>Quitte un groupe. Son propriétaire le dissout au lieu de le quitter.</summary>
    /// <remarks>
    /// Un propriétaire qui partirait sans rien dire laisserait un groupe que
    /// personne ne pourrait plus gouverner. La dissolution se propage aux
    /// membres par la politique, et le groupe reste listé, dissous, jusqu'à ce
    /// qu'on l'oublie.
    /// </remarks>
    private void LeaveGroup(GroupId id)
    {
        if (_groups.Find(id) is not { } group)
            return;

        if (GroupGovernance.RoleOf(group, _pairing.Identity?.PublicKey) is not GroupRole.Owner)
        {
            _groups.Remove(id);
            return;
        }

        try
        {
            Offer(id, GroupGovernance.Dissolve(group));
        }
        catch (InvalidOperationException e)
        {
            Log.Warning($"Dissolution de groupe en échec ({e.GetType().Name}).");
            Report("Dissolution impossible. Rouvrir la gestion du groupe et réessayer.");
        }
    }

    /// <summary>Retire de la liste un groupe dissous.</summary>
    /// <remarks>
    /// Jamais un groupe vivant dont on tient la clé : elle n'existe nulle part
    /// ailleurs, et l'oublier laisserait le groupe sans personne pour le
    /// gouverner ni le dissoudre.
    /// </remarks>
    private void ForgetGroup(GroupId id)
    {
        if (_groups.Find(id) is { SigningKey: not null, Policy.Dissolved: false } group)
        {
            Report($"Le groupe {group.Name} appartient à ce personnage. Le dissoudre avant de le retirer.");
            return;
        }

        _groups.Remove(id);
    }

    /// <summary>Applique une modification de gouvernance, signée selon notre rôle.</summary>
    private void EditGroup(GroupId id, Func<GroupRecord, ECDsa?, byte[]> change)
    {
        if (_groups.Find(id) is not { } group)
            return;

        var identity = _pairing.Identity;
        ECDsa? signer;

        switch (GroupGovernance.RoleOf(group, identity?.PublicKey))
        {
            case GroupRole.Owner:
                signer = null;
                break;

            case GroupRole.Moderator when identity is not null:
                signer = identity.Key;
                break;

            default:
                Report("Action réservée au propriétaire et aux modérateurs.");
                return;
        }

        try
        {
            Offer(id, change(group, signer));
        }
        catch (InvalidOperationException e)
        {
            Log.Warning($"Modification de groupe en échec ({e.GetType().Name}).");
            Report("Modification impossible. Rouvrir la gestion du groupe et réessayer.");
        }
    }

    /// <summary>Adopte localement une politique qu'on vient de signer ; le moteur la propage.</summary>
    private void Offer(GroupId id, byte[] policy)
    {
        // Déjà passée par les règles à la signature : un refus ici veut dire
        // que le groupe a changé entre-temps (quitté, ou politique plus récente).
        var outcome = _groups.OfferPolicy(id, policy);

        if (outcome is not (PolicyOffer.Adopted or PolicyOffer.Same))
            Report("Le groupe a changé entre-temps. Rouvrir sa gestion et réessayer.");
    }

    /// <summary>Envoie au candidat la réponse d'un modérateur.</summary>
    private void AnswerAdmission(AdmissionOutbound? outbound)
    {
        // Rien à envoyer : la demande a expiré, a déjà été tranchée, ou le
        // candidat a été banni pendant qu'elle attendait.
        if (outbound is null)
        {
            Report("Cette demande n'est plus en attente.");
            return;
        }

        RunSafely(async () => Report(await _presence.AnswerAsync(outbound, _shutdown.Token).ConfigureAwait(false)));
    }

    /// <summary>Range dans le carnet le groupe que la candidature vient d'obtenir.</summary>
    private void TakeJoinedGroup()
    {
        if (_candidate.TakeJoined(_clock.UtcNow) is not { } joined)
            return;

        Report(_groups.TryAdd(joined, out var refusal)
            ? $"Groupe {joined.Name} rejoint."
            : $"Impossible de rejoindre {joined.Name} : {refusal}.");
    }

    /// <summary>
    /// Tire les conséquences, pour nous, d'une politique adoptée.
    /// </summary>
    /// <remarks>
    /// Levé hors du fil du jeu, par le handshake qui a reçu la politique : rien
    /// ici ne touche au jeu, et Report renvoie lui-même vers le bon fil. Le
    /// propriétaire n'est jamais retiré : c'est lui qui a dissous, et aucun
    /// bannissement ne peut le viser. Le nom du groupe va au chat du joueur,
    /// jamais au journal.
    /// </remarks>
    private void OnPolicyAdopted(GroupId id)
    {
        try
        {
            if (_groups.Find(id) is not { Policy: { } policy } group
                || GroupGovernance.RoleOf(group, _pairing.Identity?.PublicKey) is GroupRole.Owner)
                return;

            if (policy.IsBanned(_pairing.Id, _state.Self?.Fingerprint))
            {
                if (_groups.Remove(id))
                    Report($"Exclusion du groupe {group.Name}.");

                return;
            }

            if (policy.Dissolved && _groups.Remove(id))
                Report($"Le groupe {group.Name} a été dissous.");
        }
        catch (Exception e)
        {
            // Une exception remonterait dans le handshake qui a offert la politique.
            Log.Warning($"Suite d'une politique adoptée en échec ({e.GetType().Name}).");
        }
    }

    /// <summary>
    /// Exécute une tâche déclenchée depuis l'interface, sans jamais laisser une
    /// exception se perdre dans un Task oublié.
    /// </summary>
    private void RunSafely(Func<Task> work)
        => _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error(e, "Action Linkpearl en échec.");
                Report("Action impossible. Réessayer. Si le problème persiste, consulter le journal.");
            }
        }, _shutdown.Token);

    /// <summary>
    /// Dit au joueur ce qui le concerne, dans son chat local : rien de ce que
    /// fait ce plugin ne doit partir vers le serveur.
    /// </summary>
    /// <remarks>
    /// Seulement ce qu'il ne verrait pas autrement : une réponse arrivée pendant
    /// que la fenêtre était fermée, une action qui a échoué. Le diagnostic va
    /// au journal de Dalamud.
    ///
    /// Appelé aussi hors du thread du jeu (handshakes, boucles de fond, tâches
    /// de l'interface) : le chat appartient au jeu, donc l'écriture y est
    /// renvoyée quand on n'y est pas. Plus rien après l'arrêt : une fermeture
    /// du plugin mise en file sur un Framework qui se décharge retiendrait
    /// l'AssemblyLoadContext, ou tournerait sur un plugin déjà libéré.
    /// </remarks>
    private void Report(string message)
    {
        // IsCancellationRequested reste lisible après Dispose de la source.
        if (_shutdown.IsCancellationRequested || Framework.IsFrameworkUnloading)
            return;

        var line = $"[Linkpearl] {message}";

        if (Framework.IsInFrameworkUpdateThread)
            Chat.Print(line);
        else
            _ = Framework.RunOnFrameworkThread(() => Chat.Print(line));
    }

    public void Dispose()
    {
        // Le déchargement de Dalamud est coopératif : une tâche encore vivante
        // ici ferait fuir l'AssemblyLoadContext, et le rechargement suivant en
        // créerait un second.
        _shutdown.Cancel();
        _groups.PolicyAdopted -= OnPolicyAdopted;
        _cacheKeeper.Opened -= OnCacheOpened;
        _cacheKeeper.Lost -= OnCacheLost;

        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        // Fermée avant d'être retirée : ses mots de passe saisis ne restent
        // pas en mémoire jusqu'au passage du ramasse-miettes.
        _groupEntry.Close();
        _windows.RemoveAllWindows();

        Commands.RemoveHandler(Command);
        ContextMenu.OnMenuOpened -= OnMenuOpened;
        Framework.Update -= PollLinks;
        Framework.Update -= FollowCharacter;
        Framework.Update -= UpdateStatusBar;
        Framework.Update -= UpdateOverlay;
        Framework.Update -= UpdateNameplates;
        Framework.Update -= UpdatePrerequisites;
        PluginInterface.UiBuilder.Draw -= _overlay.Draw;
        _nameplates.Dispose();
        _statusBar.Dispose();

        // Le moteur d'abord : il retire des pairs ce qu'il leur a posé, et cela
        // passe par des appels au jeu que la suite ne pourrait plus faire.
        _engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _applicator.Dispose();
        _appearance.Dispose();
        _transients.Dispose();
        _extras.Dispose();
        _penumbra.Dispose();
        _glamourer.Dispose();
        _links.Dispose();
        Brand.Dispose();
        Fonts.Dispose();

        _selfLoop.Dispose();
        _banFetcher.Dispose();
        _consensusFetcher.Dispose();
        _relayLoop.Dispose();
        _banScreening.Dispose();
        _presence.Dispose();

        // Après la présence : c'est elle qui leur passe les trames reçues, et
        // plus rien ne doit leur arriver une fois libérés.
        _admissionHost.Dispose();
        _candidate.Dispose();
        _pairing.Dispose();
        _shutdown.Dispose();
    }
}
