using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Integration.Extras;

namespace Linkpearl.Integration;

/// <summary>
/// Pose l'apparence d'un pair sur le personnage qui lui correspond.
/// </summary>
/// <remarks>
/// L'ordre est strict, et il a été payé en jeu : collection temporaire, puis
/// remplacements, puis redessin, puis seulement Glamourer avec une clé de
/// verrou non nulle. Inverser Glamourer et le redessin donne un état écrasé
/// une seconde plus tard par l'automation du receveur.
///
/// Rien de ce qui décide n'est ici : le plan vient de <see cref="AppearancePlanner"/>,
/// qui se teste sous Linux. Cette classe n'est que la main qui exécute, et la
/// frontière du thread de jeu.
/// </remarks>
public sealed class RemoteApplicator : IRemoteApplicator, IDisposable
{
    private readonly PenumbraIpc _penumbra;
    private readonly GlamourerIpc _glamourer;
    private readonly ExtrasIpc _extras;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IBlobStore _store;
    private readonly Quotas _quotas;
    private readonly IPluginLog _log;
    private readonly string _markerPath;

    private readonly Dictionary<PeerId, AppliedPeer> _applied = [];
    private readonly Lock _gate = new();

    private volatile string? _blockedBecause = "le jeu n'est pas encore prêt";
    private bool _penumbraAnswered;
    private int _sinceProbe;

    public RemoteApplicator(
        PenumbraIpc penumbra, GlamourerIpc glamourer, ExtrasIpc extras, IFramework framework, IObjectTable objects,
        IClientState clientState, ICondition condition, IBlobStore store, Quotas quotas,
        string root, IPluginLog log)
    {
        _penumbra = penumbra;
        _glamourer = glamourer;
        _extras = extras;
        _framework = framework;
        _objects = objects;
        _clientState = clientState;
        _condition = condition;
        _store = store;
        _quotas = quotas;
        _log = log;
        _markerPath = Path.Combine(root, "remote-collections.txt");

        // L'état du jeu se lit depuis le thread du framework, et le moteur
        // interroge CanApply depuis le sien. On rafraîchit donc à chaque frame
        // plutôt que de lire de travers depuis ailleurs.
        _framework.Update += Refresh;
    }

    /// <summary>
    /// Vrai quand le jeu est dans un état où l'on peut appliquer.
    /// </summary>
    /// <remarks>
    /// Appelable depuis n'importe quel thread : la valeur est celle de la
    /// dernière frame.
    /// </remarks>
    public bool CanApply(out string reason)
    {
        reason = _blockedBecause ?? string.Empty;
        return _blockedBecause is null;
    }

    public async Task<bool> ApplyAsync(
        PeerId peer, GameObjectRef target, CharacterManifest manifest, CancellationToken ct)
    {
        // Hors du thread de jeu : la construction du plan revalide le manifeste
        // entier et touche au cache.
        if (AppearancePlanner.TryBuild(manifest, _store, _quotas, out var plan, out var why) is false)
            throw new InvalidOperationException($"apparence refusée : {why}");

        // Des chemins de jeu et une raison, jamais de chemin local : rien ici ne
        // nomme le joueur.
        if (plan!.Dropped.Count > 0)
            _log.Warning($"Transitoires mal formés écartés ({plan.Dropped.Count}) : {string.Join(" ; ", plan.Dropped.Take(5))}");

        await _framework.RunOnFrameworkThread(() =>
        {
            if (Resolve(target) is false)
            {
                // L'index a changé de propriétaire depuis que le moteur a
                // apparié. Poser ici mettrait l'apparence du pair sur un
                // inconnu, chez nous seuls.
                _log.Debug("Objet périmé au moment de poser, application abandonnée.");
                return;
            }

            var collection = CollectionFor(peer);

            _penumbra.AssignCollection(collection, target.ObjectIndex);
            _penumbra.SetTemporaryMod(collection, new Dictionary<string, string>(plan!.PathMap), plan.MetaManipulations);
            _penumbra.Redraw(target.ObjectIndex);

            Remember(peer, applied => applied with { Object = target });
        }).ConfigureAwait(false);

        if (plan!.GlamourerState is { } state)
        {
            // Après le redessin, jamais avant. Verrouillé sous notre clé, pour que
            // l'automation de Glamourer chez nous n'écrase pas ce que le pair a
            // choisi de montrer.
            await _framework.RunOnFrameworkThread(() =>
            {
                if (Resolve(target) is false)
                    return;

                _glamourer.ApplyStateLocked(state, target.ObjectIndex);
                Remember(peer, applied => applied with { GlamourerTouched = true });
            }).ConfigureAwait(false);
        }

        // Les extras en dernier, sur un personnage entièrement chargé : Moodles
        // ignore en silence celui qu'il n'a pas encore vu s'afficher.
        return await ApplyExtrasAsync(peer, target, manifest.ExtrasOrNone, ExtrasChange.All, ct).ConfigureAwait(false);
    }

