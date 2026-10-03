using System.Text;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>Un cache en mémoire : ces tests ne portent que sur la décision.</summary>
internal sealed class FakeBlobStore : IBlobStore
{
    private readonly Dictionary<BlobHash, long> _sizes = [];
    private readonly Dictionary<BlobHash, byte[]> _contents = [];

    public List<BlobHash> Touched { get; } = [];

    public long TotalBytes => _sizes.Values.Sum();

    public int Count => _sizes.Count;

    public bool IsReadOnly => false;

    public void Add(BlobHash hash, long size) => _sizes[hash] = size;

    public BlobHash AddContent(byte[] content)
    {
        var hash = BlobHash.OfContent(content);
        _sizes[hash] = content.Length;
        _contents[hash] = content;
        return hash;
    }

    public bool TryGetSize(BlobHash hash, out long size) => _sizes.TryGetValue(hash, out size);

    public string PathFor(BlobHash hash)
        => Path.Combine("/cache/blobs", hash.CacheLevel1, hash.CacheLevel2, hash.ToHex());

    public void Touch(BlobHash hash) => Touched.Add(hash);

    public Task<Stream> OpenReadAsync(BlobHash hash, CancellationToken ct)
        => _contents.TryGetValue(hash, out var content)
            ? Task.FromResult<Stream>(new MemoryStream(content, writable: false))
            : throw new NotSupportedException("contenu non fourni à ce faux cache");

    public Task<IBlobWriter> BeginWriteAsync(BlobHash expected, long expectedSize, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<IBlobAssembly> BeginAssemblyAsync(BlobHash expected, long expectedSize, CancellationToken ct)
        => throw new NotSupportedException();

    public Task EvictToAsync(long targetBytes, IReadOnlySet<BlobHash> pinned, CancellationToken ct)
        => throw new NotSupportedException();
}

/// <summary>
/// Le dernier contrôle avant que la donnée d'un pair n'atteigne Penumbra.
/// </summary>
public class AppearancePlanTests
{
    private const string Top = "chara/equipment/e0001/model/c0101e0001_top.mdl";
    private const string Gloves = "chara/equipment/e0002/model/c0101e0002_glv.mdl";

    private static BlobHash Hash(string content) => BlobHash.OfContent(Encoding.UTF8.GetBytes(content));

    private static CharacterManifest Manifest(params FileReplacement[] replacements)
        => new(CharacterManifest.CurrentVersion, replacements, string.Empty, null);

    private const string Idle = "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap";

