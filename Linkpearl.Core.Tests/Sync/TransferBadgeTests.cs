using Linkpearl.Core.Identity;
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>
/// Ce que le badge sous un pair visible doit dire, selon où en est son apparence.
/// </summary>
public class TransferBadgeTests
{
    private const long Mo = 1024 * 1024;

    private static readonly PeerView Empty = new(null, null, null, 0, 0, false);

    private static PeerStatus Status(
        PeerSessionState state, PeerView? view = null, bool applied = false, bool disputed = false)
        => new(PeerId.FromBytes(new byte[16]), "Pair", state, view ?? Empty, applied, disputed, null, null);

    [Fact]
    public void Un_pair_hors_ligne_n_a_pas_de_badge()
    {
        // Il le serait peut-être toute la soirée : un badge permanent ne dit plus rien.
        Assert.Null(TransferBadge.Of(Status(PeerSessionState.Disconnected)));
    }

    [Fact]
    public void Une_apparence_posee_et_a_jour_n_a_pas_de_badge()
    {
        var view = Empty with { Ready = true };

        Assert.Null(TransferBadge.Of(Status(PeerSessionState.Applied, view, applied: true)));
    }

    [Theory]
    [InlineData(PeerSessionState.Connecting)]
    [InlineData(PeerSessionState.Handshaking)]
    public void Une_connexion_en_cours_se_dit(PeerSessionState state)
    {
        var badge = TransferBadge.Of(Status(state));

        Assert.NotNull(badge);
        Assert.Null(badge.Progress);
    }

    [Fact]
    public void Connecte_sans_manifeste_on_attend()
    {
        var badge = TransferBadge.Of(Status(PeerSessionState.Connected));

        Assert.Equal("en attente", badge?.Label);
    }

    [Fact]
    public void Une_reception_montre_sa_progression()
    {
        var view = Empty with { MissingBytes = 192 * Mo, ReceivedBytes = 48 * Mo };

        var badge = TransferBadge.Of(Status(PeerSessionState.Connected, view));

        Assert.NotNull(badge);
        Assert.Equal("réception 48 / 192 Mo", badge.Label);
        Assert.Equal(0.25f, badge.Progress!.Value, precision: 3);
    }

    [Fact]
    public void Un_changement_de_tenue_se_voit_meme_sur_une_apparence_deja_posee()
    {
        // L'ancienne reste posée pendant que la nouvelle arrive : c'est
        // précisément ce que l'utilisateur veut voir avancer.
        var view = Empty with { MissingBytes = 10 * Mo, ReceivedBytes = 0 };

        var badge = TransferBadge.Of(Status(PeerSessionState.Applied, view, applied: true));

        Assert.Equal("réception 0 / 10 Mo", badge?.Label);
    }

    [Fact]
    public void Tout_recu_mais_pas_encore_pose()
    {
        var view = Empty with { MissingBytes = 10 * Mo, ReceivedBytes = 10 * Mo, Ready = true };

        var badge = TransferBadge.Of(Status(PeerSessionState.Connected, view));

        Assert.Equal("application de l'apparence", badge?.Label);
    }

    [Fact]
    public void Une_empreinte_contestee_n_annonce_rien()
    {
        // Rien ne sera posé : un badge « en attente » mentirait.
        Assert.Null(TransferBadge.Of(Status(PeerSessionState.Connected, disputed: true)));
    }

    [Fact]
    public void La_progression_ne_depasse_jamais_un()
    {
        var view = Empty with { MissingBytes = 10 * Mo, ReceivedBytes = 12 * Mo };

        var badge = TransferBadge.Of(Status(PeerSessionState.Connected, view));

        Assert.True(badge?.Progress is null or <= 1f);
    }
}
