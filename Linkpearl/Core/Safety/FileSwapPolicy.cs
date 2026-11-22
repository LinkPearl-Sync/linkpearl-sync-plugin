using Linkpearl.Core.Manifest;

namespace Linkpearl.Core.Safety;

/// <summary>
/// Valide un échange : un chemin de jeu que Penumbra fera servir par un autre.
/// </summary>
/// <remarks>
/// Les deux côtés passent les mêmes contrôles qu'un chemin de fichier, puisque
/// la cible aussi atteint Penumbra. S'y ajoute une règle propre aux échanges :
/// même extension des deux côtés. Faire lire un modèle là où le jeu attend une
/// animation reviendrait à donner à du code natif un format qu'il n'attend
/// pas, et aucun mod honnête n'en a besoin.
/// </remarks>
public static class FileSwapPolicy
{
    public static bool TryNormalize(FileSwap swap, Quotas quotas, out FileSwap? normalized, out string? rejection)
    {
        normalized = null;

        if (TryNormalizeSide(swap.GamePath, quotas, out var gamePath, out rejection) is false
            || TryNormalizeSide(swap.TargetGamePath, quotas, out var target, out rejection) is false)
            return false;

        if (string.Equals(Extension(gamePath), Extension(target), StringComparison.Ordinal) is false)
        {
            rejection = "échange entre deux extensions différentes";
            return false;
        }

        if (BoundToSkeleton(gamePath) && string.Equals(Family(gamePath), Family(target), StringComparison.Ordinal) is false)
        {
            rejection = "échange entre deux squelettes différents";
            return false;
        }

        normalized = new FileSwap(gamePath, target);
        rejection = null;
        return true;
    }

    private static bool TryNormalizeSide(string raw, Quotas quotas, out string normalized, out string? rejection)
    {
        if (GamePathPolicy.TryNormalize(raw, quotas, out normalized, out rejection) is false)
            return false;

        return ExtensionAllowList.IsAllowed(normalized, out rejection);
    }

    /// <summary>
    /// Les formats liés aux os d'un squelette : animation, squelette et ses
    /// compléments, physique, points d'attache.
    /// </summary>
    /// <remarks>
    /// Une animation liée à un os au-delà du squelette qui la joue fait écrire
    /// le moteur hors de ses tableaux. L'émetteur vérifie ses propres fichiers
    /// contre son squelette, mais un échange ne porte aucun fichier : il
    /// suffisait de faire servir une animation humaine par celle d'un monstre,
    /// fichier du jeu tout à fait valide, pour passer tous les contrôles.
    /// </remarks>
    private static readonly HashSet<string> SkeletonBound = [".pap", ".sklb", ".skp", ".phyb", ".eid"];

    private static bool BoundToSkeleton(string gamePath) => SkeletonBound.Contains(Extension(gamePath));

    /// <summary>
    /// La famille de squelette d'un chemin : <c>chara/human/</c>,
    /// <c>chara/monster/</c>... ou le chemin entier hors de <c>chara/</c>.
    /// </summary>
    /// <remarks>
    /// Les mods de pose échangent des animations humaines entre elles, d'une
    /// race à l'autre : la règle les laisse passer. Hors de <c>chara/</c>, il
    /// n'y a pas de famille à comparer, et un tel échange ne vise donc qu'un
    /// chemin identique, ce qui revient à le refuser.
    /// </remarks>
    private static string Family(string gamePath)
    {
        if (gamePath.StartsWith("chara/", StringComparison.Ordinal) is false)
            return gamePath;

        var end = gamePath.IndexOf('/', "chara/".Length);
        return end < 0 ? gamePath : gamePath[..(end + 1)];
    }

    private static string Extension(string gamePath)
    {
        var dot = gamePath.LastIndexOf('.');
        return dot < 0 ? string.Empty : gamePath[dot..];
    }
}
