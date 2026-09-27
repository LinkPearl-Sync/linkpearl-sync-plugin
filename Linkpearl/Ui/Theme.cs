using Dalamud.Interface.Utility;
using System.Numerics;

namespace Linkpearl.Ui;

/// <summary>
/// Jetons de design du plugin.
/// </summary>
/// <remarks>
/// La palette est celle du site et du logo : la nuit marine où se tiennent les
/// deux mogs, le halo bleu de la perle, et l'orange de leurs pompons, réservé à
/// l'action principale d'un écran. Chaque valeur cite la variable CSS du site
/// dont elle vient, pour que les deux ne dérivent pas.
///
/// Toute dimension en pixels passe par <see cref="S(float)"/>. Sans cela
/// l'interface devient illisible dès que l'utilisateur monte l'échelle Dalamud,
/// et c'est le genre de défaut qu'on ne voit jamais soi-même.
/// </remarks>
internal static class Theme
{
    // ─── Conversion ───────────────────────────────────────────────────────────

    /// <summary>Convertit un RGB hexadécimal (0xRRGGBB) en couleur ImGui.</summary>
    public static Vector4 Hex(uint rgb, float a = 1f) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >>  8) & 0xFF) / 255f,
        ( rgb        & 0xFF) / 255f,
        a);

    // ─── Fonds ────────────────────────────────────────────────────────────────
    //
    // Échelle de profondeur, du plus enfoncé au plus surélevé. La règle qui rend
    // une interface sombre lisible : chaque niveau doit être distinct du
    // précédent, sinon les cartes disparaissent dans le fond et tout paraît plat.
    //
    //   BgSunken  <  BgSidebar  <  BgBase  <  BgSurface  <  BgRaised  <  BgHover
    //
    // BgSurface, BgRaised et BgHover valent le blanc à 5, 9 et 14 % posé sur
    // --deep : ce sont les cartes du site, précalculées en opaque pour ce qui
    // doit masquer le décor (infobulles, notifications, menus).

    public static readonly Vector4 BgSunken  = Hex(0x040B26); // --field, champs de saisie
    public static readonly Vector4 BgBase    = Hex(0x07123A); // --deep, fond de fenêtre
    public static readonly Vector4 BgSidebar = Hex(0x060F33); // barres, un cran sous le fond
    public static readonly Vector4 BgSurface = Hex(0x131E44); // cartes opaques, infobulles
    public static readonly Vector4 BgRaised  = Hex(0x1D274C); // surface survolée
    public static readonly Vector4 BgHover   = Hex(0x2A3356); // survol d'un élément surélevé

    /// <summary>Fond d'une carte posée sur la nuit : translucide, pour laisser voir le halo.</summary>
    public static readonly Vector4 CardFill      = Hex(0xFFFFFF, 0.05f);
    public static readonly Vector4 CardFillHover = Hex(0xFFFFFF, 0.09f);

    /// <summary>Haut du dégradé de fond, et cœur du halo, tirés du fond du site.</summary>
    public static readonly Vector4 NightTop  = Hex(0x0C1E5C);
    public static readonly Vector4 NightHalo = Hex(0x1D3B9A);

    /// <summary>Ombre portée sous les surfaces surélevées.</summary>
    public static readonly Vector4 Shadow = Hex(0x000000, 0.50f);

    // ─── Accents ──────────────────────────────────────────────────────────────
    //
    // Le halo bleu (--glow) marque ce qui est actif ou choisi. L'orange pompon
    // (--pom) ne sert qu'à l'action principale d'un écran, comme le bouton
    // « Copier » du site : répandu, il ne désignerait plus rien.

    public static readonly Vector4 Accent       = Hex(0x5FB4FF);
    public static readonly Vector4 AccentHover  = Hex(0x8FCBFF);
    public static readonly Vector4 AccentActive = Hex(0x3D8FE0);

    /// <summary>Version assourdie, pour les fonds et les voiles.</summary>
    public static readonly Vector4 AccentMuted = Hex(0x173067);

    public static readonly Vector4 Action       = Hex(0xFFA45C);
    public static readonly Vector4 ActionHover  = Hex(0xFFB67A);
    public static readonly Vector4 ActionActive = Hex(0xE88A40);

    /// <summary>Texte posé sur l'orange, le brun du bouton « Copier » du site.</summary>
    public static readonly Vector4 TextOnAction = Hex(0x2A1300);

    // ─── Perle ────────────────────────────────────────────────────────────────
    //
    // Le dégradé des pastilles du site, du reflet au bord : blanc, bleu clair,
    // bleu, lavande. Le halo est celui de leur box-shadow.

    public static readonly Vector4 PearlShine = Hex(0xFFFFFF);
    public static readonly Vector4 PearlLight = Hex(0xBFE4FF);
    public static readonly Vector4 PearlBody  = Hex(0x7A9CFF);
    public static readonly Vector4 PearlRim   = Hex(0xC79BFF);
    public static readonly Vector4 PearlGlow  = Hex(0x7EC3FF);

    // ─── Texte ────────────────────────────────────────────────────────────────

    public static readonly Vector4 Text      = Hex(0xEAF1FF); // --ink
    public static readonly Vector4 TextMuted = Hex(0xA9B8E6); // --mute
    public static readonly Vector4 TextFaint = Hex(0x7F8DC0); // --faint
    public static readonly Vector4 Link      = Hex(0x8FD0FF); // les étoiles du site

    /// <summary>Texte posé sur une surface claire, l'accent par exemple.</summary>
    public static readonly Vector4 TextOnLight = Hex(0x0B1433);

    // ─── Statuts ──────────────────────────────────────────────────────────────

    /// <summary>Pair joint et apparence appliquée.</summary>
    public static readonly Vector4 Online = Hex(0x6FE0B0);

    /// <summary>Transfert en cours, ou pair en pause. Jaune et non orange : l'orange est à l'action.</summary>
    public static readonly Vector4 Idle = Hex(0xF5D06F);

    public static readonly Vector4 Danger      = Hex(0xFF6B7D);
    public static readonly Vector4 DangerHover = Hex(0xFF8A98);

    // ─── Bordures ─────────────────────────────────────────────────────────────
    //
    // --line du site, rgba(143,196,255,.22), posé sur --deep ; la douce à 12 %,
    // la claire à 35 %.

    public static readonly Vector4 Border      = Hex(0x253965);
    public static readonly Vector4 BorderSoft  = Hex(0x172752);
    public static readonly Vector4 BorderLight = Hex(0x37507F);

    /// <summary>
    /// Liseré clair posé sur l'arête haute d'une surface. C'est ce qui donne
    /// l'impression que la carte capte la lumière et se détache du fond.
    /// </summary>
    public static readonly Vector4 Highlight = Hex(0xFFFFFF, 0.055f);

    // ─── Métriques (en pixels non scalés : toujours passer par S()) ───────────

    public const float RadiusWindow = 12f;

    /// <summary>Le site en a 18 : ImGui adoucit mal un si grand rayon aux petites tailles.</summary>
    public const float RadiusCard   = 14f;

    public const float RadiusFrame  =  6f;

    public const float SidebarWidth    = 168f;
    public const float SidebarItem     = 40f;
    public const float TitleBarHeight  = 40f;
    public const float StatusBarHeight = 32f;

    public const float PadWindowX = 16f;
    public const float PadWindowY = 14f;
    public const float CardPadX   = 14f;
    public const float CardPadY   = 11f;

    public const float GapXs =  3f;
    public const float GapS  =  5f;
    public const float GapM  =  8f;
    public const float GapL  = 12f;
    public const float GapXl = 22f;

    // ─── Échelle ──────────────────────────────────────────────────────────────

    /// <summary>Met une dimension à l'échelle de l'interface Dalamud.</summary>
    public static float S(float px) => px * ImGuiHelpers.GlobalScale;

    /// <summary>Met un couple de dimensions à l'échelle de l'interface Dalamud.</summary>
    public static Vector2 S(float x, float y) =>
        new(x * ImGuiHelpers.GlobalScale, y * ImGuiHelpers.GlobalScale);

    // ─── Utilitaires couleur ──────────────────────────────────────────────────

    public static Vector4 Alpha(Vector4 c, float a) => c with { W = a };

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, t);

    /// <summary>
    /// Luminance relative perçue (coefficients ITU-R BT.709). Le vert pèse dix
    /// fois plus que le bleu dans la perception, d'où l'écart des poids.
    /// </summary>
    public static float Luminance(Vector4 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>
    /// Couleur de texte lisible sur le fond donné. L'accent étant clair, du
    /// texte blanc dessus serait illisible.
    /// </summary>
    public static Vector4 TextOn(Vector4 background) =>
        Luminance(background) > 0.55f ? TextOnLight : Text;

    /// <summary>
    /// Couleur stable dérivée d'un nom, pour les pastilles d'initiales.
    /// </summary>
    /// <remarks>
    /// Hash FNV-1a sur la teinte, saturation et valeur fixées pour rester
    /// lisible sur fond sombre. Stable d'une session à l'autre : un pair garde
    /// sa couleur, ce qui aide à le reconnaître avant même d'avoir lu son nom.
    /// </remarks>
    public static Vector4 FromName(string name)
    {
        var hash = 2166136261u;

        foreach (var ch in name)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return FromHsv(hash % 360u / 360f, 0.32f, 0.62f);
    }

    /// <summary>Couleur à partir d'une teinte, d'une saturation et d'une valeur, chacune dans [0, 1].</summary>
    public static Vector4 FromHsv(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        return (i % 6) switch
        {
            0 => new Vector4(v, t, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, t, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(t, p, v, 1f),
            _ => new Vector4(v, p, q, 1f),
        };
    }
}
