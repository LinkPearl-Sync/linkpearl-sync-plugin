using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Linkpearl.Ui.Components;

/// <summary>Infobulles, pastilles d'état, écrans vides et bandeaux.</summary>
internal static class Feedback
{
    public static void Tooltip(string text) => Tooltip(() => Text.Body(text));

    /// <summary>Comme <see cref="Tooltip(string)"/>, mais le contenu est dessiné librement.</summary>
    public static void Tooltip(Action content)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Theme.S(Theme.GapL, Theme.GapM));
        using var color = ImRaii.PushColor(ImGuiCol.PopupBg, Theme.BgSurface);

        ImGui.BeginTooltip();
        using (Text.WrapAt(Theme.S(320f)))
            content();
        ImGui.EndTooltip();
    }

    /// <summary>Infobulle affichée si l'élément précédent est survolé.</summary>
    /// <remarks>
    /// <c>AllowWhenDisabled</c> : un bouton grisé est précisément celui dont on
    /// veut savoir pourquoi il l'est.
    /// </remarks>
    public static void TooltipOnHover(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(text);
    }

    /// <summary>
    /// Icône ⓘ sur la même ligne que l'élément précédent, avec l'explication
    /// en infobulle.
    /// </summary>
    /// <remarks>
    /// C'est elle qui porte, dans la page réglages, le paragraphe qu'on a
    /// sorti de la carte : la case ou le champ reste seul visible, le pourquoi
    /// n'apparaît qu'au survol.
    /// </remarks>
    public static void Hint(string text)
    {
        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        // Sans cet alignement, l'icône flotte au-dessus d'une case ou d'un
        // champ, plus hauts qu'une ligne de texte nue.
        ImGui.AlignTextToFramePadding();
        Text.Icon(Icons.Info, Theme.TextFaint);
        TooltipOnHover(text);
    }

    /// <summary>Comme <see cref="Hint(string)"/>, mais l'infobulle dessine un contenu libre.</summary>
    /// <remarks>Sert la légende des glyphes, une liste de pastilles colorées, pas un simple texte replié.</remarks>
    public static void Hint(Action content)
    {
        ImGui.SameLine(0f, Theme.S(Theme.GapS));

        ImGui.AlignTextToFramePadding();
        Text.Icon(Icons.Info, Theme.TextFaint);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(content);
    }

    /// <summary>La place que prend un <see cref="Hint(string)"/> sur sa ligne.</summary>
    /// <remarks>
    /// À réserver dans tout <c>SetNextItemWidth</c> qui précède un <c>Hint</c> sur la même
    /// ligne : sans ça, le widget prend toute la largeur restante et l'icône ⓘ se retrouve
    /// rognée hors de la carte.
    /// </remarks>
    public static float HintWidth => ImGui.CalcTextSize(Icons.Info.S()).X + Theme.S(Theme.GapS);

    /// <summary>Pastille colorée, avec un libellé facultatif.</summary>
    public static void StatusDot(Vector4 color, string? label = null)
    {
        var radius = Theme.S(4f);
        var origin = ImGui.GetCursorScreenPos();
        var line   = ImGui.GetTextLineHeight();

        ImGui.GetWindowDrawList().AddCircleFilled(
            new Vector2(origin.X + radius, origin.Y + line * 0.5f), radius, ImGui.GetColorU32(color));

        ImGui.Dummy(new Vector2(radius * 2f, line));

        if (label is null)
            return;

        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        Text.Small(label, color);
    }

    /// <summary>
    /// Écran vide.
    /// </summary>
    /// <remarks>
    /// Une liste vide sans explication se lit comme une panne. Dire ce qui
    /// manque et comment le remplir coûte trois lignes et évite un ticket.
    /// </remarks>
    public static void EmptyState(FontAwesomeIcon icon, string title, string? hint = null)
    {
        ImGui.Dummy(new Vector2(0f, Theme.S(Theme.GapXl)));

        var width = Card.Avail;
        // Vu en jeu le 26 septembre : à 72 px, le logo se perdait au milieu
        // d'une page vide. Il y a la place, c'est lui qui la remplit.
        var side  = Theme.S(160f);
        var at    = ImGui.GetCursorScreenPos() + new Vector2((width - side) * 0.5f, 0f);

        // Le logo quand il est là : une page vide est le moment où le plugin se
        // présente. L'icône de la page sinon, le temps que la texture arrive.
        if (Brand.Draw(ImGui.GetWindowDrawList(), at, side, glow: true))
        {
            ImGui.Dummy(new Vector2(0f, side));
        }
        else
        {
            using var font = Fonts.PushTitle();

            var glyph = icon.S();
            var size  = ImGui.CalcTextSize(glyph);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - size.X) * 0.5f);
            ImGui.TextColored(Theme.TextFaint, glyph);
        }

        ImGui.Dummy(new Vector2(0f, Theme.S(Theme.GapM)));

        Centered(title, Theme.Text, heading: true);

        if (hint is null)
            return;

        // Titre et phrase à la mesure du logo de 160 px : en en-tête de section
        // et en petit texte, vus en jeu le 26 septembre, ils paraissaient
        // minuscules dessous.
        ImGui.Dummy(new Vector2(0f, Theme.S(Theme.GapS)));
        Centered(hint, Theme.TextMuted);
    }

    private static void Centered(string text, Vector4 color, bool heading = false)
    {
        using var font = heading ? Fonts.PushTitle() : Fonts.PushBody();

        var safe = Glyphs.Safe(text);
        var size = ImGui.CalcTextSize(safe);

        // Trop long pour tenir sur une ligne : centrer n'a plus de sens, on replie.
        if (size.X > Card.Avail)
        {
            Text.Wrapped(text, color);
            return;
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (Card.Avail - size.X) * 0.5f);
        ImGui.TextColored(color, safe);
    }

    /// <summary>Bandeau d'information ou d'avertissement, barre d'accent à gauche.</summary>
    public static void Alert(Vector4 color, FontAwesomeIcon icon, string text)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width  = Card.Avail;
        var dl     = ImGui.GetWindowDrawList();

        var padX = Theme.S(Theme.CardPadX);
        var padY = Theme.S(Theme.GapM);

        // La hauteur se mesure avant de peindre : le texte est replié, donc son
        // encombrement dépend de la largeur disponible.
        var textWidth = width - padX * 2f - Theme.S(20f);
        var height    = ImGui.CalcTextSize(Glyphs.Safe(text), false, textWidth).Y + padY * 2f;
        var max       = new Vector2(origin.X + width, origin.Y + height);

        Surface.Panel(dl, origin, max, Theme.Mix(Theme.BgSurface, color, 0.10f),
                      Theme.Alpha(color, 0.45f), shadow: false);

        Surface.AccentBar(dl, origin, max, color);

        ImGui.Dummy(new Vector2(0f, padY));
        ImGui.Indent(padX);

        Text.WithIcon(icon, text, color, Theme.Text);

        ImGui.Unindent(padX);
        ImGui.Dummy(new Vector2(0f, padY - Theme.S(Theme.GapXs)));
    }
}