    /// <summary>Un en-tête de .pap comme ceux des mods mesurés, section Havok comprise.</summary>
    private static byte[] WellFormedPap()
    {
        var file = new byte[26 + 40 + 64 + 16];
        "pap "u8.CopyTo(file);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0x20001);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(8), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), 26);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), 66);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), 66 + 64);
        Convert.FromHexString("1E0DB0CACEFA11D0").CopyTo(file, 66);
        return file;
    }

    [Fact]
    public void Une_animation_mal_formee_est_ecartee_et_le_reste_pose()
    {
        // Un .pap est lu par du code natif du jeu : mal formé, il ne l'atteint
        // pas. L'apparence, elle, est posée quand même.
        var store = new FakeBlobStore();
        var model = Hash("un modèle");
        store.Add(model, 42);
        var broken = store.AddContent("ceci n'est pas une animation"u8.ToArray());

        var built = AppearancePlanner.TryBuild(
            Manifest(new FileReplacement([Top], model, 42), new FileReplacement([Idle], broken, 28)),
            store, Quotas.Default, out var plan, out var why);

        Assert.True(built, why);
        Assert.Contains(Top, plan!.PathMap.Keys);
        Assert.DoesNotContain(Idle, plan.PathMap.Keys);
        Assert.Contains(plan.Dropped, reason => reason.Contains(Idle));
    }

    [Fact]
    public void Une_animation_bien_formee_est_posee()
    {
        var store = new FakeBlobStore();
        var pap = WellFormedPap();
        var hash = store.AddContent(pap);

        var built = AppearancePlanner.TryBuild(
            Manifest(new FileReplacement([Idle], hash, pap.Length)), store, Quotas.Default, out var plan, out var why);

        Assert.True(built, why);
        Assert.Contains(Idle, plan!.PathMap.Keys);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void Chaque_chemin_de_jeu_recoit_le_fichier_du_cache()
    {
        var store = new FakeBlobStore();
        var hash = Hash("un modèle");
        store.Add(hash, 42);

        var built = AppearancePlanner.TryBuild(
            Manifest(new FileReplacement([Top], hash, 42)), store, Quotas.Default, out var plan, out var why);

        Assert.True(built, why);
        Assert.Equal(store.PathFor(hash), Assert.Contains(Top, plan!.PathMap));
    }

    [Fact]
    public void Le_fichier_pose_est_nomme_par_le_hash_et_par_rien_d_autre()
    {
        // Rien de ce qui vient du réseau ne devient un nom de fichier. Le pair
        // choisit le chemin de jeu remplacé, jamais le fichier qui le remplace.
        var store = new FakeBlobStore();
        var hash = Hash("un modèle");
        store.Add(hash, 42);

        AppearancePlanner.TryBuild(
            Manifest(new FileReplacement([Top], hash, 42)), store, Quotas.Default, out var plan, out _);

        var local = plan!.PathMap[Top];

        Assert.EndsWith(hash.ToHex(), local, StringComparison.Ordinal);
        Assert.DoesNotContain("e0001", local, StringComparison.Ordinal);
    }

    [Fact]
    public void Six_chemins_vers_un_meme_contenu_ne_font_qu_un_fichier()
    {
        // C'est tout l'intérêt de l'adressage par contenu : une texture
        // référencée par six chemins est un blob, pas six.
        var store = new FakeBlobStore();
        var hash = Hash("une texture partagée");
        store.Add(hash, 1024);

        var paths = Enumerable.Range(0, 6)
            .Select(i => $"chara/equipment/e000{i}/material/v0001/mt_c0101e000{i}_top_a.mtrl")
            .ToList();

        AppearancePlanner.TryBuild(
            Manifest(new FileReplacement(paths, hash, 1024)), store, Quotas.Default, out var plan, out var why);

        Assert.NotNull(plan);
        Assert.Equal(6, plan!.PathMap.Count);
        Assert.Single(plan.PathMap.Values.Distinct());
    }

    [Fact]
    public void Un_blob_absent_du_cache_fait_refuser_tout_le_plan()
    {
        // Le cache a pu perdre entre la fin du transfert et l'instant de poser.
        // Une apparence à trous est plus difficile à comprendre qu'un pair resté
        // tel qu'il est.
        var store = new FakeBlobStore();
        var present = Hash("présent");
        store.Add(present, 10);

        var built = AppearancePlanner.TryBuild(
            Manifest(
                new FileReplacement([Top], present, 10),
                new FileReplacement([Gloves], Hash("évincé"), 10)),
            store, Quotas.Default, out var plan, out var why);

        Assert.False(built);
        Assert.Null(plan);
        Assert.Contains("absent du cache", why);
    }

    [Fact]
    public void Deux_contenus_pour_un_meme_chemin_de_jeu_sont_refuses()
    {
        // Lequel poser ? Départager par l'ordre d'itération ne serait pas une
        // décision, seulement un hasard.
        var store = new FakeBlobStore();
        var premier = Hash("premier");
        var second = Hash("second");
        store.Add(premier, 10);
        store.Add(second, 10);

        var built = AppearancePlanner.TryBuild(
            Manifest(
                new FileReplacement([Top], premier, 10),
                new FileReplacement([Top], second, 10)),
            store, Quotas.Default, out _, out var why);

        Assert.False(built);
        Assert.Contains("même chemin de jeu", why);
    }

    [Fact]
    public void Un_manifeste_qui_ne_passerait_plus_la_validation_est_refuse()
    {
        // Revalidation complète, alors que la réception l'a déjà faite : elle
        // protège aussi de ce que nous aurions nous-mêmes abîmé entre les deux.
        var store = new FakeBlobStore();
        var hash = Hash("charge");
        store.Add(hash, 10);

        var built = AppearancePlanner.TryBuild(
            Manifest(new FileReplacement(["chara/equipment/e0001/model/charge.exe"], hash, 10)),
            store, Quotas.Default, out _, out var why);

        Assert.False(built);
        Assert.NotNull(why);
    }

    [Fact]
    public void Ce_qui_est_pose_voit_sa_recence_rafraichie()
    {
        // L'éviction choisit sur la récence : ce qui est à l'écran doit partir
        // en dernier.
        var store = new FakeBlobStore();
        var hash = Hash("un modèle");
        store.Add(hash, 42);

        AppearancePlanner.TryBuild(
            Manifest(new FileReplacement([Top], hash, 42)), store, Quotas.Default, out _, out _);

        Assert.Equal(hash, Assert.Single(store.Touched));
    }

    [Fact]
    public void L_etat_Glamourer_et_les_manipulations_meta_traversent_tels_quels()
    {
        // Deux chaînes opaques, produites par Penumbra et Glamourer. On les
        // transporte sans les comprendre, et on ne peut que borner leur taille.
        var store = new FakeBlobStore();
        var hash = Hash("un modèle");
        store.Add(hash, 42);

        var manifest = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [new FileReplacement([Top], hash, 42)],
            Linkpearl.Core.Tests.Safety.Gzipped.Base64(1, "manipulations"),
            Linkpearl.Core.Tests.Safety.Gzipped.Base64(6, "{}"));

        AppearancePlanner.TryBuild(manifest, store, Quotas.Default, out var plan, out _);

        Assert.Equal(manifest.MetaManipulations, plan!.MetaManipulations);
        Assert.Equal(manifest.GlamourerState, plan.GlamourerState);
    }
}
