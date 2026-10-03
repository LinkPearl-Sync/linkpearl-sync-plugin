using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Tests.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Groups;

public sealed class GroupBookPolicyTests : IDisposable
{
    private readonly ECDsa _group = CryptoPrimitives.GenerateIdentity();
    private readonly ECDsa _owner = CryptoPrimitives.GenerateIdentity();
    private readonly ECDsa _moderator = CryptoPrimitives.GenerateIdentity();
    private readonly MovableClock _clock = new();

    public void Dispose()
    {
        _group.Dispose();
        _owner.Dispose();
        _moderator.Dispose();
    }

    private static readonly PlayerFingerprint Alice = PlayerFingerprint.Of("alice", 21);
    private static readonly PlayerFingerprint Mallory = PlayerFingerprint.Of("mallory", 21);

    private GroupAttestation Attested => PolicyFixture.Attestation(_group, _owner, [_moderator]);

    private GroupPolicy PolicyV(ulong version, IReadOnlyList<GroupBan>? bans = null, string name = "Compagnie")
        => PolicyFixture.Policy(_group, Attested, _group, version: version, bans: bans, name: name);

    private GroupRecord Private(GroupPolicy? policy)
    {
        var ownerKey = PolicyFixture.Compressed(_group);
        return GroupBookTests.Group(RandomNumberGenerator.GetBytes(32), _clock.UtcNow) with
        {
            Id = GroupId.Of(ownerKey),
            OwnerKey = ownerKey,
            Policy = policy,
        };
    }

    [Fact]
    public void Une_politique_plus_recente_est_adoptee_et_renomme_le_groupe()
    {
        var book = new GroupBook(_clock);
        var group = Private(PolicyV(1));
        book.Load([group]);

        var adopted = new List<GroupId>();
        book.PolicyAdopted += (id, _) => adopted.Add(id);

        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(group.Id, GroupPolicyCodec.Encode(PolicyV(2, name: "Nouvelle"))));
        Assert.Equal("Nouvelle", book.Find(group.Id)!.Name);
        Assert.Equal(2UL, book.Find(group.Id)!.Policy!.Version);
        Assert.Equal([group.Id], adopted);

