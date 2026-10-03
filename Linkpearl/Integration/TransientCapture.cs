using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;

namespace Linkpearl.Integration;

/// <summary>
/// Capte les animations, VFX et sons moddés de notre personnage, et s'en souvient.
/// </summary>
/// <remarks>
/// Penumbra ne dit qu'une animation est moddée qu'à l'instant où le jeu la
/// charge. On écoute donc ses résolutions, on garde celles de notre personnage,
/// et la mémoire par job permet de les annoncer même quand elles ne jouent pas :
/// une idle assise doit se voir chez l'autre sans qu'on se rassoie.
///
/// Le fichier de mémoire vit dans le dossier du personnage et ne contient que
/// des chemins de jeu : ni nom, ni monde, ni chemin local.
/// </remarks>
public sealed class TransientCapture : IDisposable
{
    private const string FileName = "transients.json";

    /// <summary>Ce qui n'a pas été revu depuis ce délai est oublié.</summary>
    /// <remarks>
    /// Un mod désinstallé dont l'animation ne rejoue jamais resterait sinon
    /// dans le fichier indéfiniment. La re-résolution l'écarte de l'annonce de
    /// toute façon, mais le fichier grossirait pour rien.
    /// </remarks>
    private static readonly TimeSpan Forgotten = TimeSpan.FromDays(30);

    private readonly PenumbraIpc _penumbra;
    private readonly IFramework _framework;
    private readonly IObjectTable _objects;
    private readonly IClock _clock;
    private readonly IPluginLog _log;

    /// <summary>Chemins vus depuis la dernière construction, en attente d'être retenus.</summary>
    private readonly ConcurrentQueue<string> _captured = new();

    /// <summary>Chemins déjà signalés sous le job courant.</summary>
    /// <remarks>
    /// Une animation se recharge chaque fois qu'elle joue : sans ce filtre, un
    /// combat relancerait une construction toutes les cinq secondes. Vidé au
    /// changement de job, pour qu'une animation revue sous un autre job y soit
    /// notée et devienne commune.
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _signalled = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();
    private TransientMemory? _memory;
    private string? _file;
    private bool _dirty;
    private uint? _lastJob;

    /// <summary>Adresse de notre personnage, relevée à chaque image.</summary>
    private long _local;

    public TransientCapture(PenumbraIpc penumbra, IFramework framework, IObjectTable objects, IClock clock, IPluginLog log)
    {
        _penumbra = penumbra;
        _framework = framework;
        _objects = objects;
        _clock = clock;
        _log = log;

        _framework.Update += TrackLocal;
        _penumbra.ResourceResolved += OnResolved;
    }

    /// <summary>Levé à la première vue d'une ressource moddée : l'apparence est à reconstruire.</summary>
    public event Action? Discovered;

    /// <summary>Charge la mémoire d'un personnage, ou la détache avec null.</summary>
    public void Attach(string? characterDirectory)
    {
        lock (_gate)
        {
            _memory = null;
            _file = null;
            _dirty = false;
            _lastJob = null;
            _captured.Clear();
            _signalled.Clear();

            if (characterDirectory is null)
                return;

            _file = Path.Combine(characterDirectory, FileName);

            try
            {
                _memory = File.Exists(_file)
                    ? TransientMemory.FromJson(File.ReadAllText(_file), _clock)
                    : new TransientMemory(_clock);
            }
            catch (IOException e)
            {
                _log.Warning($"Mémoire des animations illisible, repartie de zéro ({e.GetType().Name}).");
                _log.Debug(e, "Mémoire des animations illisible, repartie de zéro.");
                _memory = new TransientMemory(_clock);
            }
        }
    }

    /// <summary>
    /// Retient ce qui a été capté sous ce job, et rend les chemins à annoncer pour lui.
    /// </summary>
    public string[] Collect(uint job)
    {
        lock (_gate)
        {
            if (_lastJob != job)
            {
                _lastJob = job;
                _signalled.Clear();
            }

            if (_memory is null)
            {
                _captured.Clear();
                return [];
            }

            while (_captured.TryDequeue(out var gamePath))
                _dirty |= _memory.Record(gamePath, job);

            _dirty |= _memory.Purge(Forgotten) > 0;

            return [.. _memory.PathsFor(job)];
        }
    }

    /// <summary>Oublie ce qui n'est plus moddé, et sauvegarde si quelque chose a changé.</summary>
    public void Settle(IReadOnlyCollection<string> noLongerModded)
    {
        lock (_gate)
        {
            if (_memory is null || _file is null)
                return;

            foreach (var gamePath in noLongerModded)
                _memory.Forget(gamePath);

            _dirty |= noLongerModded.Count > 0;

            if (_dirty is false)
                return;

            try
            {
                var temporary = _file + ".part";
                File.WriteAllText(temporary, _memory.ToJson());
                File.Move(temporary, _file, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Perdre la sauvegarde ne coûte qu'un envoi incomplet après le
                // prochain lancement ; la construction suivante réessaiera.
                _log.Warning($"Mémoire des animations non sauvegardée ({e.GetType().Name}).");
                _log.Debug(e, "Mémoire des animations non sauvegardée.");
            }
        }
    }

    private void TrackLocal(IFramework framework)
        => Interlocked.Exchange(ref _local, _objects.LocalPlayer?.Address ?? 0);

    private void OnResolved(nint address, string gamePath, string resolved)
    {
        if (address == 0 || address != (nint)Interlocked.Read(ref _local))
            return;

        if (TransientCategories.IsTransient(gamePath) is false)
            return;

        if (TransientPath.TryModded(gamePath, resolved, out _) is false)
            return;

        var normalized = gamePath.Replace('\\', '/').ToLowerInvariant();

        if (_signalled.TryAdd(normalized, 0) is false)
            return;

        _captured.Enqueue(normalized);
        Discovered?.Invoke();
    }

    public void Dispose()
    {
        _penumbra.ResourceResolved -= OnResolved;
        _framework.Update -= TrackLocal;
    }
}
