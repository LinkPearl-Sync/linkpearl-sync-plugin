using Linkpearl.Core.Safety;

namespace Linkpearl.Core.Manifest;

/// <summary>Un fichier du disque, et les chemins de jeu qu'il remplace.</summary>
public sealed record LocalFile(string LocalPath, IReadOnlyList<string> GamePaths);

/// <summary>Un chemin de jeu qui en désigne un autre, sans fichier à transférer.</summary>
public sealed record FileSwap(string GamePath, string TargetGamePath);

/// <summary>Le tri des ressources rendues par Penumbra.</summary>
public sealed record ClassifiedResources(
    IReadOnlyList<LocalFile> Files,
    IReadOnlyList<FileSwap> Swaps,
    IReadOnlyList<SkippedFile> Skipped);

/// <summary>
/// Trie ce que Penumbra rend pour un personnage.
/// </summary>
/// <remarks>
/// Penumbra rend « chemin réel -> chemins de jeu », et le chemin réel est de
/// trois natures :
/// <list type="bullet">
/// <item>un fichier du disque, quand un mod redirige la ressource ;</item>
/// <item>un autre chemin de jeu, quand un mod fait un échange de fichier ;</item>
/// <item>le chemin de jeu lui-même, quand la ressource n'est pas moddée.</item>
/// </list>
/// Les confondre ferait chercher un fichier qui n'existe pas, ou gonflerait le
/// manifeste de centaines d'entrées vanilla qu'il n'y a aucune raison de
/// transférer.
/// </remarks>
public static class ResourcePathClassifier
{
    /// <summary>Raison donnée pour un fichier redirigé hors du dossier de mods.</summary>
    public const string OutsideModDirectory = "fichier hors du dossier de mods de Penumbra";

    public static ClassifiedResources Classify(
        IEnumerable<(string ActualPath, IReadOnlyCollection<string> GamePaths)> resources,
        Quotas quotas,
        IReadOnlyCollection<string> localRoots)
    {
        var files = new List<LocalFile>();
        var swaps = new List<FileSwap>();
        var skipped = new List<SkippedFile>();

        foreach (var (actualPath, rawGamePaths) in resources)
        {
            // La nature du chemin réel se détermine en premier. La liste blanche
            // d'extensions ne s'applique qu'à ce qu'on synchroniserait vraiment :
            // l'appliquer avant ferait rapporter comme « écartées » les
            // ressources vanilla que Penumbra rend aussi, gonflant un compteur
            // montré à l'utilisateur.
            var isLocalFile = LooksLikeFileSystemPath(actualPath);
            string? target = null;

            // Un fichier hors du dossier de mods n'est pas un fichier de mod :
            // rapporté sous ses chemins de jeu, jamais sous le chemin local,
            // qui porte le nom de l'utilisateur.
            if (isLocalFile && LocalFileContainment.IsUnder(actualPath, localRoots) is false)
            {
                foreach (var raw in rawGamePaths)
                    skipped.Add(new SkippedFile(raw, OutsideModDirectory));

                continue;
            }

            if (isLocalFile is false)
            {
                // Penumbra rend parfois la cible d'un échange avec des
                // antislashs, notamment pour les animations : sans cette
                // conversion, la politique des chemins de jeu la refuserait.
                if (GamePathPolicy.TryNormalize(actualPath.Replace('\\', '/'), quotas, out var normalizedTarget, out var targetWhy) is false)
                {
                    skipped.Add(new SkippedFile(actualPath, targetWhy!));
                    continue;
                }

                target = normalizedTarget;
            }

            var gamePaths = new List<string>();

            foreach (var raw in rawGamePaths)
            {
                if (GamePathPolicy.TryNormalize(raw, quotas, out var gamePath, out var why) is false)
                {
                    skipped.Add(new SkippedFile(raw, why!));
                    continue;
                }

                // Ressource non moddée : rien à synchroniser, et rien à signaler.
                if (target is not null && string.Equals(gamePath, target, StringComparison.Ordinal))
                    continue;

                if (ExtensionAllowList.IsAllowed(gamePath, out var refus) is false)
                {
                    skipped.Add(new SkippedFile(raw, refus!));
                    continue;
                }

                if (isLocalFile)
                    gamePaths.Add(gamePath);
                else
                    swaps.Add(new FileSwap(gamePath, target!));
            }

            if (isLocalFile && gamePaths.Count > 0)
            {
                gamePaths.Sort(StringComparer.Ordinal);
                files.Add(new LocalFile(actualPath, gamePaths));
            }
        }

        return new ClassifiedResources(files, swaps, skipped);
    }

    /// <summary>
    /// Reconnaît un chemin du système de fichiers plutôt qu'un chemin de jeu.
    /// </summary>
    /// <remarks>
    /// Le test est explicite et non délégué à <c>Path</c>, dont le comportement
    /// diffère entre Windows et Linux : le noyau se teste sous Linux et doit
    /// pourtant raisonner sur des chemins Windows.
    ///
    /// Un antislash seul ne suffit pas : Penumbra écrit aussi ainsi des chemins
    /// de jeu. Un fichier du disque, lui, est toujours rendu absolu.
    /// </remarks>
    private static bool LooksLikeFileSystemPath(string path)
    {
        if (path.Length == 0)
            return false;

        if (path[0] is '/' or '\\')
            return true;                                    // racine unix, ou UNC

        return path.Length >= 2 && path[1] == ':';          // lettre de lecteur
    }
}
