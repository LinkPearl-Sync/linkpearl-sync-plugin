using Linkpearl.Core.Crypto;
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class PairRequestMessageTests
{
    private static PairRequestMessage Sample(string name = "Jhalen Tavari", bool accept = false)
    {
        using var identity = CryptoPrimitives.GenerateIdentity();
        using var ephemeral = CryptoPrimitives.GenerateEphemeral();

        return new PairRequestMessage(
            accept, CryptoPrimitives.ExportPublicPoint(identity),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength),
            CryptoPrimitives.ExportPublicPoint(ephemeral),
            name, 21);
    }

    [Fact]
    public void Une_demande_fait_l_aller_retour()
    {
        var original = Sample();

        Assert.True(PairRequestMessage.TryDecode(original.Encode(), out var parsed, out var why), why);
        Assert.Equal(original.PublicKey, parsed!.PublicKey);
        Assert.Equal(original.PairingNonce, parsed.PairingNonce);
        Assert.Equal(original.Ephemeral, parsed.Ephemeral);
        Assert.Equal("Jhalen Tavari", parsed.CharacterName);
        Assert.Equal(21, parsed.WorldId);
        Assert.False(parsed.IsAccept);
    }

    [Fact]
    public void Une_acceptation_se_distingue_d_une_demande()
    {
        Assert.True(PairRequestMessage.TryDecode(Sample(accept: true).Encode(), out var parsed, out _));
        Assert.True(parsed!.IsAccept);
    }

    [Fact]
    public void Les_accents_d_un_nom_survivent()
    {
        Assert.True(PairRequestMessage.TryDecode(Sample("Ysée Tréville").Encode(), out var parsed, out var why), why);
        Assert.Equal("Ysée Tréville", parsed!.CharacterName);
    }

    [Fact]
    public void Un_nom_contenant_des_caracteres_de_controle_est_refuse()
    {
        // Le nom vient du réseau et finit dans l'interface : aucune séquence de
        // contrôle ne doit y arriver.
        var hostile = Sample("Jhalen\nTavari").Encode();

        Assert.False(PairRequestMessage.TryDecode(hostile, out _, out var why));
        Assert.Contains("contrôle", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Jhalen‮Tavari")]   // retournement de l'affichage
    [InlineData("Jhalen​Tavari")]   // espace sans chasse, invisible
    [InlineData("⁦Jhalen Tavari")]  // isolat bidi
    public void Un_nom_porteur_de_caracteres_de_mise_en_forme_est_refuse(string name)
    {
        // Un nom qui s'affiche comme un autre est exactement ce qu'il faut à
        // qui veut se faire passer pour quelqu'un qu'on a devant soi.
        Assert.False(PairRequestMessage.TryDecode(Sample(name).Encode(), out _, out var why));
        Assert.Contains("hors règles", why!);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("　")]
    public void Un_nom_fait_de_blancs_est_refuse(string name)
    {
        Assert.False(PairRequestMessage.TryDecode(Sample(name).Encode(), out _, out var why));
        Assert.Contains("hors règles", why!);
    }

    [Fact]
    public void Un_nom_vide_est_refuse()
    {
        Assert.False(PairRequestMessage.TryDecode(Sample("").Encode(), out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Un_nom_en_utf8_invalide_est_refuse_plutot_que_remplace()
    {
        var frame = Sample("Jhalen").Encode();
        frame[^1] = 0xFF;

        Assert.False(PairRequestMessage.TryDecode(frame, out _, out var why));
        Assert.Contains("UTF-8", why!);
    }

    [Fact]
    public void Un_nom_long_et_accentue_est_coupe_sur_une_frontiere_de_caractere()
    {
        // Soixante-cinq octets dont le dernier caractère en prend deux : une
        // coupe brute à soixante-quatre laisserait une séquence tronquée, que
        // la lecture stricte refuserait.
        var name = new string('a', 63) + "é";
        var encoded = Sample(name).Encode();

        Assert.True(PairRequestMessage.TryDecode(encoded, out var parsed, out var why), why);
        Assert.Equal(new string('a', 63), parsed!.CharacterName);
    }

    [Fact]
    public void Un_nom_trop_long_est_tronque_a_l_encodage_et_accepte_a_la_lecture()
    {
        var encoded = Sample(new string('a', 300)).Encode();

        Assert.True(PairRequestMessage.TryDecode(encoded, out var parsed, out var why), why);
        Assert.True(parsed!.CharacterName.Length <= PairRequestMessage.MaxNameLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    public void Une_demande_tronquee_est_refusee_sans_lever(int length)
    {
        Assert.False(PairRequestMessage.TryDecode(new byte[length], out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Une_cle_publique_invalide_est_refusee()
    {
        var frame = Sample().Encode();
        frame.AsSpan(1, 32).Fill(0x01);

        Assert.False(PairRequestMessage.TryDecode(frame, out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Un_type_inconnu_est_refuse()
    {
        var frame = Sample().Encode();
        frame[0] = 0x7F;

        Assert.False(PairRequestMessage.TryDecode(frame, out _, out var why));
        Assert.NotNull(why);
    }
    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    public void Une_demande_de_la_version_1_est_refusee_avec_un_motif_lisible(byte kind)
    {
        // Sans éphémère, le secret ne dépendrait que de l'aléa, que le
        // rendez-vous voit passer : on refuse plutôt que de lire de travers.
        var frame = new byte[1 + 33 + 12 + 2 + 5];
        frame[0] = kind;

        Assert.False(PairRequestMessage.TryDecode(frame, out _, out var why));
        Assert.Contains("mettre à jour", why!);
    }

    [Fact]
    public void Un_ephemere_invalide_est_refuse()
    {
        var frame = Sample().Encode();
        frame.AsSpan(1 + 33 + 12 + 1, 32).Fill(0x01);

        Assert.False(PairRequestMessage.TryDecode(frame, out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Les_deux_cotes_obtiennent_le_meme_secret_et_le_rendez_vous_non()
    {
        using var alice = CryptoPrimitives.GenerateIdentity();
        using var bob = CryptoPrimitives.GenerateIdentity();
        using var aliceEphemeral = CryptoPrimitives.GenerateEphemeral();
        using var bobEphemeral = CryptoPrimitives.GenerateEphemeral();

        var aliceId = Linkpearl.Core.Identity.PeerId.Of(CryptoPrimitives.ExportPublicPoint(alice));
        var bobId = Linkpearl.Core.Identity.PeerId.Of(CryptoPrimitives.ExportPublicPoint(bob));
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength);

        var onAliceSide = Linkpearl.Core.Identity.PairSecret.Derive(
            PairRequestMessage.AgreeOnPairing(aliceEphemeral, CryptoPrimitives.ExportPublicPoint(bobEphemeral), nonce),
            aliceId, bobId);
        var onBobSide = Linkpearl.Core.Identity.PairSecret.Derive(
            PairRequestMessage.AgreeOnPairing(bobEphemeral, CryptoPrimitives.ExportPublicPoint(aliceEphemeral), nonce),
            bobId, aliceId);

        Assert.Equal(onAliceSide, onBobSide);

        // Ce que le rendez-vous voit passer ne suffit plus : l'aléa et les
        // identifiants seuls donnaient le secret de la version 1.
        Assert.NotEqual(onAliceSide, Linkpearl.Core.Identity.PairSecret.Derive(nonce, aliceId, bobId));
    }
}
