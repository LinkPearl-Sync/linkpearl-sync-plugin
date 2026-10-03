using System.Buffers;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Integration.Extras;

namespace Linkpearl.Integration;

/// <summary>
/// Notre propre apparence, telle que le moteur l'annonce aux pairs.
/// </summary>
/// <remarks>
/// Le moteur demande le manifeste courant à chaque tic. Le reconstruire là
/// serait impensable : une apparence pèse des centaines de mégaoctets à hacher.
/// Il est donc bâti en tâche de fond, et <see cref="CurrentAsync"/> rend
/// toujours la dernière version connue, sans attendre.
///
/// L'instance rendue ne change que lorsque l'apparence change pour de bon. Le
/// moteur compare par référence pour décider s'il doit réannoncer : rendre un
/// objet neuf à chaque appel ferait réannoncer à chaque tic.
/// </remarks>
public sealed class LocalAppearance : ILocalAppearance, IDisposable
{
    /// <summary>Le personnage joueur est toujours à l'index 0 de l'ObjectTable.</summary>
    private const int PlayerIndex = 0;

    private readonly PenumbraIpc _penumbra;
    private readonly GlamourerIpc _glamourer;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly ExtrasIpc _extras;
    private readonly TransientCapture _transients;
    private readonly Func<byte[]?> _moodlesKey;
    private readonly IBlobStore _store;
    private readonly IPluginLog _log;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly FileHashCache _hashes = new();

    /// <summary>Plus grand os animé de chaque <c>.pap</c> déjà examiné, null s'il ne se charge pas.</summary>
    /// <remarks>
    /// Touché seulement par la construction, qui est unique, et par le passage
    /// qu'elle attend sur le thread du framework : pas de concurrence.
    /// </remarks>
    private readonly Dictionary<string, (FileStamp Stamp, int? MaxBone)> _papBones = new(StringComparer.OrdinalIgnoreCase);

    private volatile CharacterManifest? _current;

    /// <summary>Une demande arrivée pendant une construction, à servir juste après.</summary>
    private volatile bool _pending;
    private PlayerFingerprint? _fingerprint;

    /// <summary>
    /// Jeton de génération : une reconstruction en vol au moment de
    /// <see cref="Forget"/> n'a plus le droit d'écrire son résultat.
    /// </summary>
    /// <remarks>
    /// Follow(null) seul ne suffit pas à oublier une reconstruction en cours :
    /// elle finirait quand même par réaffecter <see cref="_current"/> après
    /// coup, ressuscitant une apparence bâtie pour un cache qui vient de
    /// disparaître.
    /// </remarks>
    private long _generation;

    public LocalAppearance(
        PenumbraIpc penumbra, GlamourerIpc glamourer, IFramework framework, IObjectTable objects,
        ExtrasIpc extras, TransientCapture transients, Func<byte[]?> moodlesKey, IBlobStore store, IPluginLog log)
    {
        _penumbra = penumbra;
        _glamourer = glamourer;
        _framework = framework;
        _objects = objects;
        _extras = extras;
        _transients = transients;
        _moodlesKey = moodlesKey;
        _store = store;
        _log = log;
    }

    public PlayerFingerprint? Fingerprint => _fingerprint;

    /// <summary>Ce que la dernière construction a produit, pour l'interface.</summary>
    public string Description { get; private set; } = "aucune apparence enregistrée";

    public bool Building { get; private set; }

    public Task<CharacterManifest?> CurrentAsync(CancellationToken ct) => Task.FromResult(_current);

    /// <summary>
    /// Change le personnage dont on annonce l'apparence.
    /// </summary>
    /// <remarks>
    /// Un changement de personnage invalide tout : les mods ne sont pas les
    /// mêmes, et annoncer l'apparence de l'ancien ferait voir aux pairs
    /// quelqu'un qui n'est pas là.
    /// </remarks>
    public void Follow(PlayerFingerprint? fingerprint)
    {
        if (_fingerprint == fingerprint)
            return;

        _fingerprint = fingerprint;
        _current = null;
        Description = fingerprint is null ? "hors du jeu" : "apparence en préparation";

        if (fingerprint is not null)
            Rebuild();
    }

