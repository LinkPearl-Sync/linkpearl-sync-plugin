using Dalamud.Bindings.ImGui;
using Linkpearl.Core.Sync;
using Linkpearl.Ui.Components;

namespace Linkpearl.Ui;

/// <summary>Ce que veut dire chaque couleur de glyphe, dit au même endroit partout.</summary>
internal static class NameplateLegend
{
    public static void Draw()
    {
        Line(NameplateMark.Online, "pairé et connecté");
        Line(NameplateMark.Available, "utilise Linkpearl, pas encore pairé");
        Line(NameplateMark.Requesting, "vous a envoyé une demande de pairage");
        Line(NameplateMark.Offline, "pairé, hors ligne ou en pause");
        Line(NameplateMark.Trouble, "pairé, mais quelque chose a échoué : voir la page Pairs");
        Line(NameplateMark.GroupMember, "membre d'un de vos groupes");
    }

    private static void Line(NameplateMark mark, string meaning)
    {
        Feedback.StatusDot(NameplateGlyphs.ColorOf(mark));
        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        Text.Small(meaning);
    }
}
