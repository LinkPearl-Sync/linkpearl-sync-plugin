using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Linkpearl.Integration;
using Linkpearl.Core.Safety;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui.Pages;

/// <summary>
/// Les joueurs autour de soi qui utilisent Linkpearl, en listes.
/// </summary>
/// <remarks>
/// C'est la page du pairage, et elle est volontairement la première : on se
/// paire avec quelqu'un qu'on a devant soi, pas avec une clé reçue par message.
///
/// Seuls ceux qui utilisent le plugin sont listés. Une place bondée compte
/// couramment quarante joueurs, dont deux ou trois sont concernés : les lister
/// tous obligerait à chercher, pour une information dont on ne fait rien. Le
/// décompte des autres reste affiché, parce qu'une liste vide alors qu'il y a
/// foule ressemblerait à une panne de détection.
///
/// Des lignes et non des cartes, comme le carnet : un événement ou une place de
/// ville en montre vite une dizaine, et ce qu'on y cherche d'abord est qui
/// n'est pas encore pairé.
/// </remarks>
internal sealed class NearbyPage(
    PluginState state, PresenceService presence, PairingService pairing, Action<NearbyPlayer> requestPair,
    IServiceBans bans)
{
    private string _filter = "";

    public void Draw()
    {
        var users  = state.Nearby.Where(player => presence.Detected.ContainsKey(player.Fingerprint)).ToList();
        var others = state.Nearby.Count - users.Count;

        Text.PageHeader("À proximité",
            "Joueurs à portée utilisant Linkpearl et visibles par les autres.");

        if (users.Count == 0)
        {
            Feedback.EmptyState(
                Icons.Nearby,
                "Aucun joueur Linkpearl à proximité",
                others > 0
                    ? $"{others} joueur{(others > 1 ? "s" : "")} à portée, aucun ne se signale."
                    : "Se rapprocher d'un joueur. La liste suit les personnages visibles en jeu.");

            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##filtre_autour", "Filtrer par nom", ref _filter, 64);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        // Épinglée à la première rencontre, l'empreinte est ce qui relie un
        // joueur visible à une entrée du carnet. Un pair jamais rencontré n'en a
        // pas encore : on proposera le pairage, et le carnet refusera le doublon.
        var known = pairing.Book.Listed
            .Where(pair => pair.PinnedFingerprint is not null)
            .Select(pair => pair.PinnedFingerprint!.Value)
            .ToHashSet();

        var shown = users
            .Where(player => _filter.Length == 0
                          || player.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unpaired = shown.Where(player => known.Contains(player.Fingerprint) is false).ToList();
        var paired   = shown.Where(player => known.Contains(player.Fingerprint)).ToList();

        Group("À pairer", unpaired, isPaired: false);
        Group("Déjà pairés", paired, isPaired: true);

        if (shown.Count == 0)
            Text.Small("Aucun joueur ne correspond au filtre.", Theme.TextFaint);

        if (others == 0)
            return;

        ImGui.Dummy(Theme.S(0f, Theme.GapS));
        Text.Small($"{others} autre{(others > 1 ? "s" : "")} joueur{(others > 1 ? "s" : "")} à portée.",
                   Theme.TextFaint);
    }

    private void Group(string title, List<NearbyPlayer> players, bool isPaired)
    {
        if (players.Count == 0)
            return;

        if (Fold.Draw(title, players.Count, $"autour_{title}") is false)
            return;

        using var table = ImRaii.Table($"autour_{title}", 3, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.PadOuterX);

        if (table.Success is false)
            return;

        ImGui.TableSetupColumn("état", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("nom", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("action", ImGuiTableColumnFlags.WidthFixed, Theme.S(150f));

        foreach (var player in players)
            Row(player, isPaired);
    }

    private void Row(NearbyPlayer player, bool isPaired)
    {
        var id = player.Object.ObjectIndex;

        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + Theme.S(Theme.GapS));

        ImGui.TableNextColumn();
        AlignToFrame();
        Feedback.StatusDot(isPaired ? Theme.Online : Theme.Accent);

        ImGui.TableNextColumn();
        AlignToFrame();
        ImGui.TextColored(Theme.Text, Glyphs.Safe(player.Name));

        ImGui.TableNextColumn();

        if (isPaired)
        {
            // Proposer de se pairer avec quelqu'un qui l'est déjà enverrait
            // une demande que le carnet rejetterait, sans que rien ne le dise.
            AlignToFrame();
            Chip.Draw("déjà pairé", Theme.Online, Icons.Applied);
            return;
        }

        // Listé par un service actif : sa demande serait ignorée chez nous, et
        // la nôtre n'a pas lieu d'être. La puce remplace le bouton, motif au survol.
        if (bans.Status(player.Fingerprint) is { Verdict: BanVerdict.Listed })
        {
            AlignToFrame();
            BanChip.Draw(bans, player.Fingerprint);
            return;
        }

        // Une demande sans réponse n'expire pas de notre côté : un refus ne
        // revient jamais. On laisse donc renvoyer, en disant qu'on l'a déjà fait.
        var sent = presence.PendingOutgoing.ContainsKey(player.Fingerprint);

        // Pas d'orange ici, comme le veut la spec : un bouton par ligne, et huit
        // joueurs à portée feraient huit oranges qui ne désignent plus rien.
        if (Btn.Draw(sent ? "Renvoyer" : "Demander", sent ? BtnTone.Ghost : BtnTone.Secondary,
                     BtnSize.Small, Icons.Invite,
                     tooltip: sent ? "Demande déjà envoyée. Réponse en attente." : "Demander le pairage",
                     id: $"pair_{id}"))
            requestPair(player);
    }

    /// <summary>Centre un texte d'une ligne sur la hauteur d'un bouton.</summary>
    private static void AlignToFrame()
        => ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));
}