        Assert.Equal(PolicyOffer.Same, book.OfferPolicy(group.Id, GroupPolicyCodec.Encode(book.Find(group.Id)!.Policy!)));
        Assert.Equal(PolicyOffer.Stale, book.OfferPolicy(group.Id, GroupPolicyCodec.Encode(PolicyV(1))));
        Assert.Equal(PolicyOffer.Invalid, book.OfferPolicy(group.Id, [1, 2, 3]));
        Assert.Equal(PolicyOffer.UnknownGroup, book.OfferPolicy(GroupId.FromBytes(new byte[16]), GroupPolicyCodec.Encode(PolicyV(3))));
    }

    [Fact]
    public void Un_groupe_d_essai_n_accepte_aucune_politique()
    {
        var book = new GroupBook(_clock);
        var test = GroupBookTests.Group(RandomNumberGenerator.GetBytes(32), _clock.UtcNow);
        book.Load([test]);

        Assert.Equal(PolicyOffer.NotPrivate, book.OfferPolicy(test.Id, GroupPolicyCodec.Encode(PolicyV(1))));
    }

    [Fact]
    public void Un_membre_banni_n_est_pas_admis_et_rien_n_est_epingle()
    {
        using var mallory = CryptoPrimitives.GenerateIdentity();
        var malloryKey = CryptoPrimitives.ExportPublicPoint(mallory);
        var book = new GroupBook(_clock);
        var group = Private(PolicyV(1, bans: [new GroupBan(PeerId.Of(malloryKey), null)]));
        book.Load([group]);

        Assert.Equal(GroupAdmission.Banned, book.Admit(group.Id, Mallory, malloryKey, "Mallory"));
        Assert.Empty(book.Find(group.Id)!.Members);

        // Une clé quelconque, pas celle d'un pair protégé (propriétaire ou
        // modérateur attesté) : la clé du modérateur ferait passer ce
        // sous-test pour un bannissement contourné par la protection, alors
        // qu'il teste le bannissement par personnage seul.
        using var stranger = CryptoPrimitives.GenerateIdentity();
        var byCharacter = Private(PolicyV(2, bans: [new GroupBan(null, Alice)]));
        book.Load([byCharacter]);
        Assert.Equal(GroupAdmission.Banned, book.Admit(byCharacter.Id, Alice, CryptoPrimitives.ExportPublicPoint(stranger), "Alice"));
    }

    [Fact]
    public void La_cle_complete_est_gardee_a_l_epinglage()
    {
        var book = new GroupBook(_clock);
        var group = Private(PolicyV(1));
        book.Load([group]);
        var key = CryptoPrimitives.ExportPublicPoint(_moderator);

        Assert.Equal(GroupAdmission.Pinned, book.Admit(group.Id, Alice, key, "Alice"));
        Assert.Equal(key, book.Find(group.Id)!.Members[Alice].PublicKey);
    }

    [Fact]
    public void Politique_cles_et_membres_survivent_au_codec()
    {
        var ownerKey = CryptoPrimitives.ExportPublicPoint(_owner);
        var group = Private(PolicyV(4)) with
        {
            SigningKey = _group.ExportPkcs8PrivateKey(),
            Members = new Dictionary<PlayerFingerprint, GroupMember>
            {
                // PublicKey ne survit que cohérente avec l'Id épinglé : voir
                // Un_id_sans_cle_coherente_perd_sa_cle_mais_garde_le_membre.
                [Alice] = new() { Fingerprint = Alice, DisplayName = "Alice", Id = PeerId.Of(ownerKey), PublicKey = ownerKey },
            },
        };

        var back = Assert.Single(GroupBookCodec.Decode(GroupBookCodec.Encode([group])));

        Assert.Equal(group.OwnerKey, back.OwnerKey);
        Assert.Equal(group.SigningKey, back.SigningKey);
        Assert.Equal(4UL, back.Policy!.Version);
        Assert.Equal(group.Members[Alice].PublicKey, back.Members[Alice].PublicKey);
    }

    [Fact]
    public void Un_id_sans_cle_coherente_perd_sa_cle_mais_garde_le_membre()
    {
        // Décision du round de correction 1 : une PublicKey incohérente avec
        // l'Id épinglé (ou présente sans Id) ne doit ni faire disparaître le
        // membre, ni être attestée comme la sienne. Seule PublicKey retombe
        // à null ; l'épinglage (Id) reste, et Admit recomplétera la clé au
        // prochain contact.
        var pinned = PeerId.Of(CryptoPrimitives.ExportPublicPoint(_owner));
        var wrongKey = CryptoPrimitives.ExportPublicPoint(_moderator);   // 65 octets, mais d'une autre identité

        var group = Private(PolicyV(1)) with
        {
            Members = new Dictionary<PlayerFingerprint, GroupMember>
            {
                [Alice] = new() { Fingerprint = Alice, DisplayName = "Alice", Id = pinned, PublicKey = wrongKey },
            },
        };

        var back = Assert.Single(GroupBookCodec.Decode(GroupBookCodec.Encode([group])));

        Assert.Null(back.Members[Alice].PublicKey);
        Assert.Equal(pinned, back.Members[Alice].Id);
    }

    [Fact]
    public void Une_cle_de_mauvaise_taille_perd_sa_cle_mais_garde_le_membre()
    {
        var pinned = PeerId.Of(CryptoPrimitives.ExportPublicPoint(_owner));
        var tooShort = CryptoPrimitives.Compress(CryptoPrimitives.ExportPublicPoint(_owner));   // 33 octets, pas 65

        var group = Private(PolicyV(1)) with
        {
            Members = new Dictionary<PlayerFingerprint, GroupMember>
            {
                [Alice] = new() { Fingerprint = Alice, DisplayName = "Alice", Id = pinned, PublicKey = tooShort },
            },
        };

        var back = Assert.Single(GroupBookCodec.Decode(GroupBookCodec.Encode([group])));

        Assert.Null(back.Members[Alice].PublicKey);
        Assert.Equal(pinned, back.Members[Alice].Id);
    }

    [Fact]
    public void Une_politique_alteree_sur_disque_est_oubliee_pas_le_groupe()
    {
        var group = Private(PolicyV(1)) with { Policy = PolicyV(1) with { Name = "Falsifié" } };   // signature devenue fausse

        var back = Assert.Single(GroupBookCodec.Decode(GroupBookCodec.Encode([group])));

        Assert.Null(back.Policy);
        Assert.Equal(group.Id, back.Id);
    }

    [Fact]
    public void Une_cle_de_groupe_qui_ne_donne_pas_l_identifiant_rejette_l_entree()
    {
        var group = Private(PolicyV(1)) with { Id = GroupId.FromBytes(new byte[16]) };

        Assert.Empty(GroupBookCodec.Decode(GroupBookCodec.Encode([group])));
    }

    [Fact]
    public void Le_planificateur_ne_retient_pas_un_banni()
    {
        var group = Private(PolicyV(1, bans: [new GroupBan(null, Mallory)]));
        var planner = new GroupDialPlanner(_clock);

        var planned = planner.Plan(
            Alice,
            [new GroupSighting(group.Id, Mallory, "Mallory"), new GroupSighting(group.Id, PlayerFingerprint.Of("bob", 21), "Bob")],
            [group], []);

        Assert.Equal(PlayerFingerprint.Of("bob", 21), Assert.Single(planned).PinnedFingerprint);
    }

    [Fact]
    public void Deux_signatures_de_la_meme_politique_comptent_pour_une_seule()
    {
        // Décision de la tâche 3 : ECDSA signe au hasard, donc deux exemplaires
        // du même contenu, signés séparément, ne doivent jamais passer pour
        // deux politiques différentes. OfferPolicy compare le contenu signé,
        // pas la signature.
        var unsigned = PolicyV(1);
        var first = GroupPolicySigning.Sign(unsigned, _group);
        var second = GroupPolicySigning.Sign(unsigned, _group);
        Assert.NotEqual(first.Signature, second.Signature);

        var book = new GroupBook(_clock);
        var group = Private(first);
        book.Load([group]);

        Assert.Equal(PolicyOffer.Same, book.OfferPolicy(group.Id, GroupPolicyCodec.Encode(second)));
        Assert.Equal(first.Signature, book.Find(group.Id)!.Policy!.Signature);
    }

    [Fact]
    public void Une_politique_qui_nous_exclut_garde_le_groupe_jusqu_a_ce_qu_une_plus_recente_nous_reintegre()
    {
        // Un modérateur hostile qui bannit tout le monde ne doit pas dissoudre
        // le groupe de fait : le groupe et son secret restent, exclus, et la
        // politique suivante du propriétaire nous rattrape.
        using var self = CryptoPrimitives.GenerateIdentity();
        var ourKey = PeerId.Of(CryptoPrimitives.ExportPublicPoint(self));
        var book = new GroupBook(_clock);
        var group = Private(PolicyV(1));
        book.Load([group]);

        var previous = new List<GroupPolicy?>();
        book.PolicyAdopted += (_, before) => previous.Add(before);

        var hostile = PolicyFixture.Policy(_group, Attested, _moderator, version: 2, bans: [new GroupBan(ourKey, Alice)]);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(group.Id, GroupPolicyCodec.Encode(hostile)));

        var excluded = book.Find(group.Id);
        Assert.NotNull(excluded);
        Assert.Equal(group.Secret, excluded.Secret);
        Assert.True(excluded.Excludes(ourKey, Alice));
        Assert.Equal(1UL, previous[0]!.Version);

        // Survit à l'enregistrement : l'état se déduit de la politique gardée.
        var reloaded = Assert.Single(GroupBookCodec.Decode(GroupBookCodec.Encode(book.Stored)));
        Assert.True(reloaded.Excludes(ourKey, Alice));

        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(group.Id, GroupPolicyCodec.Encode(PolicyV(3))));
        Assert.False(book.Find(group.Id)!.Excludes(ourKey, Alice));
        Assert.True(previous[1]!.IsBanned(ourKey, Alice));
    }

    [Fact]
    public void Un_groupe_dissous_n_exclut_plus_personne()
    {
        using var self = CryptoPrimitives.GenerateIdentity();
        var ourKey = PeerId.Of(CryptoPrimitives.ExportPublicPoint(self));
        var group = Private(PolicyV(1, bans: [new GroupBan(null, Mallory)]));

        Assert.True(group.Excludes(ourKey, Mallory));
        Assert.False((group with { Policy = group.Policy! with { Dissolved = true } }).Excludes(ourKey, Mallory));
    }

    [Fact]
    public void Exclus_on_ne_compose_que_le_proprietaire_et_les_moderateurs()
    {
        // Eux seuls peuvent lever l'exclusion, et la politique qui la lève
        // ne voyage que dans une session.
        using var self = CryptoPrimitives.GenerateIdentity();
        using var bobIdentity = CryptoPrimitives.GenerateIdentity();
        var ourKey = PeerId.Of(CryptoPrimitives.ExportPublicPoint(self));
        var moderatorPrint = PlayerFingerprint.Of("moderateur", 21);
        var bob = PlayerFingerprint.Of("bob", 21);

        var group = Private(PolicyV(1, bans: [new GroupBan(ourKey, Alice)])) with
        {
            Members = new Dictionary<PlayerFingerprint, GroupMember>
            {
                [moderatorPrint] = new()
                {
                    Fingerprint = moderatorPrint, DisplayName = "Modérateur",
                    Id = PeerId.Of(CryptoPrimitives.ExportPublicPoint(_moderator)),
                },
                [bob] = new()
                {
                    Fingerprint = bob, DisplayName = "Bob",
                    Id = PeerId.Of(CryptoPrimitives.ExportPublicPoint(bobIdentity)),
                },
            },
        };

        var sightings = new[]
        {
            new GroupSighting(group.Id, moderatorPrint, "Modérateur"),
            new GroupSighting(group.Id, bob, "Bob"),
            new GroupSighting(group.Id, PlayerFingerprint.Of("inconnu", 21), "Inconnu"),
        };

        var planned = new GroupDialPlanner(_clock).Plan(Alice, sightings, [group], [], ourKey: ourKey);
        Assert.Equal(moderatorPrint, Assert.Single(planned).PinnedFingerprint);

        // Sans exclusion, tout le monde.
        Assert.Equal(3, new GroupDialPlanner(_clock).Plan(Alice, sightings, [group with { Policy = PolicyV(2) }], [], ourKey: ourKey).Count);
    }

    [Fact]
    public void Le_planificateur_retient_un_moderateur_epingle_malgre_un_bannissement_par_empreinte()
    {
        // Décision de la tâche 3 : IsBanned(null, empreinte) ignore la
        // protection du modérateur, faute de connaître sa clé. Le
        // planificateur doit donc consulter l'Id épinglé du membre pour que
        // GroupPolicy.IsBanned reconnaisse la clé protégée et laisse passer
        // le modérateur, alors même que son personnage est banni par
        // empreinte seule.
        var moderatorFingerprint = PlayerFingerprint.Of("moderateur", 21);
        var policy = PolicyV(1, bans: [new GroupBan(null, moderatorFingerprint)]);
        var group = Private(policy) with
        {
            Members = new Dictionary<PlayerFingerprint, GroupMember>
            {
                [moderatorFingerprint] = new()
                {
                    Fingerprint = moderatorFingerprint,
                    DisplayName = "Modérateur",
                    Id = PeerId.Of(CryptoPrimitives.ExportPublicPoint(_moderator)),
                },
            },
        };

        var planner = new GroupDialPlanner(_clock);
        var planned = planner.Plan(
            Alice,
            [new GroupSighting(group.Id, moderatorFingerprint, "Modérateur")],
            [group], []);

        Assert.Equal(moderatorFingerprint, Assert.Single(planned).PinnedFingerprint);
    }
}
