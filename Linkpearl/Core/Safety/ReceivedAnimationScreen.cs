namespace Linkpearl.Core.Safety;

/// <summary>
/// Juge les animations reçues contre le squelette du personnage qui les jouera.
/// </summary>
/// <remarks>
/// Une animation liée à un os au-delà du squelette qui la joue fait écrire le
/// moteur du jeu hors de ses tableaux. L'émetteur vérifie ses propres
/// fichiers, mais un émetteur modifié s'en passe : c'était le résiduel de
/// l'audit, accepté au départ parce que le pairage explicite le couvrait, ce
/// qu'il ne fait pas pour les membres d'un groupe privé.
///
/// Le jugement lui-même est ici, testable ; la lecture des os passe par le
/// chargeur Havok du jeu, dans l'intégration. Deux cas où l'on ne juge pas,
/// faute de connaître le squelette final : un squelette reçu avec
/// l'apparence (squelettes personnalisés), et un personnage dont on ne lit
/// pas le squelette. Un changement de race par Glamourer, posé après le
/// redessin, n'est pas couvert : à vérifier en jeu.
/// </remarks>
public static class ReceivedAnimationScreen
{
    /// <summary>
    /// Les chemins de jeu d'animation posés depuis un fichier reçu, à lire ;
    /// aucun si l'apparence porte son propre squelette.
    /// </summary>
    /// <param name="isLocalFile">Vrai si la valeur est un fichier du cache, et non la cible d'un échange.</param>
    public static IReadOnlyList<string> ToInspect(IReadOnlyDictionary<string, string> pathMap, Func<string, bool> isLocalFile)
    {
        if (pathMap.Keys.Any(IsSkeleton))
            return [];

        return pathMap
            .Where(entry => entry.Key.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) && isLocalFile(entry.Value))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Les animations à écarter : illisibles par le chargeur du jeu, ou qui animent un os au-delà du squelette.
    /// </summary>
    /// <param name="maxBoneByGamePath">Le plus grand indice d'os animé, ou null si l'animation ne se charge pas.</param>
    /// <param name="boneCount">Les os du squelette qui jouera, ou null s'il ne se lit pas : on ne juge pas.</param>
    public static IReadOnlyList<string> Refused(IReadOnlyDictionary<string, int?> maxBoneByGamePath, int? boneCount)
    {
        if (boneCount is not { } count)
            return [];

        return maxBoneByGamePath
            .Where(entry => entry.Value is not { } most || most >= count)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSkeleton(string gamePath)
        => gamePath.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)
           || gamePath.EndsWith(".skp", StringComparison.OrdinalIgnoreCase);
}
