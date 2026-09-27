using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System.Numerics;
using Linkpearl.Core.Safety;

namespace Linkpearl.Ui.Shell;

/// <summary>
/// Barre de titre maison, en remplacement de celle de Dalamud.
/// </summary>
/// <remarks>
/// Elle assume le déplacement de la fenêtre, que <c>NoTitleBar</c> supprime. Le
/// redimensionnement, lui, reste natif : la fenêtre ne pose pas <c>NoResize</c>,
/// ImGui gère donc les bords et la poignée.
/// </remarks>
internal static class TitleBar
{
    /// <summary>Dessine la barre. Rend vrai si la fermeture est demandée.</summary>
    /// <param name="receive">Animations, VFX et sons acceptés de tous ; null pour ne pas montrer les bascules.</param>
    public static bool Draw(float width, TransientCategories? receive = null, Action<TransientCategories>? setReceive = null)
    {
        var height = Theme.S(Theme.TitleBarHeight);
        var origin = ImGui.GetCursorScreenPos();
        var end    = new Vector2(origin.X + width, origin.Y + height);
        var dl     = ImGui.GetWindowDrawList();

        // Bandeau un cran sous la nuit, comme les barres du site : c'est le
        // contenu qui porte la lumière, pas le chrome. Le rayon est celui de la
        // fenêtre, sinon le fond déborde aux angles.
        dl.AddRectFilled(origin, end, ImGui.GetColorU32(Theme.BgSidebar),
            Theme.S(Theme.RadiusWindow), ImDrawFlags.RoundCornersTop);

        dl.AddLine(new Vector2(origin.X, end.Y - 0.5f), new Vector2(end.X, end.Y - 0.5f),
            ImGui.GetColorU32(Theme.Border), 1f);

        // Bouton carré calé sur la hauteur de la barre, et non sur la hauteur
        // d'un cadre ImGui : celle-ci dépend de la police et du padding, si bien
        // que le fond ne tomberait pas au centre du bandeau.
        var margin = MathF.Round(Theme.S(Theme.GapS));
        var side   = MathF.Round(height - margin * 2f);
        var closed = false;

        // Les bascules, puis la croix : autant de carrés pris sur la zone de
        // déplacement, qui sinon capterait leurs clics.
        var buttons = receive is null ? 1 : 4;

        // ── Zone de déplacement ───────────────────────────────────────────────
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##titledrag", new Vector2(Math.Max(1f, width - (side + margin) * buttons), height));

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

        // ── Marque ────────────────────────────────────────────────────────────
        // Le logo du site en pastille, puis le nom en Fredoka : le plugin se
        // reconnaît à ce qu'on a vu en l'installant.
        using (Fonts.PushH2())
        {
            const string text = "Linkpearl";

            var logo = MathF.Round(height - Theme.S(Theme.GapM) * 2f);
            var x    = origin.X + Theme.S(Theme.PadWindowX);

            if (Brand.Draw(dl, new Vector2(x, MathF.Round(origin.Y + (height - logo) * 0.5f)), logo))
                x += logo + Theme.S(Theme.GapS);

            var textSize = ImGui.CalcTextSize(text);

            dl.AddText(new Vector2(x, origin.Y + (height - textSize.Y) * 0.5f),
                ImGui.GetColorU32(Theme.Text), text);
        }

        // ── Fermeture ─────────────────────────────────────────────────────────
        var position = new Vector2(MathF.Round(end.X - margin - side), MathF.Round(origin.Y + margin));

        if (IconButton(dl, position, side, Icons.Close, "shell_close"))
            closed = true;

        // ── Bascules de réception ─────────────────────────────────────────────
        // Dans la barre et non dans les réglages : couper les effets d'une
        // foule doit se faire d'un clic, au moment où ils gênent.
        if (receive is { } current && setReceive is not null)
        {
            (FontAwesomeIcon Icon, bool On, string What, Func<bool, TransientCategories> With)[] toggles =
            [
                (Icons.Sounds, current.Sounds, "les sons", on => current with { Sounds = on }),
                (Icons.Vfx, current.Vfx, "les VFX", on => current with { Vfx = on }),
                (Icons.Animations, current.Animations, "les animations", on => current with { Animations = on }),
            ];

            for (var i = 0; i < toggles.Length; i++)
            {
                var (icon, on, what, with) = toggles[i];
                var at = new Vector2(MathF.Round(position.X - (side + margin) * (i + 1)), position.Y);

                if (IconButton(dl, at, side, icon, $"shell_receive_{i}", struck: on is false,
                               tooltip: on ? $"Reçoit {what} des pairs. Cliquer pour bloquer."
                                           : $"{char.ToUpperInvariant(what[0])}{what[1..]} des pairs sont bloqués. Cliquer pour les recevoir."))
                    setReceive(with(on is false));
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        return closed;
    }

    /// <summary>
    /// Bouton d'icône dessiné à la main.
    /// </summary>
    /// <remarks>
    /// <c>ImGui.Button</c> dimensionne son fond à partir du cadre courant, donc
    /// de la police et du padding : sur un bandeau de hauteur fixe, il ne tombe
    /// pas au centre. Ici le carré est posé aux coordonnées voulues, et le
    /// glyphe centré sur son encombrement réel.
    /// </remarks>
    private static bool IconButton(ImDrawListPtr dl, Vector2 position, float side,
                                   FontAwesomeIcon icon, string id, bool struck = false, string? tooltip = null)
    {
        ImGui.SetCursorScreenPos(position);

        var clicked = ImGui.InvisibleButton($"##{id}", new Vector2(side, side));
        var hovered = ImGui.IsItemHovered();

        if (hovered)
        {
            dl.AddRectFilled(position, position + new Vector2(side, side),
                ImGui.GetColorU32(Theme.Alpha(Theme.Text, 0.16f)),
                Theme.S(Theme.RadiusFrame));

            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (tooltip is not null)
                ImGui.SetTooltip(tooltip);
        }

        var glyph = icon.S();
        var size  = ImGui.CalcTextSize(glyph);
        var color = ImGui.GetColorU32(struck ? Theme.Alpha(Theme.Text, 0.45f) : Theme.Text);

        dl.AddText(
            new Vector2(MathF.Round(position.X + (side - size.X) * 0.5f),
                        MathF.Round(position.Y + (side - size.Y) * 0.5f)),
            color,
            glyph);

        // Barré plutôt qu'éteint seul : une icône grisée se lit mal sur la
        // nacre, un trait se voit d'un coup d'œil.
        if (struck)
        {
            var inset = side * 0.22f;
            dl.AddLine(position + new Vector2(inset, inset), position + new Vector2(side - inset, side - inset),
                ImGui.GetColorU32(Theme.Danger), Theme.S(2f));
        }

        return clicked;
    }
}
