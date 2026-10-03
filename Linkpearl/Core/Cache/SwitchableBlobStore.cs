namespace Linkpearl.Core.Cache;

/// <summary>
/// Un cache dont le dossier peut arriver tard, ou partir.
/// </summary>
/// <remarks>
/// L'apparence locale et l'applicateur sont construits au chargement, mais le
/// vrai cache n'existe qu'après la présentation, et disparaît si son dossier
/// est supprimé. Plutôt que de reconstruire ces deux-là, on leur donne ce
/// commutateur : sans cache attaché, tout est refusé comme par un disque
/// plein, et rien n'arrive sur le disque.
/// </remarks>
public sealed class SwitchableBlobStore : IBlobStore
{
    private const string Unavailable = "cache indisponible";

    private FileSystemBlobStore? _inner;

    public FileSystemBlobStore? Current => Volatile.Read(ref _inner);

    public bool IsAvailable => Current is not null;

    /// <returns>Le magasin qu'il remplace, s'il y en avait un : à désabonner.</returns>
    public FileSystemBlobStore? Attach(FileSystemBlobStore store) => Interlocked.Exchange(ref _inner, store);

    /// <summary>Retire le cache, et le rend à qui doit s'en désabonner.</summary>
    public FileSystemBlobStore? Detach() => Interlocked.Exchange(ref _inner, null);

    public bool TryGetSize(BlobHash hash, out long size)
    {
        if (Current is { } store)
            return store.TryGetSize(hash, out size);

        size = 0;
        return false;
    }

    public Task<Stream> OpenReadAsync(BlobHash hash, CancellationToken ct)
        => Current is { } store
            ? store.OpenReadAsync(hash, ct)
            : throw new FileNotFoundException($"{Unavailable} : {hash}");

    public Task<IBlobWriter> BeginWriteAsync(BlobHash expected, long expectedSize, CancellationToken ct)
        => Current is { } store
            ? store.BeginWriteAsync(expected, expectedSize, ct)
            : Task.FromResult<IBlobWriter>(new RefusedBlobWriter(Unavailable));

    public Task<IBlobAssembly> BeginAssemblyAsync(BlobHash expected, long expectedSize, CancellationToken ct)
        => Current is { } store
            ? store.BeginAssemblyAsync(expected, expectedSize, ct)
            : Task.FromResult<IBlobAssembly>(new RefusedBlobAssembly(Unavailable));

    /// <remarks>
    /// Seul le moteur demande un chemin, pour poser une apparence, et il n'existe
    /// que cache attaché : l'appel sans cache est une faute de programmation.
    /// </remarks>
    public string PathFor(BlobHash hash)
        => (Current ?? throw new InvalidOperationException(Unavailable)).PathFor(hash);

    public long TotalBytes => Current?.TotalBytes ?? 0;

    public int Count => Current?.Count ?? 0;

    public bool IsReadOnly => Current?.IsReadOnly ?? true;

    public void Touch(BlobHash hash) => Current?.Touch(hash);

    public Task EvictToAsync(long targetBytes, IReadOnlySet<BlobHash> pinned, CancellationToken ct)
        => Current?.EvictToAsync(targetBytes, pinned, ct) ?? Task.CompletedTask;

    public bool NeedsEviction => Current?.NeedsEviction ?? false;

    public long EvictionTarget => Current?.EvictionTarget ?? 0;

    public long QuotaBytes => Current?.QuotaBytes ?? 0;
}
