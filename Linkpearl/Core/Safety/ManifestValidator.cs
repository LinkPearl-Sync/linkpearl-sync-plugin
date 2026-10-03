using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;

namespace Linkpearl.Core.Safety;

/// <summary>
/// Dernier contrôle avant qu'un manifeste reçu d'un pair n'atteigne Penumbra.
/// </summary>
/// <remarks>
/// Un manifeste qui viole une seule règle est rejeté en entier. Un rejet
/// partiel donnerait un personnage incohérent, et il masquerait une tentative
/// en la faisant passer pour une entrée manquante.
/// </remarks>
public static class ManifestValidator
{
    public static bool TryAccept(CharacterManifest manifest, Quotas quotas, out string? rejection)
    {
        // 1 : manifeste d'avant les intégrations, toujours lisible, sans extras.
        if (manifest.Version is not (1 or CharacterManifest.CurrentVersion))
        {
            rejection = $"version de manifeste inconnue ({manifest.Version}, attendu 1 ou {CharacterManifest.CurrentVersion})";
            return false;
        }

        if (manifest.Version == 1 && manifest.ExtrasOrNone.IsEmpty is false)
        {
            rejection = "manifeste de version 1 portant des extras";
            return false;
        }

        if (manifest.Replacements.Count > quotas.MaxReplacements)
        {
            rejection = $"plafond de remplacements dépassé ({manifest.Replacements.Count}, plafond {quotas.MaxReplacements})";
            return false;
        }

        if (manifest.MetaManipulations.Length > quotas.MaxMetaManipulationChars)
        {
            rejection = $"plafond des manipulations méta dépassé (plafond {quotas.MaxMetaManipulationChars})";
            return false;
        }

        if (manifest.GlamourerState is { } glamourerLength && glamourerLength.Length > quotas.MaxGlamourerStateChars)
        {
            rejection = $"plafond de l'état Glamourer dépassé (plafond {quotas.MaxGlamourerStateChars})";
            return false;
        }

        // Les contrôles qui ne coûtent rien d'abord : les détentes ci-dessous
        // ne se paient que pour un manifeste déjà bien formé ailleurs.
        var totalPaths = 0;
        var totalBytes = 0L;
        var sizes = new Dictionary<BlobHash, long>(manifest.Replacements.Count);

        foreach (var replacement in manifest.Replacements)
        {
            if (replacement.Size < 0 || replacement.Size > quotas.MaxBlobBytes)
            {
                rejection = $"taille de blob hors bornes ({replacement.Size}, plafond {quotas.MaxBlobBytes})";
                return false;
            }

            // Une empreinte, une taille : c'est sur elle que le receveur
            // accepte ou refuse l'annonce du blob, et que la somme se calcule.
            if (sizes.TryGetValue(replacement.Hash, out var known))
            {
                if (known != replacement.Size)
                {
                    rejection = $"deux tailles pour une même empreinte ({replacement.Hash})";
                    return false;
                }
            }
            else
            {
                sizes[replacement.Hash] = replacement.Size;
                totalBytes += replacement.Size;

                if (totalBytes > quotas.MaxManifestTotalBytes)
                {
                    rejection = $"apparence trop lourde (plus de {quotas.MaxManifestTotalBytes / 1024 / 1024} Mo)";
                    return false;
                }
            }

            if (replacement.GamePaths.Count == 0)
            {
                rejection = "entrée sans chemin de jeu";
                return false;
            }

            totalPaths += replacement.GamePaths.Count;
            if (totalPaths > quotas.MaxGamePaths)
            {
                rejection = $"plafond de chemins de jeu dépassé (plafond {quotas.MaxGamePaths})";
                return false;
            }

            foreach (var path in replacement.GamePaths)
            {
                if (GamePathPolicy.TryNormalize(path, quotas, out var normalized, out var why) is false)
                {
                    rejection = $"chemin de jeu refusé : {why}";
                    return false;
                }

                // Le chemin doit arriver déjà normalisé. S'il change à la
                // normalisation, l'émetteur n'a pas produit la forme canonique,
                // et deux formes pour un même chemin est précisément ce qui
                // permet de faire diverger validation et usage.
                if (string.Equals(normalized, path, StringComparison.Ordinal) is false)
                {
                    rejection = "chemin de jeu non canonique";
                    return false;
                }

                if (ExtensionAllowList.IsAllowed(normalized, out var refus) is false)
                {
                    rejection = $"chemin de jeu refusé : {refus}";
                    return false;
                }
            }
        }

        if (manifest.SwapsOrNone.Count > 0)
        {
            var served = manifest.Replacements.SelectMany(r => r.GamePaths).ToHashSet(StringComparer.Ordinal);

            foreach (var swap in manifest.SwapsOrNone)
            {
                totalPaths++;
                if (totalPaths > quotas.MaxGamePaths)
                {
                    rejection = $"plafond de chemins de jeu dépassé (plafond {quotas.MaxGamePaths})";
                    return false;
                }

                if (FileSwapPolicy.TryNormalize(swap, quotas, out var normalized, out var why) is false)
                {
                    rejection = $"échange refusé : {why}";
                    return false;
                }

                // Même exigence de forme canonique que pour les fichiers.
                if (normalized != swap)
                {
                    rejection = "échange non canonique";
                    return false;
                }

                if (served.Add(swap.GamePath) is false)
                {
                    rejection = $"deux contenus pour un même chemin de jeu : {swap.GamePath}";
                    return false;
                }
            }
        }

        if (ExtrasValidator.TryAccept(manifest.ExtrasOrNone, quotas, out var extrasRejection) is false)
        {
            rejection = extrasRejection;
            return false;
        }

        // En dernier : ce sont les seuls contrôles qui coûtent une détente.
        if (GzipBase64.IsBounded(manifest.MetaManipulations, quotas.MaxMetaManipulationsDecompressedBytes, out var metaWhy) is false)
        {
            rejection = $"manipulations méta : {metaWhy}";
            return false;
        }

        if (manifest.GlamourerState is { } glamourer
            && GzipBase64.IsBounded(glamourer, quotas.MaxGlamourerStateDecompressedBytes, out var glamourerWhy) is false)
        {
            rejection = $"état Glamourer : {glamourerWhy}";
            return false;
        }

        rejection = null;
        return true;
    }
}
