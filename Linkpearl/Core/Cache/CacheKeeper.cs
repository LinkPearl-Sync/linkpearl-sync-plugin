using Linkpearl.Core.Abstractions;

namespace Linkpearl.Core.Cache;

/// <summary>Ce que le cache permet en ce moment.</summary>
public enum CacheGateState
{
    /// <summary>La présentation n'a pas encore été fermée : rien ne touche au disque.</summary>
    AwaitingOnboarding,

    Open,

    /// <summary>Le dossier a disparu : tout est arrêté jusqu'à un nouveau choix.</summary>
    Missing,
}

/// <summary>
/// Décide quand le cache existe, où, et ce qui arrive quand il disparaît.
/// </summary>
/// <remarks>
/// Condition posée par l'utilisateur : un dossier de cache qui a disparu n'est
/// jamais recréé. Il a peut-être été vidé exprès, ou était sur un disque
/// débranché. Le plugin s'arrête et demande d'en choisir un autre.
///
/// Les appels peuvent venir de l'interface et de la boucle de synchronisation :
/// l'état est lu sans verrou, mais Choose, OpenConfigured et Lose, qui
/// peuvent chacun être appelés de plusieurs fils à la fois, mutent l'état
/// sous _gate. Les événements Opened et Lost ne sont levés qu'une fois ce
/// verrou relâché.
/// </remarks>
public sealed class CacheKeeper(
    ICacheConfiguration configuration, string defaultRoot, IClock clock, Func<string, long> freeSpace, ILogSink log)
{
    public const long GiB = 1024L * 1024 * 1024;
    public const int MinQuotaGiB = 10;
    public const int MaxQuotaGiB = 500;
    public const int QuotaStepGiB = 5;

    private readonly object _gate = new();
    private volatile CacheGateState _state = CacheGateState.AwaitingOnboarding;
    private volatile string? _lostRoot;
    private volatile bool _restartPending;
    private long _previousBytes = -1;

    /// <summary>
    /// Vrai dès que Lost a été levé pour la perte en cours.
    /// </summary>
    /// <remarks>
    /// Un rechoix raté en état Missing rappelle MarkMissing sans qu'il y ait
    /// une nouvelle perte : sans ce jeton, ce serait une seconde notification
    /// pour le même incident. Remis à faux à la prochaine ouverture réussie.
    /// </remarks>
    private volatile bool _lostNotified;

    /// <summary>Le cache tel que le voient l'apparence locale, l'applicateur et le moteur.</summary>
    public SwitchableBlobStore Store { get; } = new();

    public CacheGateState State => _state;

    /// <summary>Le dossier disparu, pour le dire à l'utilisateur. Null hors blocage.</summary>
    public string? LostRoot => _lostRoot;

    /// <summary>Un autre dossier a été choisi cache ouvert : il servira au prochain chargement.</summary>
    public bool RestartPending => _restartPending;

    /// <summary>La taille de l'ancien cache, une fois mesurée.</summary>
    public long? PreviousBytes
    {
        get
        {
            var bytes = Interlocked.Read(ref _previousBytes);
            return bytes < 0 ? null : bytes;
        }
    }

    /// <summary>Le dossier que désigne la configuration.</summary>
    public string ConfiguredRoot => CacheLocation.Resolve(configuration.CacheDirectory, defaultRoot);

    /// <summary>Le dossier du cache en service. Null s'il n'y en a pas.</summary>
    public string? ActiveRoot => Store.Current?.Root;

    /// <summary>
    /// L'ancien cache à proposer à la suppression.
    /// </summary>
    /// <remarks>
    /// Seulement après le rechargement : tant que le changement attend, l'ancien
    /// est celui qu'on utilise.
    /// </remarks>
    public string? PreviousRoot
    {
        get
        {
            var previous = configuration.PreviousCacheDirectory;

            if (_restartPending || previous is "" || SamePath(previous, ActiveRoot))
                return null;

            return previous;
        }
    }

    public int QuotaGiB => (int)Math.Clamp(configuration.CacheQuotaBytes / GiB, MinQuotaGiB, MaxQuotaGiB);

    /// <summary>Le cache vient d'être attaché. Levé sur le fil de l'appelant.</summary>
    public event Action? Opened;

    /// <summary>Le cache est bloqué, dossier introuvable. Levé une fois par perte.</summary>
    public event Action? Lost;

    /// <summary>Au chargement du plugin.</summary>
    public void Start()
    {
        if (configuration.OnboardingSeen is false)
        {
            _state = CacheGateState.AwaitingOnboarding;
            return;
        }

        OpenConfigured();
    }

    /// <summary>La présentation vient d'être fermée, par la croix ou par « C'est parti ».</summary>
    public void FinishOnboarding()
    {
        if (configuration.OnboardingSeen)
            return;

        configuration.OnboardingSeen = true;
        configuration.Save();
        OpenConfigured();
    }

    /// <summary>
    /// Un dossier choisi par l'utilisateur.
    /// </summary>
    /// <remarks>
    /// Crée le sous-dossier et y écrit un témoin : à appeler depuis le pool de
    /// threads. Après une perte, ouvre aussi le cache, index compris.
    /// </remarks>
    /// <returns>Null si le choix est retenu, sinon pourquoi il ne l'est pas.</returns>
    public string? Choose(string chosen)
    {
        var prepared = CacheLocation.Prepare(chosen);

        if (prepared.Root is not { } root)
            return prepared.Error;

        // Les mutations d'état sont sérialisées sous _gate, comme
        // OpenConfigured et Lose : deux choix simultanés (présentation et
        // fenêtre principale) ne doivent pas se marcher dessus. Le verrou est
        // relâché avant d'appeler OpenConfigured, qui lève Opened/Lost lui-même
        // une fois le sien relâché.
        lock (_gate)
        {
            switch (_state)
            {
                case CacheGateState.Open:
                    if (SamePath(root, ActiveRoot))
                    {
                        configuration.PreviousCacheDirectory = "";
                        _restartPending = false;
                    }
                    else
                    {
                        configuration.PreviousCacheDirectory = ActiveRoot ?? "";
                        _restartPending = true;
                    }

                    configuration.CacheDirectory = root;
                    configuration.Save();
                    return null;

                case CacheGateState.Missing:
                    // Un dossier disparu ne s'offre pas à la suppression, et un
                    // changement en attente d'avant la perte n'a plus lieu
                    // d'être : ce choix-ci le remplace.
                    _restartPending = false;
                    configuration.PreviousCacheDirectory = "";
                    configuration.CacheDirectory = root;
                    configuration.Save();
                    break;

                default:
                    // AwaitingOnboarding : un utilisateur d'avant l'onboarding
                    // a pu avoir un cache au dossier par défaut. En choisir un
                    // autre ne doit pas l'abandonner en silence : il doit
                    // rester proposable à la suppression une fois le nouveau
                    // ouvert. Un second choix pendant la présentation ne doit
                    // pas écraser ce repérage par le dossier vide que Prepare
                    // vient de créer pour ce second choix : on ne remplace
                    // PreviousCacheDirectory que s'il est encore vide ou si le
                    // dossier qu'il désigne a disparu, et on le vide si le
                    // nouveau choix retombe dessus.
                    var priorRoot = ConfiguredRoot;
                    var trackedPrevious = configuration.PreviousCacheDirectory;

                    if (trackedPrevious is not "" && SamePath(root, trackedPrevious))
                    {
                        configuration.PreviousCacheDirectory = "";
                    }
                    else if ((trackedPrevious is "" || Directory.Exists(trackedPrevious) is false)
                             && Directory.Exists(priorRoot) && SamePath(root, priorRoot) is false)
                    {
                        configuration.PreviousCacheDirectory = priorRoot;
                    }

                    configuration.CacheDirectory = root;
                    configuration.Save();
                    return null;
            }
        }

        // explicitChoice : un choix explicite garde le droit de recréer
        // blobs/ et incoming/, même sur le chemin qui vient de bloquer (vidé
        // sans être supprimé).
        OpenConfigured(explicitChoice: true);
        return _state is CacheGateState.Open ? null : "impossible d'ouvrir ce dossier.";
    }

    /// <summary>Le quota, en gigaoctets, borné et appliqué tout de suite.</summary>
    public void SetQuota(int gib)
    {
        var kept = Math.Clamp(gib, MinQuotaGiB, MaxQuotaGiB);

        configuration.CacheQuotaBytes = kept * GiB;
        configuration.Save();
        Store.Current?.SetQuota(kept * GiB);
    }

    /// <summary>L'espace libre du disque qui porte ce chemin, -1 s'il est illisible.</summary>
    public long FreeSpace(string path)
    {
        try
        {
            return freeSpace(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Un lecteur débranché : on n'affiche rien plutôt qu'un chiffre faux.
            return -1;
        }
    }

    /// <summary>Le sondage : le dossier est-il toujours là ?</summary>
    public void Check()
    {
        if (_state is CacheGateState.Open && Store.Current is { RootExists: false })
            Lose();
    }

    /// <summary>Mesure l'ancien cache. Parcourt tout l'arbre : pool de threads.</summary>
    public void MeasurePrevious()
    {
        if (PreviousRoot is not { } previous)
            return;

        try
        {
            Interlocked.Exchange(ref _previousBytes, CacheLocation.Measure(previous));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Disparu ou devenu illisible entre-temps : une taille inconnue
            // plutôt qu'un pool de threads qui plante, et le seul type
            // d'exception au journal, jamais son message.
            Interlocked.Exchange(ref _previousBytes, -1);
            log.Warning($"mesure de l'ancien cache impossible ({e.GetType().Name}).");
        }
    }

    /// <summary>Efface nos fichiers de l'ancien cache, et l'oublie. Pool de threads.</summary>
    /// <returns>Le nombre de fichiers effacés.</returns>
    public int DeletePrevious()
    {
        if (PreviousRoot is not { } previous)
            return 0;

        var deleted = CacheLocation.DeleteOwned(previous);

        configuration.PreviousCacheDirectory = "";
        configuration.Save();
        Interlocked.Exchange(ref _previousBytes, -1);

        log.Info($"ancien cache supprimé : {deleted} fichier(s).");
        return deleted;
    }

    /// <param name="explicitChoice">
    /// Vrai pour un choix explicite de l'utilisateur (Choose depuis Missing) :
    /// il garde le droit de recréer blobs/ et incoming/ même sur un dossier
    /// déjà établi mais vidé. Faux à l'ouverture automatique (Start), où un
    /// dossier établi mais incomplet doit bloquer plutôt que se recréer tout
    /// seul.
    /// </param>
    private void OpenConfigured(bool explicitChoice = false)
    {
        var root = ConfiguredRoot;
        var raiseOpened = false;
        var raiseLost = false;
        FileSystemBlobStore? orphan = null;

        // Les changements d'état sont sérialisés sous _gate ; Opened/Lost ne
        // sont levés qu'après l'avoir relâché, plus bas.
        lock (_gate)
        {
            // Une ouverture déjà en cours (ou tout juste finie) sur ce même
            // dossier n'a rien de plus à faire : deux appels simultanés
            // (présentation et fenêtre principale) ne doivent pas attacher
            // deux magasins.
            if (_state is CacheGateState.Open && SamePath(ActiveRoot, root))
                return;

            if (explicitChoice is false && configuration.CacheEstablished && FileSystemBlobStore.IsIntact(root) is false)
            {
                raiseLost = MarkMissingLocked(root);
            }
            else
            {
                FileSystemBlobStore? store;

                try
                {
                    store = new FileSystemBlobStore(root, new CacheSettings { QuotaBytes = QuotaGiB * GiB }, clock, freeSpace);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                             or NotSupportedException or ArgumentException)
                {
                    // Le seul type d'exception, jamais son message :
                    // IOException et consorts embarquent souvent le chemin
                    // complet, qui porte le nom du compte Windows.
                    log.Warning($"ouverture du cache impossible ({e.GetType().Name}).");
                    raiseLost = MarkMissingLocked(root);
                    store = null;
                }

                if (store is not null)
                {
                    store.RootLost += Lose;
                    orphan = Store.Attach(store);

                    if (configuration.CacheEstablished is false)
                    {
                        configuration.CacheEstablished = true;
                        configuration.Save();
                    }

                    _lostRoot = null;
                    _lostNotified = false;
                    _state = CacheGateState.Open;
                    log.Info("cache ouvert.");
                    raiseOpened = true;
                }
            }
        }

        // Un magasin qu'on vient de remplacer sans qu'il ait jamais été le
        // courant (course perdue par un appel concurrent) : plus personne ne
        // le référence, mais son abonnement à RootLost, lui, doit partir.
        if (orphan is not null)
            orphan.RootLost -= Lose;

        if (raiseOpened)
            Opened?.Invoke();

        if (raiseLost)
            Lost?.Invoke();
    }

    /// <summary>
    /// Le dossier a disparu : on lâche le cache.
    /// </summary>
    /// <remarks>
    /// Peut arriver du sondage et d'une écriture au même instant : le verrou
    /// garantit une seule perte, donc un seul arrêt et une seule notification.
    /// </remarks>
    private void Lose()
    {
        FileSystemBlobStore? lost;
        var raiseLost = false;

        lock (_gate)
        {
            if (_state is not CacheGateState.Open)
                return;

            lost = Store.Detach();
            raiseLost = MarkMissingLocked(lost?.Root ?? ConfiguredRoot);
        }

        if (lost is not null)
            lost.RootLost -= Lose;

        if (raiseLost)
            Lost?.Invoke();
    }

    /// <summary>Mute l'état en Missing. À appeler sous _gate.</summary>
    /// <returns>
    /// Vrai si Lost doit être levé après avoir relâché le verrou : une seule
    /// fois par perte, jamais pour un rechoix raté qui retombe dans le même
    /// état.
    /// </returns>
    private bool MarkMissingLocked(string root)
    {
        _lostRoot = root;
        _state = CacheGateState.Missing;
        log.Warning("dossier du cache introuvable : synchronisation suspendue.");

        if (_lostNotified)
            return false;

        _lostNotified = true;
        return true;
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(
               Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
               StringComparison.OrdinalIgnoreCase);
}
