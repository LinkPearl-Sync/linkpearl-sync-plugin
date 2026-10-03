using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Safety;

public class ExtrasValidatorTests
{
    private static readonly Quotas Q = Quotas.Default;

    private static bool Accept(CharacterExtras extras) => ExtrasValidator.TryAccept(extras, Q, out _);

    private static string Pet() => PetNicknamesData.Neutralize(Convert.ToBase64String(Encoding.Unicode.GetBytes(
        string.Join("\r\n", PetNicknamesData.Header, "Nom", "73", "123", "411^2^Sparky^null^null"))))!;

    private static string Moodles() => MoodlesSanitizer.Sanitize(
        Convert.ToBase64String(MoodlesCodec.Encode([])), MoodlesSanitizer.KeyFor(RandomNumberGenerator.GetBytes(32)))!;

    [Fact]
    public void Des_extras_vides_passent() => Assert.True(Accept(CharacterExtras.None));

    [Fact]
    public void Des_extras_valides_passent()
        => Assert.True(Accept(new CharacterExtras(
            "{\"Bones\":{}}", "{\"DefaultOffset\":0.1}", "{\"Title\":\"le Voyageur\"}", Moodles(), Pet())));

    [Fact]
    public void CustomizePlus_au_plafond_passe_et_au_dessus_non()
    {
        static string Padded(int length) => "{\"a\":\"" + new string('x', length - 8) + "\"}";

        Assert.True(Accept(CharacterExtras.None with { CustomizePlus = Padded(Q.MaxCustomizePlusChars) }));
        Assert.False(Accept(CharacterExtras.None with { CustomizePlus = Padded(Q.MaxCustomizePlusChars + 1) }));
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("[]")]
    [InlineData("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":1}}}}}}}}}")]
    public void Un_json_invalide_ou_trop_profond_est_refuse(string json)
        => Assert.False(Accept(CharacterExtras.None with { CustomizePlus = json }));

    [Fact]
    public void Des_talons_non_nettoyes_sont_refuses()
        => Assert.False(Accept(CharacterExtras.None with { Heels = "{\"DefaultOffset\":0.1,\"Tags\":{}}" }));

    [Theory]
    [InlineData("{\"Title\":\"trente-trois caractères, un de trop\"}")]
    [InlineData("{\"Title\":\"a\\u0007b\"}")]
    [InlineData("{\"Title\":42}")]
    public void Un_titre_trop_long_ou_avec_un_controle_est_refuse(string json)
        => Assert.False(Accept(CharacterExtras.None with { Honorific = json }));

    // SimpleHeels et Honorific lisent leur JSON avec Newtonsoft, insensible à
    // la casse : une variante de casse échappait à une comparaison exacte et
    // arrivait quand même jusqu'au plugin.
    [Theory]
    [InlineData("{\"title\":\"trente-trois caractères, un de trop\"}")]
    [InlineData("{\"TITLE\":\"a\\u0007b\"}")]
    [InlineData("{\"Title\":\"court\",\"title\":\"trente-trois caractères, un de trop\"}")]
    public void Le_titre_se_controle_a_la_casse_pres_et_sans_doublon(string json)
        => Assert.False(Accept(CharacterExtras.None with { Honorific = json }));

    [Theory]
    [InlineData("{\"DefaultOffset\":0.1,\"tags\":{}}")]
    [InlineData("{\"DefaultOffset\":0.1,\"EMOTEPOSITION\":{}}")]
    [InlineData("{\"DefaultOffset\":0.1,\"defaultoffset\":9}")]
    public void Les_champs_des_talons_se_controlent_a_la_casse_pres(string json)
        => Assert.False(Accept(CharacterExtras.None with { Heels = json }));

    [Fact]
    public void Un_doublon_a_la_casse_pres_est_refuse_meme_en_profondeur()
        => Assert.False(Accept(CharacterExtras.None with { Heels = "{\"Emotes\":[{\"Offset\":1,\"offset\":2}]}" }));

    [Fact]
    public void L_emetteur_retire_les_champs_a_la_casse_pres()
    {
        var clean = HeelsSanitizer.Sanitize("{\"DefaultOffset\":0.1,\"tags\":{},\"emotePosition\":[1,2,3]}");

        Assert.NotNull(clean);
        Assert.True(Accept(CharacterExtras.None with { Heels = clean }));
        Assert.DoesNotContain("tags", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Des_moodles_non_nettoyes_sont_refuses()
    {
        var raw = Convert.ToBase64String(MoodlesCodec.Encode(
            [new MoodleStatus(Guid.NewGuid(), 1, "t", "d", "", 0, 0, 0, 1, 0, Guid.Empty, 0, "Nom@Monde", "")]));

        Assert.False(Accept(CharacterExtras.None with { Moodles = raw }));
    }

    [Fact]
    public void Des_surnoms_non_neutralises_sont_refuses()
        => Assert.False(Accept(CharacterExtras.None with
        {
            PetNicknames = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                string.Join("\r\n", PetNicknamesData.Header, "Nom", "73", "123"))),
        }));

    [Fact]
    public void A_l_envoi_un_extra_hors_plafond_est_omis_seul()
    {
        // Relevé en relecture : trente moodles aux longues descriptions dépassent le
        // plafond de réception. Envoyés tels quels, ils feraient rejeter
        // l'apparence entière chez chaque pair ; omis, seuls les moodles manquent.
        var huge = MoodlesSanitizer.Sanitize(
            Convert.ToBase64String(MoodlesCodec.Encode(Enumerable.Range(0, 30)
                .Select(_ => new MoodleStatus(Guid.NewGuid(), 1, "t", new string('d', 500), "", 0, 0, 0, 1, 0, Guid.Empty, 0, "", ""))
                .ToList())),
            MoodlesSanitizer.KeyFor(RandomNumberGenerator.GetBytes(32)))!;

        Assert.True(huge.Length > Q.MaxMoodlesChars);

        var kept = ExtrasValidator.KeepValid(
            new CharacterExtras(null, null, "{\"Title\":\"le Voyageur\"}", huge, null), Q, out var dropped);

        Assert.Null(kept.Moodles);
        Assert.Equal("{\"Title\":\"le Voyageur\"}", kept.Honorific);
        Assert.Equal(["Moodles"], dropped);
        Assert.True(ExtrasValidator.TryAccept(kept, Q, out _));
    }

    [Fact]
    public void Un_seul_champ_fautif_fait_refuser_le_manifeste_entier()
    {
        var manifest = new CharacterManifest(CharacterManifest.CurrentVersion, [], "", null,
            new CharacterExtras("{\"Bones\":{}}", null, "{\"Title\":\"a\\u0007\"}", null, null));

        Assert.False(ManifestValidator.TryAccept(manifest, Q, out var why));
        Assert.Contains("Honorific", why!);
    }
}
