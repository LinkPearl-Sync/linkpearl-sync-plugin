using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayChoiceTests
{
    private const ulong Swiss = 0x1111, America = 0x2222, Japan = 0x3333;

    private static RelayMeasurement M(ulong service, int rtt) => new(service, (ushort)rtt);

    [Fact]
    public void Deux_joueurs_americains_quittent_le_service_suisse()
    {
        var decision = RelayChoice.Decide([M(Swiss, 95), M(America, 12)], [M(Swiss, 98), M(America, 30)], Swiss);

        Assert.Equal(America, decision.Service);
        Assert.Equal(30, decision.WorstMs);
        Assert.Equal(98, decision.MatchedWorstMs);
    }

    [Fact]
    public void Sans_mesures_d_un_cote_on_garde_le_service_d_appariement()
    {
        Assert.Equal(Swiss, RelayChoice.Decide(null, [M(America, 1)], Swiss).Service);
        Assert.Equal(Swiss, RelayChoice.Decide([M(America, 1)], null, Swiss).Service);
    }

    [Fact]
    public void Une_intersection_vide_garde_le_service_d_appariement()
        => Assert.Equal(Swiss, RelayChoice.Decide([M(America, 10)], [M(Japan, 10)], Swiss).Service);

    [Fact]
    public void Un_service_injoignable_d_un_cote_n_est_pas_candidat()
        => Assert.Equal(Swiss, RelayChoice.Decide(
            [M(Swiss, 95), M(America, 12)], [M(Swiss, 98), M(America, RelayMeasurement.Unreachable)], Swiss).Service);

    [Theory]
    [InlineData(40, 25, false)]   // 15 ms de gain : sous le seuil absolu
    [InlineData(200, 175, false)] // 25 ms mais 12,5 % : sous le seuil relatif
    [InlineData(100, 79, true)]   // 21 ms et 21 % : on change
    public void L_hysteresis_garde_le_service_d_appariement_pour_un_petit_gain(int matchedRtt, int otherRtt, bool moves)
    {
        var decision = RelayChoice.Decide([M(Swiss, matchedRtt), M(America, otherRtt)], [M(Swiss, matchedRtt), M(America, otherRtt)], Swiss);

        Assert.Equal(moves ? America : Swiss, decision.Service);
    }

    [Fact]
    public void Un_service_d_appariement_non_mesure_laisse_le_meilleur_gagner()
        => Assert.Equal(Japan, RelayChoice.Decide([M(America, 80), M(Japan, 40)], [M(America, 80), M(Japan, 45)], Swiss).Service);

    [Fact]
    public void A_pire_egal_la_somme_puis_l_empreinte_departagent()
    {
        Assert.Equal(Japan, RelayChoice.Decide([M(America, 50), M(Japan, 10)], [M(America, 50), M(Japan, 50)], Swiss).Service);
        Assert.Equal(America, RelayChoice.Decide([M(Japan, 50), M(America, 50)], [M(Japan, 50), M(America, 50)], Swiss).Service);
    }

    [Fact]
    public void Le_relais_seul_se_resout_par_la_region()
    {
        // Le pair en relais seul envoie 0 pour l'Amérique et 1000 ailleurs.
        var decision = RelayChoice.Decide([M(Swiss, 1000), M(America, 0)], [M(Swiss, 98), M(America, 30)], Swiss);

        Assert.Equal(America, decision.Service);
    }

    [Fact]
    public void La_decision_est_la_meme_des_deux_cotes()
    {
        var random = new Random(20260927);
        ulong[] services = [Swiss, America, Japan, 0x4444, 0x5555];

        for (var run = 0; run < 2000; run++)
        {
            var mine = RandomMeasures(random, services);
            var theirs = RandomMeasures(random, services);
            var matched = services[random.Next(services.Length)];

            Assert.Equal(RelayChoice.Decide(mine, theirs, matched), RelayChoice.Decide(theirs, mine, matched));
        }
    }

    private static List<RelayMeasurement> RandomMeasures(Random random, ulong[] services)
        => [.. services
            .Where(_ => random.Next(4) > 0)
            .Select(service => M(service, random.Next(6) == 0 ? RelayMeasurement.Unreachable : random.Next(0, 300)))];
}
