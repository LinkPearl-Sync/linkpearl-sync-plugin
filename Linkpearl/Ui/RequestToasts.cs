using Dalamud.Bindings.ImGui;
using Linkpearl.Core.Groups;
using Linkpearl.Integration;
using Linkpearl.Ui.Components;
using Linkpearl.Ui.Pages;
using Linkpearl.Ui.Shell;
using System.Numerics;

namespace Linkpearl.Ui;

/// <summary>
/// Les demandes de pairage en attente, et les demandes d'entrée dans un groupe
/// qu'il nous revient de valider, en cartes empilées dans le coin bas
/// droit de l'écran, avec de quoi répondre sans ouvrir la fenêtre.
/// </summary>
/// <remarks>
/// Repris d'UmbraSync, que les joueurs visés connaissent : une demande arrive
/// pendant qu'on joue, fenêtre fermée, et un simple compteur dans la barre de
/// statut passerait inaperçu.
///
/// La fenêtre n'a ni fond ni bordure : seules les cartes se voient. Elle ne
/// prend pas le focus en apparaissant, pour ne pas voler la saisie d'un joueur
/// en train d'écrire dans le chat.
/// </remarks>
internal sealed class RequestToasts : ThemedWindow
{
    /// <summary>Au-delà, une ligne résume le reste : l'écran n'est pas à nous.</summary>
    private const int MaxShown = 3;

    private const float Width  = 320f;
    private const float Margin = 12f;

    private readonly PresenceService _presence;
    private readonly PluginState _state;
    private readonly Func<bool> _mainShowsRequests;
    private readonly Action _open;
    private readonly Action<IncomingRequest> _accept;
    private readonly Action<IncomingRequest> _decline;
    private readonly Func<IReadOnlyList<PendingValidation>> _admissions;
    private readonly Action<PendingValidation> _approve;
    private readonly Action<PendingValidation> _declineAdmission;

    public RequestToasts(
        PresenceService presence, PluginState state, Func<bool> mainShowsRequests, Action open,
        Action<IncomingRequest> accept, Action<IncomingRequest> decline,
        Func<IReadOnlyList<PendingValidation>> admissions,
        Action<PendingValidation> approve, Action<PendingValidation> declineAdmission)
        : base("Linkpearl : demandes##toasts",
               ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
             | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
             | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking)
    {
        _presence          = presence;
        _state             = state;
        _mainShowsRequests = mainShowsRequests;
        _open              = open;
        _accept            = accept;
        _decline           = decline;
        _admissions        = admissions;
        _approve           = approve;
        _declineAdmission  = declineAdmission;

        // Un flou derrière une fenêtre sans fond dessinerait un rectangle
        // brouillé autour des cartes.
        AllowBackgroundBlur = false;
        DisableWindowSounds = true;
        RespectCloseHotkey  = false;
        ForceMainWindow     = true;
        IsOpen              = true;
    }

    /// <remarks>
    /// Rien à montrer quand la fenêtre principale affiche déjà les demandes :
    /// les mêmes boutons deux fois à l'écran, c'est une de trop.
    /// </remarks>
    public override bool DrawConditions()
        => (_presence.RequestCount > 0 || _admissions().Count > 0) && _mainShowsRequests() is false;

    public override void PreDraw()
    {
        base.PreDraw();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        // Ancrée par son coin bas droit : la pile grandit vers le haut à mesure
        // que les demandes arrivent. Hauteur nulle, donc ajustée au contenu.
        var viewport = ImGui.GetMainViewport();
        var margin   = Theme.S(Margin);

        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize - new Vector2(margin, margin),
                               ImGuiCond.Always, Vector2.One);
        ImGui.SetNextWindowSize(new Vector2(Theme.S(Width), 0f), ImGuiCond.Always);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        base.PostDraw();
    }

    protected override void DrawContents()
    {
        var requests = _presence.PeekRequests();
        var admissions = _admissions();

        // Les pairages d'abord, comme dans la page : les deux sortes se
        // partagent la même limite, l'écran n'est pas plus grand pour autant.
        foreach (var request in requests.Take(MaxShown))
            DrawOne(request);

        foreach (var admission in admissions.Take(Math.Max(0, MaxShown - requests.Count)))
            DrawOne(admission);

        var total = requests.Count + admissions.Count;

        if (total <= MaxShown)
            return;

        var more = total - MaxShown;

        if (Btn.Draw($"{more} autre{(more > 1 ? "s" : "")} demande{(more > 1 ? "s" : "")}",
                     BtnTone.Secondary, BtnSize.Block, Icons.Requests, id: "toasts_more"))
            _open();
    }

    private void DrawOne(IncomingRequest request)
    {
        var id      = request.Id.ToHex();
        var visible = RequestsPage.IsVisible(_state, request);

        // Opaque : la fenêtre n'a pas de fond et flotte sur le décor du jeu,
        // une carte translucide s'y lirait mal sur la neige ou le sable.
        using var card = Card.Begin($"toast_{id}", background: Theme.BgSurface, accent: Theme.Accent);

        Text.WithIcon(Icons.Requests, "Demande de pairage", Theme.Accent, Theme.TextMuted);
        Text.H2(request.CharacterName);
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));

        Chip.Draw(
            visible ? "visible à proximité" : "hors de vue",
            visible ? Theme.Online : Theme.Idle,
            visible ? Icons.Character : Icons.Warning);

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        RequestsPage.DrawRecognitionHint(visible);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        if (Btn.Draw("Accepter", BtnTone.Action, BtnSize.Small, Icons.Accept, id: $"toast_accept_{id}"))
            _accept(request);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Refuser", BtnTone.Ghost, BtnSize.Small, Icons.Decline, id: $"toast_decline_{id}"))
            _decline(request);
    }

    private void DrawOne(PendingValidation pending)
    {
        var id      = Convert.ToHexString(pending.Nonce);
        var visible = RequestsPage.IsVisible(_state, pending);

        using var card = Card.Begin($"toast_admission_{id}", background: Theme.BgSurface, accent: Theme.Accent);

        Text.WithIcon(Icons.Groups, "Demande d'entrée dans un groupe", Theme.Accent, Theme.TextMuted);
        Text.H2(Glyphs.Safe(pending.CharacterName));
        Text.Small($"veut rejoindre {Glyphs.Safe(pending.GroupName)}", Theme.TextMuted);
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));

        Chip.Draw(
            visible ? "visible à proximité" : "hors de vue",
            visible ? Theme.Online : Theme.Idle,
            visible ? Icons.Character : Icons.Warning);

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        RequestsPage.DrawRecognitionHint(visible);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        if (Btn.Draw("Accepter", BtnTone.Action, BtnSize.Small, Icons.Accept, id: $"toast_admission_accept_{id}"))
            _approve(pending);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Refuser", BtnTone.Ghost, BtnSize.Small, Icons.Decline, id: $"toast_admission_decline_{id}"))
            _declineAdmission(pending);
    }
}
