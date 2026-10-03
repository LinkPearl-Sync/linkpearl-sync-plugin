using System.Buffers.Binary;
using System.IO.Compression;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transfer;

namespace Linkpearl.Harness;

/// <summary>
/// Joue le rôle d'un pair malveillant et vérifie qu'on lui dit non.
/// </summary>
/// <remarks>
/// Les tests unitaires couvrent chaque contrôle isolément. Ce mode vérifie
/// autre chose : qu'aucune de ces tentatives ne fait lever d'exception, et
/// surtout qu'aucune n'écrit quoi que ce soit, ni dans le cache ni ailleurs.
/// Un refus qui laisse un fichier derrière lui n'est pas un refus.
/// </remarks>
public static class HostileRun
{
    private sealed record Attempt(string Name, Func<Task<string?>> Run);

    public static async Task<bool> ExecuteAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "linkpearl-hostile-" + Guid.NewGuid().ToString("N"));
        var store = new FileSystemBlobStore(root, new CacheSettings(), new RealClock(), _ => long.MaxValue);

        var attempts = new[]
        {
            new Attempt("chemin de jeu remontant l'arborescence", () => Manifest("../../../etc/passwd", ".tex")),
            new Attempt("chemin absolu Windows", () => Manifest("c:/windows/system32/config/sam", "")),
            new Attempt("chemin avec antislash", () => Manifest(@"chara\equipment\a.tex", "")),
            new Attempt("octet nul dans le chemin", () => Manifest("chara/equipment/a\0.tex", "")),
            new Attempt("extension interdite, paquet de shader", () => Manifest("shader/sm5/shpk/skin.shpk", "")),
            new Attempt("extension hors périmètre, animation", () => Manifest("chara/action/emote.pap", "")),
            new Attempt("racine inconnue du jeu", () => Manifest("tmp/charge.tex", "")),
            new Attempt("plafond de remplacements dépassé", TooManyReplacements),
            new Attempt("base64 invalide dans les manipulations méta", InvalidMeta),
            new Attempt("bombe de décompression", DecompressionBomb),
            new Attempt("blob non demandé", () => Blob(store, Hostility.Unrequested)),
            new Attempt("bloc sans annonce préalable", () => Blob(store, Hostility.ChunkWithoutStart)),
            new Attempt("contenu ne correspondant pas à l'empreinte annoncée", () => Blob(store, Hostility.WrongContent)),
            new Attempt("envoi au-delà de la taille annoncée", () => Blob(store, Hostility.Oversized)),
            new Attempt("taille annoncée au-delà du plafond", () => Blob(store, Hostility.OversizedAnnouncement)),
        };

        Console.WriteLine("Tentatives d'un pair malveillant. Chacune doit être refusée proprement.");
        Console.WriteLine();

        var allRefused = true;

        foreach (var attempt in attempts)
        {
            string? outcome;

            try
            {
                outcome = await attempt.Run().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Une exception est un échec : elle remonterait dans la boucle de
                // synchronisation et couperait la session d'un pair légitime.
                Console.WriteLine($"  ÉCHEC   {attempt.Name}");
                Console.WriteLine($"          exception {e.GetType().Name} : {e.Message}");
                allRefused = false;
                continue;
            }

            if (outcome is null)
            {
                Console.WriteLine($"  ACCEPTÉ {attempt.Name}   <-- la tentative est passée");
                allRefused = false;
                continue;
            }

            Console.WriteLine($"  refusé  {attempt.Name}");
            Console.WriteLine($"          « {outcome} »");
        }

        Console.WriteLine();

        var blobs = Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories).ToList();
        var parts = Directory.EnumerateFiles(Path.Combine(root, "incoming"), "*").ToList();

        Console.WriteLine($"Cache après les tentatives : {blobs.Count} blobs, {parts.Count} écritures en suspens.");

        if (blobs.Count > 0 || parts.Count > 0)
        {
            Console.WriteLine("ÉCHEC : un refus a laissé un fichier derrière lui.");
            allRefused = false;
        }

        Directory.Delete(root, recursive: true);

        Console.WriteLine();
        Console.WriteLine(allRefused
            ? "TOUT A ÉTÉ REFUSÉ, sans exception et sans rien écrire."
            : "AU MOINS UNE TENTATIVE N'A PAS ÉTÉ CORRECTEMENT REFUSÉE.");

        return allRefused;
    }

    private static Task<string?> Manifest(string gamePath, string _)
    {
        var manifest = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [new FileReplacement([gamePath], BlobHash.OfContent("x"u8), 1)],
            "bWV0YQ==", null);

        return Task.FromResult(
            ManifestValidator.TryAccept(manifest, Quotas.Default, out var why) ? null : why);
    }

    private static Task<string?> TooManyReplacements()
    {
        var manifest = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            Enumerable.Range(0, Quotas.Default.MaxReplacements + 1)
                .Select(i => new FileReplacement(
                    [$"chara/equipment/e{i:D4}/model/c0101e{i:D4}_top.mdl"],
                    BlobHash.OfContent(System.Text.Encoding.UTF8.GetBytes($"{i}")), 1))
                .ToArray(),
            "bWV0YQ==", null);

        return Task.FromResult(
            ManifestValidator.TryAccept(manifest, Quotas.Default, out var why) ? null : why);
    }

    private static Task<string?> InvalidMeta()
    {
        var manifest = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [new FileReplacement(["chara/equipment/e0101/model/c0101e0101_top.mdl"], BlobHash.OfContent("x"u8), 1)],
            "pas du base64 !!!", null);

        return Task.FromResult(
            ManifestValidator.TryAccept(manifest, Quotas.Default, out var why) ? null : why);
    }

    private static Task<string?> DecompressionBomb()
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(new byte[64 * 1024 * 1024]);

        return Task.FromResult(
            ManifestCodec.TryDecompress(output.ToArray(), Quotas.Default, out _, out var why) ? null : why);
    }

    private static class Hostility
    {
        public const int Unrequested = 0;
        public const int ChunkWithoutStart = 1;
        public const int WrongContent = 2;
        public const int Oversized = 3;
        public const int OversizedAnnouncement = 4;
    }

    private static async Task<string?> Blob(IBlobStore store, int hostility)
    {
        var content = "ce qui est envoyé"u8.ToArray();
        var announced = BlobHash.OfContent("ce qui est annoncé"u8);
        var honest = BlobHash.OfContent(content);

        var requested = hostility == Hostility.Unrequested
            ? new Dictionary<BlobHash, long>()
            : new Dictionary<BlobHash, long> { [announced] = content.Length, [honest] = content.Length };

        await using var receiver = new BlobReceiver(store, Quotas.Default, requested);

        if (hostility == Hostility.ChunkWithoutStart)
        {
            var outcome = await receiver.HandleAsync(1, MessageKind.BlobChunk, new byte[100], default);
            return outcome.Accepted ? null : outcome.Rejection;
        }

        var size = hostility == Hostility.OversizedAnnouncement
            ? Quotas.Default.MaxBlobBytes + 1
            : content.Length;

        var start = new byte[BlobHash.SizeInBytes + sizeof(long)];
        (hostility == Hostility.WrongContent ? announced : honest).TryWriteTo(start);
        BinaryPrimitives.WriteInt64BigEndian(start.AsSpan(BlobHash.SizeInBytes), size);

        var first = await receiver.HandleAsync(1, MessageKind.BlobStart, start, default);
        if (first.Accepted is false)
            return first.Rejection;

        var chunk = new byte[4 + content.Length];
        content.CopyTo(chunk.AsSpan(4));

        var rounds = hostility == Hostility.Oversized ? 10 : 1;
        for (var i = 0; i < rounds; i++)
        {
            var outcome = await receiver.HandleAsync(1, MessageKind.BlobChunk, chunk, default);
            if (outcome.Accepted is false)
                return outcome.Rejection;
        }

        var end = new byte[BlobHash.SizeInBytes];
        (hostility == Hostility.WrongContent ? announced : honest).TryWriteTo(end);

        var closing = await receiver.HandleAsync(1, MessageKind.BlobEnd, end, default);
        return closing.Accepted ? null : closing.Rejection;
    }
}
