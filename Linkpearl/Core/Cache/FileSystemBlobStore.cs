using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;

namespace Linkpearl.Core.Cache;

/// <summary>
/// Cache adressé par contenu, sur le système de fichiers.
/// </summary>
/// <remarks>
/// Deux niveaux de 256 répertoires : à cent mille blobs, environ un fichier et
/// demi par feuille, ce qui garde l'énumération rapide sur NTFS.
///
/// Le répertoire d'arrivée partage le volume des blobs, sans quoi
/// <see cref="File.Move(string,string,bool)"/> cesserait d'être atomique et un
/// blob tronqué pourrait devenir visible.
///
/// La récence est tenue en mémoire et non lue sur le disque : l'horodatage de
/// dernier accès de NTFS est désactivé par défaut depuis Vista, s'y fier
/// donnerait une éviction arbitraire.
/// </remarks>
public sealed class FileSystemBlobStore : IBlobStore
{
    private sealed record Entry(long Size, DateTimeOffset LastUsed);

    private readonly string _root;

    /// <summary>Remplacé en bloc par <see cref="SetQuota"/>, lu sans verrou.</summary>
    private volatile CacheSettings _settings;
    private readonly IClock _clock;
    private readonly Func<string, long> _freeSpace;
    private readonly ConcurrentDictionary<BlobHash, Entry> _entries = new();

    public FileSystemBlobStore(string root, CacheSettings settings, IClock clock, Func<string, long> freeSpace)
    {
        _root = root;
        _settings = settings;
        _clock = clock;
        _freeSpace = freeSpace;

        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(IncomingDirectory);

        if (TryLoadIndex() is false)
            Rebuild();
    }

    private string BlobsDirectory => Path.Combine(_root, "blobs");
    private string IncomingDirectory => Path.Combine(_root, "incoming");
    private string IndexPath => Path.Combine(_root, "cache.index");

    /// <summary>Le dossier du cache, tel qu'il a été ouvert.</summary>
    public string Root => _root;

    /// <summary>
    /// Vrai si la racine et ses deux sous-dossiers sont là.
    /// </summary>
    /// <remarks>
    /// Un dossier vidé sans être supprimé garde une racine qui existe : sans
    /// vérifier aussi <c>blobs/</c> et <c>incoming/</c>, chaque écriture
    /// buterait sur un <see cref="DirectoryNotFoundException"/> sans jamais
    /// déclencher le blocage.
    /// </remarks>
    public static bool IsIntact(string root)
        => Directory.Exists(root)
        && Directory.Exists(Path.Combine(root, "blobs"))
        && Directory.Exists(Path.Combine(root, "incoming"));

    /// <summary>Faux si quelqu'un a supprimé ou vidé le dossier depuis l'ouverture.</summary>
    public bool RootExists => IsIntact(_root);

    /// <summary>
    /// Levé quand une écriture trouve le dossier disparu.
    /// </summary>
    /// <remarks>
    /// Le sondage périodique finirait par le voir ; ceci le dit dès qu'un
    /// transfert bute dessus. Peut être levé plusieurs fois, depuis n'importe
    /// quel fil : l'abonné doit être idempotent.
    /// </remarks>
    public event Action? RootLost;

    /// <summary>Change le quota sans rouvrir le cache.</summary>
    /// <remarks>
    /// L'éviction qui suit est l'affaire du moteur, qui sait ce qui est à
    /// l'écran et ne doit pas partir.
    /// </remarks>
    public void SetQuota(long bytes) => _settings = _settings with { QuotaBytes = bytes };

    private const string RootMissing = "dossier du cache introuvable";

    /// <summary>
    /// Vrai si la racine manque, après l'avoir signalé.
    /// </summary>
    /// <remarks>
    /// Jamais de <c>Directory.CreateDirectory</c> de la racine en dehors du
    /// constructeur : un dossier qui a disparu a peut-être été vidé exprès, ou
    /// était sur un disque débranché.
    /// </remarks>
    private bool SignalIfRootMissing()
    {
        if (RootExists)
            return false;

        RootLost?.Invoke();
        return true;
    }

    public long TotalBytes => _entries.Values.Sum(e => e.Size);

