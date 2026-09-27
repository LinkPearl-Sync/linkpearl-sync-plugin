using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Linkpearl.Ui.Components;

internal enum CardTone
{
    /// <summary>Surface neutre.</summary>
    Flat,

    /// <summary>Surface qui s'éclaircit au survol, pour une entrée de liste.</summary>
    Interactive,
}

/// <summary>
/// Portée d'une carte.
/// </summary>
/// <remarks>
/// <c>ref struct</c> délibéré : il y a une carte par pair et par image, un type
/// par référence allouerait à chaque image.
/// </remarks>
internal ref struct CardScope
{
    // La clé est capturée à l'ouverture, avant le PushID de la carte. La
    // recalculer à la fermeture donnerait un identifiant différent, calculé dans
    // la pile d'identifiants de la carte : le cache ne serait jamais relu et
    // toutes les cartes garderaient la hauteur estimée par défaut.
    private readonly uint _key;
    private readonly Vector2 _origin;
    private readonly float _previousInset;

    /// <summary>Vrai si le curseur est sur la carte.</summary>
    public bool Hovered { get; }

    internal CardScope(uint key, Vector2 origin, bool hovered, float previousInset)
    {
        _key           = key;
        _origin        = origin;
        _previousInset = previousInset;
        Hovered        = hovered;
    }

    public void Dispose()
    {
        ImGui.Unindent(Theme.S(Theme.CardPadX));
        Card.RightInset = _previousInset;

        ImGui.Dummy(new Vector2(0f, Theme.S(Theme.CardPadY)));

        // La hauteur réelle sert au rendu du fond de l'image suivante.
        Card.Remember(_key, ImGui.GetCursorScreenPos().Y - _origin.Y);

        ImGui.PopID();

        // Respiration entre deux cartes. L'espacement d'items d'ImGui, prévu
        // pour des lignes de texte, ne suffit pas à séparer deux surfaces qui
        // portent leur propre ombre : elles paraissent collées.
        ImGui.Dummy(new Vector2(0f, Theme.S(Theme.GapM)));
    }
}

/// <summary>
/// Cartes.
/// </summary>
/// <remarks>
/// La hauteur mesurée à l'image précédente est mémorisée, ce qui permet de
/// peindre le fond <em>avant</em> le contenu. La carte n'est donc exacte qu'à
/// partir de la deuxième image, ce qui est imperceptible, et deux bénéfices
/// apparaissent : l'imbrication devient sûre, et le rectangle étant connu à
/// l'avance, le survol de la carte entière devient possible.
///
/// L'alternative, découper la liste de dessin en canaux pour peindre le fond
/// après avoir mesuré, n'est pas réentrante : deux cartes imbriquées
/// corrompent l'ordre de rendu.
/// </remarks>
internal static class Card
{
    private static readonly Dictionary<uint, float> Heights = [];

    private const float EstimatedHeight = 64f;

    /// <summary>
    /// Marge droite de la carte en cours, en pixels déjà mis à l'échelle.
    /// </summary>
    /// <remarks>
    /// <c>ImGui.Indent</c> ne décale que le bord gauche : sans ce retrait, la
    /// largeur disponible mesurée à l'intérieur va jusqu'au bord de la carte et
    /// tout ce qui s'aligne à droite vient s'y coller.
    /// </remarks>
    internal static float RightInset { get; set; }

    /// <summary>Largeur utile, marge droite déduite.</summary>
    public static float FullWidth => -Math.Max(1f, RightInset);

    /// <summary>
    /// Largeur disponible depuis le curseur, marge droite déduite.
    /// </summary>
    /// <remarks>
    /// À utiliser partout où l'on mesure pour aligner à droite ou remplir la
    /// ligne : <c>GetContentRegionAvail</c> seul mène au bord de la carte.
    /// </remarks>
    public static float Avail => Math.Max(1f, ImGui.GetContentRegionAvail().X - RightInset);

    internal static void Remember(uint key, float height) => Heights[key] = height;

    /// <summary>
    /// Ouvre une carte.
    /// </summary>
    /// <remarks>
    /// L'identifiant doit être stable d'une image à l'autre, par exemple
    /// l'empreinte du pair : une clé changeante invalide le cache de hauteur et
    /// fait vibrer le fond.
    /// </remarks>
    public static CardScope Begin(string id,
                                  CardTone tone = CardTone.Flat,
                                  bool interactive = true,
                                  Vector4? background = null,
                                  Vector4? border = null,
                                  Vector4? accent = null)
    {
        var key    = ImGui.GetID(id);
        var origin = ImGui.GetCursorScreenPos();
        // La marge droite de la carte englobante est déduite : ImGui.Indent
        // ne décale que le bord gauche, et une carte imbriquée débordait
        // sinon sur le rembourrage de sa parente. Nulle au premier niveau.
        var width  = ImGui.GetContentRegionAvail().X - RightInset;

        var height = Heights.TryGetValue(key, out var cached) ? cached : Theme.S(EstimatedHeight);

        var min = origin;
        var max = new Vector2(origin.X + width, origin.Y + height);

        var hovered = interactive
                   && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
                   && ImGui.IsMouseHoveringRect(min, max);

        var bg = background ?? (tone == CardTone.Interactive && hovered ? Theme.CardFillHover : Theme.CardFill);

        var dl       = ImGui.GetWindowDrawList();
        var rounding = Theme.S(Theme.RadiusCard);

        // Une carte active se signale par un halo tout autour, comme le champ
        // du site, et non plus par une barre à gauche. Peint avant elle : son
        // fond recouvre l'intérieur. Sans ombre, qui assombrirait le halo.
        if (accent is { } accentColor)
        {
            Surface.Glow(dl, min, max, rounding, accentColor);
            Surface.Panel(dl, min, max, bg, Theme.Alpha(accentColor, 0.70f), rounding, shadow: false);
        }
        else
        {
            Surface.Panel(dl, min, max, bg, border ?? Theme.Border, rounding);
        }

        ImGui.PushID(id);
        ImGui.Dummy(new Vector2(0f, Theme.S(Theme.CardPadY)));

        var previousInset = RightInset;
        ImGui.Indent(Theme.S(Theme.CardPadX));
        RightInset = previousInset + Theme.S(Theme.CardPadX);

        return new CardScope(key, origin, hovered, previousInset);
    }
}
