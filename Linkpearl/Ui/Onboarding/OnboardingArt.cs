using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Linkpearl.Core.Sync;
using System.Numerics;

namespace Linkpearl.Ui.Onboarding;

/// <summary>
/// Les illustrations de la présentation, dessinées et non importées.
/// </summary>
/// <remarks>
/// Dessinées pour rester nettes à toute échelle et prendre les couleurs du
/// thème : une image de l'interface vieillirait au premier changement de
/// teinte.
/// </remarks>
internal static class OnboardingArt
{
    private static uint C(Vector4 color) => ImGui.GetColorU32(color);

    /// <summary>Deux joueurs reliés en direct, et le rendez-vous à l'écart.</summary>
    public static void HowItWorks()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Theme.S(150f);

        var left = new Vector2(origin.X + width * 0.18f, origin.Y + height * 0.62f);
        var right = new Vector2(origin.X + width * 0.82f, origin.Y + height * 0.62f);
        var server = new Vector2(origin.X + width * 0.5f, origin.Y + height * 0.16f);

        // Le rendez-vous : discret, en pointillé. Il aide à se trouver, rien ne
        // passe par lui.
        Dashed(dl, server, left, Theme.Alpha(Theme.TextFaint, 0.6f));
        Dashed(dl, server, right, Theme.Alpha(Theme.TextFaint, 0.6f));
        dl.AddCircleFilled(server, Theme.S(16f), C(Theme.BgBase));
        dl.AddCircle(server, Theme.S(16f), C(Theme.TextFaint), 24, 1.5f);
        CenteredText(dl, server, Icons.Rendezvous.S(), Theme.TextFaint);
        CenteredText(dl, server + new Vector2(0f, Theme.S(26f)), "service", Theme.TextFaint);

        // Le lien direct, chiffré.
        dl.AddLine(left, right, C(Theme.Accent), Theme.S(3f));
        var middle = (left + right) * 0.5f;
        dl.AddCircleFilled(middle, Theme.S(14f), C(Theme.BgSurface));
        dl.AddCircle(middle, Theme.S(14f), C(Theme.Accent), 24, 2f);
        CenteredText(dl, middle, Icons.Lock.S(), Theme.Accent);
        CenteredText(dl, middle + new Vector2(0f, Theme.S(26f)), "protégé, de joueur à joueur", Theme.Accent);

        Person(dl, left, Theme.Online, "joueur");
        Person(dl, right, Theme.Accent, "ami");

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>La plaque, le clic droit, la demande chez l'autre.</summary>
    /// <remarks>
    /// Le menu prend la plus grande part : son libellé est celui du vrai menu,
    /// et ne se raccourcit pas pour tenir.
    /// </remarks>
    public static void Pairing()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Theme.S(118f);
        var gap = Theme.S(10f);
        var side = (width - gap * 2f) * 0.28f;
        var menu = (width - gap * 2f) * 0.44f;

        // 1. La plaque de nom, icône orange à droite.
        var plateMin = origin + new Vector2(0f, Theme.S(34f));
        var plateMax = plateMin + new Vector2(side, Theme.S(30f));
        dl.AddRectFilled(plateMin, plateMax, C(Theme.Alpha(Theme.Shadow, 0.55f)), Theme.S(6f));
        dl.AddText(plateMin + Theme.S(10f, 7f), C(Theme.Text), Glyphs.Safe("Alys Fenwood"));
        dl.AddCircleFilled(new Vector2(plateMax.X - Theme.S(12f), (plateMin.Y + plateMax.Y) * 0.5f),
                           Theme.S(5f), C(NameplateGlyphs.ColorOf(NameplateMark.Available)));
        Caption(dl, new Vector2(plateMin.X, plateMax.Y + Theme.S(8f)), "1. une icône orange");

        // 2. Le menu clic droit.
        var menuMin = origin + new Vector2(side + gap, 0f);
        var line = Theme.S(22f);
        var menuMax = menuMin + new Vector2(menu, line * 3f + Theme.S(8f));
        dl.AddRectFilled(menuMin, menuMax, C(Theme.BgSurface), Theme.S(6f));
        dl.AddRect(menuMin, menuMax, C(Theme.Border), Theme.S(6f));
        MenuLine(dl, menuMin, 0, line, "Examiner", false, menu);
        MenuLine(dl, menuMin, 1, line, "Envoyer un message", false, menu);
        MenuLine(dl, menuMin, 2, line, "Linkpearl : demander le pairage", true, menu);
        Caption(dl, new Vector2(menuMin.X, menuMax.Y + Theme.S(8f)), "2. clic droit sur le joueur");

        // 3. La demande, chez l'autre.
        var toastMin = origin + new Vector2(side + menu + gap * 2f, Theme.S(18f));
        var toastMax = toastMin + new Vector2(side, Theme.S(56f));
        dl.AddRectFilled(toastMin, toastMax, C(Theme.BgSurface), Theme.S(8f));
        dl.AddRectFilled(toastMin, new Vector2(toastMin.X + Theme.S(3f), toastMax.Y), C(Theme.Accent), Theme.S(8f),
                         ImDrawFlags.RoundCornersLeft);
        dl.AddText(toastMin + Theme.S(10f, 8f), C(Theme.Text), Glyphs.Safe("Demande de pairage"));
        dl.AddText(toastMin + Theme.S(10f, 30f), C(Theme.Online), Icons.Accept.S());
        dl.AddText(toastMin + Theme.S(32f, 30f), C(Theme.Danger), Icons.Decline.S());
        Caption(dl, new Vector2(toastMin.X, toastMax.Y + Theme.S(8f)), "3. acceptation par l'autre joueur");

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>Une ligne de pair, avec sa pause et ses trois bascules.</summary>
    public static void Controls()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = Theme.S(44f);

