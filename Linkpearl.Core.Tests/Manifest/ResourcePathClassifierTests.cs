using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Manifest;

/// <summary>
/// Penumbra rend « chemin réel -> chemins de jeu ». Le chemin réel est soit un
/// fichier sur le disque, soit un autre chemin de jeu quand le mod fait un
/// échange. Confondre les deux ferait chercher un fichier qui n'existe pas.
/// </summary>
public class ResourcePathClassifierTests
{
    /// <summary>Les dossiers de mods des exemples ci-dessous, où le confinement n'est pas le sujet.</summary>
    private static readonly string[] Roots =
        [@"C:\mods", @"C:\Users\yann\Penumbra", @"D:\Jeux\FFXIV\mods", @"\\serveur\partage", "/home/yann/mods"];

    private static ClassifiedResources Classify(params (string Actual, string[] Game)[] entries)
        => Within(Roots, entries);

    private static ClassifiedResources Within(IReadOnlyCollection<string> roots, params (string Actual, string[] Game)[] entries)
        => ResourcePathClassifier.Classify(
            entries.Select(e => (e.Actual, (IReadOnlyCollection<string>)e.Game)), Quotas.Default, roots);

    private const string Body = "chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex";

    [Theory]
    [InlineData(@"C:\Users\yann\Documents\secret.tex")]
    [InlineData(@"C:\Penumbra\..\Users\yann\secret.tex")]
    [InlineData(@"C:\Penumbra\MonMod\..\..\secret.tex")]
    [InlineData(@"C:\Penumbra2\MonMod\corps.tex")]
    [InlineData(@"\\?\C:\Penumbra\MonMod\corps.tex")]
    [InlineData(@"\\serveur\partage\corps.tex")]
    [InlineData(@"C:\Penumbra\MonMod\.. \..\secret.tex")]
    public void Un_fichier_hors_du_dossier_de_mods_n_est_jamais_annonce(string actual)
    {
        // Un mod qui redirige un chemin du joueur vers un fichier quelconque
        // du disque le faisait lire, garder en cache et servir à ses pairs.
        var result = Within([@"C:\Penumbra"], (actual, [Body]));

        Assert.Empty(result.Files);
        Assert.Equal(Body, Assert.Single(result.Skipped).GamePath);
    }

