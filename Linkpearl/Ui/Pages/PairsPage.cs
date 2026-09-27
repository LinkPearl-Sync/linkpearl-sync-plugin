using System.Numerics;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Integration;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui.Pages;

/// <summary>
/// Le carnet de pairs, en listes par état.
/// </summary>
/// <remarks>
/// Des lignes et non des cartes : un carnet compte vite vingt entrées, et ce
/// qu'on y cherche est « qui est là », d'un coup d'œil. Les groupes répondent
/// à cette question avant même de lire un nom.
///
/// <b>Aucune clé n'y est montrée.</b> L'utilisateur voit des noms, qui sont
/// l'identité qui l'intéresse.
/// </remarks>
internal sealed class PairsPage(
    PairingService pairing, Func<IReadOnlyList<PeerStatus>> statuses,
    Action<PeerId, bool> setPaused, Action<PeerId> reapply, Action<PeerId> unpair,
    Action<PeerId, TransientCategories> setReceive, IServiceBans bans, BackupNudge backupNudge)
{
    private string _filter = "";

    /// <summary>Le pair dont le retrait attend un second clic, et jusqu'à quand.</summary>
    private (PeerId Id, DateTime Until)? _confirming;

    public int Count => pairing.Book.Listed.Count;

    public void Draw()
    {
        Text.PageHeader("Pairs",
            "Ce que chacun vous montre, et où en est le transfert.");

        var pairs = pairing.Book.Listed;

        if (pairs.Count == 0)
        {
            Feedback.EmptyState(
                Icons.Pairs,
                "Aucun pair",
                "Allez dans « Autour de vous » et demandez le pairage à quelqu'un qui utilise Linkpearl, "
              + "ou restaurez une sauvegarde depuis les réglages.");

            return;
        }

        DrawBackupNudge();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##filtre_pairs", "Filtrer par nom", ref _filter, 64);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var byPeer = statuses().ToDictionary(status => status.Peer);

        var shown = pairs
            .Where(pair => _filter.Length == 0
                        || pair.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var online  = new List<PairRecord>();
        var offline = new List<PairRecord>();
        var paused  = new List<PairRecord>();
        var blocked = new List<PairRecord>();

        foreach (var pair in shown)
        {
            byPeer.TryGetValue(pair.Id, out var status);

            (pair.Trust is PairTrust.Blocked ? blocked
             : pair.Paused ? paused
             : Linked(status) ? online
             : offline).Add(pair);
        }

        // « En attente de lien » et non « hors ligne » : on y trouve aussi ceux
        // que le moteur est en train de chercher, et ceux dont l'essai a échoué.
        // La puce de chacun dit lequel.
        Group("En ligne", online, byPeer, defaultOpen: true);
        Group("En attente de lien", offline, byPeer, defaultOpen: true);
        Group("En pause", paused, byPeer, defaultOpen: false);
        Group("Bloqués", blocked, byPeer, defaultOpen: false);

        if (shown.Count == 0)
            Text.Small("aucun pair ne correspond au filtre.", Theme.TextFaint);
    }

    /// <summary>
    /// Le rappel de sauvegarder, tant qu'aucune sauvegarde n'est faite.
    /// </summary>
    /// <remarks>
    /// Ici et pas ailleurs : c'est en regardant ses pairs qu'on mesure ce qu'une
    /// réinstallation ferait perdre. Le rappel unique dans le chat passait, et
    /// la carte des réglages ne se trouve qu'en la cherchant.
    /// </remarks>
    private void DrawBackupNudge()
    {
        if (backupNudge.Due() is false)
            return;

        using (Card.Begin("backup_nudge", interactive: false))
        {
            Text.WithIcon(Icons.Backup, "Pensez à sauvegarder", Theme.Idle, Theme.Text);
            ImGui.Dummy(Theme.S(0f, Theme.GapXs));
            Text.Wrapped("Vos pairs sont liés à ce PC. Sans sauvegarde, une réinstallation de Windows "
                       + "oblige à refaire chaque pairage.", Theme.TextMuted);
            ImGui.Dummy(Theme.S(0f, Theme.GapS));

            if (Btn.Draw("Sauvegarder", BtnTone.Action, BtnSize.Small, Icons.Backup, id: "nudge_backup"))
                backupNudge.Open();

            ImGui.SameLine(0f, Theme.S(Theme.GapS));

            if (Btn.Draw("Ne plus rappeler", BtnTone.Ghost, BtnSize.Small, id: "nudge_dismiss"))
                backupNudge.Dismiss();
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapM));
    }

    private void Group(string title, List<PairRecord> pairs, Dictionary<PeerId, PeerStatus> byPeer, bool defaultOpen)
    {
        if (pairs.Count == 0)
            return;

        if (Fold.Draw(title, pairs.Count, $"pairs_{title}", defaultOpen) is false)
            return;

        using var table = ImRaii.Table($"pairs_{title}", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.PadOuterX);

        if (table.Success is false)
            return;

        ImGui.TableSetupColumn("état", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("nom", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("statut", ImGuiTableColumnFlags.WidthFixed, Theme.S(190f));
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, (ImGui.GetFrameHeight() * 4f) + Theme.S(Theme.GapS * 3f));

        foreach (var pair in pairs)
        {
            byPeer.TryGetValue(pair.Id, out var status);
            Row(pair, status);
        }
    }

    private void Row(PairRecord pair, PeerStatus? status)
    {
        var id = pair.Id.ToHex();

        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + Theme.S(Theme.GapS));

        ImGui.TableNextColumn();
        AlignToFrame();
        Feedback.StatusDot(Tint(pair, status));

        ImGui.TableNextColumn();
        AlignToFrame();
        ImGui.TextColored(Theme.Text, Glyphs.Safe(pair.DisplayName));

        if (bans.Status(pair.PinnedFingerprint ?? default) is { Verdict: BanVerdict.Listed })
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            BanChip.Draw(bans, pair.PinnedFingerprint);
        }

        if (status is { State: not PeerSessionState.Disconnected, Route: { } route })
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            DrawRoute(pair, route);
        }

        if (status is { FingerprintDisputed: true })
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Text.Icon(Icons.Warning, Theme.Danger);
            Feedback.TooltipOnHover(
                "Ce pair utilise un autre personnage que celui avec lequel vous vous êtes pairés. "
              + "Son apparence n'est pas appliquée.");
        }
        else if (status is { LastFailure: { } failure })
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Text.Icon(Icons.Warning, Theme.Idle);
            Feedback.TooltipOnHover(failure);
        }

        ImGui.TableNextColumn();
        AlignToFrame();
        DrawState(pair, status);

        ImGui.TableNextColumn();

        if (pair.Paused)
        {
            if (Btn.Icon(Icons.Resume, $"resume_{id}", tooltip: "Reprendre"))
                setPaused(pair.Id, false);
        }
        else if (Btn.Icon(Icons.Paused, $"pause_{id}", tooltip: "Mettre en pause : la connexion est coupée et son apparence retirée"))
        {
            setPaused(pair.Id, true);
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        var canReapply = Linked(status);

        if (Btn.Icon(Icons.Refresh, $"reapply_{id}",
                     tooltip: canReapply ? "Réappliquer : redemander la dernière apparence et la reposer" : "Pas encore connecté",
                     disabled: canReapply is false))
            reapply(pair.Id);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        DrawReceive(pair, id);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        // Retirer se confirme par un second clic : un carnet ne se vide pas
        // par un geste qui glisse.
        var confirming = _confirming is { } c && c.Id == pair.Id && c.Until > DateTime.UtcNow;

        if (Btn.Icon(Icons.Remove, $"unpair_{id}",
                     tone: confirming ? BtnTone.Danger : BtnTone.Ghost,
                     tooltip: confirming ? "Cliquer encore pour retirer ce pair" : "Retirer ce pair"))
        {
            if (confirming)
            {
                _confirming = null;
                unpair(pair.Id);
            }
            else
            {
                _confirming = (pair.Id, DateTime.UtcNow.AddSeconds(4));
            }
        }
    }

    /// <summary>Le bouton des animations, VFX et sons de ce pair, et son menu.</summary>
    /// <remarks>
    /// Accentué dès qu'une catégorie est bloquée : sans cela, un pair dont on
    /// a coupé les sons il y a un mois paraîtrait simplement cassé.
    /// </remarks>
    private void DrawReceive(PairRecord pair, string id)
    {
        var receive = pair.Receive;
        var limited = receive != TransientCategories.All;

        if (Btn.Icon(Icons.Effects, $"effects_{id}",
                     tone: limited ? BtnTone.Secondary : BtnTone.Ghost,
                     tooltip: limited ? "Animations, VFX et sons : certains sont bloqués pour ce pair"
                                      : "Animations, VFX et sons reçus de ce pair"))
            ImGui.OpenPopup($"effets_{id}");

        using var popup = ImRaii.Popup($"effets_{id}");

        if (popup.Success is false)
            return;

        Text.Small("Recevoir de ce pair :");

        if (EffectsPicker.Draw(receive, $"fx_{id}") is { } changed)
            setReceive(pair.Id, changed);
    }

    /// <summary>Centre un texte d'une ligne sur la hauteur d'un bouton.</summary>
    private static void AlignToFrame()
        => ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));

    /// <summary>Une session est ouverte : le reste de la phase parle de son apparence.</summary>
    private static bool Linked(PeerStatus? status)
        => status?.Phase is PeerPhase.AwaitingAppearance or PeerPhase.Receiving
                         or PeerPhase.OutOfView or PeerPhase.Applied;

    private static Vector4 Tint(PairRecord pair, PeerStatus? status)
        => pair.Trust is PairTrust.Blocked ? Theme.Danger
         : pair.Paused ? Theme.Idle
         : status is null ? Theme.TextFaint
         : status.Phase switch
           {
               PeerPhase.Applied                       => Theme.Online,
               PeerPhase.Searching or PeerPhase.Failing => Theme.Idle,
               PeerPhase.Absent                        => Theme.TextFaint,
               _                                       => Theme.Accent,
           };

    /// <summary>
    /// Direct ou par le relais, et la latence.
    /// </summary>
    /// <remarks>
    /// Discret, parce que le chemin ne change rien à ce que l'on voit. Mais il
    /// dit qui connaît l'adresse de qui, et un joueur en relais seul doit
    /// pouvoir vérifier que c'est bien le cas.
    /// </remarks>
    private static void DrawRoute(PairRecord pair, PeerRoute route)
    {
        var latency = route.RoundTripMs > 0 ? $", {route.RoundTripMs} ms" : "";

        if (route.Relayed is false)
        {
            Text.Icon(Icons.Direct, Theme.TextFaint);
            Feedback.TooltipOnHover($"Connexion directe{latency}. Ce pair connaît votre adresse IP.");
            return;
        }

        // Relayé après un perçage raté, les adresses ont déjà circulé : seul le
        // relais choisi d'emblée garde la nôtre pour nous. Le dire autrement
        // promettrait une protection qui n'a pas eu lieu.
        var privacy = pair.Policy is ConnectionPolicy.RelayOnly
            ? "Votre adresse IP n'a pas été transmise à ce pair."
            : "La connexion directe a échoué, mais vos adresses IP ont été échangées pendant l'essai.";

        Text.Icon(Icons.Relayed, Theme.TextFaint);
        Feedback.TooltipOnHover(
            $"Connexion relayée par un service{latency} : les données passent par lui, chiffrées, sans qu'il puisse les lire. {privacy}");
    }

    /// <summary>La puce d'état, qui résume ce que le moteur sait du pair.</summary>
    private static void DrawState(PairRecord pair, PeerStatus? status)
    {
        if (pair.Trust is PairTrust.Blocked)
        {
            Chip.Draw("bloqué", Theme.Danger, Icons.Blocked);
            return;
        }

        if (pair.Paused)
        {
            Chip.Draw("en pause", Theme.Idle, Icons.Paused);
            return;
        }

        if (status is null)
        {
            Chip.Draw("recherche…", Theme.Idle, Icons.Waiting);
            return;
        }

        switch (status.Phase)
        {
            case PeerPhase.Searching:
                Chip.Draw("recherche…", Theme.Idle, Icons.Waiting);
                Feedback.TooltipOnHover("Linkpearl essaie de joindre ce pair. Cela peut prendre jusqu'à 25 secondes.");
                return;

            case PeerPhase.Absent:
                Chip.Draw("absent", Theme.TextFaint, Icons.Waiting);
                Feedback.TooltipOnHover(
                    "Ce pair ne répond pas : il est hors ligne, ou il vous a mis en pause. "
                  + "Linkpearl réessaie toutes les 30 secondes.");
                return;

            case PeerPhase.Failing:
                // Court : la colonne fait 190 px, et un libellé rogné perdait
                // justement le compte à rebours. L'échec se dit dans l'infobulle.
                Chip.Draw($"réessai {Countdown(status.NextAttempt)}", Theme.Idle, Icons.Warning);
                Feedback.TooltipOnHover($"Échec de la dernière tentative : {status.LastFailure ?? "raison inconnue"}.");
                return;

            case PeerPhase.Applied:
                Chip.Draw("apparence posée", Theme.Online, Icons.Applied);
                return;

            case PeerPhase.OutOfView:
                Chip.Draw("prêt, hors de vue", Theme.Accent, Icons.Connected);
                return;

            case PeerPhase.Receiving:
                var received = status.View.ReceivedBytes / 1024 / 1024;
                var total    = status.View.MissingBytes / 1024 / 1024;

                Chip.Draw($"réception {received} / {total} Mo", Theme.Idle, Icons.Receiving);
                return;

            default:
                Chip.Draw("attend son apparence", Theme.Accent, Icons.Connected);
                return;
        }
    }

    /// <summary>« dans 12 s », « dans 3 min », ou « imminent ».</summary>
    private static string Countdown(DateTimeOffset? next)
    {
        var left = (next ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;

        return left.TotalSeconds < 1 ? "imminent"
             : left.TotalSeconds < 60 ? $"dans {Math.Ceiling(left.TotalSeconds):0} s"
             : $"dans {Math.Ceiling(left.TotalMinutes):0} min";
    }
}

/// <summary>Le rappel de sauvegarde de la page Pairs : quand le montrer, où mener, comment le taire.</summary>
internal sealed record BackupNudge(Func<bool> Due, Action Open, Action Dismiss);
