using Linkpearl.Core.Groups;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Tests.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Groups;

public sealed class PublicGroupTests
{
    internal static readonly RendezvousAddress Service = new("rdv.exemple.ch", 47900);

    private readonly MovableClock _clock = new();

    [Fact]
    public void Le_secret_et_l_identifiant_sont_figes()
    {
        // Tous les clients doivent tomber sur les mêmes boîtes : ces valeurs ne
        // changent jamais, et docs/protocol.md les recopie.
        Assert.Equal("41b3c4bdfd876a04e0c524eca45a469d6baf181d0657e464eeef4f59bfaa5a7a", Convert.ToHexStringLower(PublicGroup.Secret));
        Assert.Equal("c92263f11cad477c5082acba30895596", PublicGroup.Id.ToString());
    }

    [Fact]
    public void Public_bloque_les_effets_par_defaut_et_n_a_ni_cle_ni_politique()
    {
        var group = PublicGroup.Create([Service], _clock.UtcNow);

        Assert.True(group.IsPublic);
        Assert.Equal(TransientCategories.None, group.DefaultReceive);
        Assert.Null(group.OwnerKey);
        Assert.Null(group.Policy);
        Assert.Equal(PublicGroup.Name, group.Name);
    }

    [Fact]
    public void Un_membre_sans_reglage_suit_le_defaut_du_groupe()
    {
        var member = new GroupMember { Fingerprint = PlayerFingerprint.Of("bob", 21), DisplayName = "Bob" };
        var group = PublicGroup.Create([Service], _clock.UtcNow);

        Assert.Equal(TransientCategories.None, group.ReceiveOf(member));
        Assert.Equal(TransientCategories.All, group.ReceiveOf(member with { Receive = TransientCategories.All }));
        Assert.Equal(TransientCategories.All, (group with { DefaultReceive = TransientCategories.All }).ReceiveOf(null));
    }

    [Fact]
    public void Un_bloque_est_refuse_par_cle_ou_par_empreinte()
    {
        var bob = PlayerFingerprint.Of("bob", 21);
        var key = PeerId.FromBytes(new byte[PeerId.SizeInBytes]);
        var group = PublicGroup.Create([Service], _clock.UtcNow) with { Blocked = [new GroupBan(key, bob)] };

        Assert.True(group.Refuses(null, bob));
        Assert.True(group.Refuses(key, null));
        Assert.False(group.Refuses(null, PlayerFingerprint.Of("alice", 21)));
    }

    private static readonly PlayerFingerprint Bob = PlayerFingerprint.Of("bob", 21);

    /// <summary>Une clé d'identité neuve à chaque appel, point de 65 octets.</summary>
    private static byte[] FreshKey()
    {
        using var identity = Linkpearl.Core.Crypto.CryptoPrimitives.GenerateIdentity();
        return Linkpearl.Core.Crypto.CryptoPrimitives.ExportPublicPoint(identity);
    }

    [Fact]
    public void Public_dormant_n_est_pas_dans_All()
    {
        var book = new GroupBook(_clock);

        book.SetPublic(true, [Service]);
        Assert.Contains(book.All, group => group.IsPublic);

        book.SetPublic(false, [Service]);
        Assert.DoesNotContain(book.All, group => group.IsPublic);
        Assert.True(book.Public!.Dormant);
        Assert.Null(book.Find(PublicGroup.Id));
    }

    [Fact]
    public void Reactiver_retrouve_les_blocages()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        book.Block(PublicGroup.Id, Bob);

        book.SetPublic(false, [Service]);
        book.SetPublic(true, [Service]);

