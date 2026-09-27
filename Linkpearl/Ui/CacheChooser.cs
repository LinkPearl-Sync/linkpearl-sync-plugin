using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Linkpearl.Core.Cache;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui;

/// <summary>
/// Le dossier et le quota du cache, tels que la présentation, les réglages et
/// la page de blocage les montrent.
/// </summary>
/// <remarks>
/// Une instance par fenêtre : chacune dessine ses propres fenêtres de choix de
/// dossier. Le choix part sur le pool de threads, parce qu'il crée un dossier
/// et, après une perte, ouvre un cache entier.
/// </remarks>
internal sealed class CacheChooser(CacheKeeper keeper)
{
    private const float Mo = 1024f * 1024f;

    private readonly FileDialogManager _dialogs = new();

    private volatile string? _error;
    private volatile bool _busy;

    /// <summary>Le dossier dont on a lu l'espace libre, pour ne pas le relire à chaque image.</summary>
    private volatile string? _freeFor;
    private long _free = -1;

    /// <summary>La valeur du curseur pendant qu'on le tire : rien n'est écrit avant qu'on le lâche.</summary>
    private int? _dragging;

    /// <summary>Le quota affiché, curseur en cours de glissement compris.</summary>
    public int ShownQuotaGiB => _dragging ?? keeper.QuotaGiB;

    /// <summary>Les fenêtres de choix de dossier, à dessiner à chaque image.</summary>
    public void DrawDialogs() => _dialogs.Draw();

    public void Draw()
    {
        var shown = keeper.State is CacheGateState.Open && keeper.RestartPending is false
            ? keeper.ActiveRoot ?? keeper.ConfiguredRoot
            : keeper.ConfiguredRoot;

        DrawFolder(shown);
        ImGui.Dummy(Theme.S(0f, Theme.GapM));
        DrawQuota(shown);
    }

    private void DrawFolder(string shown)
    {
        var browse = Btn.Measure("Parcourir…", BtnSize.Small, Icons.Folder);
        var display = shown;

        ImGui.SetNextItemWidth(Card.Avail - browse - Theme.S(Theme.GapS) - Feedback.HintWidth);
        ImGui.InputText("##cache_folder", ref display, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Parcourir…", BtnTone.Secondary, BtnSize.Small, Icons.Folder, disabled: _busy, id: "cache_browse"))
            _dialogs.OpenFolderDialog("Dossier du cache Linkpearl", OnChosen, StartFolder(shown), isModal: false);

        // Le pourquoi (sous-dossier réservé, purge des plus anciennes) passe en
        // infobulle : la ligne d'état qui suit (erreur, redémarrage en
        // attente) est ce qui mérite de rester en clair, pas la répéter à
        // chaque image.
        Feedback.Hint(
            $"Linkpearl utilise uniquement le sous-dossier {CacheLocation.FolderName}. Les apparences reçues y "
          + "restent pour éviter un nouveau téléchargement. Une fois le quota atteint, les plus anciennes sont supprimées, sauf "
          + "celles affichées à l'écran.");

        if (_error is { } error)
            Text.Small(error, Theme.Danger);
        else if (keeper.RestartPending)
            Text.Small("Le nouveau dossier servira au prochain chargement du plugin.", Theme.Idle);
    }

    private void DrawQuota(string shown)
    {
        var quota = ShownQuotaGiB;

        Text.Label("Taille maximale du cache");
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        ImGui.SetNextItemWidth(Card.Avail);

        if (ImGui.SliderInt("##cache_quota", ref quota, CacheKeeper.MinQuotaGiB, CacheKeeper.MaxQuotaGiB, "%d Go"))
        {
            var step = CacheKeeper.QuotaStepGiB;
            _dragging = Math.Clamp((int)Math.Round(quota / (double)step) * step,
                                   CacheKeeper.MinQuotaGiB, CacheKeeper.MaxQuotaGiB);
        }

        if (ImGui.IsItemDeactivatedAfterEdit() && _dragging is { } chosen)
            keeper.SetQuota(chosen);

        if (ImGui.IsItemActive() is false)
            _dragging = null;

        if (_freeFor != shown)
        {
            _freeFor = shown;
            Interlocked.Exchange(ref _free, keeper.FreeSpace(shown));
        }

        var free = Interlocked.Read(ref _free);
        var appearances = (int)(ShownQuotaGiB * 1024f / 800f);

        // Une seule ligne faible : la maquette resserre en une ligne les deux
        // mesures (apparences, espace libre) qui tenaient chacune la leur.
        Text.Small(free < 0
            ? $"≈ {appearances} apparences"
            : $"≈ {appearances} apparences · {Format(free)} libres", Theme.TextFaint);

        if (free >= 0 && ShownQuotaGiB * CacheKeeper.GiB > free)
            Text.Small("La taille maximale dépasse l'espace libre. Le cache s'arrêtera une fois le disque plein.", Theme.Idle);
    }

    private void OnChosen(bool chosen, string path)
    {
        if (chosen is false)
            return;

        _busy = true;

        _ = Task.Run(() =>
        {
            try
            {
                _error = keeper.Choose(path);
            }
            catch (Exception e)
            {
                // Montré, pas avalé : l'utilisateur doit savoir que son choix
                // n'a pas été retenu.
                Plugin.Log.Warning($"Choix du dossier du cache en échec ({e.GetType().Name}).");
                _error = "Choix impossible. Vérifier que le dossier existe et reste accessible.";
            }
            finally
            {
                _freeFor = null;
                _busy = false;
            }
        });
    }

    /// <summary>Le dialogue s'ouvre sur le parent du cache, ou sur les documents.</summary>
    private static string StartFolder(string shown)
    {
        var parent = Path.GetDirectoryName(shown);

        return parent is not null && Directory.Exists(parent)
            ? parent
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public static string Format(long bytes)
        => bytes >= CacheKeeper.GiB
            ? $"{bytes / (double)CacheKeeper.GiB:0.#} Go"
            : $"{bytes / Mo:0} Mo";
}