        var max = origin + new Vector2(width, rowHeight);
        dl.AddRectFilled(origin, max, C(Theme.BgSurface), Theme.S(8f));
        dl.AddCircleFilled(origin + new Vector2(Theme.S(18f), rowHeight * 0.5f), Theme.S(5f), C(Theme.Online));
        dl.AddText(origin + new Vector2(Theme.S(32f), rowHeight * 0.5f - ImGui.GetTextLineHeight() * 0.5f),
                   C(Theme.Text), Glyphs.Safe("Alys Fenwood"));

        // De droite à gauche. Les sons sont coupés : on peut bloquer une seule
        // catégorie, et c'est ce que l'image doit montrer.
        var x = max.X - Theme.S(8f);
        x = Toggle(dl, x, origin.Y, rowHeight, Icons.Paused, true);
        x -= Theme.S(12f);
        x = Toggle(dl, x, origin.Y, rowHeight, Icons.Sounds, false);
        x = Toggle(dl, x, origin.Y, rowHeight, Icons.Vfx, true);
        Toggle(dl, x, origin.Y, rowHeight, Icons.Animations, true);

        ImGui.Dummy(new Vector2(width, rowHeight + Theme.S(4f)));
    }

    /// <summary>La jauge du quota, une marque par apparence moyenne de 800 Mo.</summary>
    public static void QuotaGauge(int quotaGiB)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Theme.S(14f);
        var max = origin + new Vector2(width, height);

        dl.AddRectFilled(origin, max, C(Theme.BgSunken), height * 0.5f);

        // Tant que les marques restent distinctes : au-delà, la jauge
        // deviendrait un aplat.
        var appearances = (int)(quotaGiB * 1024f / 800f);
        var spacing = width / Math.Max(appearances, 1);

        if (spacing >= Theme.S(4f))
        {
            for (var i = 1; i < appearances; i++)
            {
                var px = origin.X + spacing * i;
                dl.AddLine(new Vector2(px, origin.Y + Theme.S(3f)), new Vector2(px, max.Y - Theme.S(3f)),
                           C(Theme.Alpha(Theme.Accent, 0.55f)), 1f);
            }
        }

        dl.AddRect(origin, max, C(Theme.Border), height * 0.5f);
        ImGui.Dummy(new Vector2(width, height + Theme.S(4f)));
    }

    private static float Toggle(ImDrawListPtr dl, float right, float top, float rowHeight,
                                FontAwesomeIcon icon, bool on)
    {
        var size = Theme.S(28f);
        var min = new Vector2(right - size, top + (rowHeight - size) * 0.5f);
        var max = min + new Vector2(size, size);
        var tint = on ? Theme.Accent : Theme.TextFaint;

        dl.AddRectFilled(min, max, C(on ? Theme.AccentMuted : Theme.BgSunken), Theme.S(6f));
        CenteredText(dl, (min + max) * 0.5f, icon.S(), tint);

        if (on is false)
            dl.AddLine(min + Theme.S(6f, 6f), max - Theme.S(6f, 6f), C(Theme.Danger), 2f);

        return min.X - Theme.S(6f);
    }

    private static void Person(ImDrawListPtr dl, Vector2 feet, Vector4 color, string label)
    {
        var head = feet - new Vector2(0f, Theme.S(44f));
        dl.AddCircleFilled(head, Theme.S(11f), C(color));
        dl.AddRectFilled(head + new Vector2(-Theme.S(15f), Theme.S(14f)), feet + new Vector2(Theme.S(15f), 0f),
                         C(color), Theme.S(10f), ImDrawFlags.RoundCornersTop);
        CenteredText(dl, feet + new Vector2(0f, Theme.S(14f)), label, Theme.TextMuted);
    }

    private static void MenuLine(ImDrawListPtr dl, Vector2 menuMin, int index, float line, string text,
                                 bool highlighted, float width)
    {
        var min = menuMin + new Vector2(Theme.S(4f), Theme.S(4f) + line * index);
        var max = min + new Vector2(width - Theme.S(8f), line);

        if (highlighted)
            dl.AddRectFilled(min, max, C(Theme.AccentMuted), Theme.S(4f));

        dl.AddText(min + new Vector2(Theme.S(6f), (line - ImGui.GetTextLineHeight()) * 0.5f),
                   C(highlighted ? Theme.Text : Theme.TextMuted), Glyphs.Safe(text));
    }

    private static void Caption(ImDrawListPtr dl, Vector2 at, string text)
        => dl.AddText(at, C(Theme.TextFaint), Glyphs.Safe(text));

    private static void CenteredText(ImDrawListPtr dl, Vector2 center, string text, Vector4 color)
    {
        var safe = Glyphs.Safe(text);
        var size = ImGui.CalcTextSize(safe);
        dl.AddText(center - size * 0.5f, C(color), safe);
    }

    private static void Dashed(ImDrawListPtr dl, Vector2 from, Vector2 to, Vector4 color)
    {
        var length = Vector2.Distance(from, to);
        var direction = (to - from) / length;
        var dash = Theme.S(5f);

        for (var d = 0f; d < length; d += dash * 2f)
        {
            var end = Math.Min(d + dash, length);
            dl.AddLine(from + direction * d, from + direction * end, C(color), 1.5f);
        }
    }
}
