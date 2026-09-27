using Dalamud.Bindings.ImGui;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui.Pages;

/// <summary>Ce qu'un annuaire a proposé, en attente du choix de l'utilisateur.</summary>
/// <remarks>
/// Rien n'est ajouté tant qu'une case n'est pas cochée. C'est ce qui empêche un
/// annuaire de devenir une autorité : il propose, l'utilisateur dispose.
/// </remarks>
public sealed class DiscoveryState
{
    public RendezvousAddress? From { get; set; }

    public IReadOnlyList<DirectoryEntry> Offered { get; set; } = [];

    public HashSet<string> Chosen { get; } = [];

    public string? Failure { get; set; }

    public bool Running { get; set; }

    public void Reset()
    {
        From = null;
        Offered = [];
        Chosen.Clear();
        Failure = null;
    }
}

/// <summary>
/// Les réglages, et surtout ce qu'ils coûtent.
/// </summary>
/// <remarks>
/// Chaque réglage est suivi de sa conséquence, pas de sa description : ce qui
/// se paie en vie privée doit se lire avant d'être coché, pas après.
/// </remarks>
internal sealed class SettingsPage(
    Configuration configuration, DiscoveryState discovery, Action<RendezvousAddress> discover, BackupCard backup,
    Action<bool> setUploadLimited, CacheChooser cacheChooser, CacheKeeper cacheKeeper, Action showOnboarding,
    OpenCircle openCircle)
{
    private string _newAddress = "";

    /// <summary>Vrai pendant la suppression de l'ancien cache : le bouton se désactive, un clic répété ne lance pas une deuxième suppression concurrente.</summary>
    private volatile bool _deletingPrevious;

    /// <summary>L'échec de la dernière suppression de l'ancien cache, montré sous le bouton.</summary>
    private volatile string? _deletePreviousError;

    public void Draw()
    {
        Text.PageHeader("Réglages");

        // La sauvegarde en tête : c'est la seule carte dont l'oubli coûte
        // cher, et une réinstallation n'attend pas qu'on ait fait défiler.
        DrawIdentity();
        DrawVisibility();
        DrawCache();
        DrawNetwork();
        DrawDiscovery();
    }

    /// <summary>Visibilité : ce qui se voit de vous, et ce que ça coûte en vie privée.</summary>
    private void DrawVisibility()
    {
        using var card = Card.Begin("settings_visibility");

        Text.WithIcon(Icons.Discoverable, "Visibilité", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var discoverable = configuration.Discoverable;

        if (Toggle.Draw("Me signaler aux autres joueurs", ref discoverable, "discoverable",
                        hint: "Permet aux autres joueurs de Linkpearl de vous voir et de vous envoyer une demande "
                            + "de pairage. En contrepartie, les services de votre liste savent quand votre personnage "
                            + "est en ligne. Désactivé, personne de nouveau ne peut vous trouver, et vous ne pouvez "
                            + "pas rejoindre de groupe."))
        {
            configuration.Discoverable = discoverable;
            configuration.Save();
        }

        // Exception voulue à la règle « tout en infobulle » : ce qui se paie
        // en vie privée se lit avant de cocher, pas au survol d'une icône.
        Text.Small("Le service sait alors que vous êtes en ligne.", Theme.TextFaint);

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var glyphs = configuration.ShowNameplateGlyphs;

        if (Toggle.Draw("Glyphe à côté du nom", ref glyphs, "nameplate_glyphs", hintContent: NameplateLegend.Draw))
        {
            configuration.ShowNameplateGlyphs = glyphs;
            configuration.Save();
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var badges = configuration.ShowTransferBadges;

        if (Toggle.Draw("Badges de transfert", ref badges, "transfer_badges",
                        hint: "Affiche un petit badge sous un pair tant que son apparence n'est pas arrivée : "
                            + "connexion, téléchargement avec sa progression, application. Il disparaît une fois "
                            + "l'apparence posée."))
        {
            configuration.ShowTransferBadges = badges;
            configuration.Save();
        }
    }

    /// <summary>Cache : où les apparences reçues vivent sur le disque, et combien.</summary>
    private void DrawCache()
    {
        using var card = Card.Begin("settings_cache");

        Text.WithIcon(Icons.Cache, "Cache", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        cacheChooser.Draw();

        if (cacheKeeper.PreviousRoot is not { } previous)
            return;

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        var size = cacheKeeper.PreviousBytes is { } bytes ? CacheChooser.Format(bytes) : "une taille inconnue";
        Text.Small($"L'ancien cache occupe {size} dans {previous}.", Theme.TextMuted);

        if (Btn.Draw("Supprimer l'ancien cache", BtnTone.Danger, BtnSize.Small, Icons.Remove, id: "cache_delete_previous",
                     disabled: _deletingPrevious,
                     tooltip: "N'efface que les fichiers de Linkpearl. Tout autre fichier de ce dossier reste."))
        {
            // Le drapeau désactive le bouton tout de suite : sans lui, un
            // second clic pendant la suppression lancerait une deuxième
            // suppression concurrente sur le même dossier.
            _deletingPrevious = true;
            _deletePreviousError = null;

            _ = Task.Run(() =>
            {
                try
                {
                    cacheKeeper.DeletePrevious();
                }
                catch (Exception e)
                {
                    // Montré, pas seulement journalisé : le bouton et la
                    // taille resteraient sinon affichés comme si de rien
                    // n'était, alors que rien n'a été supprimé. Le journal ne
                    // garde que le type d'exception ; l'interface, elle, peut
                    // afficher le message complet.
                    Plugin.Log.Warning($"Suppression de l'ancien cache en échec ({e.GetType().Name}).");
                    _deletePreviousError = $"échec de la suppression : {e.Message}";
                }
                finally
                {
                    _deletingPrevious = false;
                }
            });
        }

        if (_deletePreviousError is { } error)
            Text.Small(error, Theme.Danger);
    }

    /// <summary>Réseau : débit et services de rendez-vous.</summary>
    private void DrawNetwork()
    {
        using var card = Card.Begin("settings_network");

        Text.WithIcon(Icons.Rendezvous, "Réseau", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var limited = configuration.LimitUpload;

        if (Toggle.Draw("Brider l'envoi", ref limited, "limit_upload",
                        hint: "Activé, vos envois ralentissent dès que votre ping monte : le jeu reste fluide, utile "
                            + "en donjon, mais vos amis reçoivent votre tenue en plusieurs minutes. Désactivé, ils la "
                            + "reçoivent en quelques secondes, avec un ping plus élevé pendant l'envoi."))
            setUploadLimited(limited);

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        // Aligné comme dans DrawService : sans ça, le texte flotte au-dessus
        // de l'icône ⓘ qui suit, calée sur la hauteur d'un cadre.
        ImGui.AlignTextToFramePadding();
        Text.Body("Services de rendez-vous");
        Feedback.Hint(
            "Des serveurs qui mettent les joueurs en relation, et font passer les données quand deux "
          + "joueurs ne peuvent pas se connecter directement. Ils ne voient ni vos fichiers ni vos "
          + "apparences. Vous ne voyez que les joueurs qui ont au moins un service en commun avec vous.");

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        for (var i = 0; i < configuration.Rendezvous.Count; i++)
            DrawService(i);

        if (configuration.Rendezvous.Count == 0)
            Text.Small("Aucun service. Personne ne peut vous voir, et vous ne voyez personne.", Theme.Danger);

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        ImGui.SetNextItemWidth(Card.FullWidth - Theme.S(110f) - Feedback.HintWidth);
        ImGui.InputTextWithHint("##nouveau", "rdv.exemple.ch ou rdv.exemple.ch:443", ref _newAddress, 260);
        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Ajouter", BtnTone.Secondary, BtnSize.Small, Icons.Invite, id: "add_rdv"))
            Add(_newAddress);

        Feedback.Hint(
            "Chaque service de la liste sait quand votre personnage est en ligne et quels joueurs de "
          + "Linkpearl sont autour de vous. Plus de services, c'est plus de joueurs visibles, mais "
          + "aussi plus d'opérateurs au courant.");

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        var open = configuration.OpenCircle;

        if (Toggle.Draw("Réseau ouvert", ref open, "open_circle",
                        hint: "Une fois pairé avec quelqu'un, vous vous retrouvez aussi par des serveurs de "
                            + "bénévoles, admis après trois jours de bon fonctionnement. Ils ne voient ni votre "
                            + "nom ni vos fichiers, et les demandes de pairage passent toujours par votre liste."))
        {
            configuration.OpenCircle = open;
            configuration.Save();
            openCircle.Enabled = open;
        }

        Text.Small(openCircle.Current is { } list
            ? $"{list.Entries.Count} service(s) ouvert(s), liste valable jusqu'au {DateTimeOffset.FromUnixTimeSeconds(list.Expires).ToLocalTime():d MMMM HH:mm}."
            : "Aucune liste valable : tout passe par les services ci-dessus.", Theme.TextFaint);
    }

    /// <summary>Identité : sauvegarde du personnage et de son carnet, et la présentation.</summary>
    private void DrawIdentity()
    {
        using var card = Card.Begin("settings_identity");

        Text.WithIcon(Icons.Backup, "Sauvegarde", Theme.Accent);
        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        backup.Draw();

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        if (Btn.Draw("Revoir la présentation", BtnTone.Ghost, BtnSize.Small, Icons.Info, id: "settings_onboarding"))
            showOnboarding();
    }

    private void DrawService(int index)
    {
        var entry = configuration.Rendezvous[index];

        using var id = Dalamud.Interface.Utility.Raii.ImRaii.PushId(index);

        var enabled = entry.Enabled;

        if (Toggle.Switch(ref enabled, "actif",
                          enabled ? "Service actif. Cliquer pour le couper." : "Service coupé. Cliquer pour l'activer."))
        {
            configuration.Rendezvous[index] = entry with { Enabled = enabled };
            configuration.Save();
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapM));

        // L'adresse et le libellé peuvent venir d'un annuaire, donc du réseau.
        var shown = entry.Label.Length > 0
            ? $"{Glyphs.Safe(entry.Label)}  ({Glyphs.Safe(entry.Address.ToString())})"
            : Glyphs.Safe(entry.Address.ToString());

        // Le texte se cale sur la hauteur des cadres, sans quoi il flotte au
        // dessus de la ligne que forment la case et les boutons.
        ImGui.AlignTextToFramePadding();
        Text.Body(shown, enabled ? Theme.Text : Theme.TextFaint);

        // Deux boutons carrés et l'espace qui les sépare : une largeur fixe
        // ne suivait ni l'échelle ni l'espacement, et poussait la corbeille
        // contre le bord.
        var buttons = ImGui.GetFrameHeight() * 2f + Theme.S(Theme.GapS);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttons);

        if (Btn.Icon(Icons.Refresh, "discover", tooltip: "Voir les autres services que celui-ci connaît"))
            discover(entry.Address);

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Icon(Icons.Remove, "remove", BtnTone.Danger, "Retirer ce service"))
        {
            configuration.Rendezvous.RemoveAt(index);
            configuration.Save();
        }
    }

    private void DrawDiscovery()
    {
        if (discovery.Running is false && discovery.From is null && discovery.Failure is null)
            return;

        using var card = Card.Begin("settings_discovery", accent: Theme.Accent);

        Text.WithIcon(Icons.Rendezvous, $"Services connus de {Glyphs.Safe(discovery.From?.ToString() ?? "")}",
                      Theme.Accent);

        ImGui.Dummy(Theme.S(0f, Theme.GapS));

        if (discovery.Running)
        {
            Text.Muted("Interrogation en cours...");
            return;
        }

        if (discovery.Failure is { } failure)
        {
            Text.Small(failure, Theme.Danger);
            return;
        }

        if (discovery.Offered.Count == 0)
        {
            Text.Small("Ce service ne connaît personne, ou n'en publie aucun.", Theme.TextFaint);
        }

        foreach (var offered in discovery.Offered)
        {
            var known = configuration.Rendezvous.Any(e => e.Address.ToString() == offered.Address);
            var chosen = discovery.Chosen.Contains(offered.Address);

            using var id = Dalamud.Interface.Utility.Raii.ImRaii.PushId(offered.Address);
            using var disabled = Dalamud.Interface.Utility.Raii.ImRaii.Disabled(known);

            if (Toggle.Switch(ref chosen, "choisi"))
            {
                if (chosen)
                    discovery.Chosen.Add(offered.Address);
                else
                    discovery.Chosen.Remove(offered.Address);
            }

            ImGui.SameLine(0f, Theme.S(Theme.GapM));

            Text.Body(offered.Label.Length > 0
                ? $"{Glyphs.Safe(offered.Label)}  ({Glyphs.Safe(offered.Address)})"
                : Glyphs.Safe(offered.Address));

            if (known)
            {
                ImGui.SameLine(0f, Theme.S(Theme.GapS));
                Text.Small("déjà dans votre liste", Theme.TextFaint);
            }
        }

        ImGui.Dummy(Theme.S(0f, Theme.GapM));

        if (Btn.Draw($"Ajouter les {discovery.Chosen.Count} cochés", BtnTone.Action, BtnSize.Small,
                     Icons.Accept, disabled: discovery.Chosen.Count == 0, id: "add_chosen"))
        {
            foreach (var address in discovery.Chosen)
                Add(address, discovery.Offered.FirstOrDefault(o => o.Address == address)?.Label ?? "");

            discovery.Reset();
        }

        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        if (Btn.Draw("Fermer", BtnTone.Ghost, BtnSize.Small, id: "close_discovery"))
            discovery.Reset();
    }

    /// <summary>Ajoute un service, en refusant ce qui ne se lit pas.</summary>
    private void Add(string text, string label = "")
    {
        if (RendezvousAddress.TryParse(text, out var address, out var why) is false)
        {
            // La raison s'affiche telle quelle : elle est écrite pour être lue.
            discovery.Failure = $"adresse refusée : {why}";
            return;
        }

        if (configuration.Rendezvous.Any(entry => entry.Address == address))
            return;

        configuration.Rendezvous.Add(new RendezvousEntry(address, label, true));
        configuration.Save();

        _newAddress = "";
    }
}
