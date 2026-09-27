using Dalamud.Bindings.ImGui;
using Linkpearl.Ui.Components;
using System.Numerics;

namespace Linkpearl.Ui.Shell;

/// <summary>Ce que la barre d'état montre, préparé par l'appelant.</summary>
/// <remarks>
/// Un enregistrement plutôt que des services : la barre ne doit rien
/// interroger. Une image bloquée, c'est le jeu qui saccade.
/// </remarks>
internal readonly record struct ShellStatus(
    bool Connected,
    string? Failure,
    string? Character,
    int Pairs,
    int Applied);

/// <summary>
/// Bandeau de pied de fenêtre : état du rendez-vous, personnage suivi, pairs.
/// </summary>
internal static class StatusBar
{
    public static void Draw(ShellStatus status)
    {
        var height = Theme.S(Theme.StatusBarHeight);
        var origin = ImGui.GetCursorScreenPos();
        var width  = ImGui.GetContentRegionAvail().X;
        var end    = new Vector2(origin.X + width, origin.Y + height);
        var dl     = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(Theme.BgSidebar),
            Theme.S(Theme.RadiusWindow), ImDrawFlags.RoundCornersBottom);

        dl.AddLine(origin, new Vector2(end.X, origin.Y), ImGui.GetColorU32(Theme.BorderSoft), 1f);

        // Corps de texte et non petit : vu en jeu le 26 septembre, l'état se
        // lisait mal en bas d'une grande fenêtre.
        using var font = Fonts.PushBody();

        var mid = origin.Y + height * 0.5f;

        // Pastille d'état, à gauche : verte quand le service répond, éteinte
        // sinon. Pas la perle : c'est un statut, et un statut garde sa couleur.
        // Essayée le 26 septembre, la perle bleue ne disait plus « connecté ».
        dl.AddCircleFilled(
            new Vector2(origin.X + Theme.S(Theme.PadWindowX), mid),
            Theme.S(4.5f),
            ImGui.GetColorU32(status.Connected ? Theme.Online : Theme.TextFaint));

        // Nommer l'état en toutes lettres : un point n'a jamais rien dit à
        // personne, et le plugin sert pour de bon sans rendez-vous joignable.
        // Il s'agit de la disponibilité du service, pas de celle du plugin :
        // les sessions déjà établies peuvent continuer sans lui.
        var state  = status.Connected ? "Service disponible" : "Service indisponible";
        var detail = status.Connected ? status.Character : status.Failure;
        var left   = detail is null ? state : $"{state} · {detail}";

        // Décompte des pairs, à droite. Mesuré d'abord : c'est lui qui borne
        // la place laissée au texte de gauche.
        var right = status.Pairs switch
        {
            0 => null,
            _ when status.Applied > 0 => $"{status.Applied} / {status.Pairs} pairs visibles",
            _ => $"{status.Pairs} pair{(status.Pairs > 1 ? "s" : "")}",
        };

        var rightWidth = right is null ? 0f : ImGui.CalcTextSize(right).X + Theme.S(Theme.GapL);
        var leftX      = origin.X + Theme.S(Theme.PadWindowX + 12f);
        var room       = end.X - Theme.S(Theme.PadWindowX) - rightWidth - leftX;

        // L'état d'abord, dans la couleur de la pastille ; le détail ensuite,
        // plus discret. Une fenêtre étroite rognait la panne en plein mot : on
        // abrège le détail, et le texte entier reste au survol.
        var stateSize = ImGui.CalcTextSize(state);
        var top       = mid - stateSize.Y * 0.5f;

        dl.AddText(new Vector2(leftX, top),
            ImGui.GetColorU32(status.Connected ? Theme.Online : Theme.TextMuted), state);

        var shownDetail = detail is null ? null : Ellipsize($" · {detail}", room - stateSize.X);
        var leftEnd     = new Vector2(leftX + stateSize.X, top + stateSize.Y);

        if (shownDetail is not null)
        {
            dl.AddText(new Vector2(leftX + stateSize.X, top), ImGui.GetColorU32(Theme.TextFaint), shownDetail);
            leftEnd.X += ImGui.CalcTextSize(shownDetail).X;
        }

        if (right is not null)
        {
            var size = ImGui.CalcTextSize(right);

            dl.AddText(new Vector2(end.X - size.X - Theme.S(Theme.PadWindowX), mid - size.Y * 0.5f),
                ImGui.GetColorU32(Theme.TextFaint), right);
        }

        var hoveringLeft = ImGui.IsMouseHoveringRect(new Vector2(leftX, top), leftEnd);
        var abridged     = detail is not null && shownDetail != $" · {detail}";

        ImGui.Dummy(new Vector2(width, height));

        if (hoveringLeft && abridged)
            Feedback.Tooltip(() => Text.Body(left));
    }

    /// <summary>Le texte tel quel s'il tient dans la largeur, sinon abrégé par « … ».</summary>
    private static string Ellipsize(string text, float room)
    {
        if (ImGui.CalcTextSize(text).X <= room)
            return text;

        const string ellipsis = "…";

        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text[..length] + ellipsis).X > room)
            length--;

        return text[..length].TrimEnd(' ', ',') + ellipsis;
    }
}