    /// <summary>
    /// Oublie le personnage suivi et invalide toute reconstruction en vol.
    /// </summary>
    /// <remarks>
    /// À utiliser quand le cache n'est plus disponible (présentation en
    /// attente, dossier perdu) : contrairement à <see cref="Follow"/>, le
    /// jeton de génération empêche une construction déjà lancée de réaffecter
    /// <see cref="_current"/> une fois terminée.
    /// </remarks>
    public void Forget()
    {
        _fingerprint = null;
        _current = null;
        Description = "hors du jeu";
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Reconstruit l'apparence, en tâche de fond.</summary>
    /// <remarks>
    /// Une seule construction à la fois : deux hachages simultanés de plusieurs
    /// centaines de mégaoctets se disputeraient le disque pour rien.
    ///
    /// Mais aucune demande n'est perdue : celle qui arrive pendant une
    /// construction est notée, et une nouvelle construction suit. Sans cela, une
    /// retouche faite pendant le hachage de la précédente n'était jamais
    /// capturée, et les pairs gardaient l'avant-dernière tenue.
    /// </remarks>
    public void Rebuild()
    {
        if (_life.IsCancellationRequested)
            return;

        _pending = true;

        _ = Task.Run(async () =>
        {
            if (await _oneAtATime.WaitAsync(0, _life.Token).ConfigureAwait(false) is false)
                return;

            Building = true;

            try
            {
                while (_pending && _life.IsCancellationRequested is false)
                {
                    _pending = false;
                    await RebuildAsync(_life.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Description = $"préparation impossible : {e.Message}";
                // Le type seul par défaut : une erreur de lecture porte le
                // chemin d'un fichier de mod, donc le dossier de l'utilisateur.
                _log.Warning($"Construction de l'apparence locale en échec ({e.GetType().Name}).");
                _log.Debug(e, "Construction de l'apparence locale en échec.");
            }
            finally
            {
                Building = false;
                _oneAtATime.Release();
            }

            // Une demande arrivée entre la dernière vérification et la
            // libération a trouvé le verrou pris : on la relance ici.
            if (_pending && _life.IsCancellationRequested is false)
                Rebuild();
        }, _life.Token);
    }

    private async Task RebuildAsync(CancellationToken ct)
    {
        // Relevée avant le travail long : si Forget() l'incrémente pendant la
        // construction, le résultat ne doit plus être écrit une fois fini.
        var generation = Interlocked.Read(ref _generation);

        // L'ObjectTable et l'IPC ne se touchent que depuis le thread du
        // framework. Ce qui suit est une copie, manipulable ailleurs.
        var snapshot = await ReadWhenDrawnAsync(ct).ConfigureAwait(false);

        if (snapshot is null)
        {
            Description = "personnage en cours de chargement. Apparence précédente conservée";
            _log.Warning(Description);
            return;
        }

        var (resources, meta, glamourer, rawExtras, transientPaths, transientResolved) = snapshot.Value;

        if (resources is null)
        {
            Description = "aucun fichier reçu de Penumbra";
            return;
        }

        resources = WithTransients(resources, transientPaths, transientResolved);

        var classified = ResourcePathClassifier.Classify(
            resources.Select(kv => (kv.Key, (IReadOnlyCollection<string>)kv.Value)), Quotas.Default);

        // Hachage et copies hors du thread de jeu : plusieurs centaines de Mo
        // passent ici, et une seule image bloquée se voit.
        var resolved = new List<ResolvedFile>();
        var known = new Dictionary<string, (BlobHash Hash, long Size)>(StringComparer.OrdinalIgnoreCase);
        var hashed = 0;

        var files = await KeepPlayableAsync(classified.Files, ct).ConfigureAwait(false);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(file.LocalPath) is false)
            {
                // Penumbra a annoncé un fichier que le disque n'a plus. Le
                // signaler sans chemin complet : il porte le nom de l'utilisateur.
                _log.Warning($"Fichier annoncé par Penumbra mais absent du disque : {Path.GetFileName(file.LocalPath)}");
                continue;
            }

            if (known.TryGetValue(file.LocalPath, out var entry) is false)
            {
                var info = new FileInfo(file.LocalPath);
                var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc);

                // Le cache est ce qui rend une reconstruction abordable : un
                // redessin arrive à chaque changement de zone, et sans lui
                // chacun rehacherait huit cents mégaoctets pour aboutir au même
                // manifeste.
                if (_hashes.TryGet(file.LocalPath, stamp, out var cached))
                {
                    entry = (cached, stamp.Size);
                }
                else
                {
                    await using (var stream = File.OpenRead(file.LocalPath))
                        entry = (await BlobHash.OfStreamAsync(stream, ct).ConfigureAwait(false), stream.Length);

                    _hashes.Remember(file.LocalPath, stamp, entry.Hash);
                    hashed++;
                }

                known[file.LocalPath] = entry;
                await StoreAsync(file.LocalPath, entry.Hash, entry.Size, ct).ConfigureAwait(false);
            }

            foreach (var gamePath in file.GamePaths)
                resolved.Add(new ResolvedFile(gamePath, entry.Hash, entry.Size));
        }

        var build = ManifestBuilder.Build(resolved, meta, glamourer, Quotas.Default, classified.Swaps);

        // Nettoyés ici, hors du thread du jeu, avant de rien annoncer : un
        // nom, un ContentId ou un GUID qui relie nos personnages ne quitte pas
        // cette machine. Un extra qui ne se nettoie pas est omis plutôt
        // qu'envoyé tel quel.
        var extras = Clean(rawExtras);
        var manifest = build.Manifest with { Extras = extras.IsEmpty ? null : extras };

        // Une apparence vide remplacerait la précédente, et reviendrait à
        // annoncer aux pairs qu'on n'a plus aucun mod. Le cas arrive pour de
        // bon : il suffit d'avoir désactivé sa collection Penumbra le temps
        // d'un essai. On garde la précédente plutôt que de laisser
        // l'utilisateur le découvrir en se voyant nu chez les autres.
        if (manifest.Replacements.Count == 0 && _current is { Replacements.Count: > 0 })
        {
            Description = "aucun fichier moddé trouvé. Apparence précédente conservée";
            _log.Warning(Description);
            return;
        }

        // Le moteur compare par référence pour décider s'il doit réannoncer.
        // Rendre une instance neuve à chaque reconstruction ferait réannoncer à
        // tous les pairs à chaque changement de zone, et chacun redemanderait
        // le manifeste : une tempête pour une apparence identique.
        if (_current is { } previous && ManifestCodec.HashOf(previous) == ManifestCodec.HashOf(manifest))
        {
            Description = $"inchangée, {hashed} fichier(s) vérifié(s) à nouveau";
            return;
        }

        // Forget() a été appelé pendant le travail ci-dessus (cache perdu,
        // personnage quitté) : ce résultat ne concerne plus rien de suivi.
        if (Interlocked.Read(ref _generation) != generation)
        {
            Description = "préparation annulée : le personnage suivi a changé";
            return;
        }

        _current = manifest;

        Description = $"{manifest.Replacements.Count} fichiers, "
                    + $"{manifest.Replacements.Sum(r => r.GamePaths.Count)} éléments du jeu, "
                    + $"{manifest.SwapsOrNone.Count} échange(s), "
                    + $"{known.Values.Sum(e => e.Size) / 1024 / 1024} Mo, {hashed} fichier(s) vérifié(s)"
                    + $"{(build.Skipped.Count > 0 ? $", {build.Skipped.Count} écartés" : "")}";

        _log.Information($"Apparence locale construite : {Description}");
    }

