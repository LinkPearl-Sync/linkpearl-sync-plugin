using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Linkpearl.Ui.Components;

internal enum BtnTone
{
    /// <summary>L'action principale de l'écran, en orange pompon. Une seule par vue.</summary>
    Action,

    /// <summary>L'option choisie dans un groupe de boutons, teintée du halo.</summary>
    Selected,

    /// <summary>Action courante, surface translucide bordée.</summary>
    Secondary,

    /// <summary>Action discrète : pas de fond, sauf au survol.</summary>
    Ghost,

    /// <summary>Action destructrice.</summary>
    Danger,

    /// <summary>Action de confirmation.</summary>
    Success,
}

internal enum BtnSize
{
    Small,
    Medium,

    /// <summary>Occupe toute la largeur disponible.</summary>
    Block,
}

/// <summary>
/// Boutons.
/// </summary>
/// <remarks>
/// La couleur du libellé est déduite de la luminance du fond, ce qui garantit
/// la lisibilité même sur l'orange, qui est clair, et permet de changer
/// la palette sans repasser sur chaque bouton.
/// </remarks>
internal static class Btn
{
    public static bool Draw(string label,
                            BtnTone tone = BtnTone.Secondary,
                            BtnSize size = BtnSize.Medium,
                            FontAwesomeIcon? icon = null,
                            bool disabled = false,
                            string? tooltip = null,
                            string? id = null)
    {
        var (normal, hovered, active) = Palette(tone);
        var caption = Compose(label, icon);

        using var color = ImRaii.PushColor(ImGuiCol.Button, normal)
                                .Push(ImGuiCol.ButtonHovered, hovered)
                                .Push(ImGuiCol.ButtonActive,  active)
                                .Push(ImGuiCol.Text,          TextFor(tone, normal));

        // En pilule, comme les boutons du site : le rayon vaut la moitié de la
        // hauteur, ce qui reste juste à toute échelle. Le contour des champs de
        // saisie ne doit pas déborder sur les boutons, d'où la bordure nulle.
        var rounding = ImGui.GetFrameHeight() * 0.5f;

        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f)
                                .Push(ImGuiStyleVar.FrameRounding, rounding);

        bool clicked;

        using (ImRaii.Disabled(disabled))
        {
            clicked = ImGui.Button($"{caption}##{id ?? label}", Dimensions(size, caption));
            Outline(tone, rounding);
        }

        // Hors de la portée désactivée : un widget désactivé ne remonte pas le survol.
        if (tooltip != null)
            Feedback.TooltipOnHover(tooltip);

