using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Safety;

/// <summary>Un échange fait lire un fichier du jeu à la place d'un autre : jamais d'un squelette à l'autre.</summary>
public class FileSwapPolicyTests
{
    private const string HumanEmote = "chara/human/c0101/animation/a0001/bt_common/emote/pose01_loop.pap";

    private static bool Accepts(string gamePath, string target)
        => FileSwapPolicy.TryNormalize(new FileSwap(gamePath, target), Quotas.Default, out _, out _);

    [Theory]
    [InlineData("chara/monster/m0001/animation/a0001/bt_common/resident/monster.pap")]
    [InlineData("chara/demihuman/d1001/animation/a0001/bt_common/resident/demihuman.pap")]
    [InlineData("bg/ffxiv/anim.pap")]
    public void Une_animation_humaine_n_est_pas_servie_par_celle_d_un_autre_squelette(string target)
        // L'audit : un échange légal faisait jouer à un humain une animation
        // de monstre, liée à des os que son squelette n'a pas, sans fichier
        // forgé ni contrôle d'aucune sorte chez l'émetteur.
        => Assert.False(Accepts(HumanEmote, target));

    [Fact]
    public void Un_squelette_humain_n_est_pas_servi_par_celui_d_un_monstre()
        => Assert.False(Accepts(
            "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb",
            "chara/monster/m0001/skeleton/base/b0001/skl_m0001b0001.sklb"));

    [Fact]
    public void Un_mod_de_pose_qui_echange_deux_animations_humaines_passe()
        => Assert.True(Accepts(HumanEmote, "chara/human/c0201/animation/a0001/bt_common/emote/pose02_loop.pap"));

    [Fact]
    public void Une_texture_peut_venir_d_ailleurs()
        => Assert.True(Accepts(
            "chara/human/c0101/obj/body/b0001/texture/--c0101b0001_base.tex",
            "chara/monster/m0001/obj/body/b0001/texture/v01_m0001b0001_d.tex"));
}
