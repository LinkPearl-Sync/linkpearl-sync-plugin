using Dalamud.Plugin;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Linkpearl.Integration;

/// <summary>
/// Adaptateur mince vers Penumbra. Aucune logique : elle est dans Core.
/// </summary>
/// <remarks>
/// Tous ces appels prennent des index de l'ObjectTable et doivent donc partir
/// du thread du framework. L'adaptateur ne fait pas le saut lui-même : c'est à
/// l'appelant de savoir sur quel thread il est, sinon on masquerait une faute
/// de thread derrière une couche d'indirection.
/// </remarks>
public sealed class PenumbraIpc : IDisposable
{
    /// <summary>Marque nos modifications temporaires, pour pouvoir les retirer.</summary>
    public const string Tag = "Linkpearl";

    private readonly ApiVersion _version;
    private readonly GetGameObjectResourcePaths _resourcePaths;
    private readonly GetPlayerMetaManipulations _meta;
    private readonly CreateTemporaryCollection _createCollection;
    private readonly AssignTemporaryCollection _assignCollection;
    private readonly AddTemporaryMod _addMod;
    private readonly RemoveTemporaryMod _removeMod;
    private readonly DeleteTemporaryCollection _deleteCollection;
    private readonly RedrawObject _redraw;
    private readonly ResolvePlayerPaths _resolvePlayer;
    private readonly GetModDirectory _modDirectory;
    private readonly IDisposable _redrawn;
    private readonly IDisposable _settings;
    private readonly IDisposable _resolved;

    public PenumbraIpc(IDalamudPluginInterface pi)
    {
        _version           = new ApiVersion(pi);
        _resourcePaths     = new GetGameObjectResourcePaths(pi);
        _meta              = new GetPlayerMetaManipulations(pi);
        _createCollection  = new CreateTemporaryCollection(pi);
        _assignCollection  = new AssignTemporaryCollection(pi);
        _addMod            = new AddTemporaryMod(pi);
        _removeMod         = new RemoveTemporaryMod(pi);
        _deleteCollection  = new DeleteTemporaryCollection(pi);
        _redraw            = new RedrawObject(pi);
        _resolvePlayer     = new ResolvePlayerPaths(pi);
        _modDirectory      = new GetModDirectory(pi);

        // Le redessin est le signal qui compte : tout changement de mod
        // affectant le personnage en produit un. S'abonner aux changements de
        // réglage à la place attraperait aussi les collections qui ne nous
        // concernent pas.
        _redrawn = GameObjectRedrawn.Subscriber(pi, (_, index) => Redrawn?.Invoke(index));

        // Le redessin ne suffit pas : cocher un mod dans Penumbra sans redessiner
        // change les fichiers résolus sans qu'aucun redessin n'ait lieu, et
        // l'apparence annoncée restait alors celle d'avant. Le signal est plus
        // large que nécessaire (toute collection), mais l'anti-rebond et la
        // comparaison par hachage absorbent les reconstructions pour rien.
        _settings = ModSettingChanged.Subscriber(pi, (_, _, _, _) => SettingsChanged?.Invoke());

        // Seul moyen d'apprendre qu'une animation ou un VFX est moddé : le jeu
        // ne les charge qu'au moment de les jouer, et l'arbre des ressources
        // d'un personnage ne les montre pas.
        _resolved = GameObjectResourcePathResolved.Subscriber(
            pi, (address, gamePath, localPath) => ResourceResolved?.Invoke(address, gamePath, localPath));
    }

    /// <summary>Penumbra a résolu une ressource pour un objet : adresse, chemin de jeu, chemin résolu.</summary>
    /// <remarks>
    /// Levé très souvent, et pas forcément depuis le thread du jeu : l'abonné
    /// ne fait que trier et mettre en file.
    /// </remarks>
    public event Action<nint, string, string>? ResourceResolved;

    /// <summary>Un réglage de mod a changé, dans n'importe quelle collection.</summary>
    public event Action? SettingsChanged;

    /// <summary>Un objet du jeu vient d'être redessiné, avec son index.</summary>
    /// <remarks>
    /// Levé depuis le thread du jeu. Ce qui en découle ne doit donc rien faire
    /// de long ici : on se contente de signaler.
    /// </remarks>
    public event Action<int>? Redrawn;

    public void Dispose()
    {
        _redrawn.Dispose();
        _settings.Dispose();
        _resolved.Dispose();
    }

    /// <summary>
    /// Version de l'API, ou null si Penumbra n'est pas chargé.
    /// </summary>
    /// <remarks>
    /// Sans serveur central pour imposer une version minimale, chaque pair peut
    /// avoir la sienne. On se désactive proprement plutôt que de planter.
    /// </remarks>
    public (int Major, int Minor)? TryGetVersion()
    {
        try
        {
            return _version.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Ressources résolues pour un objet : chemin réel vers chemins de jeu.</summary>
    public IReadOnlyDictionary<string, HashSet<string>>? ResourcePathsOf(ushort objectIndex)
        => _resourcePaths.Invoke(objectIndex)[0];

    /// <summary>Résout des chemins de jeu pour la collection du joueur ; même longueur, même ordre.</summary>
    /// <remarks>Un chemin que Penumbra ne touche pas revient tel quel.</remarks>
    public string[] ResolvePlayer(string[] gamePaths)
        => gamePaths.Length == 0 ? [] : _resolvePlayer.Invoke(gamePaths, []).Item1;

    /// <summary>Le dossier racine des mods de Penumbra, ou une chaîne vide s'il ne répond pas.</summary>
    /// <remarks>
    /// Vide, aucun fichier n'est annoncé : mieux vaut une apparence
    /// incomplète qu'un fichier du disque servi sans savoir d'où il vient.
    /// </remarks>
    public string ModDirectory()
    {
        try
        {
            return _modDirectory.Invoke() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public string MetaManipulations()
        => _meta.Invoke();

    public Guid CreateCollection(string name)
    {
        var ec = _createCollection.Invoke(Tag, name, out var id);
        if (ec is not PenumbraApiEc.Success)
            throw new InvalidOperationException($"création de collection refusée par Penumbra : {ec}");

        return id;
    }

    public void AssignCollection(Guid collection, int objectIndex)
    {
        var ec = _assignCollection.Invoke(collection, objectIndex, forceAssignment: true);
        if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            throw new InvalidOperationException($"affectation de collection refusée par Penumbra : {ec}");
    }

    /// <summary>
    /// Pose la table des remplacements.
    /// </summary>
    /// <remarks>
    /// Avec le même tag, l'appel remplace l'ensemble d'un coup au lieu de
    /// s'ajouter, ce qui évite l'état intermédiaire où le personnage porte la
    /// moitié de l'ancienne apparence et la moitié de la nouvelle.
    /// </remarks>
    public void SetTemporaryMod(Guid collection, Dictionary<string, string> pathMap, string metaManipulations)
    {
        // Retirer avant de poser : on ne dépend pas de ce que Penumbra fait d'un
        // second mod sous la même étiquette, et une apparence mise à jour part
        // ainsi d'un état vide plutôt que d'un cumul.
        _removeMod.Invoke(Tag, collection, priority: 1000);

        var ec = _addMod.Invoke(Tag, collection, pathMap, metaManipulations, priority: 1000);
        if (ec is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            throw new InvalidOperationException($"pose des remplacements refusée par Penumbra : {ec}");
    }

    public void DeleteCollection(Guid collection)
        => _deleteCollection.Invoke(collection);

    public void Redraw(int objectIndex)
        => _redraw.Invoke(objectIndex, RedrawType.Redraw);
}
