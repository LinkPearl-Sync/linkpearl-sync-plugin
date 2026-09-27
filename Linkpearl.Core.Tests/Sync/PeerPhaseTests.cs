using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>
/// Où en est un pair, tel que la liste des pairs le dit.
/// </summary>
/// <remarks>
/// Vu en jeu le 26 septembre : un pair repris affichait « hors ligne » pendant
/// toute la recherche au rendez-vous, alors que le moteur le cherchait. Chaque
/// cas ci-dessous est une situation que le moteur distingue et que l'interface
/// confondait.
/// </remarks>
public class PeerPhaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly PeerView Empty = new(null, null, null, 0, 0, false);

    private static PeerPhase Offline(bool dialing = false, TimeSpan? wait = null, string? failure = null,
                                     bool wasAbsent = false, bool pausedByPeer = false)
        => PeerPhases.Of(dialing, hasSession: false, Now, Now + (wait ?? TimeSpan.Zero), failure, wasAbsent,
                         pausedByPeer, Empty, applied: false);

    private static PeerPhase Online(PeerView view, bool applied = false, bool pausedByPeer = false)
        => PeerPhases.Of(dialing: false, hasSession: true, Now, Now, null, wasAbsent: false, pausedByPeer, view,
                         applied);

    [Fact]
    public void Une_recherche_en_cours_n_est_pas_une_absence()
    {
        Assert.Equal(PeerPhase.Searching, Offline(dialing: true, wait: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Une_tentative_due_se_dit_deja_recherche()
    {
        // Entre la reprise et le tic qui lance la tentative, rien n'est encore
        // parti : ce n'est pas pour autant un pair absent.
        Assert.Equal(PeerPhase.Searching, Offline());
    }

    [Fact]
    public void Un_pair_absent_du_rendez_vous_attend_son_prochain_essai()
    {
        Assert.Equal(PeerPhase.Absent, Offline(wait: TimeSpan.FromSeconds(20), wasAbsent: true));
    }

    [Fact]
    public void Un_absent_recherche_de_nouveau_reste_absent()
    {
        // Un ami hors ligne est recherché vingt-cinq secondes sur trente : sans
        // cette règle, sa ligne clignoterait entre « recherche » et « absent ».
        Assert.Equal(PeerPhase.Absent, Offline(dialing: true, wasAbsent: true));
    }

    [Fact]
    public void Un_echec_se_distingue_d_une_absence()
    {
        Assert.Equal(PeerPhase.Failing, Offline(wait: TimeSpan.FromSeconds(20), failure: "relais injoignable"));
    }

    [Fact]
    public void Une_session_sans_apparence_attend_la_sienne()
    {
        Assert.Equal(PeerPhase.AwaitingAppearance, Online(Empty));
    }

    [Fact]
    public void Des_octets_manquants_font_une_reception()
    {
        Assert.Equal(PeerPhase.Receiving, Online(Empty with { MissingBytes = 800, ReceivedBytes = 12 }));
    }

    [Fact]
    public void Une_nouvelle_tenue_en_reception_passe_avant_l_ancienne_posee()
    {
        // Comme le badge en jeu : pendant un changement de tenue, c'est la
        // nouvelle que l'on veut voir avancer.
        Assert.Equal(PeerPhase.Receiving, Online(Empty with { MissingBytes = 800 }, applied: true));
    }

    [Fact]
    public void Une_apparence_complete_non_posee_attend_d_etre_en_vue()
    {
        Assert.Equal(PeerPhase.OutOfView, Online(Empty with { Ready = true }));
    }

    [Fact]
    public void Une_apparence_posee_se_dit_posee()
    {
        Assert.Equal(PeerPhase.Applied, Online(Empty with { Ready = true }, applied: true));
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, "relais injoignable")]
    public void Un_pair_qui_nous_a_mis_en_pause_le_reste_sans_session(bool dialing, bool wasAbsent, string? failure)
    {
        // Pendant les essais qui suivent la pause, il sera tour à tour
        // recherché, absent ou en échec : la ligne doit dire la pause.
        Assert.Equal(PeerPhase.PausedByPeer,
                     Offline(dialing, TimeSpan.FromSeconds(20), failure, wasAbsent, pausedByPeer: true));
    }

    [Fact]
    public void Une_session_rouverte_l_emporte_sur_la_pause()
    {
        Assert.Equal(PeerPhase.AwaitingAppearance, Online(Empty, pausedByPeer: true));
    }
}
