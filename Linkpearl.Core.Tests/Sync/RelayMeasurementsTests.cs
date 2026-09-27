using System.Net;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayMeasurementsTests
{
    private static readonly IPEndPoint[] Addresses = [new(IPAddress.Parse("203.0.113.7"), 40000)];

    private static RelayMeasurement[] Sample => [new(0x0102030405060708, 12), new(0xA0A0A0A0A0A0A0A0, RelayMeasurement.Unreachable)];

    [Fact]
    public void Le_format_est_fige()
        // Longueur : 1 + 2 × 10 = 21 = 0x0015.
        => Assert.Equal(
            "010015" + "02" + "0102030405060708" + "000c" + "a0a0a0a0a0a0a0a0" + "ffff",
            Convert.ToHexStringLower(RelayMeasurements.Encode(Sample)));

    [Fact]
    public void Les_mesures_se_relisent_apres_les_adresses()
    {
        byte[] block = [.. CandidateSet.Encode(Addresses), .. RelayMeasurements.Encode(Sample)];

        Assert.True(CandidateSet.TryDecode(block, out var candidates, out var consumed, out var why), why);
        Assert.Equal(Addresses, candidates);
        Assert.Equal(Sample, RelayMeasurements.TryRead(block.AsSpan(consumed)));
    }

    [Fact]
    public void Un_decodeur_d_aujourd_hui_ignore_les_mesures()
    {
        byte[] block = [.. CandidateSet.Encode(Addresses), .. RelayMeasurements.Encode(Sample)];

        Assert.True(CandidateSet.TryDecode(block, out var candidates, out var why), why);
        Assert.Equal(Addresses, candidates);
    }

    [Fact]
    public void Un_bloc_d_avant_n_a_pas_de_mesures()
    {
        var block = CandidateSet.Encode(Addresses);

        Assert.True(CandidateSet.TryDecode(block, out _, out var consumed, out _));
        Assert.Null(RelayMeasurements.TryRead(block.AsSpan(consumed)));
    }

    [Fact]
    public void Une_etiquette_inconnue_est_sautee()
    {
        byte[] extensions = [0x7F, 0x00, 0x02, 0xEE, 0xEE, .. RelayMeasurements.Encode(Sample)];

        Assert.Equal(Sample, RelayMeasurements.TryRead(extensions));
    }

    [Theory]
    [InlineData("01")]                         // en-tête tronqué
    [InlineData("010015")]                     // contenu absent
    [InlineData("0100010a")]                   // dix mesures annoncées, aucune présente
    [InlineData("01000100" + "01000100")]      // deux blocs de mesures
    [InlineData("010001" + "11")]              // dix-sept mesures
    public void Une_extension_malformee_ne_donne_aucune_mesure(string hex)
        => Assert.Null(RelayMeasurements.TryRead(Convert.FromHexString(hex)));

    [Fact]
    public void Au_plus_seize_mesures_sont_ecrites()
    {
        var many = Enumerable.Range(0, 20).Select(i => new RelayMeasurement((ulong)i, 10)).ToArray();

        Assert.Equal(RelayMeasurements.MaxMeasurements, RelayMeasurements.TryRead(RelayMeasurements.Encode(many))!.Count);
    }

    [Fact]
    public void Un_rtt_se_borne_au_plafond()
    {
        Assert.Equal(RelayMeasurement.Ceiling, RelayMeasurement.Of(1, TimeSpan.FromMinutes(5)).RttMs);
        Assert.Equal(RelayMeasurement.Unreachable, RelayMeasurement.Of(1, null).RttMs);
        Assert.Equal((ushort)13, RelayMeasurement.Of(1, TimeSpan.FromMilliseconds(12.2)).RttMs);
    }

    [Fact]
    public void En_relais_seul_seule_la_region_la_plus_proche_est_a_zero()
    {
        var eu = new RelayPlace(new RendezvousAddress("rdv.eu.ch", 47900), "EU", true);
        var na = new RelayPlace(new RendezvousAddress("rdv.na.us", 47900), "NA", true);
        var anchor = new RelayPlace(new RendezvousAddress("ancre.ch", 47900), null, false);
        var dead = new RelayPlace(new RendezvousAddress("rdv.mort.ch", 47900), "EU", true);

        var synthetic = RelayMeasurements.Synthetic(
            [eu, na, anchor, dead],
            [new(anchor.Fingerprint, 3), new(na.Fingerprint, 90), new(eu.Fingerprint, 15), new(dead.Fingerprint, RelayMeasurement.Unreachable)]);

        Assert.Equal(RelayMeasurements.Far, synthetic.Single(m => m.Service == anchor.Fingerprint).RttMs);
        Assert.Equal(RelayMeasurements.Far, synthetic.Single(m => m.Service == na.Fingerprint).RttMs);
        Assert.Equal((ushort)0, synthetic.Single(m => m.Service == eu.Fingerprint).RttMs);
        Assert.Equal(RelayMeasurement.Unreachable, synthetic.Single(m => m.Service == dead.Fingerprint).RttMs);
    }

    [Fact]
    public void En_relais_seul_sans_region_rien_n_est_a_zero()
    {
        var anchor = new RelayPlace(new RendezvousAddress("ancre.ch", 47900), null, false);

        var synthetic = RelayMeasurements.Synthetic([anchor], [new(anchor.Fingerprint, 3)]);

        Assert.Equal(RelayMeasurements.Far, Assert.Single(synthetic).RttMs);
    }
}
