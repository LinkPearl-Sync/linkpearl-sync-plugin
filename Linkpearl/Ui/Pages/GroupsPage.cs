using System.Numerics;
using System.Security.Cryptography;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui.Pages;

/// <summary>
/// Les groupes privés : en rejoindre un, en créer un, et gouverner ceux qu'on tient.
/// </summary>
/// <remarks>
/// La page ne fait que lire le carnet et la candidature : chaque geste passe
/// par <see cref="GroupActions"/>, qui décide du thread et du signataire. Elle
/// ne choisit donc jamais sous quelle clé une politique est signée.
///
/// <b>Aucune clé n'y est montrée</b>, pas plus que dans les pairs : un membre
/// se reconnaît à son nom. Seul le code du groupe s'affiche, et seulement à
/// ceux qui ont le droit de le distribuer.
///
/// Chaque groupe est une carte, et sa gestion des sous-cartes, une par
/// réglage, comme la page Réglages : un titre, un contrôle, puis la
/// conséquence écrite en clair. Gouverner un groupe engage d'autres joueurs,
/// ce qui se lit avant de cliquer et non au survol d'une icône.
/// </remarks>
internal sealed class GroupsPage(
    GroupBook groups, AdmissionCandidate candidate, Func<IReadOnlyList<PeerStatus>> statuses, GroupActions actions,
    GroupEntryWindow entry, IServiceBans bans)
{
    /// <summary>
    /// La dissolution n'est relayée que par le propriétaire : l'oublier trop
    /// tôt laisserait des membres dans un groupe qu'ils croient vivant.
    /// </summary>
    private const string DissolveReminder =
        "Les membres seront informés lors d'une prochaine rencontre. Garder le groupe dans la liste jusque-là.";

    private const string DissolveConsequence = "Le groupe est supprimé pour tous les membres. " + DissolveReminder;

    private const string NewCodeConsequence =
        "L'ancien code ne fonctionne plus. Les membres restent dans le groupe. Seuls les nouveaux "
      + "membres ont besoin du nouveau code.";

    private const string ConfirmTooltip = "Cliquer encore dans les quatre secondes pour confirmer.";

    /// <summary>Le nouveau mot de passe saisi, par groupe : deux groupes ouverts ne partagent pas un champ.</summary>
    private readonly Dictionary<GroupId, string> _newPasswords = [];

    /// <summary>Les groupes dont la gestion est dépliée.</summary>
    /// <remarks>Repliée par défaut : on y vient rarement, et rien ne doit s'y toucher par mégarde.</remarks>
    private readonly HashSet<GroupId> _managing = [];

    /// <summary>Carte dépliée ou non, par groupe, le temps de la session.</summary>
    private readonly Dictionary<GroupId, bool> _expanded = [];

    /// <summary>
    /// Jusqu'à ce nombre de groupes, leurs cartes s'ouvrent dépliées.
    /// </summary>
    /// <remarks>
    /// Une carte ouverte, membres et code compris, occupe vite la hauteur de la
    /// fenêtre : au-delà de deux, la page devient un défilement où l'on cherche
    /// son groupe. Replier par défaut garde tous les noms à l'écran d'un coup.
    /// </remarks>
    private const int MaxExpandedByDefault = 2;

    /// <summary>Le groupe dont le code vient d'être copié, et jusqu'à quand le bouton le dit.</summary>
    /// <remarks>Sans ce retour, rien ne distingue un clic réussi d'un clic perdu.</remarks>
    private (GroupId Id, DateTime Until)? _copied;

    /// <summary>Le geste qui attend un second clic, et jusqu'à quand.</summary>
    /// <remarks>Une clé textuelle plutôt qu'un identifiant : exclure, quitter et dissoudre se confirment tous ainsi.</remarks>
    private (string Key, DateTime Until)? _confirming;

    /// <summary>
    /// Fond des sous-cartes de gestion, un cran sous celui de la carte du groupe.
    /// </summary>
    /// <remarks>
    /// Posées sur une carte du même fond, elles ne se distingueraient que par
    /// leur bordure : enfoncées, elles se lisent comme les réglages d'un tout.
    /// </remarks>
    private static readonly Vector4 PanelBackground = Theme.Mix(Theme.BgSurface, Theme.BgBase, 0.6f);

    private static readonly Vector4 DangerBackground = Theme.Mix(PanelBackground, Theme.Danger, 0.06f);

    private static readonly Vector4 DangerBorder = Theme.Alpha(Theme.Danger, 0.35f);

    /// <summary>Largeur commune des champs courts, la même que ceux de la sauvegarde.</summary>
    private static float FieldWidth => Theme.S(260f);

    public void Draw()
    {
        Text.PageHeader("Groupes",
            "Un groupe partage les apparences entre tous ses membres, sans pairage individuel.");

        DrawPublic();
        DrawEntry();

        var all = groups.All.Where(group => group.IsPublic is false).ToList();

        if (all.Count == 0)
        {
            Feedback.EmptyState(Icons.Groups, "Aucun groupe", "Créer un groupe ou le rejoindre avec son code.");
            return;
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var known = statuses();
        var ours = actions.OurIdentityKey();

        foreach (var group in all.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
            DrawGroup(group, GroupGovernance.RoleOf(group, ours), known, all.Count);
    }

    private const string PublicWarning =
        "Le Mode public partage votre apparence avec tous les joueurs visibles qui l'ont activé, "
      + "même si vous ne les connaissez pas. Linkpearl ne peut pas prouver qu'un inconnu est bien "
      + "le personnage qu'il affiche : bloquez une apparence qui vous semble usurpée.";

    private const string ExcludedNotice =
        "Ce personnage a été exclu du groupe. Le groupe reste listé, sans partage d'apparence, "
      + "au cas où l'exclusion serait levée.";

    /// <summary>Vrai tant que l'avertissement attend sa réponse, avant la première activation.</summary>
    private bool _publicWarningOpen;

    /// <summary>
    /// L'interrupteur du Public, ses effets, ses joueurs rencontrés et bloqués.
    /// </summary>
    /// <remarks>
    /// En tête de page, avant les groupes privés : c'est le seul réglage de la
    /// page qui expose le joueur à des inconnus, et il doit se voir sans
    /// défiler. L'avertissement s'affiche à la première activation.
    /// </remarks>
    private void DrawPublic()
    {
        var @public = groups.Public;
        var enabled = @public is { Dormant: false };

        {
            // Une carte comme les groupes privés, accentuée tant qu'elle est
            // active : c'est l'état qui expose le joueur, il doit se voir de loin.
            using var card = Card.Begin("public_card", accent: enabled ? Theme.Online : null);

            DrawPublicHeader(enabled);

            ImGui.Dummy(Theme.S(0f, Theme.GapXs));
            Text.Small(enabled
                           ? "Les joueurs visibles avec le Mode public activé partagent leur apparence."
                           : "L'apparence reste visible uniquement par les pairs et les membres des groupes.",
                       Theme.TextFaint);

            if (_publicWarningOpen)
                DrawPublicWarning();

            if (@public is not null)
            {
                // Les effets ne se règlent qu'actif : désactivé, personne du
                // Public n'est reçu, et trois boutons sans effet brouilleraient
                // la carte. Les blocages, eux, restent visibles : ils survivent.
                if (enabled)
                {
                    ImGui.Dummy(Theme.S(0f, Theme.GapM));
                    DrawPublicEffects(@public);

                    if (@public.Members.Count > 0)
                    {
                        ImGui.Dummy(Theme.S(0f, Theme.GapM));
                        DrawPublicMembers(@public);
                    }
                }

                if (@public.Blocked.Count > 0)
                {
                    ImGui.Dummy(Theme.S(0f, Theme.GapM));
                    DrawBlocked(@public);
                }
            }
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapM));
    }

    /// <summary>Le titre de la carte, et l'interrupteur calé à droite comme les actions d'une ligne.</summary>
    private void DrawPublicHeader(bool enabled)
    {
        ImGui.AlignTextToFramePadding();
        Text.WithIcon(Icons.World, "Mode public", enabled ? Theme.Online : Theme.Accent);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        ImGui.AlignTextToFramePadding();
        Text.Small(enabled ? "activé" : "désactivé", enabled ? Theme.Online : Theme.TextFaint);

        var label = enabled ? "Désactiver le mode public" : "Activer le mode public";
        var icon = enabled ? Icons.Hidden : Icons.World;

        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        var room = ImGui.GetContentRegionAvail().X - Card.RightInset - Btn.Measure(label, BtnSize.Small, icon);

        if (room > 0f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room);

        // Pendant l'avertissement, le bouton se tait : c'est l'avertissement
        // qui porte la confirmation, et deux boutons « Activer » se liraient mal.
        if (Btn.Draw(label, BtnTone.Secondary, BtnSize.Small, icon,
                     id: "public_toggle", disabled: _publicWarningOpen))
        {
            if (enabled is false && actions.PublicWarningSeen() is false)
                _publicWarningOpen = true;
            else
                actions.SetPublic(enabled is false);
        }
    }

    /// <summary>Les effets reçus des joueurs du Public que l'on n'a pas réglés un par un.</summary>
    private void DrawPublicEffects(GroupRecord @public)
    {
        Text.Label("Effets reçus");

        if (EffectsPicker.Draw(@public.DefaultReceive, "public_fx") is { } changed)
            actions.SetDefaultReceive(PublicGroup.Id, changed);

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small("Réglage utilisé pour les joueurs sans réglage individuel.", Theme.TextFaint);
    }

    /// <summary>Les joueurs du Public déjà croisés, repliés par défaut.</summary>
    /// <remarks>Dans une foule, la liste dépliée repousserait les groupes privés hors de l'écran.</remarks>
    private void DrawPublicMembers(GroupRecord @public)
    {
        if (Fold.Draw("Joueurs rencontrés", @public.Members.Count, "public_members", defaultOpen: false) is false)
            return;

        using var scope = ImRaii.PushId("public");
        DrawMembers(@public, GroupRole.Member, statuses());
    }

    private void DrawPublicWarning()
    {
        ImGui.Dummy(Theme.S(0f, Theme.GapS));
        Feedback.Alert(Theme.Idle, Icons.Warning, PublicWarning);
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));

        if (Btn.Draw("Activer le mode public", BtnTone.Action, BtnSize.Small, Icons.World, id: "public_confirm"))
        {
            actions.AcknowledgePublicWarning();
            actions.SetPublic(true);
            _publicWarningOpen = false;
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Annuler", BtnTone.Secondary, BtnSize.Small, Icons.Close, id: "public_cancel"))
            _publicWarningOpen = false;
    }

    private void DrawBlocked(GroupRecord @public)
    {
        Text.Label($"Bloqués ({@public.Blocked.Count})");

        foreach (var (ban, index) in @public.Blocked.Select((ban, index) => (ban, index)))
        {
            using var scope = ImRaii.PushId(index);

            // Une clé bloquée seule reste du premier temps d'un déblocage :
            // son personnage est libre, elle ne se nomme donc plus par lui.
            var name = ban switch
            {
                { Fingerprint: { } print } when @public.Members.TryGetValue(print, out var member)
                    => Glyphs.Safe(member.DisplayName),
                { Fingerprint: null } => "identité Linkpearl bloquée",
                _ => "joueur bloqué",
            };

            ImGui.AlignTextToFramePadding();
            Text.Body(name);

            if (ban.Fingerprint is null)
                Feedback.TooltipOnHover(
                    "Celle qui se présentait sous un personnage débloqué. La débloquer la laisse revenir sous n'importe quel personnage.");

            // Calé à droite, comme « Lever » dans la liste des exclus.
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            var room = ImGui.GetContentRegionAvail().X - Card.RightInset - Btn.Measure("Débloquer", BtnSize.Small, Icons.Resume);

            if (room > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room);

            if (Btn.Draw("Débloquer", BtnTone.Ghost, BtnSize.Small, Icons.Resume, id: "unblock"))
                actions.Unblock(PublicGroup.Id, ban);
        }
    }

    /// <summary>Les deux portes d'entrée, et où en est une candidature pour qui a fermé sa fenêtre.</summary>
    /// <remarks>
    /// Rejoindre et créer vivent dans leur propre fenêtre : ce sont des gestes
    /// d'une fois, et leurs formulaires repoussaient la liste des groupes, ce
    /// qu'on vient voir tous les jours, sous la ligne de flottaison.
    /// </remarks>
    private void DrawEntry()
    {
        if (Btn.Draw("Rejoindre un groupe", BtnTone.Action, BtnSize.Small, Icons.Invite, id: "open_join"))
            entry.OpenJoin();

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Créer un groupe", BtnTone.Secondary, BtnSize.Small, Icons.Groups, id: "open_create"))
            entry.OpenCreate();

        if (GroupEntryWindow.TryDescribe(candidate, out var status, out var color))
        {
            ImGui.Dummy(Theme.S(0f, Theme.GapXs));
            Text.Small(status, color);

            // Tout sauf la réussite rouvre la fenêtre : c'est là qu'on annule,
            // qu'on saisit le mot de passe demandé ou qu'on relance.
            if (candidate.State is not CandidacyState.Idle)
            {
                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                Feedback.TooltipOnHover("Rouvrir la fenêtre de la demande");

                if (ImGui.IsItemClicked())
                    entry.OpenJoin();
            }
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapL));
    }

    private void DrawGroup(GroupRecord group, GroupRole role, IReadOnlyList<PeerStatus> known, int groupCount)
    {
        var id = group.Id.ToString();
        var policy = group.Policy;
        var dissolved = policy is { Dissolved: true };

        // Exclu, on ne montre que l'état : la liste des membres et la gestion
        // appartiennent à un groupe dont on ne fait plus partie, tant qu'une
        // politique plus récente ne nous a pas réintégrés.
        var excluded = actions.IsExcluded(group);

        // Sans politique, un modérateur n'est pas encore reconnu comme tel :
        // RoleOf rend alors « membre », et rien de la gestion n'est proposé.
        var manages = role is not GroupRole.Member && policy is not null && dissolved is false && excluded is false;

        // L'identifiant du groupe, jamais son nom : la carte garderait sinon
        // sa hauteur mémorisée d'un renommage à l'autre, et ses widgets
        // changeraient d'identifiant. Card.Begin pousse aussi cet identifiant
        // dans la pile d'ImGui, ce qui isole les boutons d'un groupe à l'autre.
        using var card = Card.Begin($"group_{id}", accent: dissolved || excluded ? Theme.Danger : null);

        // L'état est fixé à la première image où le groupe paraît, puis
        // mémorisé : recalculé à chaque image, l'arrivée d'un troisième groupe
        // replierait d'un coup ceux que l'on était en train de lire.
        if (_expanded.TryGetValue(group.Id, out var expanded) is false)
        {
            expanded = groupCount <= MaxExpandedByDefault;
            _expanded[group.Id] = expanded;
        }

        if (DrawHeader(group, role, policy, known, expanded, excluded))
        {
            expanded = !expanded;
            _expanded[group.Id] = expanded;
        }

        if (expanded is false)
            return;

        if (excluded)
        {
            ImGui.Dummy(Theme.S(0f, Theme.GapS));
            Text.Small(ExcludedNotice, Theme.TextFaint);
            ImGui.Dummy(Theme.S(0f, Theme.GapM));
            DrawFooter(group, role, dissolved, manages);
            return;
        }

        if (manages && GroupGovernance.CodeOf(group) is { } code)
            DrawInvite(group, policy!, code);

        ImGui.Dummy(Theme.S(0f, Theme.GapM));
        Text.Label("Membres");
        DrawMembers(group, role, known);

        ImGui.Dummy(Theme.S(0f, Theme.GapM));
        DrawFooter(group, role, dissolved, manages);

        if (manages && _managing.Contains(group.Id))
        {
            ImGui.Dummy(Theme.S(0f, Theme.GapM));
            DrawManagement(group, role, policy!);
        }
    }

    /// <summary>
    /// L'en-tête d'une carte de groupe : chevron, nom et puces, cliquable sur toute sa largeur.
    /// </summary>
    /// <returns>Vrai si l'en-tête vient d'être cliqué.</returns>
    /// <remarks>
    /// Le bouton invisible est posé après coup sur la surface dessinée : sa
    /// hauteur dépend des polices du nom et des puces, qu'on ne connaît
    /// qu'après les avoir dessinés. Rien d'interactif ne s'y trouve dessous,
    /// donc il ne vole aucun clic.
    /// </remarks>
    private static bool DrawHeader(GroupRecord group, GroupRole role, GroupPolicy? policy,
                                   IReadOnlyList<PeerStatus> known, bool expanded, bool excluded)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - Card.RightInset;

        var glyph = (expanded ? Icons.Expanded : Icons.Collapsed).S();
        var chevronWidth = ImGui.CalcTextSize(Icons.Expanded.S()).X;
        var gutter = chevronWidth + Theme.S(Theme.GapM);

        float nameHeight;

        using (Fonts.PushH2())
            nameHeight = ImGui.GetTextLineHeight();

        // Le chevron dans une réserve à gauche, centré sur la ligne du nom : en
        // police de corps posé à côté d'un titre, il flotterait au-dessus.
        ImGui.Dummy(new Vector2(chevronWidth, nameHeight));
        ImGui.SameLine(0f, Theme.S(Theme.GapM));

        // Le nom vient de la politique, donc du réseau : Text.H2 le passe par Glyphs.Safe.
        Text.H2(group.Name);
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));

        // Les puces s'alignent sous le nom, pas sous le chevron.
        ImGui.Indent(gutter);
        DrawChips(group, role, policy, excluded);

        if (expanded is false && excluded is false)
            DrawOnlineSummary(group, known);

        ImGui.Unindent(gutter);

        var end = ImGui.GetCursorScreenPos();
        var height = end.Y - start.Y - ImGui.GetStyle().ItemSpacing.Y;

        ImGui.SetCursorScreenPos(start);
        var clicked = ImGui.InvisibleButton("header", new Vector2(Math.Max(1f, width), Math.Max(1f, height)));
        var hovered = ImGui.IsItemHovered();

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var chevronSize = ImGui.CalcTextSize(glyph);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(start.X, MathF.Round(start.Y + (nameHeight - chevronSize.Y) * 0.5f)),
            ImGui.GetColorU32(hovered ? Theme.Accent : Theme.TextFaint), glyph);

        return clicked;
    }

    /// <summary>Replié, le nombre de membres joints : ce qu'on vient chercher sans rouvrir la carte.</summary>
    /// <remarks>Joint ou apparence appliquée, la même frontière que la pastille des membres.</remarks>
    private static void DrawOnlineSummary(GroupRecord group, IReadOnlyList<PeerStatus> known)
    {
        var online = 0;

        foreach (var status in known)
        {
            if (status.Group == group.Id && status.State is not PeerSessionState.Disconnected
                && status.View.Fingerprint is { } fingerprint && group.Members.ContainsKey(fingerprint))
                online++;
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapM));

        // Calé sur le texte des puces, qui portent un rembourrage vertical.
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.S(Theme.GapXs));
        Text.Small($"{online} en ligne", online > 0 ? Theme.Online : Theme.TextFaint);
    }

    /// <summary>Rôle, taille, mode d'entrée et état du groupe, d'un coup d'œil.</summary>
    private static void DrawChips(GroupRecord group, GroupRole role, GroupPolicy? policy, bool excluded)
    {
        if (excluded)
        {
            Chip.Draw("exclu", Theme.Danger, Icons.Blocked);
            return;
        }

        var (label, icon) = role switch
        {
            GroupRole.Owner     => ("propriétaire", Icons.Verified),
            GroupRole.Moderator => ("modérateur", Icons.Moderator),
            _                   => ("membre", Icons.Character),
        };

        Chip.Draw(label, Theme.Accent, icon);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        Chip.Draw(group.Members.Count == 1 ? "1 membre" : $"{group.Members.Count} membres", Theme.TextMuted, Icons.Members);

        // Le mode d'entrée n'intéresse que ceux qui distribuent le code : un
        // membre n'a rien à en faire, et le modérateur ne peut pas le changer
        // mais doit savoir ce que son code ouvre.
        if (role is not GroupRole.Member && policy is { Dissolved: false })
        {
            var password = policy.Attestation.Admission == AdmissionMode.Password;

            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Chip.Draw(password ? "entrée par mot de passe" : "entrée validée", Theme.TextMuted,
                      password ? Icons.Lock : Icons.Moderator);
        }

        if (policy is { Dissolved: true })
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Chip.Draw("dissous", Theme.Danger, Icons.Blocked);
        }

        if (policy is null)
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Chip.Draw("règles du groupe en attente", Theme.Idle, Icons.Waiting);
        }
    }

    /// <summary>Le code à distribuer, montré à ceux qui peuvent le changer.</summary>
    private void DrawInvite(GroupRecord group, GroupPolicy policy, InvitationTicket code)
    {
        var services = policy.Rendezvous.Count > 0 ? policy.Rendezvous : group.Rendezvous;

        if (services.Count == 0)
            return;

        var text = InvitationTicketText.Encode(code, services[0]);

        // Par groupes de quatre, comme on le dicte : le ticket ignore ses
        // propres tirets à la lecture, donc la forme copiée reste valable.
        var ticket = text[..InvitationTicket.Length];
        var grouped = $"{ticket[..4]}-{ticket[4..8]}-{ticket[8..]}";
        var service = text[InvitationTicket.Length..];

        ImGui.Dummy(Theme.S(0f, Theme.GapM));
        Text.Label("Inviter");

        var copied = _copied is { } c && c.Id == group.Id && c.Until > DateTime.UtcNow;
        var copyLabel = copied ? "Copié" : "Copier le code";
        var copyIcon = copied ? Icons.Check : Icons.Copy;

        // L'adresse du service peut venir de la politique, donc du réseau.
        CodeBox(grouped, Glyphs.Safe(service),
                Btn.Measure(copyLabel, BtnSize.Small, copyIcon) + Theme.S(Theme.GapS));

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw(copyLabel, copied ? BtnTone.Success : BtnTone.Secondary, BtnSize.Small, copyIcon, id: "copy_code"))
        {
            ImGui.SetClipboardText(grouped + service);
            _copied = (group.Id, DateTime.UtcNow.AddSeconds(2));
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small(policy.Attestation.Admission == AdmissionMode.Password
                       ? "À envoyer par /tell. Le code et le mot de passe permettent une entrée immédiate."
                       : "À envoyer par /tell. Chaque entrée doit être validée dans Demandes par le propriétaire ou un modérateur.",
                   Theme.TextFaint);
    }

    /// <summary>
    /// Le code dans un cadre enfoncé, à la hauteur d'un bouton.
    /// </summary>
    /// <remarks>
    /// Un fond de champ plutôt que du texte nu : c'est ce qu'on recopie, il
    /// doit se détacher du reste. Le service, plus long et moins lu, reste
    /// estompé, et rogné plutôt que de pousser le bouton hors de la carte.
    /// </remarks>
    private static void CodeBox(string ticket, string service, float reserved)
    {
        var origin = ImGui.GetCursorScreenPos();
        var padX = Theme.S(Theme.GapM);
        var height = ImGui.GetFrameHeight();

        var ticketSize = ImGui.CalcTextSize(ticket);
        var serviceWidth = ImGui.CalcTextSize(service).X;

        var natural = padX * 2f + ticketSize.X + serviceWidth;
        var room = ImGui.GetContentRegionAvail().X - Card.RightInset - reserved;
        var width = Math.Max(Math.Min(natural, room), padX * 2f + ticketSize.X);

        var max = new Vector2(origin.X + width, origin.Y + height);
        var rounding = Theme.S(Theme.RadiusFrame);
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, max, ImGui.GetColorU32(Theme.BgSunken), rounding);
        dl.AddRect(origin, max, ImGui.GetColorU32(Theme.Border), rounding, ImDrawFlags.None, 1f);

        var y = MathF.Round(origin.Y + (height - ticketSize.Y) * 0.5f);

        dl.PushClipRect(origin, new Vector2(max.X - padX * 0.5f, max.Y), true);
        dl.AddText(new Vector2(origin.X + padX, y), ImGui.GetColorU32(Theme.Text), ticket);
        dl.AddText(new Vector2(origin.X + padX + ticketSize.X, y), ImGui.GetColorU32(Theme.TextFaint), service);
        dl.PopClipRect();

        ImGui.Dummy(new Vector2(width, height));

        if (natural > width)
            Feedback.TooltipOnHover(ticket + service);
    }

    /// <summary>Le pied de la carte : déplier la gestion, quitter, ou retirer un groupe dissous.</summary>
    private void DrawFooter(GroupRecord group, GroupRole role, bool dissolved, bool manages)
    {
        if (dissolved)
        {
            if (Btn.Draw("Retirer de la liste", BtnTone.Ghost, BtnSize.Small, Icons.Remove, id: "forget"))
                actions.Forget(group.Id);

            if (role is GroupRole.Owner)
            {
                ImGui.Dummy(Theme.S(0f, Theme.GapXs));
                Text.Small(DissolveReminder, Theme.Idle);
            }

            return;
        }

        if (manages)
        {
            var open = _managing.Contains(group.Id);

            if (Btn.Draw(open ? "Masquer la gestion" : "Gérer le groupe", open ? BtnTone.Secondary : BtnTone.Ghost,
                         BtnSize.Small, Icons.Settings, id: "manage"))
            {
                if (_managing.Remove(group.Id) is false)
                    _managing.Add(group.Id);
            }
        }

        // Le propriétaire ne quitte pas : il dissout, depuis la zone sensible.
        if (role is GroupRole.Owner)
            return;

        if (manages)
            ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Confirmed($"leave_{group.Id}", "Quitter le groupe", "Confirmer : quitter", Icons.Leave, BtnTone.Ghost,
                      "Quitter le groupe arrête le partage d'apparences avec ses membres. Le code sera nécessaire pour revenir."))
            actions.Leave(group.Id);
    }

    private void DrawMembers(GroupRecord group, GroupRole role, IReadOnlyList<PeerStatus> known)
    {
        if (group.Members.Count == 0)
        {
            Text.Small("Aucun membre rencontré pour l'instant.", Theme.TextFaint);
            return;
        }

        // Largeur explicite : un tableau prend sinon jusqu'au bord de la
        // fenêtre, et ses boutons d'action débordaient sur la marge de la carte.
        using var table = ImRaii.Table("members", 4, ImGuiTableFlags.NoBordersInBody | ImGuiTableFlags.PadOuterX,
                                       new Vector2(ImGui.GetContentRegionAvail().X - Card.RightInset, 0f));

        if (table.Success is false)
            return;

        ImGui.TableSetupColumn("état", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("nom", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("statut", ImGuiTableColumnFlags.WidthFixed, Theme.S(220f));
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, (ImGui.GetFrameHeight() * 4f) + Theme.S(Theme.GapS * 3f));

        foreach (var member in group.Members.Values.OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            // Par groupe et par personnage, pas par clé : la clé d'un membre
            // n'est connue qu'après le premier handshake.
            var status = known.FirstOrDefault(
                status => status.Group == group.Id && status.View.Fingerprint == member.Fingerprint);

            MemberRow(group, role, member, status);
        }
    }

    private void MemberRow(GroupRecord group, GroupRole role, GroupMember member, PeerStatus? status)
    {
        var id = $"{member.Fingerprint.High:x16}{member.Fingerprint.Low:x16}";
        var policy = group.Policy;
        var banned = policy?.IsBanned(member.Id, member.Fingerprint) is true;
        var moderator = member.PublicKey is { } key && policy?.IsModerator(CryptoPrimitives.Compress(key)) is true;

        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + Theme.S(Theme.GapS));

        ImGui.TableNextColumn();
        AlignToFrame();
        Feedback.StatusDot(Tint(member, banned, status));

        ImGui.TableNextColumn();
        AlignToFrame();
        ImGui.TextColored(Theme.Text, Glyphs.Safe(member.DisplayName));

        if (bans.Status(member.Fingerprint) is { Verdict: BanVerdict.Listed })
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            BanChip.Draw(bans, member.Fingerprint);
        }

        if (moderator)
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Text.Icon(Icons.Moderator, Theme.TextFaint);
            Feedback.TooltipOnHover("Modérateur");
        }

        if (status?.LastFailure is not null)
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            Text.Icon(Icons.Warning, Theme.Idle);
            Feedback.TooltipOnHover(
                "La dernière synchronisation a échoué. Consulter le journal si le problème persiste.");
        }

        ImGui.TableNextColumn();
        AlignToFrame();
        DrawState(member, banned, status);

        ImGui.TableNextColumn();

        if (member.Paused)
        {
            if (Btn.Icon(Icons.Resume, $"resume_{id}", tooltip: "Reprendre"))
                actions.SetPaused(group.Id, member.Fingerprint, false);
        }
        else if (Btn.Icon(Icons.Paused, $"pause_{id}", tooltip: "Mettre en pause : fermer la connexion et retirer l'apparence"))
        {
            actions.SetPaused(group.Id, member.Fingerprint, true);
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        DrawReceive(group, member, id);

        // Le Public n'a ni politique ni modérateurs : bloquer chez soi est sa
        // seule modération, avec les listes des services.
        if (group.IsPublic)
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            DrawBlock(group, member, id);
            return;
        }

        if (policy is null || policy.Dissolved)
            return;

        // Un exclu ne se nomme pas : SetModerators lèverait son bannissement
        // par clé dans la même politique, sans le dire.
        if (role is GroupRole.Owner && banned is false)
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            DrawModeratorToggle(group, member, moderator, id);
        }

        if (role is not GroupRole.Member && banned is false)
        {
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            DrawExclude(group, role, member, id);
        }
    }

    /// <summary>Nommer ou démettre un modérateur. Au propriétaire seul.</summary>
    private void DrawModeratorToggle(GroupRecord group, GroupMember member, bool moderator, string id)
    {
        // La politique porte des clés, pas des personnages : un membre dont on
        // ne connaît que le personnage ne peut pas être nommé.
        if (member.PublicKey is not { } key)
        {
            Btn.Icon(Icons.Moderator, $"moderator_{id}", disabled: true,
                     tooltip: "Une première rencontre en jeu est nécessaire.");
            return;
        }

        if (Btn.Icon(Icons.Moderator, $"moderator_{id}",
                     tone: moderator ? BtnTone.Secondary : BtnTone.Ghost,
                     tooltip: moderator ? "Retirer ce modérateur" : "Nommer modérateur"))
            actions.Edit(group.Id, (current, _) => GroupGovernance.SetModerators(current, Toggled(current, key)));
    }

    /// <summary>La liste des modérateurs, avec ou sans cette clé.</summary>
    /// <remarks>
    /// Recalculée sur le groupe que le plugin tient au moment de signer, pas
    /// sur celui qu'on a dessiné : une politique arrivée entre-temps ne perd
    /// ainsi pas un modérateur nommé ailleurs.
    /// </remarks>
    private static List<byte[]> Toggled(GroupRecord group, byte[] key)
    {
        var target = PeerId.Of(key);
        var moderators = new List<byte[]>();
        var present = false;

        foreach (var compressed in group.Policy?.Attestation.Moderators ?? [])
        {
            byte[] full;

            try
            {
                full = CryptoPrimitives.Decompress(compressed);
            }
            catch (CryptographicException)
            {
                // Une politique acceptée n'en porte pas, mais le plugin ne
                // rattrape que ce type-là : une exception d'un autre type
                // remonterait jusqu'au dessin de la fenêtre.
                throw new InvalidOperationException("clé de modérateur illisible");
            }

            if (PeerId.Of(full) == target)
                present = true;
            else
                moderators.Add(full);
        }

        if (present is false)
            moderators.Add(key);

        return moderators;
    }

    /// <summary>Exclure : bannir la clé et le personnage, en deux clics.</summary>
    private void DrawExclude(GroupRecord group, GroupRole role, GroupMember member, string id)
    {
        // Les règles refusent qu'un modérateur vise le propriétaire ou un
        // autre modérateur : autant ne pas proposer le geste.
        if (role is GroupRole.Moderator && member.Id is { } peer && group.Policy!.IsProtected(peer))
        {
            Btn.Icon(Icons.Blocked, $"exclude_{id}", disabled: true,
                     tooltip: "Seul le propriétaire peut exclure un modérateur.");
            return;
        }

        var key = $"exclude_{group.Id}_{id}";
        var confirming = IsConfirming(key);

        if (Btn.Icon(Icons.Blocked, $"exclude_{id}",
                     tone: confirming ? BtnTone.Danger : BtnTone.Ghost,
                     tooltip: confirming ? "Cliquer encore pour exclure ce membre" : "Exclure"))
        {
            if (confirming)
            {
                _confirming = null;

                // La clé et le personnage : l'une seule laisserait revenir la
                // même personne sous l'autre.
                var ban = new GroupBan(member.Id, member.Fingerprint);
                actions.Edit(group.Id, (current, signer) => GroupGovernance.Ban(current, ban, signer));
            }
            else
            {
                _confirming = (key, DateTime.UtcNow.AddSeconds(4));
            }
        }
    }

    /// <summary>Le bouton des animations, VFX et sons de ce membre, et son menu.</summary>
    /// <remarks>Le même que celui des pairs : accentué dès qu'une catégorie est bloquée.</remarks>
    private void DrawReceive(GroupRecord group, GroupMember member, string id)
    {
        var receive = group.ReceiveOf(member);
        var limited = receive != TransientCategories.All;

        if (Btn.Icon(Icons.Effects, $"effects_{id}",
                     tone: limited ? BtnTone.Secondary : BtnTone.Ghost,
                     tooltip: limited ? "Animations, VFX ou sons de ce membre bloqués"
                                      : "Animations, VFX et sons reçus de ce membre"))
            ImGui.OpenPopup($"effets_{id}");

        using var popup = ImRaii.Popup($"effets_{id}");

        if (popup.Success is false)
            return;

        Text.Small("Recevoir de ce membre :");

        if (EffectsPicker.Draw(receive, $"fx_{id}") is { } changed)
            actions.SetReceive(group.Id, member.Fingerprint, changed);

        // Un membre du Public réglé à la main ne suit plus le réglage commun :
        // ce bouton l'y ramène.
        if (group.IsPublic && member.Receive is not null
            && Btn.Draw("Suivre le mode public", BtnTone.Ghost, BtnSize.Small, Icons.Refresh, id: $"follow_{id}"))
            actions.SetReceive(group.Id, member.Fingerprint, null);
    }

    /// <summary>Bloque un membre du Public, en deux clics comme l'exclusion.</summary>
    private void DrawBlock(GroupRecord group, GroupMember member, string id)
    {
        var key = $"block_{group.Id}_{id}";
        var confirming = IsConfirming(key);

        if (Btn.Icon(Icons.Blocked, $"block_{id}",
                     tone: confirming ? BtnTone.Danger : BtnTone.Ghost,
                     tooltip: confirming
                         ? "Cliquer encore pour bloquer ce joueur"
                         : "Bloquer : aucun partage d'apparence avec ce joueur dans le Mode public."))
        {
            if (confirming)
            {
                _confirming = null;
                actions.Block(group.Id, member.Fingerprint);
            }
            else
            {
                _confirming = (key, DateTime.UtcNow.AddSeconds(4));
            }
        }
    }

    /// <summary>La gestion : une sous-carte par réglage, la zone sensible à part et en dernier.</summary>
    private void DrawManagement(GroupRecord group, GroupRole role, GroupPolicy policy)
    {
        // Le mode d'admission s'atteste : seul le propriétaire le signe.
        if (role is GroupRole.Owner)
            DrawAdmissionMode(group, policy);

        DrawPassword(group, policy);
        DrawBans(group, policy);
        DrawSensitive(group, role);
    }

    private static CardScope Panel(string id) => Card.Begin(id, background: PanelBackground, border: Theme.BorderSoft);

    private void DrawAdmissionMode(GroupRecord group, GroupPolicy policy)
    {
        using var card = Panel("manage_admission");

        Text.WithIcon(Icons.Admission, "Admission", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var password = policy.Attestation.Admission == AdmissionMode.Password;

        // Les règles refusent le mode mot de passe sans mot de passe : ce
        // serait un groupe où personne ne pourrait plus entrer.
        var blocked = policy.Password.Length == 0 && password is false;

        // Deux boutons accolés plutôt que des cases radio : le mode choisi se
        // lit à sa couleur, et chaque choix porte son icône comme ailleurs.
        if (Btn.Draw("Mot de passe", password ? BtnTone.Selected : BtnTone.Secondary, BtnSize.Small, Icons.Lock,
                     id: "mode_password", disabled: blocked,
                     tooltip: blocked ? "Définir d'abord un mot de passe dans la carte ci-dessous." : null)
            && password is false)
            actions.Edit(group.Id, (current, _) => GroupGovernance.SetAdmission(current, AdmissionMode.Password));

        ImGui.SameLine(0f, Theme.S(Theme.GapXs));

        if (Btn.Draw("Validation par un modérateur", password ? BtnTone.Secondary : BtnTone.Selected, BtnSize.Small,
                     Icons.Moderator, id: "mode_validation")
            && password)
            actions.Edit(group.Id, (current, _) => GroupGovernance.SetAdmission(current, AdmissionMode.Validation));

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small(password
                       ? "Le code et le mot de passe permettent une entrée immédiate."
                       : "Chaque demande attend la validation du propriétaire ou d'un modérateur dans Demandes. "
                       + "Sans personne en ligne pour répondre, elle expire au bout de dix minutes.",
                   Theme.TextFaint);
    }

    private void DrawPassword(GroupRecord group, GroupPolicy policy)
    {
        using var card = Panel("manage_password");

        Text.WithIcon(Icons.Lock, "Mot de passe", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var password = _newPasswords.GetValueOrDefault(group.Id, "");
        var defined = policy.Password.Length > 0;

        ImGui.SetNextItemWidth(FieldWidth);

        if (ImGui.InputTextWithHint("##new_password", defined ? "Nouveau mot de passe" : "Mot de passe", ref password,
                                    GroupPolicyCodec.MaxPasswordBytes, ImGuiInputTextFlags.Password))
            _newPasswords[group.Id] = password;

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        // Vide, il serait refusé en mode mot de passe : les règles
        // n'acceptent pas un groupe où personne ne pourrait plus entrer.
        if (Btn.Draw(defined ? "Changer" : "Définir", BtnTone.Secondary, BtnSize.Small, Icons.Lock, id: "set_password",
                     disabled: password.Length == 0))
        {
            actions.Edit(group.Id, (current, signer) => GroupGovernance.SetPassword(current, password, signer));

            // Effacé dès l'envoi, comme à l'entrée : il n'a plus rien à faire
            // dans un champ.
            _newPasswords.Remove(group.Id);
        }

        // Vrai par construction : le mot de passe ne sert qu'à la preuve
        // d'admission (AdmissionHost), jamais aux sessions des membres.
        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small(defined
                       ? "Les membres actuels ne sont pas touchés ; le nouveau vaut pour les prochaines entrées."
                       : "Aucun mot de passe défini. Le mot de passe sert uniquement en mode « Mot de passe ».",
                   Theme.TextFaint);

        if (defined && policy.Attestation.Admission == AdmissionMode.Validation)
            Text.Small("En mode « Validation par un modérateur », le mot de passe ne permet pas d'entrer.", Theme.Idle);
    }

    private void DrawBans(GroupRecord group, GroupPolicy policy)
    {
        using var card = Panel("manage_bans");

        Text.WithIcon(Icons.Blocked, policy.Bans.Count == 0 ? "Exclus" : $"Exclus ({policy.Bans.Count})", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        if (policy.Bans.Count == 0)
            Text.Small("Aucun joueur exclu.", Theme.TextFaint);

        for (var index = 0; index < policy.Bans.Count; index++)
        {
            var ban = policy.Bans[index];

            using var scope = ImRaii.PushId(index);

            ImGui.AlignTextToFramePadding();
            Text.Body(NameOf(group, ban));

            // « Lever » calé à droite, comme les actions d'une ligne de pair :
            // collé au nom, il suivait sa longueur et rien ne s'alignait.
            ImGui.SameLine(0f, Theme.S(Theme.GapS));
            var room = ImGui.GetContentRegionAvail().X - Card.RightInset - Btn.Measure("Lever", BtnSize.Small, Icons.Accept);

            if (room > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + room);

            if (Btn.Draw("Lever", BtnTone.Ghost, BtnSize.Small, Icons.Accept, id: "unban",
                         tooltip: "Autoriser de nouveau ce personnage à rejoindre le groupe."))
                actions.Edit(group.Id, (current, signer) => GroupGovernance.Unban(current, ban, signer));
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small("Exclure un joueur depuis sa ligne. Le joueur exclu n'est plus synchronisé et ne peut plus rejoindre le groupe.",
                   Theme.TextFaint);
    }

    /// <summary>Ce qui ne se défait pas : à part, bordé de rouge, et en deux clics.</summary>
    private void DrawSensitive(GroupRecord group, GroupRole role)
    {
        using var card = Card.Begin("manage_danger", background: DangerBackground, border: DangerBorder,
                                    accent: Theme.Danger);

        Text.WithIcon(Icons.Warning, "Zone sensible", Theme.Danger);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        // En deux clics : le code déjà distribué cesse de fonctionner pour
        // tous ceux qui ne sont pas encore entrés.
        if (Confirmed($"new_code_{group.Id}", "Nouveau code", "Confirmer le nouveau code", Icons.Refresh,
                      BtnTone.Secondary, tooltip: null))
            actions.Edit(group.Id, GroupGovernance.NewCode);

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small(NewCodeConsequence, Theme.TextFaint);

        if (role is not GroupRole.Owner)
            return;

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        // Pour le propriétaire, quitter c'est dissoudre : voir LeaveGroup
        // dans le plugin.
        if (Confirmed($"dissolve_{group.Id}", "Dissoudre le groupe", "Confirmer la dissolution", Icons.Remove,
                      BtnTone.Secondary, tooltip: null))
            actions.Leave(group.Id);

        ImGui.Dummy(Theme.S(0f, Theme.GapXs));
        Text.Small(DissolveConsequence, Theme.TextFaint);
    }

    /// <summary>Un bouton qui n'agit qu'au second clic dans les quatre secondes, comme le retrait d'un pair.</summary>
    /// <remarks>
    /// Le libellé change pendant l'attente : une couleur seule ne dit pas
    /// qu'un second clic est attendu, et l'infobulle ne se lit qu'au survol.
    /// L'identifiant, lui, reste la clé, pour que le bouton ne perde pas son
    /// état en changeant de texte.
    /// </remarks>
    private bool Confirmed(string key, string label, string confirmLabel, FontAwesomeIcon icon, BtnTone idle,
                           string? tooltip)
    {
        var confirming = IsConfirming(key);

        if (Btn.Draw(confirming ? confirmLabel : label, confirming ? BtnTone.Danger : idle, BtnSize.Small, icon, id: key,
                     tooltip: confirming ? ConfirmTooltip : tooltip) is false)
            return false;

        if (confirming)
        {
            _confirming = null;
            return true;
        }

        _confirming = (key, DateTime.UtcNow.AddSeconds(4));
        return false;
    }

    /// <summary>Le nom d'un exclu, si on l'a croisé ; sinon rien qui ressemble à une clé.</summary>
    private static string NameOf(GroupRecord group, GroupBan ban)
    {
        if (ban.Fingerprint is { } fingerprint && group.Members.TryGetValue(fingerprint, out var byCharacter))
            return byCharacter.DisplayName;

        if (ban.Peer is { } peer && group.Members.Values.FirstOrDefault(member => member.Id == peer) is { } byKey)
            return byKey.DisplayName;

        return "personnage jamais croisé";
    }

    private bool IsConfirming(string key) => _confirming is { } c && c.Key == key && c.Until > DateTime.UtcNow;

    /// <summary>Centre un texte d'une ligne sur la hauteur d'un bouton.</summary>
    private static void AlignToFrame()
        => ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));

    private static Vector4 Tint(GroupMember member, bool banned, PeerStatus? status)
        => banned ? Theme.Danger
         : member.Paused ? Theme.Idle
         : status is null || status.State is PeerSessionState.Disconnected ? Theme.TextFaint
         : status.Applied ? Theme.Online
         : Theme.Accent;

    /// <summary>La puce d'état, même logique que celle des pairs, plus l'exclusion.</summary>
    private static void DrawState(GroupMember member, bool banned, PeerStatus? status)
    {
        if (banned)
        {
            Chip.Draw("exclu", Theme.Danger, Icons.Blocked);
            return;
        }

        if (member.Paused)
        {
            Chip.Draw("en pause", Theme.Idle, Icons.Paused);
            return;
        }

        if (status is null || status.State is PeerSessionState.Disconnected)
        {
            Chip.Draw("hors ligne", Theme.TextFaint, Icons.Waiting);
            return;
        }

        if (status.Applied)
        {
            Chip.Draw("apparence appliquée", Theme.Online, Icons.Applied);
            return;
        }

        var view = status.View;

        if (view.Ready)
        {
            Chip.Draw("prêt quand le joueur sera visible", Theme.Accent, Icons.Connected);
            return;
        }

        if (view.MissingBytes > 0)
        {
            var received = view.ReceivedBytes / 1024 / 1024;
            var total    = view.MissingBytes / 1024 / 1024;

            Chip.Draw($"réception {received} / {total} Mo", Theme.Idle, Icons.Receiving);
            return;
        }

        Chip.Draw("connecté", Theme.Accent, Icons.Connected);
    }
}
