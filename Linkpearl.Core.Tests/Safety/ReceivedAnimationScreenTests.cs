using Linkpearl.Core.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Safety;

/// <summary>Les animations reçues, jugées contre le squelette qui les jouera.</summary>
public class ReceivedAnimationScreenTests
{
    private const string Emote = "chara/human/c0101/animation/a0001/bt_common/emote/pose01_loop.pap";
    private const string Idle = "chara/human/c0101/animation/a0001/bt_common/resident/idle.pap";

    private static bool Local(string value) => value.StartsWith("C:/", StringComparison.Ordinal);

    [Fact]
    public void Seules_les_animations_posees_depuis_un_fichier_sont_a_lire()
    {
        var map = new Dictionary<string, string>
        {
            [Emote] = "C:/cache/ab.blob",
            [Idle] = "chara/human/c0201/animation/a0001/bt_common/resident/idle.pap",
            ["chara/human/c0101/obj/body/b0001/texture/--c0101b0001_base.tex"] = "C:/cache/cd.blob",
        };

        Assert.Equal([Emote], ReceivedAnimationScreen.ToInspect(map, Local));
    }

    [Theory]
    [InlineData("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb")]
    [InlineData("chara/human/c0101/skeleton/hair/h0001/skl_c0101h0001.sklb")]
    [InlineData("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.skp")]
    public void Un_squelette_recu_avec_l_apparence_empeche_de_juger(string skeleton)
    {
        // Le squelette posé après le redessin n'est pas celui qu'on lit
        // avant : juger contre lui écarterait à tort les animations faites
        // pour un squelette personnalisé.
        var map = new Dictionary<string, string> { [Emote] = "C:/cache/ab.blob", [skeleton] = "C:/cache/sk.blob" };

        Assert.Empty(ReceivedAnimationScreen.ToInspect(map, Local));
    }

    [Fact]
    public void Une_animation_qui_anime_un_os_hors_du_squelette_est_ecartee()
    {
        // Le constat de l'audit : seul l'émetteur vérifiait, et un émetteur
        // modifié s'en passait. L'os d'indice égal au nombre d'os est déjà
        // hors du tableau.
        var refused = ReceivedAnimationScreen.Refused(
            new Dictionary<string, int?> { [Emote] = 120, [Idle] = 119 }, boneCount: 120);

        Assert.Equal([Emote], refused);
    }

    [Fact]
    public void Une_animation_que_le_chargeur_ne_lit_pas_est_ecartee()
        => Assert.Equal([Emote], ReceivedAnimationScreen.Refused(new Dictionary<string, int?> { [Emote] = null }, boneCount: 120));

    [Fact]
    public void Sans_squelette_lisible_on_ne_juge_pas()
        => Assert.Empty(ReceivedAnimationScreen.Refused(new Dictionary<string, int?> { [Emote] = 500 }, boneCount: null));
}