    public int Count => _entries.Count;

    public bool IsReadOnly => FreeBytes() < _settings.MinimumFreeBytes;

    public long QuotaBytes => _settings.QuotaBytes;

    /// <summary>
    /// Octets promis aux écritures en cours et pas encore arrivés sur le disque.
    /// </summary>
    /// <remarks>
    /// L'espace libre ne voit que ce qui est déjà écrit. Sans cette réserve,
    /// soixante-quatre assemblages de 128 Mo s'ouvraient tous sur un disque
    /// qui n'avait de place que pour un seul, chacun ayant constaté à son
    /// ouverture qu'il restait assez de place. Chaque écriture rend la part
    /// qu'elle consomme, la fermeture rend le reste.
    /// </remarks>
    private long _reserved;

    private long FreeBytes()
    {
        try
        {
            return _freeSpace(_root);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Un chemin UNC où DriveInfo échoue, par exemple : l'espace
            // est inconnu, ce qui ne doit pas bloquer chaque écriture.
            return long.MaxValue;
        }
    }

    /// <summary>Réserve la place d'un blob à venir, ou dit pourquoi elle manque.</summary>
    private string? TryReserve(long size)
    {
        var free = FreeBytes();
        var promised = Interlocked.Add(ref _reserved, size);

        // Un espace inconnu (long.MaxValue) ne se compare pas : la soustraction
        // ne déborderait pas, mais elle ne voudrait rien dire.
        if (free == long.MaxValue || free - promised >= _settings.MinimumFreeBytes)
            return null;

        Interlocked.Add(ref _reserved, -size);
        return $"espace libre insuffisant sur le volume du cache (moins de {_settings.MinimumFreeBytes} octets "
             + "une fois les transferts en cours écrits)";
    }

    /// <summary>Rend une part de réservation, écrite ou abandonnée.</summary>
    private void Release(long size)
    {
        if (size > 0)
            Interlocked.Add(ref _reserved, -size);
    }

    public string PathFor(BlobHash hash)
        => Path.Combine(BlobsDirectory, hash.CacheLevel1, hash.CacheLevel2, hash.ToHex());

    public bool TryGetSize(BlobHash hash, out long size)
    {
        if (_entries.TryGetValue(hash, out var entry))
        {
            size = entry.Size;
            return true;
        }

        size = 0;
        return false;
    }

    public void Touch(BlobHash hash)
    {
        if (_entries.TryGetValue(hash, out var entry))
            _entries[hash] = entry with { LastUsed = _clock.UtcNow };
    }

