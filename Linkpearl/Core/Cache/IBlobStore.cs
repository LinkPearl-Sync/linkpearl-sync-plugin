namespace Linkpearl.Core.Cache;

/// <summary>Cache adressé par contenu.</summary>
public interface IBlobStore
{
    bool TryGetSize(BlobHash hash, out long size);

    Task<Stream> OpenReadAsync(BlobHash hash, CancellationToken ct);

    Task<IBlobWriter> BeginWriteAsync(BlobHash expected, long expectedSize, CancellationToken ct);

    /// <summary>Un blob reçu par tronçons, dans le désordre.</summary>
    Task<IBlobAssembly> BeginAssemblyAsync(BlobHash expected, long expectedSize, CancellationToken ct);

    /// <summary>Chemin du blob, tel qu'on le donne à Penumbra.</summary>
    string PathFor(BlobHash hash);

    long TotalBytes { get; }

    int Count { get; }

    bool IsReadOnly { get; }

    /// <summary>Rafraîchit la récence d'un blob qu'on vient de servir.</summary>
    void Touch(BlobHash hash);

    Task EvictToAsync(long targetBytes, IReadOnlySet<BlobHash> pinned, CancellationToken ct);

    /// <summary>Vrai quand le cache dépasse son quota.</summary>
    /// <remarks>
    /// Par défaut faux : les doublures de test n'ont pas de quota, et n'ont pas
    /// à en inventer un.
    /// </remarks>
    bool NeedsEviction => false;

    /// <summary>La taille à laquelle une éviction doit redescendre.</summary>
    long EvictionTarget => TotalBytes;

    /// <summary>Le quota du cache, sans limite pour les doublures de test.</summary>
    long QuotaBytes => long.MaxValue;
}

/// <summary>
/// Écriture d'un blob en cours.
/// </summary>
/// <remarks>
/// L'écriture se fait dans un fichier temporaire et n'est publiée qu'une fois
/// le hash vérifié : un blob visible est toujours un blob complet et conforme.
/// </remarks>
public interface IBlobWriter : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    ValueTask<BlobCommitResult> CommitAsync(CancellationToken ct);

    void Abort();
}

/// <summary>
/// Écriture d'un blob par morceaux placés, dans n'importe quel ordre.
/// </summary>
/// <remarks>
/// Mêmes garanties que <see cref="IBlobWriter"/> : rien n'est publié avant que
/// la taille et l'empreinte du fichier complet aient été vérifiées. Savoir si
/// chaque zone a bien été écrite une fois, et une seule, reste l'affaire de
/// l'appelant, qui connaît le découpage.
/// </remarks>
public interface IBlobAssembly : IAsyncDisposable
{
    ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct);

    ValueTask<BlobCommitResult> CommitAsync(CancellationToken ct);

    void Abort();
}
