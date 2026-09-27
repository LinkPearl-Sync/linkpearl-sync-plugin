using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui.Pages;

/// <summary>Où en est une sauvegarde ou une restauration, écrit par le plugin, lu par la carte.</summary>
public sealed class BackupState
{
    public volatile bool Running;

    public string? Message { get; set; }

    public bool Failed { get; set; }

    /// <summary>Le fichier choisi pour une restauration, en attente de son mot de passe.</summary>
    public string? AwaitingPassword { get; set; }
}

/// <summary>
/// Sauvegarder ses personnages dans un fichier, et les restaurer.
/// </summary>
/// <remarks>
/// C'est ce qui survit à une réinstallation : sur disque, l'identité est liée au
/// compte Windows, et copier le dossier de configuration ne suffit pas.
///
/// L'utilisateur manipule un fichier et, s'il le veut, un mot de passe. Jamais
/// une clé.
/// </remarks>
internal sealed class BackupCard(BackupState state, Action<string, string?> backup, Action<string, string?> restore)
{
    private const string Filter = "Sauvegarde Linkpearl{.lpbackup}";

    private readonly FileDialogManager _dialogs = new();

    private string _password = "";
    private string _confirmation = "";
    private string _restorePassword = "";

    /// <summary>Les fenêtres de choix de fichier, dessinées à chaque image quelle que soit la page.</summary>
    public void DrawDialogs() => _dialogs.Draw();

    public void Draw()
    {
        if (state.AwaitingPassword is { } path)
            DrawPasswordPrompt(path);
        else
            DrawActions();

        if (state.Message is { } message)
        {
            ImGui.Dummy(Theme.S(0f, Theme.GapS));
            Text.Small(message, state.Failed ? Theme.Danger : Theme.Online);
        }
    }

    private void DrawActions()
    {
        ImGui.SetNextItemWidth(Theme.S(260f));
        ImGui.InputTextWithHint("##backup_password", "Mot de passe (facultatif)", ref _password, 128,
                                ImGuiInputTextFlags.Password);

        var mismatch = false;

        if (_password.Length > 0)
        {
            ImGui.SetNextItemWidth(Theme.S(260f));
            ImGui.InputTextWithHint("##backup_confirmation", "Le même, une seconde fois", ref _confirmation, 128,
                                    ImGuiInputTextFlags.Password);

            mismatch = _password != _confirmation;

            if (mismatch && _confirmation.Length > 0)
                Text.Small("Les deux mots de passe diffèrent.", Theme.Idle);
        }
        else
        {
            // Dit avant le clic, pas après : ce fichier finira sur un nuage ou
            // dans une conversation. Reste visible (sécurité), une ligne.
            Text.Small("Sans mot de passe, toute personne possédant ce fichier peut reprendre vos personnages et leurs pairages.",
                       Theme.Idle);
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        if (Btn.Draw("Sauvegarder…", BtnTone.Action, BtnSize.Small, Icons.Backup,
                     disabled: state.Running || mismatch, id: "backup_save"))
        {
            var password = _password.Length > 0 ? _password : null;

            _dialogs.SaveFileDialog("Sauvegarder les personnages et leurs pairages", Filter, "Linkpearl", ".lpbackup",
                (chosen, destination) =>
                {
                    // La fenêtre de Dalamud n'ajoute pas l'extension : sans
                    // elle, le fichier n'apparaîtrait pas à la restauration,
                    // qui filtre sur .lpbackup.
                    if (chosen)
                        backup(Path.ChangeExtension(destination, ".lpbackup"), password);
                },
                Documents(), isModal: false);
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Restaurer…", BtnTone.Secondary, BtnSize.Small, Icons.Restore,
                     disabled: state.Running, id: "backup_restore",
                     tooltip: "Remplace les personnages et leurs pairages par ceux de la sauvegarde. "
                            + "Une copie de l'identité Linkpearl actuelle est conservée."))
        {
            _dialogs.OpenFileDialog("Restaurer une sauvegarde Linkpearl", Filter,
                (chosen, paths) =>
                {
                    if (chosen && paths.Count > 0)
                        restore(paths[0], null);
                },
                1, Documents(), isModal: false);
        }

        // L'explication de ce que fait une sauvegarde, sortie de la carte :
        // les deux boutons parlent d'eux-mêmes, le pourquoi attend le survol.
        Feedback.Hint(
            "Tous les personnages et leurs pairs dans un seul fichier. Après une réinstallation de "
          + "Windows ou un changement de PC, restaurer ce fichier évite de refaire chaque pairage.");
    }

    private void DrawPasswordPrompt(string path)
    {
        Text.Small("Cette sauvegarde est protégée par un mot de passe.");
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));

        ImGui.SetNextItemWidth(Theme.S(260f));
        var submitted = ImGui.InputTextWithHint("##restore_password", "Mot de passe", ref _restorePassword, 128,
                                                ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        submitted |= Btn.Draw("Restaurer", BtnTone.Action, BtnSize.Small, Icons.Restore,
                              disabled: state.Running || _restorePassword.Length == 0, id: "restore_confirm");

        if (submitted && state.Running is false && _restorePassword.Length > 0)
        {
            restore(path, _restorePassword);
            _restorePassword = "";
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Annuler", BtnTone.Ghost, BtnSize.Small, id: "restore_cancel"))
        {
            state.AwaitingPassword = null;
            state.Message = null;
            _restorePassword = "";
        }
    }

    private static string Documents() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
