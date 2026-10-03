using System.IO.Compression;
using System.Text;
using Linkpearl.Core.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Safety;

/// <summary>Ce que Penumbra et Glamourer produisent : un octet de version et des données, en gzip puis en base64.</summary>
internal static class Gzipped
{
    public static string Base64(byte[] plain)
    {
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(plain);

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Un octet de version suivi d'un texte, comme <c>ToCompressedBase64</c>.</summary>
    public static string Base64(byte version, string text) => Base64([version, .. Encoding.UTF8.GetBytes(text)]);
}

public class GzipBase64Tests
{
    [Fact]
    public void Une_chaine_vide_veut_dire_rien_a_poser()
        => Assert.True(GzipBase64.IsBounded("", 1024, out _));

    [Fact]
    public void Ce_que_produisent_Penumbra_et_Glamourer_passe()
    {
        Assert.True(GzipBase64.IsBounded(Gzipped.Base64(0, "[{\"Type\":\"Eqp\"}]"), 1024, out var why), why);
        Assert.True(GzipBase64.IsBounded(Gzipped.Base64(6, "{\"FileVersion\":2}"), 1024, out why), why);
    }

    [Fact]
    public void Une_bombe_de_decompression_est_refusee_sans_etre_detendue_en_entier()
    {
        // 300 Mo de zéros tiennent en 300 Kio de gzip, donc sous le
        // plafond de caractères : seul un contrôle de la détente les arrête.
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];

            for (var i = 0; i < 300; i++)
                gzip.Write(zeros);
        }

        var bomb = Convert.ToBase64String(output.ToArray());

        Assert.True(bomb.Length < Quotas.Default.MaxMetaManipulationChars);
        Assert.False(GzipBase64.IsBounded(bomb, Quotas.Default.MaxMetaManipulationsDecompressedBytes, out var why));
        Assert.Contains("détend", why!);
    }

    [Theory]
    [InlineData("pas du base64 !!!")]
    [InlineData("bWV0YQ==")]          // « meta » en clair, sans gzip
    [InlineData("H4sIAAAAAAAA")]      // en-tête gzip tronqué
    public void Un_format_inattendu_ou_illisible_est_refuse(string value)
        => Assert.False(GzipBase64.IsBounded(value, 1024 * 1024, out _));

    // Écrit à la main : GZipStream ne produit rien du tout quand on ne lui
    // donne rien.
    [Fact]
    public void Un_flux_gzip_vide_est_refuse()
        => Assert.False(GzipBase64.IsBounded(
            Convert.ToBase64String(Convert.FromHexString("1F8B080000000000000303000000000000000000")), 1024, out _));

    [Fact]
    public void Le_plafond_est_inclusif()
    {
        var exact = Gzipped.Base64(new byte[1000]);

        Assert.True(GzipBase64.IsBounded(exact, 1000, out _));
        Assert.False(GzipBase64.IsBounded(exact, 999, out _));
    }
}
