using System.Net;
using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

/// <summary>La liste signée du cercle ouvert : format, signature, et ce qui la fait refuser.</summary>
public class ServiceConsensusTests
{
    private const long Now = 1_790_000_000;

    private static ServiceConsensus Sample(uint version = 7, long issued = Now) => new(
        version, issued, issued + (long)ServiceConsensus.Lifetime.TotalSeconds,
        [
            new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)0x0F, 8)]),
            new ConsensusEntry("rdv.autre.ch:443", "Ailleurs", [.. Enumerable.Repeat((byte)0x2A, 8)]),
        ]);

    [Fact]
    public void Une_liste_signee_se_verifie()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(Sample(), key);

        Assert.True(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], Now, out var list, out var why), why);
        Assert.Equal(7u, list!.Version);
        Assert.Equal(new[] { "rdv.ami.ch:47900", "rdv.autre.ch:443" }, list.Entries.Select(entry => entry.Address));
        Assert.Equal(Enumerable.Repeat((byte)0x2A, 8), list.Entries[1].Family);
    }

    [Fact]
    public void Un_octet_change_la_rend_invalide()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(Sample(), key);
        document[30] ^= 0x01;

        Assert.False(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], Now, out _, out _));
    }

    [Fact]
    public void Une_cle_inconnue_est_refusee()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(Sample(), key);

        Assert.False(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(other)], Now, out _, out var why));
        Assert.Equal("aucune signature d'une clé connue", why);
    }

    [Fact]
    public void Une_liste_expiree_est_refusee()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(Sample(), key);
        var later = Now + (long)ServiceConsensus.Lifetime.TotalSeconds;

        Assert.False(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], later, out _, out var why));
        Assert.Equal("liste expirée", why);
    }

    [Fact]
    public void Un_document_tronque_ou_prolonge_est_refuse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(Sample(), key);
        byte[][] trusted = [ServiceConsensus.PublicPoint(key)];

        Assert.False(ServiceConsensus.TryVerify(document[..^1], trusted, Now, out _, out _));
        Assert.False(ServiceConsensus.TryVerify([.. document, 0], trusted, Now, out _, out _));
        Assert.False(ServiceConsensus.TryVerify(ReadOnlySpan<byte>.Empty, trusted, Now, out _, out _));
    }

    [Fact]
    public void Une_adresse_illisible_est_refusee()
    {
        var list = Sample() with { Entries = [new ConsensusEntry("pas une adresse!", "x", new byte[8])] };
        var document = ServiceConsensus.Assemble(list, [new ConsensusSignature(new byte[8], new byte[64])]);

        Assert.False(ServiceConsensus.TryParse(document, out _, out _, out _, out var why));
        Assert.Equal("adresse 0 illisible", why);
    }

    [Fact]
    public void L_adresse_canonique_ignore_la_casse_et_ecrit_le_port()
    {
        Assert.Equal("rdv.x.ch:47900", ServiceConsensus.Canonical(new RendezvousAddress("RDV.X.ch", 47900)));
        Assert.Equal("rdv.x.ch:443", ServiceConsensus.Canonical(new RendezvousAddress("rdv.x.ch", 443)));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("83.228.242.221", true)]
    [InlineData("2001:1600:18:202::1e4", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Seules_les_adresses_publiques_sont_joignables(string address, bool expected)
        => Assert.Equal(expected, ServiceConsensus.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("203.0.113.57", "f2298267ef14beca")]
    [InlineData("203.0.113.200", "f2298267ef14beca")]
    [InlineData("203.0.114.1", "c54a51a214749b54")]
    [InlineData("::ffff:203.0.113.9", "f2298267ef14beca")]
    [InlineData("2001:db8:1234:5678::1", "5c5ad3455d6875fb")]
    [InlineData("2001:db8:1234:ffff::9", "5c5ad3455d6875fb")]
    public void La_famille_suit_le_24_ou_le_48(string address, string expected)
        => Assert.Equal(expected, Convert.ToHexStringLower(ServiceConsensus.Family(IPAddress.Parse(address))));

    private static ServiceConsensus WithRegions() => new(
        7, Now, Now + (long)ServiceConsensus.Lifetime.TotalSeconds,
        [
            new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)0x0F, 8)], "EU"),
            new ConsensusEntry("rdv.loin.ch:47900", "Loin", [.. Enumerable.Repeat((byte)0x1E, 8)]),
        ]);

    [Fact]
    public void Une_liste_v2_signee_se_verifie_avec_ses_regions()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.SignV2(WithRegions(), key);

        Assert.True(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], Now, out var list, out var why), why);
        Assert.Equal("EU", list!.Entries[0].Region);
        Assert.Null(list.Entries[1].Region);
        Assert.Equal(Enumerable.Repeat((byte)0x1E, 8), list.Entries[1].Family);
    }

    [Fact]
    public void La_v1_ne_porte_pas_les_regions()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(WithRegions(), key);

        Assert.True(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], Now, out var list, out var why), why);
        Assert.All(list!.Entries, entry => Assert.Null(entry.Region));
    }

    [Fact]
    public void Une_signature_v1_ne_vaut_pas_pour_la_v2()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v1 = ServiceConsensus.Sign(WithRegions(), key);
        ServiceConsensus.TryParse(v1, out _, out var signatures, out _, out _);

        var forged = ServiceConsensus.AssembleV2(WithRegions(), signatures);

        Assert.False(ServiceConsensus.TryVerify(forged, [ServiceConsensus.PublicPoint(key)], Now, out _, out _));
    }

    [Fact]
    public void Une_signature_v2_ne_vaut_pas_pour_la_v1()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v2 = ServiceConsensus.SignV2(WithRegions(), key);
        ServiceConsensus.TryParse(v2, out _, out var signatures, out _, out _);

        var forged = ServiceConsensus.Assemble(WithRegions(), signatures);

        Assert.False(ServiceConsensus.TryVerify(forged, [ServiceConsensus.PublicPoint(key)], Now, out _, out _));
    }

    [Fact]
    public void Une_region_hors_des_majuscules_est_refusee_a_la_lecture()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.SignV2(WithRegions(), key);

        // La région de la première entrée suit sa famille : on la retrouve par
        // son contenu plutôt que par un décalage calculé à la main.
        var at = document.AsSpan().IndexOf("EU"u8);
        document[at] = (byte)'e';

        Assert.False(ServiceConsensus.TryParse(document, out _, out _, out _, out var why));
        Assert.Contains("région", why);
    }

    [Fact]
    public void L_ecrivain_refuse_une_region_invalide()
    {
        var list = new ServiceConsensus(1, Now, Now + 60,
            [new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)1, 8)], "eu")]);

        Assert.Throws<ArgumentException>(() => list.SignedPortionV2());
    }

    [Fact]
    public void Une_v2_tronquee_dans_sa_region_est_refusee()
    {
        var list = new ServiceConsensus(1, Now, Now + 60,
            [new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)1, 8)], "EU")]);
        var portion = list.SignedPortionV2();

        Assert.False(ServiceConsensus.TryParse(portion[..^1], out _, out _, out _, out var why));
        Assert.Contains("région", why);
    }
}
