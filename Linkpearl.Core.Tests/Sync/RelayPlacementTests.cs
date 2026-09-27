using System.Security.Cryptography;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayPlacementTests
{
    private static readonly byte[] Secret = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private static ConsensusEntry Entry(string address, byte family, string? region = null)
        => new(address, "", [.. Enumerable.Repeat(family, 8)], region);

    private static string[] Hosts(IReadOnlyList<RelayPlace> places) => [.. places.Select(place => ServiceConsensus.Canonical(place.At))];

    [Fact]
    public void L_empreinte_est_celle_de_l_adresse_canonique()
        => Assert.Equal(
            Convert.ToUInt64(Convert.ToHexString(SHA256.HashData("rdv.a.ch:47900"u8.ToArray())[..8]), 16),
            RelayPlace.FingerprintOf(new RendezvousAddress("RDV.A.CH", 47900)));

    [Fact]
    public void Le_placement_d_appariement_est_toujours_eligible()
    {
        IReadOnlyList<ConsensusEntry> entries =
            [Entry("rdv.a.ch:47900", 1), Entry("rdv.b.ch:47900", 2), Entry("rdv.c.ch:443", 3), Entry("rdv.d.ch:47900", 4)];

        var places = RelayPlacement.Eligible(Secret, entries, []);

        Assert.Equal(ServicePlacement.Choose(Secret, entries).Select(ServiceConsensus.Canonical), Hosts(places));
        Assert.All(places, place => Assert.True(place.Open));
    }

    [Fact]
    public void Une_region_a_deux_familles_ajoute_son_meilleur_service()
    {
        IReadOnlyList<ConsensusEntry> entries =
        [
            Entry("rdv.a.ch:47900", 1, "EU"), Entry("rdv.b.ch:47900", 2, "EU"),
            Entry("rdv.na1.us:47900", 3, "NA"), Entry("rdv.na2.us:47900", 4, "NA"),
        ];

        var places = RelayPlacement.Eligible(Secret, entries, []);

        Assert.Contains(places, place => place.Region == "NA");
        Assert.Contains(places, place => place.Region == "EU");
        Assert.Equal(places.Count, places.Select(place => place.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Une_region_a_une_seule_famille_n_ajoute_rien()
    {
        IReadOnlyList<ConsensusEntry> entries =
        [
            Entry("rdv.a.ch:47900", 1, "EU"), Entry("rdv.b.ch:47900", 2, "EU"), Entry("rdv.c.ch:47900", 5, "EU"),
            Entry("rdv.na1.us:47900", 3, "NA"), Entry("rdv.na2.us:47900", 3, "NA"),
        ];

        var placement = ServicePlacement.Choose(Secret, entries).Select(ServiceConsensus.Canonical).ToHashSet();
        var places = RelayPlacement.Eligible(Secret, entries, []);

        // Deux services NA d'une même famille : pas de tirage NA. Un service NA
        // n'est éligible que s'il est déjà du placement.
        Assert.All(places.Where(place => place.Region == "NA"),
            place => Assert.Contains(ServiceConsensus.Canonical(place.At), placement));
    }

    [Fact]
    public void L_ancrage_s_ajoute_sans_doublon()
    {
        IReadOnlyList<ConsensusEntry> entries = [Entry("rdv.a.ch:47900", 1), Entry("rdv.b.ch:47900", 2)];

        var places = RelayPlacement.Eligible(Secret, entries,
            [new RendezvousAddress("rdv.a.ch", 47900), new RendezvousAddress("ancre.ch", 47900)]);

        Assert.Equal(3, places.Count);
        Assert.False(places.Single(place => place.At.Host == "ancre.ch").Open);
        Assert.True(places.Single(place => place.At.Host == "rdv.a.ch").Open);
    }

    [Fact]
    public void Sans_liste_il_ne_reste_que_l_ancrage()
    {
        var places = RelayPlacement.Eligible(Secret, [], [new RendezvousAddress("ancre.ch", 47900)]);

        Assert.Equal(["ancre.ch:47900"], Hosts(places));
    }

    [Fact]
    public void Le_resultat_ne_depend_pas_de_l_ordre_de_la_liste()
    {
        List<ConsensusEntry> entries =
        [
            Entry("rdv.a.ch:47900", 1, "EU"), Entry("rdv.b.ch:47900", 2, "EU"),
            Entry("rdv.na1.us:47900", 3, "NA"), Entry("rdv.na2.us:47900", 4, "NA"),
            Entry("rdv.jp1.jp:47900", 5, "AS"), Entry("rdv.jp2.jp:47900", 6, "AS"),
        ];

        var forward = Hosts(RelayPlacement.Eligible(Secret, entries, [])).Order();
        entries.Reverse();
        var backward = Hosts(RelayPlacement.Eligible(Secret, entries, [])).Order();

        Assert.Equal(forward, backward);
    }
}
