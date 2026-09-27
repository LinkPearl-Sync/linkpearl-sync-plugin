using Dalamud.Bindings.ImGui;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Safety;

namespace Linkpearl.Ui.Components;

/// <summary>La puce « banni » et son motif, identique sur toutes les pages.</summary>
/// <remarks>
/// Le motif vient d'un service tiers : il passe par <see cref="Glyphs.Safe"/>
/// comme un nom de pair, et ne s'affiche qu'au survol pour ne pas occuper la ligne.
/// </remarks>
internal static class BanChip
{
    /// <summary>Dessine la puce si le joueur est listé ; rend vrai dans ce cas.</summary>
    public static bool Draw(IServiceBans bans, PlayerFingerprint? player)
    {
        if (player is not { } print || bans.Status(print) is not { Verdict: BanVerdict.Listed, Ban: { } ban })
            return false;

        Chip.Draw("bloqué par un service", Theme.Danger, Icons.Blocked);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{ban.Service} : {Glyphs.Safe(ban.Reason)}");

        return true;
    }
}
