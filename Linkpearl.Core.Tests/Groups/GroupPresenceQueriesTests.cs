using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Groups;

public class GroupPresenceQueriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly RendezvousAddress Settings = new("rdv.exemple.ch", 47900);
    private static readonly RendezvousAddress CircleService = new("cercle.exemple.org", 47900);

    private static GroupRecord Group(byte fill, params RendezvousAddress[] services)
    {
        var secret = Enumerable.Repeat(fill, 32).ToArray();

        return new GroupRecord
        {
            Id = GroupId.Of(secret),
            Name = $"groupe {fill}",
            Secret = secret,
            Rendezvous = services,
            JoinedAt = Now,
        };
    }

    private static readonly PlayerFingerprint Alice = PlayerFingerprint.Of("alice", 21);
    private static readonly PlayerFingerprint Bob = PlayerFingerprint.Of("bob", 21);

    [Fact]
    public void Un_service_de_groupe_est_interroge_sur_tous_les_joueurs_visibles_sans_detection()
    {
        // Le cas d'un cercle qui héberge son propre service : ses membres ne
        // partagent aucun service des réglages, donc personne n'est détecté.
        var circle = Group(1, CircleService);

        var questions = GroupPresenceQueries.For(
            CircleService, settings: false, [circle], [Alice, Bob], new HashSet<PlayerFingerprint>(), Now);

        Assert.Equal([Alice, Bob], questions.Select(question => question.Member));
        Assert.All(questions, question => Assert.Equal(circle.Id, question.Group));
        Assert.Equal(
            GroupDerivation.PresenceAddress(circle.Secret, Alice, Now).ToBytes(),
            questions[0].Address);
    }

    [Fact]
    public void Un_service_de_groupe_n_entend_parler_que_de_ses_groupes()
    {
        var circle = Group(1, CircleService);
        var elsewhere = Group(2, Settings);

        var questions = GroupPresenceQueries.For(
            CircleService, settings: false, [circle, elsewhere], [Alice], new HashSet<PlayerFingerprint> { Alice }, Now);

        Assert.All(questions, question => Assert.Equal(circle.Id, question.Group));
        Assert.Single(questions);
    }

    [Fact]
    public void Un_service_des_reglages_n_est_interroge_que_sur_les_joueurs_detectes()
    {
        var circle = Group(1, CircleService);
        var local = Group(2, Settings);

        var questions = GroupPresenceQueries.For(
            Settings, settings: true, [circle, local], [Alice, Bob], new HashSet<PlayerFingerprint> { Bob }, Now);

        Assert.All(questions, question => Assert.Equal(Bob, question.Member));
        Assert.Equal(2, questions.Count);
    }

    [Fact]
    public void Aucune_adresse_personnelle_n_est_demandee()
    {
        // L'adresse personnelle se calcule depuis le nom : la demander à un
        // service de groupe lui apprendrait qui passe autour de nous.
        var circle = Group(1, CircleService);
        var personal = Linkpearl.Core.Identity.MailboxAddress.Of(Alice, Now).ToBytes();

        var questions = GroupPresenceQueries.For(
            CircleService, settings: false, [circle], [Alice], new HashSet<PlayerFingerprint>(), Now);

        Assert.DoesNotContain(questions, question => question.Address.AsSpan().SequenceEqual(personal));
    }
}