    [Theory]
    [InlineData(@"C:\Penumbra\MonMod\corps.tex")]
    [InlineData("c:/penumbra/MonMod/corps.tex")]
    [InlineData(@"C:\Penumbra\\MonMod\corps.tex")]
    public void Un_fichier_du_dossier_de_mods_est_annonce_quelle_que_soit_sa_graphie(string actual)
        => Assert.Single(Within([@"C:\Penumbra\"], (actual, [Body])).Files);

    [Fact]
    public void Sans_dossier_de_mods_connu_aucun_fichier_n_est_annonce()
        => Assert.Empty(Within([], (@"C:\Penumbra\MonMod\corps.tex", [Body])).Files);

    [Fact]
    public void Un_fichier_windows_redirige_est_un_fichier_a_transferer()
    {
        var result = Classify((@"C:\Users\yann\Penumbra\MonMod\corps.tex",
                               ["chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex"]));

        var file = Assert.Single(result.Files);
        Assert.Equal(@"C:\Users\yann\Penumbra\MonMod\corps.tex", file.LocalPath);
        Assert.Equal("chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex", file.GamePaths[0]);
        Assert.Empty(result.Swaps);
    }

    [Fact]
    public void Un_chemin_reel_qui_est_un_chemin_de_jeu_est_un_echange_et_non_un_fichier()
    {
        var result = Classify(("chara/equipment/e0201/model/c0101e0201_top.mdl",
                               ["chara/equipment/e0101/model/c0101e0101_top.mdl"]));

        Assert.Empty(result.Files);
        var swap = Assert.Single(result.Swaps);
        Assert.Equal("chara/equipment/e0101/model/c0101e0101_top.mdl", swap.GamePath);
        Assert.Equal("chara/equipment/e0201/model/c0101e0201_top.mdl", swap.TargetGamePath);
    }

    [Fact]
    public void Un_echange_ecrit_avec_des_antislashs_reste_un_echange()
    {
        // Vu en jeu : pour un mod d'animation qui fait jouer celle d'une autre
        // race, Penumbra rend la cible avec des antislashs. Prise pour un
        // fichier, elle était « absente du disque » et l'animation ne partait
        // jamais chez les pairs.
        var result = Classify((@"chara\human\c0201\animation\a0001\bt_common\resident\idle.pap",
                               ["chara/human/c1801/animation/a0001/bt_common/resident/idle.pap"]));

        Assert.Empty(result.Files);
        var swap = Assert.Single(result.Swaps);
        Assert.Equal("chara/human/c1801/animation/a0001/bt_common/resident/idle.pap", swap.GamePath);
        Assert.Equal("chara/human/c0201/animation/a0001/bt_common/resident/idle.pap", swap.TargetGamePath);
    }

    [Fact]
    public void Un_chemin_de_jeu_vanilla_ecrit_avec_des_antislashs_s_ignore()
    {
        var result = Classify((@"chara\human\c1801\animation\a0001\bt_common\resident\idle.pap",
                               ["chara/human/c1801/animation/a0001/bt_common/resident/idle.pap"]));

        Assert.Empty(result.Files);
        Assert.Empty(result.Swaps);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Un_chemin_reel_identique_au_chemin_de_jeu_est_du_vanilla_et_s_ignore()
    {
        // Penumbra rend aussi les ressources non moddees. Les embarquer
        // gonflerait le manifeste de centaines d'entrees inutiles.
        var result = Classify(("chara/equipment/e0101/model/c0101e0101_top.mdl",
                               ["chara/equipment/e0101/model/c0101e0101_top.mdl"]));

        Assert.Empty(result.Files);
        Assert.Empty(result.Swaps);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Une_ressource_vanilla_hors_perimetre_ne_compte_pas_comme_ecartee()
    {
        // Observe en jeu : Penumbra rend aussi les shaders standard du jeu, qui
        // ne sont pas moddes. Les rapporter comme ecartes gonflerait un compteur
        // montre a l'utilisateur et ferait croire que quelque chose manque.
        var result = Classify(("shader/sm5/shpk/skin.shpk", ["shader/sm5/shpk/skin.shpk"]));

        Assert.Empty(result.Files);
        Assert.Empty(result.Swaps);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Une_ressource_hors_perimetre_reellement_moddee_reste_rapportee()
    {
        // Le meme shader, mais redirige vers un fichier : la, il est bien exclu
        // du perimetre v1 et l'utilisateur doit le savoir.
        var result = Classify((@"C:\mods\skin.shpk", ["shader/sm5/shpk/skin.shpk"]));

        Assert.Empty(result.Files);
        Assert.Contains("shader", Assert.Single(result.Skipped).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_echange_hors_perimetre_est_rapporte()
    {
        var result = Classify(("shader/sm5/shpk/autre.shpk", ["shader/sm5/shpk/skin.shpk"]));

        Assert.Empty(result.Swaps);
        Assert.Single(result.Skipped);
    }

    [Theory]
    [InlineData(@"C:\mods\a.tex")]
    [InlineData("C:/mods/a.tex")]
    [InlineData(@"D:\Jeux\FFXIV\mods\a.tex")]
    [InlineData(@"\\serveur\partage\a.tex")]
    [InlineData("/home/yann/mods/a.tex")]
    public void Les_formes_de_chemin_du_systeme_de_fichiers_sont_reconnues(string actual)
    {
        var result = Classify((actual, ["chara/equipment/e0101/model/c0101e0101_top.mdl"]));
        Assert.Single(result.Files);
    }

    [Fact]
    public void Plusieurs_chemins_de_jeu_vers_un_meme_fichier_restent_groupes()
    {
        var result = Classify((@"C:\mods\partage.tex",
        [
            "chara/human/c0201/obj/body/b0001/texture/--c0201b0001_base.tex",
            "chara/human/c0101/obj/body/b0001/texture/--c0101b0001_base.tex",
        ]));

        Assert.Equal(2, Assert.Single(result.Files).GamePaths.Count);
    }

    [Fact]
    public void Les_chemins_de_jeu_sont_normalises_et_tries()
    {
        var result = Classify((@"C:\mods\a.tex",
        [
            "chara/human/c0201/obj/body/b0001/texture/--zzz.tex",
            "Chara/Human/C0101/Obj/Body/B0001/Texture/--AAA.tex",
        ]));

        var file = Assert.Single(result.Files);
        Assert.Equal("chara/human/c0101/obj/body/b0001/texture/--aaa.tex", file.GamePaths[0]);
        Assert.Equal("chara/human/c0201/obj/body/b0001/texture/--zzz.tex", file.GamePaths[1]);
    }

    [Fact]
    public void Une_entree_sans_chemin_de_jeu_valide_est_ecartee_et_rapportee()
    {
        var result = Classify((@"C:\mods\a.tex", ["../../../etc/passwd"]));

        Assert.Empty(result.Files);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Une_extension_hors_perimetre_est_ecartee_et_rapportee()
    {
        var result = Classify((@"C:\mods\character.shpk", ["shader/sm5/shpk/character.shpk"]));

        Assert.Empty(result.Files);
        Assert.Contains("shader", Assert.Single(result.Skipped).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seuls_les_chemins_de_jeu_valides_d_une_entree_sont_retenus()
    {
        var result = Classify((@"C:\mods\a.tex",
        [
            "chara/human/c0201/obj/body/b0001/texture/--ok.tex",
            "../../../etc/passwd",
        ]));

        Assert.Single(Assert.Single(result.Files).GamePaths);
        Assert.Single(result.Skipped);
    }
}
