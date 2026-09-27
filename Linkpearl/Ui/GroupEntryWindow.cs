using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Linkpearl.Core.Groups;
using Linkpearl.Ui.Components;
using Linkpearl.Ui.Shell;

namespace Linkpearl.Ui;

/// <summary>
/// La petite fenêtre qui rejoint ou crée un groupe.
/// </summary>
/// <remarks>
/// Une seule fenêtre à deux modes plutôt que deux fenêtres : on ne remplit
/// jamais les deux formulaires à la fois, le plugin n'a qu'une fenêtre de plus
/// à enregistrer et à nettoyer, et un clic sur l'autre bouton de la page
/// bascule la fenêtre déjà ouverte au lieu d'en empiler une deuxième.
///
/// Comme la page, elle ne touche jamais au carnet : tout passe par
/// <see cref="GroupActions"/>, depuis le thread du jeu où Dalamud la dessine.
/// </remarks>
public sealed class GroupEntryWindow : ThemedWindow
{
    private const float WindowWidth  = 460f;
    private const float WindowHeight = 250f;

    /// <summary>
    /// « ### » : l'identifiant ImGui ne suit pas le titre, sans quoi basculer
    /// de mode ouvrirait une autre fenêtre ailleurs à l'écran.
    /// </summary>
    private const string JoinTitle   = "Rejoindre un groupe###linkpearl_group_entry";
    private const string CreateTitle = "Créer un groupe###linkpearl_group_entry";

    private readonly AdmissionCandidate _candidate;
    private readonly GroupActions _actions;

    private bool _creating;

    private string _joinCode = "";
    private string _joinPassword = "";
    private string _createName = "";
    private string _createPassword = "";

    /// <summary>Vrai entre l'envoi d'une demande et son issue : c'est elle que la fenêtre attend pour se fermer.</summary>
    private bool _awaitingJoin;

    /// <summary>
    /// Vrai dès que la candidature a quitté le repos après l'envoi.
    /// </summary>
    /// <remarks>
    /// La demande part en tâche de fond : à l'image du clic, la candidature
    /// est encore au repos et porte peut-être le nom d'un groupe rejoint plus
    /// tôt. Sans ce témoin, la fenêtre se refermerait aussitôt ouverte.
    /// </remarks>
    private bool _sawProgress;

