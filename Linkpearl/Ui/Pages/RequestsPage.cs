using Dalamud.Bindings.ImGui;
using Linkpearl.Core.Groups;
using Linkpearl.Integration;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui.Pages;

/// <summary>
/// Les demandes de pairage reçues, puis les demandes d'entrée dans un groupe
/// qu'il nous revient de valider.
/// </summary>
/// <remarks>
/// Le repère qui compte est affiché avant les boutons : cette personne est-elle
/// devant vous ? C'est la seule vérification qu'un joueur fera réellement, et
/// elle vaut mieux qu'une empreinte à comparer.
/// </remarks>
internal sealed class RequestsPage(
    PluginState state, PresenceService presence,
    Action<IncomingRequest> accept, Action<IncomingRequest> decline,
    Func<IReadOnlyList<PendingValidation>> admissions,
    Action<PendingValidation> approve, Action<PendingValidation> declineAdmission)
{
    public int Count => presence.RequestCount + admissions().Count;

    /// <summary>La personne qui demande est-elle devant nous ?</summary>
    public static bool IsVisible(PluginState state, IncomingRequest request)
        => state.Nearby.Any(player => string.Equals(player.Name, request.CharacterName, StringComparison.Ordinal));

    /// <summary>Le candidat est-il devant nous ?</summary>
    /// <remarks>
    /// Par nom et par monde : le candidat les donne tous deux, et un homonyme
    /// d'un autre monde croisé par hasard ne doit pas passer pour lui.
    /// </remarks>
    public static bool IsVisible(PluginState state, PendingValidation pending)
        => state.Nearby.Any(player => player.WorldId == pending.WorldId
                                   && string.Equals(player.Name, pending.CharacterName, StringComparison.Ordinal));

    public void Draw()
    {
        var requests = presence.PeekRequests();
        var pending = admissions();

        Text.PageHeader("Demandes",
            "Demandes de partage d'apparences ou d'entrée dans un groupe.");

        if (requests.Count == 0 && pending.Count == 0)
        {
            Feedback.EmptyState(
                Icons.Requests,
                "Aucune demande en attente",
                "Les demandes arrivent en jeu, sans quitter le client.");

            return;
        }

        foreach (var request in requests)
        {
            var visible = IsVisible(state, request);

            using var card = Card.Begin($"request_{request.Id.ToHex()}", accent: Theme.Accent);

            Text.H2(request.CharacterName);
            ImGui.Dummy(Theme.S(0f, Theme.GapXs));

            Chip.Draw(
                visible ? "visible à proximité" : "hors de vue",
                visible ? Theme.Online : Theme.Idle,
                visible ? Icons.Character : Icons.Warning);

            ImGui.Dummy(Theme.S(0f, Theme.GapXs));
            DrawRecognitionHint(visible);
            ImGui.Dummy(Theme.S(0f, Theme.GapS));

            if (Btn.Draw("Accepter", BtnTone.Action, BtnSize.Small, Icons.Accept,
                         id: $"accept_{request.Id.ToHex()}"))
                accept(request);

            ImGui.SameLine(0f, Theme.S(Theme.GapS));

            if (Btn.Draw("Refuser", BtnTone.Ghost, BtnSize.Small, Icons.Decline,
                         id: $"decline_{request.Id.ToHex()}"))
                decline(request);
        }

        foreach (var admission in pending)
            DrawAdmission(admission);
    }

    /// <summary>Une demande d'entrée dans un groupe, que nous validons comme propriétaire ou modérateur.</summary>
    private void DrawAdmission(PendingValidation pending)
    {
        var id = Convert.ToHexString(pending.Nonce);
        var visible = IsVisible(state, pending);

        using var card = Card.Begin($"admission_{id}", accent: Theme.Accent);

        // Le nom du candidat et celui du groupe viennent du réseau.
        Text.H2(Glyphs.Safe(pending.CharacterName));
        Text.Small($"veut rejoindre {Glyphs.Safe(pending.GroupName)}", Theme.TextMuted);
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));

        Chip.Draw(
            visible ? "visible à proximité" : "hors de vue",
            visible ? Theme.Online : Theme.Idle,
            visible ? Icons.Character : Icons.Warning);

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        DrawRecognitionHint(visible);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        if (Btn.Draw("Accepter", BtnTone.Action, BtnSize.Small, Icons.Accept, id: $"admission_accept_{id}"))
            approve(pending);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Refuser", BtnTone.Ghost, BtnSize.Small, Icons.Decline, id: $"admission_decline_{id}"))
            declineAdmission(pending);
    }

    /// <summary>Le repère de confiance montré avant toute acceptation.</summary>
    public static void DrawRecognitionHint(bool visible)
    {
        Text.Wrapped("N'accepter que si vous reconnaissez ce personnage.", Theme.TextMuted);

        if (visible is false)
            Text.Wrapped("Linkpearl ne peut pas confirmer qu'il se trouve actuellement à proximité.", Theme.Idle);
    }
}
