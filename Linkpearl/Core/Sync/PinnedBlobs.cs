using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;

namespace Linkpearl.Core.Sync;

/// <summary>Les blobs qu'une éviction ne doit pas toucher.</summary>
/// <remarks>
/// Deux degrés. Ceux des apparences posées à l'écran (Penumbra lit leurs
/// fichiers) et de la nôtre (les pairs nous les demandent) le sont toujours.
/// Ceux des apparences en cours de réception ne le sont que tant qu'ils
/// tiennent sous le quota : épinglés sans condition, un pair qui annonce plus
/// que le quota, ou dix pairs qui arrivent ensemble, faisaient grossir le cache
/// sans que l'éviction puisse rien retirer.
/// </remarks>
public static class PinnedBlobs
{
    public static HashSet<BlobHash> Of(IEnumerable<CharacterManifest?> manifests)
    {
        var pinned = new HashSet<BlobHash>();

        foreach (var manifest in manifests)
        {
            if (manifest is null)
                continue;

            foreach (var replacement in manifest.Replacements)
                pinned.Add(replacement.Hash);
        }

        return pinned;
    }

    /// <summary>
    /// Les blobs toujours épinglés, plus ceux des réceptions qui tiennent dans le budget.
    /// </summary>
    /// <param name="always">Apparences à l'écran et la nôtre : jamais évincées.</param>
    /// <param name="receiving">Apparences en cours de réception, épinglées si la place le permet.</param>
    /// <param name="budget">Ce que l'éviction vise : au-delà, rien de plus ne s'épingle.</param>
    /// <remarks>
    /// Une réception s'épingle en entier ou pas du tout : en garder la moitié
    /// coûterait la place sans rien permettre d'afficher. Les plus légères
    /// d'abord, pour en garder le plus grand nombre. Seuls les blobs présents
    /// comptent : ce qui n'est pas encore arrivé n'occupe rien.
    /// </remarks>
    public static HashSet<BlobHash> Within(
        IEnumerable<CharacterManifest?> always, IEnumerable<CharacterManifest?> receiving, IBlobStore store, long budget)
    {
        var pinned = Of(always);
        var used = 0L;

        foreach (var hash in pinned)
        {
            if (store.TryGetSize(hash, out var size))
                used += size;
        }

        var candidates = receiving
            .OfType<CharacterManifest>()
            .Select(manifest => PresentBeyond(manifest, pinned, store))
            .OrderBy(candidate => candidate.Bytes)
            .ToList();

        foreach (var (hashes, _) in candidates)
        {
            // Recompté : une réception précédente a pu épingler une partie de
            // ces blobs, qui ne coûtent alors plus rien.
            var extra = 0L;

            foreach (var hash in hashes)
            {
                if (pinned.Contains(hash) is false && store.TryGetSize(hash, out var size))
                    extra += size;
            }

            if (used + extra > budget)
                continue;

            pinned.UnionWith(hashes);
            used += extra;
        }

        return pinned;
    }

    private static (HashSet<BlobHash> Hashes, long Bytes) PresentBeyond(
        CharacterManifest manifest, HashSet<BlobHash> pinned, IBlobStore store)
    {
        var hashes = new HashSet<BlobHash>();
        var bytes = 0L;

        foreach (var replacement in manifest.Replacements)
        {
            if (pinned.Contains(replacement.Hash) || store.TryGetSize(replacement.Hash, out var size) is false)
                continue;

            if (hashes.Add(replacement.Hash))
                bytes += size;
        }

        return (hashes, bytes);
    }
}
