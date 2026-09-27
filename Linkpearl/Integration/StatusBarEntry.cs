using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Identity;

namespace Linkpearl.Integration;

/// <summary>
/// L'entrée de Linkpearl dans la barre de statut du jeu : le symbole HQ, suivi
/// du nombre de pairs à portée, et des demandes en attente entre parenthèses.
/// </summary>
/// <remarks>
/// Le symbole HQ est le glyphe du plugin. Il vient de la police du jeu
/// (<see cref="SeIconChar.HighQuality"/>), et non d'une police embarquée : la
/// barre est dessinée par le jeu, qui l'a déjà.
///
/// Le compte se fait sur les empreintes épinglées au carnet, croisées avec ce
/// que le personnage voit : c'est la réponse à « qui, parmi les miens, est là »,
/// indépendamment de l'état du transfert.
///
/// <see cref="Update"/> ne s'appelle que depuis le thread du framework : la
/// barre est un nœud de l'interface du jeu.
/// </remarks>
internal sealed class StatusBarEntry : IDisposable
{
    private static readonly string Glyph = SeIconChar.HighQuality.ToIconString();

    private readonly IDtrBarEntry _entry;
    private (int Nearby, int Pending, bool CacheMissing)? _shown;

    public StatusBarEntry(IDtrBar bar, Action open)
    {
        _entry = bar.Get("Linkpearl");
        _entry.OnClick = _ => open();
        Show(0, 0, false);
    }

    public void Update(IReadOnlyList<NearbyPlayer> nearby, IEnumerable<PairRecord> pairs, int pending, bool cacheMissing)
    {
        var pinned = pairs
            .Where(pair => pair.Trust is not PairTrust.Blocked && pair.PinnedFingerprint is not null)
            .Select(pair => pair.PinnedFingerprint!.Value)
            .ToHashSet();

        Show(nearby.Count(player => pinned.Contains(player.Fingerprint)), pending, cacheMissing);
    }

    private void Show(int count, int pending, bool cacheMissing)
    {
        // Réécrire le texte à chaque appel marquerait le nœud comme modifié
        // pour rien, et le jeu le recomposerait à chaque fois.
        if (_shown == (count, pending, cacheMissing))
            return;

        _shown = (count, pending, cacheMissing);

        // Le blocage passe devant tout : tant qu'il dure, rien ne se synchronise,
        // et le nombre de pairs à portée ne voudrait rien dire.
        if (cacheMissing)
        {
            _entry.Text = $"{Glyph} cache introuvable";
            _entry.Tooltip = "Linkpearl : dossier du cache introuvable. Synchronisation arrêtée.\n"
                           + "Choisir un autre dossier.";
            return;
        }

        // Rien entre parenthèses sans demande : un « (0) » permanent apprendrait
        // à ne plus regarder la parenthèse.
        _entry.Text = pending is 0 ? $"{Glyph} {count}" : $"{Glyph} {count} ({pending})";

        var nearby = count switch
        {
            0 => "Linkpearl : aucun pair à portée",
            1 => "Linkpearl : 1 pair à portée",
            _ => $"Linkpearl : {count} pairs à portée",
        };

        _entry.Tooltip = pending switch
        {
            0 => nearby,
            // Pairage et admission dans un groupe confondus : les deux attendent
            // le même geste, ouvrir la page des demandes.
            1 => $"{nearby}\n1 demande en attente",
            _ => $"{nearby}\n{pending} demandes en attente",
        };
    }

    public void Dispose() => _entry.Remove();
}