        return clicked && disabled is false;
    }

    /// <summary>Bouton réduit à une icône, carré.</summary>
    public static bool Icon(FontAwesomeIcon icon, string id,
                            BtnTone tone = BtnTone.Ghost,
                            string? tooltip = null,
                            bool disabled = false)
    {
        var (normal, hovered, active) = Palette(tone);
        var side = ImGui.GetFrameHeight();

        using var color = ImRaii.PushColor(ImGuiCol.Button, normal)
                                .Push(ImGuiCol.ButtonHovered, hovered)
                                .Push(ImGuiCol.ButtonActive,  active)
                                .Push(ImGuiCol.Text,          TextFor(tone, normal));

        using var flat = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);

        bool clicked;

        using (ImRaii.Disabled(disabled))
        {
            // Le bouton sans libellé, et l'icône posée au centre à la main :
            // ImGui centre l'avance du glyphe, pas le glyphe dessiné, et les
            // icônes FontAwesome fusionnées dans Inter en sortent décalées.
            clicked = ImGui.Button($"##{id}", new Vector2(side, side));

            var glyph = icon.S();
            var size  = ImGui.CalcTextSize(glyph);
            var min   = ImGui.GetItemRectMin();
            var max   = ImGui.GetItemRectMax();

            ImGui.GetWindowDrawList().AddText(
                new Vector2(MathF.Round((min.X + max.X - size.X) * 0.5f),
                            MathF.Round((min.Y + max.Y - size.Y) * 0.5f)),
                ImGui.GetColorU32(ImGuiCol.Text), glyph);

            Outline(tone, Theme.S(Theme.RadiusFrame));
        }

        if (tooltip != null)
            Feedback.TooltipOnHover(tooltip);

        return clicked && disabled is false;
    }

    /// <summary>Largeur qu'occuperait le bouton, pour aligner à droite.</summary>
    public static float Measure(string label, BtnSize size = BtnSize.Medium, FontAwesomeIcon? icon = null)
    {
        var caption = Compose(label, icon);
        var width   = Dimensions(size, caption).X;

        return width > 0f ? width : Width(caption);
    }

    private static string Compose(string label, FontAwesomeIcon? icon)
        => icon is { } value ? $"{value.S()}  {label}" : label;

    /// <summary>
    /// Encombrement d'un bouton.
    /// </summary>
    /// <remarks>
    /// La petite taille donne une largeur régulière aux boutons courts, qui
    /// s'alignent ainsi les uns sous les autres, mais c'est un plancher et non
    /// une largeur imposée : un libellé plus long l'emporte. Fixée pour de bon,
    /// elle rognerait le texte, et un libellé tronqué se lit comme un bug.
    /// </remarks>
    private static Vector2 Dimensions(BtnSize size, string caption) => size switch
    {
        BtnSize.Block => new Vector2(Card.FullWidth, 0f),
        BtnSize.Small => new Vector2(Math.Max(Theme.S(88f), Width(caption)), 0f),
        _             => Vector2.Zero,   // largeur ajustée au contenu
    };

    /// <summary>Largeur du libellé, marges du cadre comprises.</summary>
    private static float Width(string caption)
        => ImGui.CalcTextSize(caption).X + ImGui.GetStyle().FramePadding.X * 2f;

    private static (Vector4 Normal, Vector4 Hovered, Vector4 Active) Palette(BtnTone tone) => tone switch
    {
        BtnTone.Action   => (Theme.Action, Theme.ActionHover, Theme.ActionActive),
        BtnTone.Selected => (Theme.Alpha(Theme.Accent, 0.20f), Theme.Alpha(Theme.Accent, 0.30f),
                             Theme.Alpha(Theme.Accent, 0.38f)),
        BtnTone.Danger   => (Theme.Danger, Theme.DangerHover, Theme.Mix(Theme.Danger, Theme.BgBase, 0.3f)),
        BtnTone.Success  => (Theme.Online, Theme.Mix(Theme.Online, Theme.Text, 0.2f),
                             Theme.Mix(Theme.Online, Theme.BgBase, 0.3f)),
        BtnTone.Ghost    => (Vector4.Zero, Theme.Alpha(Theme.Text, 0.08f), Theme.Alpha(Theme.Text, 0.12f)),
        _                => (Theme.Alpha(Theme.Text, 0.06f), Theme.Alpha(Theme.Text, 0.11f),
                             Theme.Alpha(Theme.Text, 0.04f)),
    };

    /// <summary>
    /// Couleur du libellé.
    /// </summary>
    /// <remarks>
    /// Les tons translucides sont du blanc à quelques pour cent : leur
    /// luminance, calculée sur la seule couleur, les ferait passer pour clairs
    /// et leur donnerait un texte sombre, illisible sur la nuit.
    /// </remarks>
    private static Vector4 TextFor(BtnTone tone, Vector4 background) => tone switch
    {
        BtnTone.Action                                         => Theme.TextOnAction,
        BtnTone.Selected or BtnTone.Secondary or BtnTone.Ghost => Theme.Text,
        _                                                      => Theme.TextOn(background),
    };

    /// <summary>
    /// Liseré des tons translucides.
    /// </summary>
    /// <remarks>
    /// Sans lui, un bouton secondaire posé sur une carte translucide n'a plus de
    /// bord, et un bouton choisi ne se distingue que par une teinte.
    /// </remarks>
    private static void Outline(BtnTone tone, float rounding)
    {
        Vector4? line = tone switch
        {
            BtnTone.Selected  => Theme.Accent,
            BtnTone.Secondary => Theme.Border,
            _                 => null,
        };

        if (line is { } color)
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(color), rounding, ImDrawFlags.None, 1f);
    }
}
