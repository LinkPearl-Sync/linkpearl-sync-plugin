using System.Numerics;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Sync;
using Linkpearl.Integration;

namespace Linkpearl.Ui;

/// <summary>
/// Un glyphe HQ coloré à droite du nom des joueurs qui utilisent Linkpearl.
/// </summary>
/// <remarks>
/// Par l'API de plaques de Dalamud, qui compose nos ajouts avec ceux des autres
/// plugins au lieu de réécrire la plaque par-dessus eux.
///
/// La plaque ne se reconstruit que quand le jeu la juge sale : <see cref="Update"/>
/// réclame un redessin seulement quand une marque change, faute de quoi un pair
/// qui se connecte garderait sa couleur d'avant jusqu'au prochain mouvement de
/// caméra.
///
/// Rien de ce qui s'affiche ici n'est nouveau : la page « Autour de vous » et le
/// carnet disent déjà la même chose, pour les mêmes joueurs.
/// </remarks>
internal sealed class NameplateGlyphs : IDisposable
{
    private static readonly SeString Before = new();

    private static readonly IReadOnlyDictionary<NameplateMark, SeString> After =
        Enum.GetValues<NameplateMark>()
            .Where(mark => mark is not NameplateMark.None)
            .ToDictionary(mark => mark, mark => Glyph(ColorOf(mark)));

    /// <summary>La couleur d'une marque, partagée avec la légende des réglages.</summary>
    /// <remarks>
    /// Les couleurs vives de la palette : un glyphe de quelques pixels posé sur
    /// la scène se lit mal en pastel. L'orange pompon pour qui attend qu'on
    /// l'aborde, le halo pour une demande, la lavande de la perle pour un groupe.
    /// </remarks>
    public static Vector4 ColorOf(NameplateMark mark) => mark switch
    {
        NameplateMark.Online      => Theme.Online,
        NameplateMark.Available   => Theme.Action,
        NameplateMark.Requesting  => Theme.Accent,
        NameplateMark.Offline     => Theme.TextFaint,
        NameplateMark.Trouble     => Theme.Danger,
        NameplateMark.GroupMember => Theme.PearlRim,
        _                         => Theme.Text,
    };

    private readonly INamePlateGui _plates;

    /// <summary>Les marques par objet, écrites et lues sur le thread du framework.</summary>
    private Dictionary<ulong, NameplateMark> _marks = [];

    public NameplateGlyphs(INamePlateGui plates)
    {
        _plates = plates;
        _plates.OnNamePlateUpdate += OnUpdate;
    }

    public void Update(
        bool enabled,
        IReadOnlyList<NearbyPlayer> nearby,
        IReadOnlyList<PairRecord> pairs,
        IReadOnlyList<PeerStatus> statuses,
        IReadOnlyCollection<PlayerFingerprint> detected,
        IReadOnlyList<IncomingRequest> requests)
    {
        var marks = new Dictionary<ulong, NameplateMark>();

        if (enabled && nearby.Count > 0)
        {
            // Une demande porte le nom en clair : c'est ici, dans l'adaptateur,
            // qu'elle se relie au joueur visible, jamais dans le noyau. Par
            // l'empreinte, nom et monde, calculée à la réception comme pour
            // la page des demandes : les deux disent la même chose du même joueur.
            var requesting = requests.Count == 0
                ? []
                : nearby.Where(player => requests.Any(request => request.Sender == player.Fingerprint))
                        .Select(player => player.Fingerprint)
                        .ToList();

            var byPrint = NameplateMarks.Build(pairs, statuses, detected, requesting);

            foreach (var player in nearby)
            {
                if (byPrint.GetValueOrDefault(player.Fingerprint) is var mark and not NameplateMark.None)
                    marks[player.Object.StableId] = mark;
            }
        }

        if (Same(marks, _marks))
            return;

        _marks = marks;
        _plates.RequestRedraw();
    }

    private void OnUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var marks = _marks;

        if (marks.Count == 0)
            return;

        // Seuls des joueurs figurent dans la table : un PNJ ou un familier n'y
        // trouve jamais son identifiant.
        foreach (var handler in handlers)
        {
            if (marks.TryGetValue(handler.GameObjectId, out var mark))
                handler.NameParts.TextWrap = (Before, After[mark]);
        }
    }

    private static bool Same(Dictionary<ulong, NameplateMark> a, Dictionary<ulong, NameplateMark> b)
        => a.Count == b.Count
        && a.All(entry => b.TryGetValue(entry.Key, out var other) && other == entry.Value);

    /// <summary>Une espace puis le glyphe HQ, dans sa couleur, que la couleur du nom ne déborde pas.</summary>
    private static SeString Glyph(Vector4 color)
    {
        var builder = new Lumina.Text.SeStringBuilder();

        builder.Append(" ")
               .PushColorRgba(color)
               .Append(SeIconChar.HighQuality.ToIconString())
               .PopColor();

        return SeString.Parse(builder.ToArray());
    }

    public void Dispose()
    {
        _plates.OnNamePlateUpdate -= OnUpdate;
        _plates.RequestRedraw();
    }
}