    /// <summary>Range un fichier dans le cache, sous son hash pour seul nom.</summary>
    /// <remarks>
    /// Par le magasin et non par une copie à la main : c'est lui qui écrit à
    /// côté puis publie après vérification, de sorte qu'un arrêt brutal ne
    /// laisse jamais un blob visible et tronqué.
    /// </remarks>
    /// <summary>
    /// Lit ressources, métadonnées et état Glamourer, une fois le personnage
    /// entièrement chargé. Null s'il ne l'est toujours pas au bout de dix secondes.
    /// </summary>
    /// <remarks>
    /// Le contrôle et la lecture se font dans le même passage sur le thread du
    /// framework : entre les deux, un redessin pourrait sinon recommencer.
    /// </remarks>
    ///
    /// Les animations retenues pour le job courant sont re-résolues dans le
    /// même passage, par la collection du joueur telle qu'elle est maintenant :
    /// un mod désactivé depuis ne doit plus rien faire partir.
    /// </remarks>
    private async Task<(IReadOnlyDictionary<string, HashSet<string>>? Resources, string Meta, string? Glamourer,
            CharacterExtras Extras, string[] TransientPaths, string[] TransientResolved)?>
        ReadWhenDrawnAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var snapshot = await _framework.RunOnFrameworkThread(() =>
            {
                if (_objects[PlayerIndex] is not { } local || DrawReadiness.IsReady(local) is false)
                    return null;

                var job = local is ICharacter character ? character.ClassJob.RowId : 0;
                var transients = _transients.Collect(job);

                return ((IReadOnlyDictionary<string, HashSet<string>>?, string, string?, CharacterExtras, string[], string[])?)(
                    _penumbra.ResourcePathsOf(PlayerIndex),
                    _penumbra.MetaManipulations(),
                    _glamourer.StateOf(PlayerIndex),
                    _extras.ReadLocal(local),
                    transients,
                    _penumbra.ResolvePlayer(transients));
            }).ConfigureAwait(false);