        Assert.True(book.Public!.Refuses(null, Bob));
    }

    [Fact]
    public void Un_Public_dormant_n_admet_personne()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        book.SetPublic(false, [Service]);

        Assert.Equal(GroupAdmission.UnknownGroup, book.Admit(PublicGroup.Id, Bob, FreshKey(), "Bob"));
    }

    [Fact]
    public void Un_bloque_n_est_pas_admis_et_sa_cle_epinglee_est_bloquee_aussi()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        var key = FreshKey();
        Assert.Equal(GroupAdmission.Pinned, book.Admit(PublicGroup.Id, Bob, key, "Bob"));

        book.Block(PublicGroup.Id, Bob);

        Assert.Equal(GroupAdmission.Banned, book.Admit(PublicGroup.Id, Bob, key, "Bob"));
        Assert.Contains(book.Public!.Blocked, ban => ban.Peer == PeerId.Of(key) && ban.Fingerprint == Bob);
    }

    [Fact]
    public void Debloquer_rend_le_personnage_sans_rendre_la_cle()
    {
        // L'usurpateur d'un passant est bloqué, puis on débloque le passant :
        // la clé de l'usurpateur reste refusée sous tout personnage, et le
        // vrai joueur fait un premier contact neuf au lieu d'être contesté.
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        var usurper = FreshKey();
        var genuine = FreshKey();
        var carol = PlayerFingerprint.Of("carol", 21);

        Assert.Equal(GroupAdmission.Pinned, book.Admit(PublicGroup.Id, Bob, usurper, "Bob"));
        book.Block(PublicGroup.Id, Bob);

        // Bloqué : ni le personnage, ni la clé.
        Assert.Equal(GroupAdmission.Banned, book.Admit(PublicGroup.Id, Bob, genuine, "Bob"));
        Assert.Equal(GroupAdmission.Banned, book.Admit(PublicGroup.Id, carol, usurper, "Carol"));

        book.Unblock(PublicGroup.Id, Assert.Single(book.Public!.Blocked));

        var residue = Assert.Single(book.Public!.Blocked);
        Assert.Equal(new GroupBan(PeerId.Of(usurper), null), residue);
        Assert.Null(book.Public.Members[Bob].Id);

        Assert.Equal(GroupAdmission.Banned, book.Admit(PublicGroup.Id, Bob, usurper, "Bob"));
        Assert.Equal(GroupAdmission.Banned, book.Admit(PublicGroup.Id, carol, usurper, "Carol"));
        Assert.Equal(GroupAdmission.Pinned, book.Admit(PublicGroup.Id, Bob, genuine, "Bob"));
        Assert.Equal(PeerId.Of(genuine), book.Public!.Members[Bob].Id);

        // Second temps : la clé elle-même.
        book.Unblock(PublicGroup.Id, residue);
        Assert.Empty(book.Public!.Blocked);
    }

    [Fact]
    public void Une_cle_bloquee_seule_ne_tient_plus_son_personnage()
    {
        // Un carnet relu où la clé épinglée est bloquée seule : le personnage
        // doit se libérer comme après Unblock, pour le carnet comme pour le planificateur.
        var usurper = PeerId.Of(FreshKey());
        var group = PublicGroup.Create([Service], _clock.UtcNow) with
        {
            Members = new Dictionary<PlayerFingerprint, GroupMember>
            {
                [Bob] = new() { Fingerprint = Bob, DisplayName = "Bob", Id = usurper },
            },
            Blocked = [new GroupBan(usurper, null)],
        };

        var book = new GroupBook(_clock);
        book.Load([group]);
        Assert.Equal(GroupAdmission.Pinned, book.Admit(PublicGroup.Id, Bob, FreshKey(), "Bob"));

        var planned = new GroupDialPlanner(_clock).Plan(
            PlayerFingerprint.Of("alice", 21), [new GroupSighting(PublicGroup.Id, Bob, "Bob")], [group], []);

        Assert.False(Assert.Single(planned).Group!.Pinned);
    }

    [Fact]
    public void Un_Public_desactive_s_enregistre_avec_ses_blocages()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        book.Block(PublicGroup.Id, Bob);
        book.SetPublic(false, [Service]);

        var back = Assert.Single(GroupBookCodec.Decode(GroupBookCodec.Encode(book.Stored)));

        Assert.True(back.IsPublic);
        Assert.True(back.Dormant);
        Assert.True(back.Refuses(null, Bob));
    }

    [Fact]
    public void Un_membre_rencontre_suit_le_defaut_jusqu_a_ce_qu_on_le_regle()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        book.Admit(PublicGroup.Id, Bob, FreshKey(), "Bob");

        Assert.Null(book.Public!.Members[Bob].Receive);
        Assert.Equal(TransientCategories.None, book.Public.ReceiveOf(book.Public.Members[Bob]));

        book.SetDefaultReceive(PublicGroup.Id, TransientCategories.All);
        Assert.Equal(TransientCategories.All, book.Public!.ReceiveOf(book.Public.Members[Bob]));

        book.SetReceive(PublicGroup.Id, Bob, TransientCategories.None);
        Assert.Equal(TransientCategories.None, book.Public!.ReceiveOf(book.Public.Members[Bob]));
    }

    [Fact]
    public void Les_services_du_Public_suivent_la_configuration_sans_ecriture_inutile()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);
        var writes = 0;
        book.Changed += () => writes++;

        book.SetPublicServices([Service]);
        Assert.Equal(0, writes);

        var other = new RendezvousAddress("rdv.autre.ch", 47900);
        book.SetPublicServices([Service, other]);
        Assert.Equal(1, writes);
        Assert.Equal([Service, other], book.Public!.Rendezvous);
    }

    [Fact]
    public void Public_ne_prend_pas_la_place_d_un_groupe_prive()
    {
        var book = new GroupBook(_clock);
        book.SetPublic(true, [Service]);

        for (var i = 0; i < GroupBook.MaxGroups; i++)
            Assert.True(book.TryAdd(GroupBookTests.Group([.. Enumerable.Range(0, 32).Select(b => (byte)(b + i))], _clock.UtcNow), out var why), why);

        Assert.False(book.TryAdd(PublicGroup.Create([Service], _clock.UtcNow), out _));
    }
}