    public GroupEntryWindow(AdmissionCandidate candidate, GroupActions actions)
        : base(JoinTitle, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking)
    {
        _candidate = candidate;
        _actions   = actions;

        LogicalSizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(WindowWidth, WindowHeight),
            MaximumSize = new Vector2(WindowWidth, WindowHeight),
        };
    }

    public void OpenJoin() => Show(creating: false);

    public void OpenCreate() => Show(creating: true);

    /// <summary>Ferme et oublie les mots de passe saisis. Appelé aussi au déchargement du plugin.</summary>
    public void Close()
    {
        IsOpen = false;
        Forget();
    }

    private void Show(bool creating)
    {
        _creating = creating;
        WindowName = creating ? CreateTitle : JoinTitle;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        base.PreDraw();

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    /// <summary>Toute fermeture efface les mots de passe : la croix, Échap, ou la réussite.</summary>
    public override void OnClose() => Forget();

    private void Forget()
    {
        // Un mot de passe n'a pas à survivre dans un champ que plus personne
        // ne voit. Le code et le nom, eux, ne donnent rien à qui les lit.
        _joinPassword = "";
        _createPassword = "";
    }

    protected override void DrawContents()
    {
        if (_creating)
            DrawCreate();
        else
            DrawJoin();
    }

    private static float FieldWidth => Theme.S(260f);

    private void DrawJoin()
    {
        FollowCandidacy();

        Text.Small("Code fourni par le propriétaire ou un modérateur du groupe.", Theme.TextMuted);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##group_code", "XXXX-XXXX-XXXX@service", ref _joinCode, 300);

        ImGui.SetNextItemWidth(FieldWidth);
        ImGui.InputTextWithHint("##group_join_password", "Mot de passe (facultatif)", ref _joinPassword,
                                GroupPolicyCodec.MaxPasswordBytes, ImGuiInputTextFlags.Password);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        // Une candidature à la fois : en relancer une pendant qu'un membre
        // vérifie la preuve la remplacerait sous ses pieds.
        var busy = _candidate.State is CandidacyState.Waiting or CandidacyState.Proving;

        if (Btn.Draw("Rejoindre", BtnTone.Action, BtnSize.Small, Icons.Invite, id: "group_join",
                     disabled: busy || _joinCode.Trim().Length == 0))
        {
            _actions.Join(_joinCode, _joinPassword);
            _awaitingJoin = true;
            _sawProgress = false;

            // Effacé dès l'envoi : un mot de passe n'a pas à rester lisible
            // dans un champ pendant qu'on joue, fenêtre ouverte.
            _joinPassword = "";
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small("Laisser vide si chaque entrée doit être validée.", Theme.TextFaint);

        if (TryDescribe(_candidate, out var status, out var color) is false)
            return;

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        if (_candidate.State is not CandidacyState.Waiting)
        {
            Text.Small(status, color);
            return;
        }

        // Le texte se cale sur la hauteur du bouton qui le suit, sans quoi il
        // flotte au-dessus de la ligne.
        ImGui.AlignTextToFramePadding();
        Text.Small(status, color);
        ImGui.SameLine(0f, Theme.S(Theme.GapM));

        if (Btn.Draw("Annuler", BtnTone.Ghost, BtnSize.Small, Icons.Decline, id: "group_join_cancel"))
        {
            _actions.CancelJoin();
            _awaitingJoin = false;
        }
    }

    /// <summary>Ferme la fenêtre une fois le groupe rejoint : la page et le chat le disent déjà.</summary>
    private void FollowCandidacy()
    {
        if (_awaitingJoin is false)
            return;

        var state = _candidate.State;

        if (state is not CandidacyState.Idle)
            _sawProgress = true;

        if (state is CandidacyState.Joined
            || (state is CandidacyState.Idle && _sawProgress && _candidate.LastJoinedName is not null))
        {
            _awaitingJoin = false;
            _joinCode = "";
            Close();
        }
    }

    /// <summary>Écarte tout caractère qu'un nom de groupe ne peut pas porter.</summary>
    /// <remarks>En champ : le délégué passe au code natif, il ne doit pas être recréé à chaque image.</remarks>
    private static readonly ImGui.ImGuiInputTextCallbackDelegate KeepNameChars =
        (ref ImGuiInputTextCallbackData data) => GroupPolicyCodec.IsNameChar((char)data.EventChar) ? 0 : 1;

    /// <summary>L'indication d'un champ vide, pour un champ qui ne peut pas passer par InputTextWithHint.</summary>
    private static void DrawHint(string value, string hint)
    {
        if (value.Length > 0 || ImGui.IsItemActive())
            return;

        var padding = ImGui.GetStyle().FramePadding;
        var dl      = ImGui.GetWindowDrawList();

        // Rognée au cadre, comme le faisait InputTextWithHint : une indication
        // trop longue sortait du champ. Et en TextFaint : plus claire, elle se
        // prenait pour du texte déjà saisi.
        dl.PushClipRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), true);
        dl.AddText(ImGui.GetItemRectMin() + padding, ImGui.GetColorU32(Theme.TextFaint), hint);
        dl.PopClipRect();
    }

    private void DrawCreate()
    {
        Text.Small("Le créateur devient propriétaire du groupe, distribue le code et gère les entrées.", Theme.TextMuted);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        // Le tampon en octets, la limite en caractères : un nom accentué de
        // trente-deux caractères dépasse trente-deux octets, et c'est
        // IsCreatableName qui tranche. Les espaces et caractères spéciaux sont
        // refusés à la frappe comme au collage : le filtre d'ImGui voit les
        // deux. InputTextWithHint n'accepte pas de filtre dans les bindings de
        // Dalamud, d'où l'indication dessinée à la main.
        ImGui.SetNextItemWidth(FieldWidth);
        ImGui.InputText("##group_name", ref _createName, GroupPolicyCodec.MaxNameBytes,
                        ImGuiInputTextFlags.CallbackCharFilter, KeepNameChars);
        DrawHint(_createName, "Nom, sans espace");

        ImGui.SetNextItemWidth(FieldWidth);
        ImGui.InputTextWithHint("##group_create_password", "Mot de passe (facultatif)", ref _createPassword,
                                GroupPolicyCodec.MaxPasswordBytes, ImGuiInputTextFlags.Password);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Créer", BtnTone.Action, BtnSize.Small, Icons.Accept, id: "group_create",
                     disabled: GroupPolicyCodec.IsCreatableName(_createName) is false)
            && _actions.Create(_createName, _createPassword))
        {
            // Fermée seulement si le groupe existe : un refus est dit dans le
            // chat, et la saisie reste là pour être corrigée.
            _createName = "";
            Close();
        }

        // La conséquence suit la saisie : c'est le mot de passe, ou son
        // absence, qui choisit le mode d'admission du groupe à sa naissance.
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small(_createPassword.Length == 0
                       ? "Sans mot de passe, chaque entrée nécessite la validation du propriétaire ou d'un modérateur."
                       : "Avec le code et le mot de passe, l'entrée est immédiate.",
                   Theme.TextFaint);
    }

    /// <summary>
    /// Où en est la candidature, en une phrase et une couleur.
    /// </summary>
    /// <remarks>
    /// Partagé avec la page Groupes, qui montre la même ligne à qui a fermé la
    /// fenêtre : les deux ne peuvent pas se contredire.
    /// </remarks>
    public static bool TryDescribe(AdmissionCandidate candidate, out string text, out Vector4 color)
    {
        (text, color) = candidate.State switch
        {
            CandidacyState.Waiting       => ("Demande envoyée, en attente de la réponse du groupe.", Theme.Accent),
            CandidacyState.NeedsPassword => ("Ce groupe demande un mot de passe. Le saisir, puis réessayer.", Theme.Idle),
            CandidacyState.Proving       => ("Mot de passe envoyé…", Theme.Accent),
            CandidacyState.Refused       => (candidate.RefusalReason switch
            {
                RefusalReason.WrongPassword   => "Mot de passe incorrect.",
                RefusalReason.TooManyAttempts => "Trop d'essais. Réessayer dans une demi-heure.",
                _                             => "Demande refusée par un modérateur.",
            }, Theme.Danger),
            CandidacyState.Expired => ("Aucune réponse après dix minutes. Au moins un membre doit être en ligne.", Theme.Idle),
            CandidacyState.Idle when candidate.LastJoinedName is { } name
                => ($"Groupe {Glyphs.Safe(name)} rejoint.", Theme.Online),
            _ => ("", Theme.TextFaint),
        };

        return text.Length > 0;
    }
}