            if (snapshot is not null)
                return snapshot;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Écarte les animations que notre squelette ne sait pas jouer.</summary>
    /// <remarks>
    /// Les fichiers sont lus ici, hors du thread du jeu ; seul le chargement
    /// Havok, court, passe sur le thread du framework, et une seule fois par
    /// fichier tant qu'il ne change pas.
    /// </remarks>
    private async Task<IReadOnlyList<LocalFile>> KeepPlayableAsync(IReadOnlyList<LocalFile> files, CancellationToken ct)
    {
        static bool IsPap(string gamePath) => gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase);

        var animations = files.Where(f => f.GamePaths.Any(IsPap) && File.Exists(f.LocalPath)).ToList();

        if (animations.Count == 0)
            return files;

        var pending = new List<(string LocalPath, FileStamp Stamp, byte[] Buffer, int Length)>();

        try
        {
            foreach (var file in animations)
            {
                ct.ThrowIfCancellationRequested();

                var info = new FileInfo(file.LocalPath);
                var stamp = new FileStamp(info.Length, info.LastWriteTimeUtc);

                if (_papBones.TryGetValue(file.LocalPath, out var known) && known.Stamp == stamp)
                    continue;

                var gamePath = file.GamePaths.First(IsPap);

                try
                {
                    if (PapSkeletonCheck.TryReadHavok(file.LocalPath, gamePath, out var buffer, out var length))
                        pending.Add((file.LocalPath, stamp, buffer, length));
                    else
                        _papBones[file.LocalPath] = (stamp, null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Pas mis en cache : un fichier verrouillé un instant se
                    // relira à la construction suivante.
                    _log.Debug($"Animation illisible pour l'instant : {Path.GetFileName(file.LocalPath)} ({e.GetType().Name})");
                }
            }

            var bones = await _framework.RunOnFrameworkThread(() =>
            {
                foreach (var (localPath, stamp, buffer, length) in pending)
                    _papBones[localPath] = (stamp, PapSkeletonCheck.MaxAnimatedBone(buffer.AsSpan(0, length)));

                return _objects[PlayerIndex] is { } local ? PapSkeletonCheck.BoneCount(local) : null;
            }).ConfigureAwait(false);

            // Sans squelette lisible, on ne juge pas : mieux vaut annoncer ce
            // que notre propre jeu joue déjà que de tout retirer sur un doute.
            if (bones is not { } count)
                return files;

            var refused = animations
                .Where(f => _papBones.TryGetValue(f.LocalPath, out var seen) && (seen.MaxBone is null || seen.MaxBone >= count))
                .ToHashSet();

            if (refused.Count == 0)
                return files;

            var gamePaths = refused.SelectMany(f => f.GamePaths).ToList();
            _transients.Settle(gamePaths);

            // Des chemins de jeu et des noms de fichier seulement : un chemin
            // local complet porte le nom de l'utilisateur.
            _log.Warning($"{refused.Count} animation(s) non annoncée(s), hors de notre squelette ({count} os) ou illisible(s) : "
                       + string.Join(", ", refused.Take(5).Select(f => $"{f.GamePaths[0]} ({Path.GetFileName(f.LocalPath)})")));

            return files.Where(f => refused.Contains(f) is false).ToList();
        }
        finally
        {
            foreach (var (_, _, buffer, _) in pending)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Ajoute aux ressources les animations retenues qui sont encore moddées.</summary>
    /// <remarks>
    /// Celles qui ne le sont plus sont oubliées : leur mod a été retiré ou
    /// désactivé, et les annoncer ferait poser chez l'autre ce qu'on ne porte
    /// plus.
    /// </remarks>
    private IReadOnlyDictionary<string, HashSet<string>> WithTransients(
        IReadOnlyDictionary<string, HashSet<string>> resources, string[] paths, string[] resolved)
    {
        if (paths.Length == 0 || resolved.Length != paths.Length)
        {
            _transients.Settle([]);
            return resources;
        }

        var merged = resources.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        var vanilla = new List<string>();

        for (var i = 0; i < paths.Length; i++)
        {
            if (TransientPath.TryModded(paths[i], resolved[i], out var actual) is false)
            {
                vanilla.Add(paths[i]);
                continue;
            }

            if (merged.TryGetValue(actual, out var gamePaths) is false)
                merged[actual] = gamePaths = [];

            gamePaths.Add(paths[i]);
        }

        _transients.Settle(vanilla);

        if (vanilla.Count > 0)
            _log.Debug($"{vanilla.Count} animation(s) retenue(s) redevenue(s) vanilla, oubliée(s).");

        return merged;
    }

    private CharacterExtras Clean(CharacterExtras raw)
    {
        var key = _moodlesKey();

        var cleaned = new CharacterExtras(
            raw.CustomizePlus,
            raw.Heels is { } heels ? HeelsSanitizer.Sanitize(heels) : null,
            raw.Honorific,
            raw.Moodles is { } moodles && key is not null ? MoodlesSanitizer.Sanitize(moodles, key) : null,
            raw.PetNicknames is { } pets ? PetNicknamesData.Neutralize(pets) : null);

        // Ce que le receveur refuserait est omis ici, un par un : sinon un seul
        // extra trop gros ferait rejeter notre apparence entière chez chacun.
        var kept = ExtrasValidator.KeepValid(cleaned, Quotas.Default, out var dropped);

        if (dropped.Count > 0)
            _log.Warning($"Omis de l'apparence annoncée, hors des bornes de réception : {string.Join(", ", dropped)}.");

        return kept;
    }

    private async Task StoreAsync(string source, BlobHash hash, long size, CancellationToken ct)
    {
        if (_store.TryGetSize(hash, out _))
            return;

        await using var writer = await _store.BeginWriteAsync(hash, size, ct).ConfigureAwait(false);
        await using var input = File.OpenRead(source);

        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);

            if (read == 0)
                break;

            await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        var result = await writer.CommitAsync(ct).ConfigureAwait(false);

        if (result.Accepted is false)
            _log.Warning($"Blob refusé par le cache : {result.Rejection}");
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
        _oneAtATime.Dispose();
    }
}
