using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Tests.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Groups;

public sealed class GroupBookTests
{
    private static readonly byte[] Secret = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly PlayerFingerprint Alice = PlayerFingerprint.Of("alice", 21);
    private static readonly byte[] AliceKey = [2, .. Enumerable.Repeat((byte)7, 32)];
    private static readonly byte[] OtherKey = [3, .. Enumerable.Repeat((byte)9, 32)];

    private readonly MovableClock _clock = new();

    internal static GroupRecord Group(byte[] secret, DateTimeOffset joinedAt, string name = "Compagnie")
        => new()
        {
            Id = GroupId.Of(secret),
            Name = name,
            Secret = secret,
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            JoinedAt = joinedAt,
        };

    private GroupBook BookWithOneGroup()
    {
        var book = new GroupBook(_clock);
        Assert.True(book.TryAdd(Group(Secret, _clock.UtcNow), out _));
        return book;
    }

    [Fact]
    public void La_premiere_cle_vue_est_epinglee_puis_seule_admise()
    {
        var book = BookWithOneGroup();
        var id = GroupId.Of(Secret);

        Assert.Equal(GroupAdmission.Pinned, book.Admit(id, Alice, AliceKey, "Alice"));
        Assert.Equal(GroupAdmission.Admitted, book.Admit(id, Alice, AliceKey, "Alice"));
        Assert.Equal(GroupAdmission.Disputed, book.Admit(id, Alice, OtherKey, "Alice"));

        Assert.Equal(PeerId.Of(AliceKey), book.Find(id)!.Members[Alice].Id);
    }

    [Fact]
    public void Un_groupe_inconnu_n_admet_personne()
        => Assert.Equal(
            GroupAdmission.UnknownGroup,
            new GroupBook(_clock).Admit(GroupId.Of(Secret), Alice, AliceKey, "Alice"));

    [Fact]
    public void Au_dela_de_dix_groupes_on_refuse()
    {
        var book = new GroupBook(_clock);

        for (var i = 0; i < GroupBook.MaxGroups; i++)
            Assert.True(book.TryAdd(Group([.. Enumerable.Repeat((byte)i, 32)], _clock.UtcNow), out _));

        Assert.False(book.TryAdd(Group([.. Enumerable.Repeat((byte)99, 32)], _clock.UtcNow), out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Un_groupe_deja_present_n_est_pas_ajoute_deux_fois()
    {
        var book = BookWithOneGroup();

        Assert.False(book.TryAdd(Group(Secret, _clock.UtcNow), out _));
        Assert.Single(book.All);
    }

    [Fact]
    public void Au_plafond_le_nouveau_venu_est_refuse_et_aucun_membre_epingle_n_est_oublie()
    {
        // Oublier le plus ancien rouvrirait son premier contact à qui saurait
        // remplir le carnet : c'est le nouveau qui attend.
        var book = BookWithOneGroup();
        var id = GroupId.Of(Secret);
        var first = PlayerFingerprint.Of("membre0", 21);

        for (var i = 0; i < GroupBook.MaxMembersPerGroup; i++)
        {
            Assert.Equal(GroupAdmission.Pinned,
                book.Admit(id, PlayerFingerprint.Of($"membre{i}", 21), [2, .. Enumerable.Repeat((byte)i, 32)], $"M{i}"));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(GroupAdmission.Full, book.Admit(id, PlayerFingerprint.Of("nouveau", 21), OtherKey, "Nouveau"));

        var members = book.Find(id)!.Members;
        Assert.Equal(GroupBook.MaxMembersPerGroup, members.Count);
        Assert.True(members.ContainsKey(first));
        Assert.False(members.ContainsKey(PlayerFingerprint.Of("nouveau", 21)));

        // Un membre déjà connu reste admis, plafond ou non.
        Assert.Equal(GroupAdmission.Admitted, book.Admit(id, first, [2, .. Enumerable.Repeat((byte)0, 32)], "M0"));
    }

    [Fact]
    public void Une_cle_epinglee_ne_parle_pas_pour_un_autre_personnage()
    {
        // Sans cette règle, une seule identité se poserait sur chaque passant
        // l'un après l'autre.
        var book = BookWithOneGroup();
        var id = GroupId.Of(Secret);
        var bob = PlayerFingerprint.Of("bob", 21);

        Assert.Equal(GroupAdmission.Pinned, book.Admit(id, Alice, AliceKey, "Alice"));
        Assert.Equal(GroupAdmission.Disputed, book.Admit(id, bob, AliceKey, "Bob"));

        Assert.False(book.Find(id)!.Members.ContainsKey(bob));
        Assert.Equal(GroupAdmission.Pinned, book.Admit(id, bob, OtherKey, "Bob"));
    }

    [Fact]
    public void Le_Public_a_son_propre_plafond()
    {
        Assert.Equal(GroupBook.MaxPublicMembers, GroupBook.MemberCap(PublicGroup.Create([], _clock.UtcNow)));
        Assert.Equal(GroupBook.MaxMembersPerGroup, GroupBook.MemberCap(Group(Secret, _clock.UtcNow)));
    }

    [Fact]
    public void Epingler_et_regler_levent_Changed()
    {
        var book = BookWithOneGroup();
        var id = GroupId.Of(Secret);
        var changes = 0;
        book.Changed += () => changes++;

        book.Admit(id, Alice, AliceKey, "Alice");
        book.Admit(id, Alice, AliceKey, "Alice");   // rien de neuf à enregistrer
        book.SetPaused(id, Alice, true);
        book.SetReceive(id, Alice, TransientCategories.None);

        Assert.Equal(3, changes);
        Assert.True(book.Find(id)!.Members[Alice].Paused);
        Assert.Equal(TransientCategories.None, book.Find(id)!.Members[Alice].Receive);
    }

    [Fact]
    public void La_porte_n_admet_que_les_pairs_d_origine_groupe()
    {
        var book = BookWithOneGroup();
        var id = GroupId.Of(Secret);
        var bob = PlayerFingerprint.Of("bob", 21);

        var direct = new PairRecord
        {
            Id = PeerId.Of(AliceKey),
            PairSecret = new byte[32],
            DisplayName = "Alice",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            PairedAt = _clock.UtcNow,
        };

        IGroupGate gate = book;

        Assert.False(gate.Admits(direct, AliceKey));
        Assert.True(gate.Admits(direct with { Group = new GroupOrigin(id, bob, Alice) }, AliceKey));
        Assert.False(gate.Admits(direct with { Group = new GroupOrigin(id, bob, Alice) }, OtherKey));
    }
}
