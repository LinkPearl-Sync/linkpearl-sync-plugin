using System.Text;
using Linkpearl.Core.Cache;
using Xunit;

namespace Linkpearl.Core.Tests.Cache;

/// <summary>
/// Le cache est alimenté par un tiers. Ces tests portent moins sur le chemin
/// nominal que sur ce qui arrive quand le contenu ment, quand l'écriture est
/// interrompue, ou quand le disque se remplit.
/// </summary>
public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "linkpearl-tests-" + Guid.NewGuid().ToString("N"));

    private long _free = long.MaxValue;

    private FileSystemBlobStore Store(CacheSettings? settings = null)
        => new(_root, settings ?? new CacheSettings(), new FakeClock(), _ => _free);

    private sealed class FakeClock : Linkpearl.Core.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    }

    private static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);

    private static async Task<BlobCommitResult> PutAsync(FileSystemBlobStore store, byte[] content, BlobHash? claim = null)
    {
        var hash = claim ?? BlobHash.OfContent(content);
        await using var writer = await store.BeginWriteAsync(hash, content.Length, default);
        await writer.WriteAsync(content, default);
        return await writer.CommitAsync(default);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Un_blob_ecrit_se_relit_a_l_identique()
    {
        var store = Store();
        var content = Bytes("contenu de texture");

        Assert.True((await PutAsync(store, content)).Accepted);

        await using var stream = await store.OpenReadAsync(BlobHash.OfContent(content), default);
        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);

        Assert.Equal(content, reader.ToArray());
    }

    [Fact]
    public async Task Un_contenu_qui_ne_correspond_pas_au_hash_annonce_est_rejete()
    {
        // Le cas central : un pair annonce une empreinte et envoie autre chose.
        var store = Store();
        var mensonge = BlobHash.OfContent(Bytes("ce qui est annoncé"));

        var result = await PutAsync(store, Bytes("ce qui est envoyé"), mensonge);

        Assert.False(result.Accepted);
        Assert.NotNull(result.Rejection);
        Assert.False(store.TryGetSize(mensonge, out _));
    }

    [Fact]
    public async Task Une_taille_qui_ne_correspond_pas_a_celle_annoncee_est_rejetee()
    {
        var store = Store();
        var content = Bytes("douze octets");

        await using var writer = await store.BeginWriteAsync(BlobHash.OfContent(content), content.Length + 100, default);
        await writer.WriteAsync(content, default);
        var result = await writer.CommitAsync(default);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Une_ecriture_interrompue_ne_laisse_aucun_blob_visible()
    {
        var store = Store();
        var content = Bytes("contenu partiel");
        var hash = BlobHash.OfContent(content);

        var writer = await store.BeginWriteAsync(hash, content.Length, default);
        await writer.WriteAsync(content.AsMemory(0, 5), default);
        writer.Abort();
        await writer.DisposeAsync();

        Assert.False(store.TryGetSize(hash, out _));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "incoming")));
    }

    [Fact]
    public async Task Deux_ecritures_concurrentes_du_meme_contenu_aboutissent_toutes_les_deux()
    {
        // Deux pairs peuvent envoyer la même texture en même temps. Aucun des
        // deux ne doit échouer, et le blob final doit être correct.
        var store = Store();
        var content = Bytes("texture partagée");

        var results = await Task.WhenAll(
            PutAsync(store, content),
            PutAsync(store, content),
            PutAsync(store, content));

        Assert.All(results, r => Assert.True(r.Accepted, r.Rejection));
        Assert.True(store.TryGetSize(BlobHash.OfContent(content), out var size));
        Assert.Equal(content.Length, size);
    }

    [Fact]
    public async Task Le_blob_porte_son_hash_pour_seul_nom_dans_deux_niveaux_de_repertoire()
    {
        var store = Store();
        var content = Bytes("x");
        await PutAsync(store, content);

        var hash = BlobHash.OfContent(content);
        var expected = Path.Combine(_root, "blobs", hash.CacheLevel1, hash.CacheLevel2, hash.ToHex());

        Assert.Equal(expected, store.PathFor(hash));
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task L_eviction_epargne_les_blobs_epingles()
    {
        // Un blob épinglé est référencé par un manifeste actuellement appliqué :
        // l'évincer casserait l'apparence d'un pair à l'écran.
        var store = Store(new CacheSettings { QuotaBytes = 1000, LowWatermark = 0.5 });

        var epingle = Bytes(new string('a', 400));
        var jetable = Bytes(new string('b', 400));
        await PutAsync(store, epingle);
        await PutAsync(store, jetable);

        await store.EvictToAsync(400, new HashSet<BlobHash> { BlobHash.OfContent(epingle) }, default);

        Assert.True(store.TryGetSize(BlobHash.OfContent(epingle), out _));
        Assert.False(store.TryGetSize(BlobHash.OfContent(jetable), out _));
    }

    [Fact]
    public async Task L_eviction_commence_par_le_moins_recemment_utilise()
    {
        var clock = new FakeClock();
        var store = new FileSystemBlobStore(_root, new CacheSettings(), clock, _ => _free);

        var ancien = Bytes(new string('a', 100));
        await PutAsync(store, ancien);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var recent = Bytes(new string('b', 100));
        await PutAsync(store, recent);

        await store.EvictToAsync(100, new HashSet<BlobHash>(), default);

        Assert.False(store.TryGetSize(BlobHash.OfContent(ancien), out _));
        Assert.True(store.TryGetSize(BlobHash.OfContent(recent), out _));
    }

    [Fact]
    public async Task Une_lecture_rafraichit_la_recence()
    {
        var clock = new FakeClock();
        var store = new FileSystemBlobStore(_root, new CacheSettings(), clock, _ => _free);

        var premier = Bytes(new string('a', 100));
        var second = Bytes(new string('b', 100));
        await PutAsync(store, premier);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await PutAsync(store, second);

        // On se sert du premier : il ne doit plus être le candidat à l'éviction.
        clock.UtcNow = clock.UtcNow.AddHours(1);
        store.Touch(BlobHash.OfContent(premier));

        await store.EvictToAsync(100, new HashSet<BlobHash>(), default);

        Assert.True(store.TryGetSize(BlobHash.OfContent(premier), out _));
        Assert.False(store.TryGetSize(BlobHash.OfContent(second), out _));
    }

    [Fact]
    public async Task L_index_se_reconstruit_quand_il_manque()
    {
        var content = Bytes("survivant");

        var premier = Store();
        await PutAsync(premier, content);
        await premier.SaveIndexAsync(default);

        File.Delete(Path.Combine(_root, "cache.index"));

        var second = Store();
        Assert.True(second.TryGetSize(BlobHash.OfContent(content), out var size));
        Assert.Equal(content.Length, size);
    }

    [Fact]
    public async Task L_index_survit_a_un_redemarrage()
    {
        var content = Bytes("persistant");

        var premier = Store();
        await PutAsync(premier, content);
        await premier.SaveIndexAsync(default);

        var second = Store();
        Assert.Equal(content.Length, second.TotalBytes);
        Assert.Equal(1, second.Count);
    }

    [Fact]
    public async Task Un_index_corrompu_ne_fait_pas_echouer_le_demarrage()
    {
        var content = Bytes("resilient");
        var premier = Store();
        await PutAsync(premier, content);
        await premier.SaveIndexAsync(default);

        await File.WriteAllTextAsync(Path.Combine(_root, "cache.index"), "n'importe quoi\nvraiment\n");

        var second = Store();
        Assert.True(second.TryGetSize(BlobHash.OfContent(content), out _));
    }

    [Fact]
    public async Task Sous_le_seuil_d_espace_libre_le_cache_refuse_d_ecrire()
    {
        // Mieux vaut ne plus synchroniser que remplir le disque de l'utilisateur.
        var store = Store(new CacheSettings { MinimumFreeBytes = 1_000_000 });
        _free = 500_000;

        var result = await PutAsync(store, Bytes("trop tard"));

        Assert.False(result.Accepted);
        Assert.Contains("espace", result.Rejection!, StringComparison.OrdinalIgnoreCase);
        Assert.True(store.IsReadOnly);
    }

    [Fact]
    public async Task Les_assemblages_en_vol_reservent_leur_place()
    {
        // L'espace libre ne voit que ce qui est écrit : sans réserve, chaque
        // assemblage constatait à son ouverture qu'il restait de la place, et
        // soixante-quatre de 128 Mo passaient ensemble sur un disque qui n'en
        // tenait qu'un.
        var store = Store(new CacheSettings { MinimumFreeBytes = 1_000 });
        _free = 1_000 + 150;

        var first = Bytes(new string('a', 100));
        var second = Bytes(new string('b', 100));

        var opened = await store.BeginAssemblyAsync(BlobHash.OfContent(first), first.Length, default);
        var refused = await store.BeginAssemblyAsync(BlobHash.OfContent(second), second.Length, default);

        Assert.False((await refused.CommitAsync(default)).Accepted);

        // Abandonné, le premier rend sa réserve, et le second trouve sa place.
        opened.Abort();
        await opened.DisposeAsync();

        await using var retried = await store.BeginAssemblyAsync(BlobHash.OfContent(second), second.Length, default);
        await retried.WriteAtAsync(0, second, default);
        Assert.True((await retried.CommitAsync(default)).Accepted);
    }

    [Fact]
    public async Task Une_ecriture_rend_sa_reserve_en_se_terminant()
    {
        var store = Store(new CacheSettings { MinimumFreeBytes = 1_000 });
        _free = 1_000 + 150;

        for (var i = 0; i < 5; i++)
        {
            // Cinq blobs de 100 octets l'un après l'autre : chacun tient seul,
            // et aucune réserve ne doit survivre à sa publication.
            Assert.True((await PutAsync(store, Bytes($"{i}" + new string('c', 99)))).Accepted);
        }
    }

    [Fact]
    public async Task Un_blob_plus_gros_que_le_quota_entier_est_refuse()
    {
        var store = Store(new CacheSettings { QuotaBytes = 100 });
        var result = await PutAsync(store, Bytes(new string('a', 200)));

        Assert.False(result.Accepted);
        Assert.Contains("quota", result.Rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Le_total_et_le_compte_suivent_les_ecritures_et_les_evictions()
    {
        var store = Store();
        await PutAsync(store, Bytes(new string('a', 30)));
        await PutAsync(store, Bytes(new string('b', 70)));

        Assert.Equal(100, store.TotalBytes);
        Assert.Equal(2, store.Count);

        await store.EvictToAsync(70, new HashSet<BlobHash>(), default);

        Assert.Equal(70, store.TotalBytes);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Lire_un_blob_absent_echoue_proprement()
    {
        var store = Store();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.OpenReadAsync(BlobHash.OfContent(Bytes("jamais vu")), default));
    }

    [Fact]
    public void Le_repertoire_d_arrivee_partage_le_volume_des_blobs()
    {
        // File.Move n'est atomique que sur un même volume : c'est ce qui garantit
        // qu'un blob visible est un blob complet.
        var store = Store();

        Assert.Equal(
            Path.GetPathRoot(Path.Combine(_root, "blobs")),
            Path.GetPathRoot(Path.Combine(_root, "incoming")));
        Assert.True(Directory.Exists(Path.Combine(_root, "incoming")));
    }

    [Fact]
    public async Task Un_cache_dont_le_dossier_a_disparu_refuse_d_ecrire_sans_le_recreer()
    {
        var store = Store();
        var lost = 0;
        store.RootLost += () => lost++;

        Directory.Delete(_root, recursive: true);

        var result = await PutAsync(store, Bytes("après la disparition"));

        Assert.False(result.Accepted);
        Assert.False(Directory.Exists(_root));
        Assert.True(lost > 0);
    }

    [Fact]
    public async Task Un_dossier_disparu_pendant_une_ecriture_n_est_pas_recree_a_la_publication()
    {
        // Windows refuse de supprimer un dossier qui contient un fichier ouvert :
        // le scénario n'y existe pas, et le Directory.Delete ci-dessous lèverait.
        // La publication tourne sur un runner Windows, qui passe donc ce cas.
        if (OperatingSystem.IsWindows())
            return;

        var store = Store();
        var content = Bytes("écrit pendant qu'on vide le disque");

        await using var writer = await store.BeginWriteAsync(BlobHash.OfContent(content), content.Length, default);
        await writer.WriteAsync(content, default);

        // Sous Linux, un dossier se supprime même avec un fichier ouvert dedans :
        // c'est le cas le plus défavorable, celui où la publication arrive après.
        Directory.Delete(_root, recursive: true);

        var result = await writer.CommitAsync(default);

        Assert.False(result.Accepted);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Un_quota_abaisse_a_chaud_demande_une_eviction()
    {
        var store = Store();
        await PutAsync(store, new byte[100]);

        Assert.False(store.NeedsEviction);

        store.SetQuota(50);

        Assert.True(store.NeedsEviction);
        Assert.Equal((long)(50 * new CacheSettings().LowWatermark), store.EvictionTarget);
    }

    [Fact]
    public async Task Un_quota_abaisse_a_chaud_refuse_un_blob_plus_gros_que_lui()
    {
        var store = Store();
        store.SetQuota(10);

        var result = await PutAsync(store, new byte[20]);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Un_dossier_vide_sans_etre_supprime_refuse_d_ecrire_sans_recreer_les_sous_dossiers()
    {
        var store = Store();
        var lost = 0;
        store.RootLost += () => lost++;

        Directory.Delete(Path.Combine(_root, "blobs"), recursive: true);
        Directory.Delete(Path.Combine(_root, "incoming"), recursive: true);

        var result = await PutAsync(store, Bytes("après le vidage"));

        Assert.False(result.Accepted);
        Assert.True(lost > 0);
        Assert.False(Directory.Exists(Path.Combine(_root, "blobs")));
        Assert.False(Directory.Exists(Path.Combine(_root, "incoming")));
    }

    [Fact]
    public async Task Un_espace_libre_illisible_ne_bloque_pas_les_ecritures()
    {
        // Un chemin UNC où DriveInfo échoue ne doit pas faire échouer chaque
        // écriture : l'espace inconnu ne bloque pas, il ne se contente que de
        // ne rien afficher.
        var store = new FileSystemBlobStore(
            _root, new CacheSettings(), new FakeClock(), _ => throw new IOException("chemin illisible"));

        Assert.False(store.IsReadOnly);

        var result = await PutAsync(store, Bytes("ça passe quand même"));
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task L_index_ne_fait_pas_perdre_un_blob_publie_apres_la_derniere_eviction()
    {
        // EvictToAsync écrit l'index même sans rien évincer. Tout ce qui est
        // publié après cette écriture doit quand même être vu au rechargement.
        var premier = Store();
        var a = Bytes("A, présent à l'écriture de l'index");
        await PutAsync(premier, a);

        await premier.EvictToAsync(long.MaxValue, new HashSet<BlobHash>(), default);

        var b = Bytes("B, publié après la dernière écriture de l'index");
        await PutAsync(premier, b);

        var second = Store();

        Assert.True(second.TryGetSize(BlobHash.OfContent(b), out var sizeB));
        Assert.Equal(b.Length, sizeB);
        Assert.Equal(a.Length + b.Length, second.TotalBytes);
    }
}
