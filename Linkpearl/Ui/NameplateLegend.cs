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
        Line(NameplateMark.Requesting, "demande de pairage reçue");
        Line(NameplateMark.Offline, "pairé, hors ligne ou en pause");
        Line(NameplateMark.Trouble, "pairé, erreur de synchronisation : consulter le carnet");
        Line(NameplateMark.GroupMember, "membre d'un groupe commun");
    }

    private static void Line(NameplateMark mark, string meaning)
    {
        Feedback.StatusDot(NameplateGlyphs.ColorOf(mark));
        ImGui.SameLine(0f, Theme.S(Theme.GapS));
        Text.Small(meaning);
    }
}
