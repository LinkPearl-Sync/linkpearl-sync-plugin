using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Tests.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Manifest;

public class ManifestBuilderTests
{
    private const string Meta = "bWV0YQ==";
    private const string Glam = "Z2xhbW91cmVy";

    private static ResolvedFile File(string gamePath, string content)
        => new(gamePath, BlobHash.OfContent(System.Text.Encoding.UTF8.GetBytes(content)), content.Length);

    private static readonly ResolvedFile Torse =
        File("chara/equipment/e0101/model/c0101e0101_top.mdl", "modele-torse");
    private static readonly ResolvedFile Peau =
        File("chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex", "texture-peau");

    [Fact]
    public void Un_manifeste_reprend_les_fichiers_resolus()
    {
        var result = ManifestBuilder.Build([Torse, Peau], Meta, Glam, Quotas.Default);

        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.Manifest.Replacements.Count);
        Assert.Equal(Meta, result.Manifest.MetaManipulations);
        Assert.Equal(Glam, result.Manifest.GlamourerState);
    }

    [Fact]
    public void Un_meme_contenu_vise_par_plusieurs_chemins_ne_donne_qu_une_entree()
    {
        // C'est toute la raison du regroupement par hash : une texture reference
        // par six chemins de jeu, c'est un blob a transferer et non six.
        var a = File("chara/equipment/e0101/model/c0101e0101_top.mdl", "identique");
        var b = File("chara/equipment/e0102/model/c0101e0102_top.mdl", "identique");

        var result = ManifestBuilder.Build([a, b], Meta, Glam, Quotas.Default);

        var entry = Assert.Single(result.Manifest.Replacements);
        Assert.Equal(2, entry.GamePaths.Count);
    }

    [Fact]
    public void L_ordre_d_arrivee_ne_change_pas_le_hash_du_manifeste()
    {
        // Sans cette propriete, on renverrait un manifeste « change » a chaque
        // recalcul, et le diff ne servirait plus a rien.
        var direct = ManifestBuilder.Build([Torse, Peau], Meta, Glam, Quotas.Default).Manifest;
        var inverse = ManifestBuilder.Build([Peau, Torse], Meta, Glam, Quotas.Default).Manifest;

        Assert.Equal(ManifestCodec.HashOf(direct), ManifestCodec.HashOf(inverse));
    }

    [Fact]
    public void Un_contenu_different_change_le_hash_du_manifeste()
    {
        var avant = ManifestBuilder.Build([Torse], Meta, Glam, Quotas.Default).Manifest;
        var apres = ManifestBuilder.Build(
            [File("chara/equipment/e0101/model/c0101e0101_top.mdl", "autre-modele")],
            Meta, Glam, Quotas.Default).Manifest;

        Assert.NotEqual(ManifestCodec.HashOf(avant), ManifestCodec.HashOf(apres));
    }

    [Fact]
    public void L_etat_glamourer_entre_dans_le_hash()
    {
        var a = ManifestBuilder.Build([Torse], Meta, Glam, Quotas.Default).Manifest;
        var b = ManifestBuilder.Build([Torse], Meta, "YXV0cmU=", Quotas.Default).Manifest;

        Assert.NotEqual(ManifestCodec.HashOf(a), ManifestCodec.HashOf(b));
    }

    [Fact]
    public void La_casse_du_chemin_est_normalisee_dans_le_manifeste()
    {
        var result = ManifestBuilder.Build(
            [File("Chara/Equipment/E0101/Model/C0101E0101_Top.MDL", "x")], Meta, Glam, Quotas.Default);

        Assert.Equal("chara/equipment/e0101/model/c0101e0101_top.mdl",
                     Assert.Single(result.Manifest.Replacements).GamePaths[0]);
    }

    [Fact]
    public void Un_chemin_local_invalide_est_ecarte_et_rapporte_plutot_que_de_tout_faire_echouer()
    {
        // A la construction on ecarte : un seul mod tordu ne doit pas priver
        // l'utilisateur de toute synchronisation. A la reception, au contraire,
        // le manifeste entier est rejete.
        var result = ManifestBuilder.Build(
            [Torse, File("../../../etc/passwd", "x")], Meta, Glam, Quotas.Default);

        Assert.Single(result.Manifest.Replacements);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("../../../etc/passwd", skipped.GamePath);
        Assert.Contains("traversée", skipped.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Une_extension_hors_perimetre_est_ecartee_et_rapportee()
    {
        var result = ManifestBuilder.Build(
            [Torse, File("shader/sm5/shpk/character.shpk", "x")], Meta, Glam, Quotas.Default);

        Assert.Single(result.Manifest.Replacements);
        Assert.Contains("shader", Assert.Single(result.Skipped).Reason, StringComparison.OrdinalIgnoreCase);
    }
}

public class ManifestCodecTests
{
    private static CharacterManifest Sample()
        => ManifestBuilder.Build(
            [
                new ResolvedFile("chara/equipment/e0101/model/c0101e0101_top.mdl",
                                 BlobHash.OfContent("a"u8), 1),
                new ResolvedFile("chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex",
                                 BlobHash.OfContent("b"u8), 2),
            ],
            Meta, Glam, Quotas.Default).Manifest;

    private static readonly string Meta = Gzipped.Base64(1, "manipulations");
    private static readonly string Glam = Gzipped.Base64(6, "{\"FileVersion\":2}");

    [Fact]
    public void L_aller_retour_compresse_conserve_le_manifeste()
    {
        var origine = Sample();
        var compresse = ManifestCodec.Compress(origine);

        Assert.True(ManifestCodec.TryDecompress(compresse, Quotas.Default, out var relu, out var why), why);
        Assert.Equal(ManifestCodec.HashOf(origine), ManifestCodec.HashOf(relu!));
        Assert.Equal(origine.Replacements.Count, relu!.Replacements.Count);
        Assert.Equal(origine.MetaManipulations, relu.MetaManipulations);
        Assert.Equal(origine.GlamourerState, relu.GlamourerState);
    }

    [Fact]
    public void L_encodage_canonique_est_stable_octet_pour_octet()
    {
        Assert.Equal(ManifestCodec.Encode(Sample()), ManifestCodec.Encode(Sample()));
    }

    [Fact]
    public void La_compression_reduit_reellement_un_manifeste_realiste()
    {
        var files = Enumerable.Range(0, 400)
            .Select(i => new ResolvedFile(
                $"chara/equipment/e{i:D4}/model/c0101e{i:D4}_top.mdl",
                BlobHash.OfContent(System.Text.Encoding.UTF8.GetBytes($"contenu{i}")), i))
            .ToList();

        var manifest = ManifestBuilder.Build(files, "bWV0YQ==", "Z2xhbQ==", Quotas.Default).Manifest;

        var brut = ManifestCodec.Encode(manifest).Length;
        var compresse = ManifestCodec.Compress(manifest).Length;

        Assert.True(compresse < brut / 2, $"brut {brut} octets, compressé {compresse}");
    }

    [Fact]
    public void Un_manifeste_illisible_est_refuse_sans_lever()
    {
        Assert.False(ManifestCodec.TryDecompress("ceci n'est pas du brotli"u8, Quotas.Default, out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Une_bombe_de_decompression_est_arretee_au_plafond()
    {
        // Un pair peut envoyer 1 Mo qui se detend en plusieurs Go. La lecture
        // s'arrete au plafond au lieu de remplir la memoire du processus du jeu.
        var enorme = new byte[64 * 1024 * 1024];
        using var sortie = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(sortie, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(enorme);

        Assert.False(ManifestCodec.TryDecompress(sortie.ToArray(), Quotas.Default, out _, out var why));
        Assert.Contains("plafond", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_manifeste_recu_qui_viole_une_seule_regle_est_rejete_en_entier()
    {
        // Asymetrie voulue avec la construction : un rejet partiel donnerait un
        // personnage incoherent et masquerait une tentative.
        var hostile = new CharacterManifest(
            CharacterManifest.CurrentVersion,
            [
                new FileReplacement(["chara/equipment/e0101/model/c0101e0101_top.mdl"], BlobHash.OfContent("a"u8), 1),
                new FileReplacement(["../../../etc/passwd"], BlobHash.OfContent("b"u8), 2),
            ],
            "bWV0YQ==", null);

        Assert.False(ManifestValidator.TryAccept(hostile, Quotas.Default, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Un_manifeste_qui_depasse_le_plafond_d_entrees_est_rejete()
    {
        var etroit = Quotas.Default with { MaxReplacements = 1 };
        var manifest = Sample();

        Assert.False(ManifestValidator.TryAccept(manifest, etroit, out var why));
        Assert.Contains("plafond", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_manifeste_legitime_est_accepte()
    {
        Assert.True(ManifestValidator.TryAccept(Sample(), Quotas.Default, out var why), why);
    }

    [Fact]
    public void Une_version_de_manifeste_inconnue_est_rejetee()
    {
        var futur = Sample() with { Version = 9999 };
        Assert.False(ManifestValidator.TryAccept(futur, Quotas.Default, out _));
    }

    private static FileReplacement Entry(int i, long size, BlobHash? hash = null)
        => new([$"chara/equipment/e{i:D4}/model/c0101e{i:D4}_top.mdl"],
               hash ?? BlobHash.OfContent(System.Text.Encoding.UTF8.GetBytes($"blob{i}")), size);

    [Fact]
    public void Une_apparence_plus_lourde_que_le_plafond_total_est_refusee()
    {
        // Chaque blob reste sous son plafond, mais leur somme remplirait le
        // disque : c'est la somme qui compte pour le cache.
        var quotas = Quotas.Default with { MaxManifestTotalBytes = 250 };
        var manifest = Sample() with { Replacements = [Entry(1, 100), Entry(2, 100), Entry(3, 100)] };

        Assert.False(ManifestValidator.TryAccept(manifest, quotas, out var why));
        Assert.Contains("lourde", why!);
    }

    [Fact]
    public void Un_meme_blob_repete_ne_compte_qu_une_fois_dans_le_total()
    {
        var hash = BlobHash.OfContent("partagé"u8);
        var quotas = Quotas.Default with { MaxManifestTotalBytes = 150 };
        var manifest = Sample() with { Replacements = [Entry(1, 100, hash), Entry(2, 100, hash)] };

        Assert.True(ManifestValidator.TryAccept(manifest, quotas, out var why), why);
    }

    [Fact]
    public void Deux_tailles_pour_une_meme_empreinte_sont_refusees()
    {
        // Le receveur exige que l'annonce d'un blob porte la taille du
        // manifeste : deux tailles pour un contenu rendraient cette règle
        // ambiguë, et la somme fausse.
        var hash = BlobHash.OfContent("partagé"u8);
        var manifest = Sample() with { Replacements = [Entry(1, 100, hash), Entry(2, 5000, hash)] };

        Assert.False(ManifestValidator.TryAccept(manifest, Quotas.Default, out var why));
        Assert.Contains("deux tailles", why!);
    }

    [Fact]
    public void Des_manipulations_meta_qui_cachent_une_bombe_sont_refusees()
    {
        // Penumbra détendrait tout sur le thread du jeu, sans plafond.
        var bomb = Gzipped.Base64(new byte[Quotas.Default.MaxMetaManipulationsDecompressedBytes + 1]);
        var manifest = Sample() with { MetaManipulations = bomb };

        Assert.True(bomb.Length < Quotas.Default.MaxMetaManipulationChars);
        Assert.False(ManifestValidator.TryAccept(manifest, Quotas.Default, out var why));
        Assert.Contains("méta", why!);
    }

    [Fact]
    public void Un_etat_Glamourer_qui_cache_une_bombe_est_refuse()
    {
        var bomb = Gzipped.Base64(new byte[Quotas.Default.MaxGlamourerStateDecompressedBytes + 1]);
        var manifest = Sample() with { GlamourerState = bomb };

        Assert.True(bomb.Length < Quotas.Default.MaxGlamourerStateChars);
        Assert.False(ManifestValidator.TryAccept(manifest, Quotas.Default, out var why));
        Assert.Contains("Glamourer", why!);
    }

    [Theory]
    [InlineData("bWV0YQ==")]
    [InlineData("pas du base64 !!!")]
    public void Des_manipulations_meta_hors_format_sont_refusees(string meta)
        => Assert.False(ManifestValidator.TryAccept(Sample() with { MetaManipulations = meta }, Quotas.Default, out _));

    [Fact]
    public void Le_plus_gros_manifeste_honnete_tient_sous_le_plafond_de_detente()
    {
        // Le calcul qui fixe MaxManifestDecompressedBytes : chaque plafond du
        // validateur atteint à la fois, avec des chaînes comme les plugins en
        // produisent (base64 réel, JSON réel), et des chemins au plus long.
        var q = Quotas.Default;

        static string LongPath(string prefix, int i, int length)
        {
            var head = $"chara/{prefix}{i:D5}/";
            return head + new string('a', length - head.Length - 4) + ".pap";
        }

        var replacements = Enumerable.Range(0, q.MaxReplacements)
            .Select(i => new FileReplacement(
                [LongPath("r", i, q.MaxGamePathLength)],
                BlobHash.OfContent(System.Text.Encoding.UTF8.GetBytes($"blob{i}")), q.MaxBlobBytes / 1000))
            .ToList();

        var swaps = Enumerable.Range(0, q.MaxGamePaths - q.MaxReplacements)
            .Select(i => new FileSwap(LongPath("s", i, q.MaxGamePathLength), LongPath("t", i, q.MaxGamePathLength)))
            .ToList();

        // Du base64 d'octets aléatoires : un « + » sur soixante-quatre, que
        // l'encodeur JSON échappe en six octets.
        string RandomBase64(int chars)
            => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(chars * 3 / 4))[..chars];

        // Du JSON dense en guillemets, comme un profil Customize+ : l'encodeur
        // échappe chacun en six octets.
        static string JsonOfLength(int chars)
        {
            var json = new System.Text.StringBuilder("{");

            while (json.Length < chars - 16)
                json.Append("\"X\":0.1,");

            return json.Append("\"Z\":0}").ToString();
        }

        var manifest = new CharacterManifest(
            CharacterManifest.CurrentVersion, replacements,
            RandomBase64(q.MaxMetaManipulationChars), RandomBase64(q.MaxGlamourerStateChars),
            new CharacterExtras(
                JsonOfLength(q.MaxCustomizePlusChars), JsonOfLength(q.MaxHeelsChars), JsonOfLength(q.MaxHonorificChars),
                RandomBase64(q.MaxMoodlesChars), RandomBase64(q.MaxPetNicknamesChars)),
            swaps);

        var encoded = ManifestCodec.Encode(manifest).Length;

        Assert.True(encoded <= q.MaxManifestDecompressedBytes,
            $"{encoded} octets encodés pour un plafond de {q.MaxManifestDecompressedBytes}");
        Assert.True(encoded > q.MaxManifestDecompressedBytes * 3 / 4,
            $"{encoded} octets : le plafond n'est plus serré, le revoir à la baisse");
    }
}
