using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Sync;

namespace Linkpearl.Integration.Extras;

/// <summary>
/// Les cinq plugins voisins, derrière une seule façade.
/// </summary>
/// <remarks>
/// Un plugin absent, d'une version qui ne convient pas, ou qui lève en cours
/// d'appel, n'empêche jamais les autres : chacun est essayé à part, et son
/// échec n'est signalé qu'une fois dans le journal, pas à chaque image.
///
/// Tout appel se fait depuis le thread du framework.
/// </remarks>
public sealed class ExtrasIpc : IDisposable
{
    private readonly CustomizePlusIpc _customize;
    private readonly HeelsIpc _heels;
    private readonly HonorificIpc _honorific;
    private readonly MoodlesIpc _moodles;
    private readonly PetNicknamesIpc _pets;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _reported = [];

    public ExtrasIpc(IDalamudPluginInterface pi, IObjectTable objects, IPluginLog log)
    {
        _log = log;
        _customize = new CustomizePlusIpc(pi);
        _heels = new HeelsIpc(pi);
        _honorific = new HonorificIpc(pi);
        _moodles = new MoodlesIpc(pi, () => objects.LocalPlayer?.Address ?? nint.Zero);
        _pets = new PetNicknamesIpc(pi);

        _customize.Changed += RaiseChanged;
        _heels.Changed += RaiseChanged;
        _honorific.Changed += RaiseChanged;
        _moodles.Changed += RaiseChanged;
        _pets.Changed += RaiseChanged;

        _honorific.Ready += RaiseReady;
        _moodles.Ready += RaiseReady;
        _pets.Ready += RaiseReady;
    }

    /// <summary>Notre état a changé chez l'un d'eux : l'apparence est à reconstruire.</summary>
    public event Action? Changed;

    /// <summary>Un plugin vient de (re)démarrer : ce qui était posé chez lui est perdu.</summary>
    public event Action? Ready;

    public CharacterExtras ReadLocal(IGameObject local) => new(
        Read("Customize+", _customize.IsAvailable, _customize.ReadLocal),
        Read("SimpleHeels", _heels.IsAvailable, _heels.ReadLocal),
        Read("Honorific", _honorific.IsAvailable, _honorific.ReadLocal),
        Read("Moodles", _moodles.IsAvailable, () => _moodles.ReadLocal(local.Address)),
        Read("PetNicknames", _pets.IsAvailable, _pets.ReadLocal));

    public void Apply(IGameObject target, CharacterExtras extras, ExtrasChange change)
    {
        if (change.CustomizePlus)
            Put("Customize+", _customize.IsAvailable, extras.CustomizePlus, d => _customize.Apply(target, d), () => _customize.Clear(target));

        if (change.Heels)
            Put("SimpleHeels", _heels.IsAvailable, extras.Heels, d => _heels.Apply(target, d), () => _heels.Clear(target));

        if (change.Honorific)
            Put("Honorific", _honorific.IsAvailable, extras.Honorific, d => _honorific.Apply(target, d), () => _honorific.Clear(target));

        if (change.Moodles)
            Put("Moodles", _moodles.IsAvailable, extras.Moodles, d => _moodles.Apply(target, d), () => _moodles.Clear(target));

        if (change.PetNicknames)
            Put("PetNicknames", _pets.IsAvailable, extras.PetNicknames, d => _pets.Apply(target, d), () => _pets.Clear(target));
    }

    public void Clear(IGameObject target) => Apply(target, CharacterExtras.None, ExtrasChange.All);

    private string? Read(string name, Func<bool> available, Func<string?> read)
    {
        try
        {
            return available() ? read() : null;
        }
        catch (Exception e)
        {
            ReportOnce(name, e);
            return null;
        }
    }

    private void Put(string name, Func<bool> available, string? data, Action<string> apply, Action clear)
    {
        try
        {
            if (available() is false)
                return;

            if (data is null)
                clear();
            else
                apply(data);
        }
        catch (Exception e)
        {
            ReportOnce(name, e);
        }
    }

    private void ReportOnce(string name, Exception e)
    {
        // Le type seul par défaut : le message d'une exception d'IPC ou de
        // disque porte volontiers un chemin local complet, le détail attend
        // le niveau Debug.
        if (_reported.Add(name) is false)
            return;

        _log.Warning($"{name} ne répond pas comme attendu, ignoré ({e.GetType().Name}).");
        _log.Debug(e, $"{name} ne répond pas comme attendu, ignoré.");
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void RaiseReady() => Ready?.Invoke();

    public void Dispose()
    {
        _customize.Dispose();
        _heels.Dispose();
        _honorific.Dispose();
        _moodles.Dispose();
        _pets.Dispose();
    }
}