    public Task<Stream> OpenReadAsync(BlobHash hash, CancellationToken ct)
    {
        var path = PathFor(hash);

        if (File.Exists(path) is false)
            throw new FileNotFoundException($"blob absent du cache : {hash}", path);

        Touch(hash);

        // FileShare.Read : un blob ne change jamais après sa publication, donc
        // plusieurs lecteurs simultanés ne posent aucun problème.
        return Task.FromResult<Stream>(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true));
    }

    public Task<IBlobWriter> BeginWriteAsync(BlobHash expected, long expectedSize, CancellationToken ct)
    {
        if (SignalIfRootMissing())
            return Task.FromResult<IBlobWriter>(new RefusedBlobWriter(RootMissing));

        if (expectedSize < 0 || expectedSize > _settings.QuotaBytes)
            return Task.FromResult<IBlobWriter>(new RefusedBlobWriter(
                $"blob plus gros que le quota entier ({expectedSize} octets pour un quota de {_settings.QuotaBytes})"));

        if (TryReserve(expectedSize) is { } full)
            return Task.FromResult<IBlobWriter>(new RefusedBlobWriter(full));

        var part = Path.Combine(IncomingDirectory, Guid.NewGuid().ToString("N") + ".part");

        try
        {
            return Task.FromResult<IBlobWriter>(new BlobWriter(this, expected, expectedSize, part));
        }
        catch (DirectoryNotFoundException)
        {
            Release(expectedSize);

            // Le dossier a pu être vidé (sans être supprimé) entre la
            // vérification ci-dessus et l'ouverture du fichier temporaire :
            // même traitement qu'une racine perdue, sans rien recréer.
            RootLost?.Invoke();
            return Task.FromResult<IBlobWriter>(new RefusedBlobWriter(RootMissing));
        }
    }

    public Task<IBlobAssembly> BeginAssemblyAsync(BlobHash expected, long expectedSize, CancellationToken ct)
    {
        if (SignalIfRootMissing())
            return Task.FromResult<IBlobAssembly>(new RefusedBlobAssembly(RootMissing));

        if (expectedSize < 0 || expectedSize > _settings.QuotaBytes)
            return Task.FromResult<IBlobAssembly>(new RefusedBlobAssembly(
                $"blob plus gros que le quota entier ({expectedSize} octets pour un quota de {_settings.QuotaBytes})"));

        if (TryReserve(expectedSize) is { } full)
            return Task.FromResult<IBlobAssembly>(new RefusedBlobAssembly(full));

        var part = Path.Combine(IncomingDirectory, Guid.NewGuid().ToString("N") + ".part");

        try
        {
            return Task.FromResult<IBlobAssembly>(new BlobAssembly(this, expected, expectedSize, part));
        }
        catch (DirectoryNotFoundException)
        {
            Release(expectedSize);

            // Même situation que dans BeginWriteAsync : le dossier a pu être
            // vidé entre-temps.
            RootLost?.Invoke();
            return Task.FromResult<IBlobAssembly>(new RefusedBlobAssembly(RootMissing));
        }
    }

    public async Task EvictToAsync(long targetBytes, IReadOnlySet<BlobHash> pinned, CancellationToken ct)
    {
        var candidates = _entries
            .Where(pair => pinned.Contains(pair.Key) is false)
            .OrderBy(pair => pair.Value.LastUsed)
            .ToList();

        var total = TotalBytes;

        foreach (var (hash, entry) in candidates)
        {
            if (total <= targetBytes || ct.IsCancellationRequested)
                break;

            if (_entries.TryRemove(hash, out _) is false)
                continue;

            try
            {
                File.Delete(PathFor(hash));
            }
            catch (IOException)
            {
                // Le blob est peut-être en cours de lecture : l'index l'a déjà
                // oublié, le fichier partira à la prochaine reconstruction.
            }

            total -= entry.Size;
        }

        await SaveIndexAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Seuil au-delà duquel une éviction doit être déclenchée.</summary>
    public bool NeedsEviction => TotalBytes > _settings.QuotaBytes;

    public long EvictionTarget => (long)(_settings.QuotaBytes * _settings.LowWatermark);

    /// <summary>
    /// Écrit l'index en entier.
    /// </summary>
    /// <remarks>
    /// Réécriture complète plutôt que journal : quarante octets par blob, soit
    /// deux mégaoctets pour cinquante mille blobs, ce qui ne coûte rien et évite
    /// d'avoir à écrire un journal correct.
    /// </remarks>
    public async Task SaveIndexAsync(CancellationToken ct)
    {
        var lines = _entries.Select(pair =>
            $"{pair.Key.ToHex()} {pair.Value.Size} {pair.Value.LastUsed.ToUnixTimeSeconds()}");

        var temporary = IndexPath + ".part";
        await File.WriteAllLinesAsync(temporary, lines, ct).ConfigureAwait(false);
        File.Move(temporary, IndexPath, overwrite: true);
    }

    private bool TryLoadIndex()
    {
        if (File.Exists(IndexPath) is false)
            return false;

        var loaded = 0;

        foreach (var line in File.ReadLines(IndexPath))
        {
            var parts = line.Split(' ');

            if (parts.Length != 3
                || BlobHash.TryParseHex(parts[0], out var hash) is false
                || long.TryParse(parts[1], CultureInfo.InvariantCulture, out var size) is false
                || long.TryParse(parts[2], CultureInfo.InvariantCulture, out var lastUsed) is false)
                continue;   // ligne illisible : on l'ignore, la reconstruction rattrapera

            if (File.Exists(PathFor(hash)) is false)
                continue;

            _entries[hash] = new Entry(size, DateTimeOffset.FromUnixTimeSeconds(lastUsed));
            loaded++;
        }

        // Un index vide alors que des blobs existent signale une corruption :
        // mieux vaut reconstruire que démarrer en croyant le cache vide.
        if (loaded == 0 && Directory.EnumerateFiles(BlobsDirectory, "*", SearchOption.AllDirectories).Any())
            return false;

        // L'index n'est écrit qu'à l'éviction : tout blob publié depuis la
        // dernière écriture y manque. On le retrouve en parcourant blobs/,
        // avec sa date de publication (l'écriture du fichier) comme dernier
        // accès, faute de mieux.
        foreach (var path in Directory.EnumerateFiles(BlobsDirectory, "*", SearchOption.AllDirectories))
        {
            if (BlobHash.TryParseHex(Path.GetFileName(path), out var hash) is false || _entries.ContainsKey(hash))
                continue;

            _entries[hash] = new Entry(new FileInfo(path).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }

        return true;
    }

    /// <summary>
    /// Reconstruit l'index depuis le disque.
    /// </summary>
    /// <remarks>
    /// Seuls le nom et la taille sont relus. Revérifier le hash de vingt
    /// gigaoctets à chaque démarrage serait inacceptable ; la vérification
    /// complète est faite à l'écriture, et le nom du fichier est le hash.
    /// </remarks>
    private void Rebuild()
    {
        _entries.Clear();

        foreach (var path in Directory.EnumerateFiles(BlobsDirectory, "*", SearchOption.AllDirectories))
        {
            if (BlobHash.TryParseHex(Path.GetFileName(path), out var hash) is false)
                continue;

            _entries[hash] = new Entry(new FileInfo(path).Length, _clock.UtcNow);
        }

        // Les écritures interrompues d'une session précédente ne servent plus à
        // rien : leur nom était aléatoire, on ne saurait pas les reprendre.
        foreach (var part in Directory.EnumerateFiles(IncomingDirectory, "*.part"))
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
                // sans conséquence
            }
        }
    }

    private void Publish(BlobHash hash, long size) => _entries[hash] = new Entry(size, _clock.UtcNow);

    /// <summary>
    /// Un blob écrit par morceaux placés.
    /// </summary>
    /// <remarks>
    /// L'empreinte se calcule au fil de l'écriture tant que les morceaux
    /// arrivent dans l'ordre, et seule la partie arrivée en désordre est relue
    /// à la validation. Un petit blob, qui tient en un tronçon, n'est donc
    /// jamais relu ; un gros ne l'est qu'à partir du premier trou.
    ///
    /// Pas de préallocation : un pair pourrait sinon faire réserver d'un coup
    /// la taille maximale d'un blob, autant de fois qu'il ouvre d'assemblages.
    /// </remarks>
    private sealed class BlobAssembly(FileSystemBlobStore store, BlobHash expected, long expectedSize, string partPath)
        : IBlobAssembly
    {
        private const int ReadBackBlock = 1024 * 1024;

        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle = File.OpenHandle(
            partPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);

        private long _hashedUpTo;
        private bool _aborted;
        private bool _committed;
        private bool _closed;

        /// <summary>La part de la réservation déjà rendue, écrite ou abandonnée.</summary>
        private long _released;

        public async ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await RandomAccess.WriteAsync(_handle, data, offset, ct).ConfigureAwait(false);

            // Ce qui est écrit se voit désormais dans l'espace libre : le
            // garder aussi en réserve le compterait deux fois.
            var consumed = Math.Min(expectedSize - _released, data.Length);
            _released += consumed;
            store.Release(consumed);

            if (offset == _hashedUpTo)
            {
                _hash.AppendData(data.Span);
                _hashedUpTo += data.Length;
            }
        }

        public async ValueTask<BlobCommitResult> CommitAsync(CancellationToken ct)
        {
            var length = RandomAccess.GetLength(_handle);

            if (length != expectedSize)
            {
                Close();
                Discard();
                return BlobCommitResult.Refused($"taille reçue {length}, annoncée {expectedSize}");
            }

            await HashRemainderAsync(ct).ConfigureAwait(false);
            Close();

            var actual = BlobHash.FromBytes(_hash.GetHashAndReset());

            if (actual != expected)
            {
                Discard();
                return BlobCommitResult.Refused($"empreinte reçue {actual}, annoncée {expected}");
            }

            // Le dossier a pu disparaître pendant le transfert : le recréer
            // ici, c'est remplir un disque que l'utilisateur vient de vider.
            if (store.SignalIfRootMissing())
            {
                Discard();
                return BlobCommitResult.Refused(RootMissing);
            }

            var destination = store.PathFor(expected);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            try
            {
                File.Move(partPath, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                Discard();
            }

            store.Publish(expected, expectedSize);
            _committed = true;
            return BlobCommitResult.Ok;
        }

        /// <summary>Relit ce qui n'a pas été haché au fil de l'eau, par blocs empruntés.</summary>
        private async Task HashRemainderAsync(CancellationToken ct)
        {
            if (_hashedUpTo >= expectedSize)
                return;

            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ReadBackBlock);

            try
            {
                while (_hashedUpTo < expectedSize)
                {
                    var wanted = (int)Math.Min(ReadBackBlock, expectedSize - _hashedUpTo);
                    var read = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(0, wanted), _hashedUpTo, ct)
                                                 .ConfigureAwait(false);

                    if (read == 0)
                        break;

                    _hash.AppendData(buffer, 0, read);
                    _hashedUpTo += read;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Abort() => _aborted = true;

        public ValueTask DisposeAsync()
        {
            _hash.Dispose();
            Close();
            store.Release(expectedSize - _released);
            _released = expectedSize;

            if (_committed is false && (_aborted || File.Exists(partPath)))
                Discard();

            return ValueTask.CompletedTask;
        }

        private void Close()
        {
            if (_closed)
                return;

            _closed = true;
            _handle.Dispose();
        }

        private void Discard()
        {
            try
            {
                if (File.Exists(partPath))
                    File.Delete(partPath);
            }
            catch (IOException)
            {
                // sans conséquence : la reconstruction nettoiera
            }
        }
    }

    private sealed class BlobWriter(FileSystemBlobStore store, BlobHash expected, long expectedSize, string partPath)
        : IBlobWriter
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly FileStream _stream = new(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        private long _written;
        private bool _aborted;
        private bool _committed;

        /// <summary>La part de la réservation déjà rendue, voir <see cref="BlobAssembly"/>.</summary>
        private long _released;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _hash.AppendData(data.Span);
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            _written += data.Length;

            var consumed = Math.Min(expectedSize - _released, data.Length);
            _released += consumed;
            store.Release(consumed);
        }

        public async ValueTask<BlobCommitResult> CommitAsync(CancellationToken ct)
        {
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);

            if (_written != expectedSize)
            {
                Discard();
                return BlobCommitResult.Refused($"taille reçue {_written}, annoncée {expectedSize}");
            }

            var actual = BlobHash.FromBytes(_hash.GetHashAndReset());

            if (actual != expected)
            {
                Discard();
                return BlobCommitResult.Refused($"empreinte reçue {actual}, annoncée {expected}");
            }

            // Le dossier a pu disparaître pendant le transfert : le recréer
            // ici, c'est remplir un disque que l'utilisateur vient de vider.
            if (store.SignalIfRootMissing())
            {
                Discard();
                return BlobCommitResult.Refused(RootMissing);
            }

            var destination = store.PathFor(expected);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            try
            {
                // overwrite: false, et l'échec est un succès : un autre pair vient
                // de publier le même contenu, qui est par définition identique.
                File.Move(partPath, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                Discard();
            }

            store.Publish(expected, _written);
            _committed = true;
            return BlobCommitResult.Ok;
        }

        public void Abort() => _aborted = true;

        public async ValueTask DisposeAsync()
        {
            _hash.Dispose();
            store.Release(expectedSize - _released);
            _released = expectedSize;

            if (_committed)
                return;

            await _stream.DisposeAsync().ConfigureAwait(false);

            if (_aborted || File.Exists(partPath))
                Discard();
        }

        private void Discard()
        {
            try
            {
                if (File.Exists(partPath))
                    File.Delete(partPath);
            }
            catch (IOException)
            {
                // sans conséquence : la reconstruction nettoiera
            }
        }
    }
}