    public async Task<bool> ApplyExtrasAsync(
        PeerId peer, GameObjectRef target, CharacterExtras extras, ExtrasChange change, CancellationToken ct)
    {
        if (change.Any is false)
            return true;

        // Dix secondes au plus, comme pour notre propre personnage.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var done = await _framework.RunOnFrameworkThread(() =>
            {
                if (Resolve(target) is false)
                    return true;   // parti : rien à poser, et rien à attendre

                if (_objects[target.ObjectIndex] is not { } found || DrawReadiness.IsReady(found) is false)
                    return false;

                _extras.Apply(found, extras, change);
                Remember(peer, applied => applied with { ExtrasTouched = true });
                return true;
            }).ConfigureAwait(false);

            if (done)
                return true;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        // Dit au moteur, qui les retentera seuls : les croire posés laisserait
        // le pair sans ses proportions ni sa hauteur jusqu'à sa réapparition.
        _log.Debug("Extras non posés : le personnage n'a pas fini de se charger en dix secondes.");
        return false;
    }

    public async Task RemoveAsync(PeerId peer, CancellationToken ct)
    {
        AppliedPeer applied;

        lock (_gate)
        {
            if (_applied.TryGetValue(peer, out applied!) is false)
                return;   // rien posé, rien à défaire

            _applied.Remove(peer);
        }

        SaveMarker();

        await _framework.RunOnFrameworkThread(() =>
        {
            // On ne défait que sur l'objet qui est encore celui du pair : sinon
            // on rendrait à son état normal un personnage auquel nous n'avons
            // jamais touché.
            var stillThere = applied.Object is { } target && Resolve(target);

            if (stillThere && applied.ExtrasTouched && _objects[applied.Object!.Value.ObjectIndex] is { } found)
                Try(() => _extras.Clear(found), "retrait des extras");

            if (stillThere && applied.GlamourerTouched)
                Try(() => _glamourer.Release(applied.Object!.Value.ObjectIndex), "relâchement Glamourer");

            Try(() => _penumbra.DeleteCollection(applied.Collection), "suppression de collection");

            if (stillThere)
                Try(() => _penumbra.Redraw(applied.Object!.Value.ObjectIndex), "redessin");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Retire ce qu'une session précédente aurait laissé dans Penumbra.
    /// </summary>
    /// <remarks>
    /// Un rechargement du plugin perd l'état en mémoire, mais pas les
    /// collections posées : sans cette trace sur le disque, elles resteraient
    /// affectées à des personnages et plus rien ne saurait les retirer. C'est
    /// ce qui laisse un joueur avec l'apparence d'un autre jusqu'au redémarrage
    /// du jeu. À appeler depuis le thread du framework.
    /// </remarks>
    public int CleanLeftovers()
    {
        var left = ReadMarker();

        foreach (var collection in left)
            Try(() => _penumbra.DeleteCollection(collection), $"suppression de la collection {collection}");

        if (left.Count > 0)
        {
            // Le verrou relâche tout ce qui porte notre clé, sans rien changer
            // d'autre : on ignore sur quels objets nous avions posé.
            Try(() => _glamourer.UnlockEverything(), "relâchement des verrous Glamourer");
            SaveMarker();
        }

        return left.Count;
    }

    public void Dispose()
    {
        _framework.Update -= Refresh;

        // Le déchargement est coopératif : ce qui reste ici reste à l'écran.
        foreach (var peer in Peers())
            RemoveAsync(peer, CancellationToken.None).GetAwaiter().GetResult();
    }

    private IReadOnlyList<PeerId> Peers()
    {
        lock (_gate)
            return _applied.Keys.ToList();
    }

    private void Refresh(IFramework framework)
    {
        // Une image sur soixante pour l'appel IPC, soit environ une seconde.
        // Interroger Penumbra à chaque image coûterait un appel inter-plugins
        // soixante fois par seconde pour une réponse qui ne change qu'au
        // chargement ou au déchargement d'un plugin.
        if (_sinceProbe-- <= 0)
        {
            _sinceProbe = 60;
            _penumbraAnswered = _penumbra.TryGetVersion() is not null;
        }

        _blockedBecause = Blocked();
    }

    private string? Blocked()
    {
        if (_clientState.IsLoggedIn is false)
            return "hors du jeu";

        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            return "changement de zone";

        if (_condition[ConditionFlag.LoggingOut])
            return "déconnexion en cours";

        if (_penumbraAnswered is false)
            return "Penumbra n'est pas chargé";

        return null;
    }

    /// <summary>Vrai si l'objet visé est toujours celui que le moteur a apparié.</summary>
    /// <remarks>
    /// L'index change au rechargement de zone. L'identifiant stable est ce qui
    /// permet de s'en apercevoir plutôt que de poser sur quelqu'un d'autre.
    /// </remarks>
    private bool Resolve(GameObjectRef target)
        => _objects[target.ObjectIndex] is { } found && found.GameObjectId == target.StableId;

    private Guid CollectionFor(PeerId peer)
    {
        lock (_gate)
        {
            if (_applied.TryGetValue(peer, out var applied))
                return applied.Collection;
        }

        // Le nom n'est qu'une étiquette de diagnostic dans Penumbra, et il ne
        // porte pas de nom de personnage : l'empreinte suffit à s'y retrouver
        // sans rien révéler dans une capture d'écran.
        var collection = _penumbra.CreateCollection($"Linkpearl {peer.ToHex()[..8]}");

        lock (_gate)
            _applied[peer] = new AppliedPeer(collection, null, false);

        SaveMarker();
        return collection;
    }

    private void Remember(PeerId peer, Func<AppliedPeer, AppliedPeer> change)
    {
        lock (_gate)
        {
            if (_applied.TryGetValue(peer, out var applied))
                _applied[peer] = change(applied);
        }
    }

    private void Try(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.Warning($"{what} en échec ({e.GetType().Name}).");
            _log.Debug(e, $"{what} en échec.");
        }
    }

    private List<Guid> ReadMarker()
    {
        try
        {
            if (File.Exists(_markerPath) is false)
                return [];

            return File.ReadAllLines(_markerPath)
                .Select(line => Guid.TryParse(line.Trim(), out var id) ? id : (Guid?)null)
                .OfType<Guid>()
                .ToList();
        }
        catch (Exception e)
        {
            // Le type seul par défaut : le message porte le chemin du fichier,
            // donc le dossier de l'utilisateur, nom de session Windows compris.
            _log.Warning($"Trace des collections illisible ({e.GetType().Name}).");
            _log.Debug(e, "Trace des collections illisible.");
            return [];
        }
    }

    private void SaveMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);

            lock (_gate)
                File.WriteAllLines(_markerPath, _applied.Values.Select(applied => applied.Collection.ToString()));
        }
        catch (Exception e)
        {
            _log.Warning($"Trace des collections non écrite : un rechargement laisserait des restes ({e.GetType().Name}).");
            _log.Debug(e, "Trace des collections non écrite : un rechargement laisserait des restes.");
        }
    }

    /// <summary>Ce que nous avons posé pour un pair, et sur quoi.</summary>
    private sealed record AppliedPeer(Guid Collection, GameObjectRef? Object, bool GlamourerTouched, bool ExtrasTouched = false);
}
