namespace Linkpearl.Core.Safety;

/// <summary>
/// Vérifie qu'un fichier du disque que l'on s'apprête à annoncer vit sous un
/// dossier autorisé : celui des mods de Penumbra.
/// </summary>
/// <remarks>
/// Penumbra rend un chemin réel pour chaque ressource redirigée, et tout
/// chemin absolu passait pour un fichier de mod : il était lu, gardé en cache
/// et servi aux pairs. Un mod tiers qui redirige un chemin du joueur vers un
/// fichier quelconque du disque l'aurait ainsi fait parvenir à son auteur,
/// s'il est notre pair ou membre d'un de nos groupes.
///
/// La comparaison est textuelle, et non déléguée à <c>Path.GetFullPath</c> :
/// le noyau se teste sous Linux et doit raisonner sur des chemins Windows.
/// Elle ne se trompe donc que dans un sens. Un segment fait seulement de
/// points ou d'espaces (que Windows réduirait, « .. » compris) refuse le
/// chemin, et toute graphie qu'elle ne sait pas rapprocher du dossier (nom
/// court 8.3, préfixe <c>\\?\</c>) aussi : au pire, un fichier légitime n'est
/// pas synchronisé.
/// </remarks>
public static class LocalFileContainment
{
    /// <summary>Vrai si <paramref name="path"/> est strictement sous l'un des <paramref name="roots"/>.</summary>
    public static bool IsUnder(string path, IReadOnlyCollection<string> roots)
    {
        if (TryCanonical(path, out var file) is false)
            return false;

        foreach (var root in roots)
        {
            if (TryCanonical(root, out var folder)
                && file.Length > folder.Length + 1
                && file[folder.Length] == '/'
                && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Séparateurs unifiés, doublons et barre finale retirés. Échoue sur un
    /// segment qui remonterait d'un dossier.
    /// </summary>
    private static bool TryCanonical(string path, out string canonical)
    {
        canonical = "";

        var unified = path.Replace('\\', '/');
        var prefix = unified.StartsWith("//", StringComparison.Ordinal) ? "//" : unified.StartsWith('/') ? "/" : "";
        var segments = unified[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return false;

        foreach (var segment in segments)
        {
            if (segment.AsSpan().Trim(". ").IsEmpty)
                return false;
        }

        canonical = prefix + string.Join('/', segments);
        return true;
    }
}
