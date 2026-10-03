using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

internal sealed class MovableClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public class DebouncerTests
{
    private static Debouncer New(MovableClock clock)
        => new(clock, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(5));

    [Fact]
    public void Rien_ne_se_declenche_sans_signal()
    {
        Assert.False(New(new MovableClock()).TryConsume());
    }

    [Fact]
    public void Une_rafale_ne_declenche_qu_un_seul_recalcul()
    {
        // Un changement de tenue produit une dizaine d'événements en quelques
        // centaines de millisecondes.
        var clock = new MovableClock();
        var debouncer = New(clock);

        for (var i = 0; i < 10; i++)
        {
            debouncer.Signal();
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.False(debouncer.TryConsume());
        }

        clock.Advance(TimeSpan.FromMilliseconds(800));
        Assert.True(debouncer.TryConsume());
        Assert.False(debouncer.TryConsume());
    }

    [Fact]
    public void Des_changements_continus_finissent_par_se_declencher_au_plafond()
    {
        // Sans plafond, quelqu'un qui bricole son apparence pendant dix minutes
        // ne serait jamais synchronisé.
        var clock = new MovableClock();
        var debouncer = New(clock);

        for (var i = 0; i < 100; i++)
        {
            debouncer.Signal();
            clock.Advance(TimeSpan.FromMilliseconds(100));

            if (debouncer.TryConsume())
                return;
        }

        Assert.Fail("le plafond n'a jamais été atteint");
    }

    [Fact]
    public void L_annulation_efface_l_attente()
    {
        var clock = new MovableClock();
        var debouncer = New(clock);

        debouncer.Signal();
        debouncer.Cancel();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.False(debouncer.TryConsume());
    }
}

public class VisibilityMatcherTests
{
    private static PeerId Peer(string seed)
        => PeerId.Of(System.Text.Encoding.UTF8.GetBytes(seed));

    private static PlayerFingerprint Print(string name)
        => PlayerFingerprint.Of(name, 21);

    [Fact]
    public void Un_pair_annonce_et_visible_est_reconnu()
    {
        var visible = new List<VisiblePlayer> { new(new GameObjectRef(4, 100), Print("jhalen")) };
        var announced = new Dictionary<PeerId, PlayerFingerprint> { [Peer("a")] = Print("jhalen") };

        var match = Assert.Single(VisibilityMatcher.Match(visible, announced));

        Assert.Equal(Peer("a"), match.Peer);
        Assert.Equal(4, match.Object.ObjectIndex);
    }

    [Fact]
    public void Un_joueur_visible_sans_pair_correspondant_est_ignore()
    {
        var visible = new List<VisiblePlayer> { new(new GameObjectRef(4, 100), Print("inconnu")) };
        var announced = new Dictionary<PeerId, PlayerFingerprint> { [Peer("a")] = Print("jhalen") };

        Assert.Empty(VisibilityMatcher.Match(visible, announced));
    }

    [Fact]
    public void Un_pair_annonce_mais_absent_du_champ_n_est_pas_reconnu()
    {
        var announced = new Dictionary<PeerId, PlayerFingerprint> { [Peer("a")] = Print("jhalen") };

        Assert.Empty(VisibilityMatcher.Match([], announced));
    }

    [Fact]
    public void Deux_pairs_revendiquant_la_meme_empreinte_ne_donnent_aucun_appariement()
    {
        // L'un des deux ment. Appliquer au hasard ferait porter à quelqu'un
        // l'apparence choisie par un tiers, visible chez nous seuls.
        var visible = new List<VisiblePlayer> { new(new GameObjectRef(4, 100), Print("jhalen")) };
        var announced = new Dictionary<PeerId, PlayerFingerprint>
        {
            [Peer("a")] = Print("jhalen"),
            [Peer("b")] = Print("jhalen"),
        };

        Assert.Empty(VisibilityMatcher.Match(visible, announced));
    }

    [Fact]
    public void L_empreinte_depend_du_monde_autant_que_du_nom()
    {
        // Deux homonymes sur deux mondes sont deux personnes différentes.
        Assert.NotEqual(PlayerFingerprint.Of("jhalen", 21), PlayerFingerprint.Of("jhalen", 36));
    }

    [Fact]
    public void L_empreinte_fait_l_aller_retour_binaire()
    {
        var original = Print("jhalen");
        Assert.Equal(original, PlayerFingerprint.FromBytes(original.ToBytes()));
    }
}

public class PairBookTests
{
    /// <summary>Un lieu de rendez-vous quelconque, ces tests ne portant pas dessus.</summary>
    private static readonly RendezvousAddress Place = new("rdv.exemple.ch", 47900);

    private static PairBook New() => new(new MovableClock());

    private static (PairingCode Code, PeerId Ours, byte[] TheirKey) Invitation()
    {
        using var theirs = CryptoPrimitives.GenerateIdentity();
        using var ours = CryptoPrimitives.GenerateIdentity();

        var theirKey = CryptoPrimitives.ExportPublicPoint(theirs);

        return (PairingCode.Create(PeerId.Of(theirKey), "rdv.exemple.ch"),
                PeerId.Of(CryptoPrimitives.ExportPublicPoint(ours)),
                theirKey);
    }

    [Fact]
    public void Un_pair_ajoute_n_est_pas_encore_verifie_de_vive_voix()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();

        var record = book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        Assert.Equal(PairTrust.Accepted, record.Trust);
        Assert.False(record.KeyVerified);
    }

    [Fact]
    public void Un_pair_ajoute_passe_le_handshake_et_devient_actif()
    {
        // Sans cela, la première connexion échouerait et l'utilisateur ne verrait
        // jamais la demande à confirmer. Rien ne lui est appliqué pour autant.
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        Assert.True(book.IsAuthorized(theirKey));
        Assert.Single(book.Active);
    }

    [Fact]
    public void Un_pair_accepte_devient_actif()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        Assert.Single(book.Active);
    }

    [Fact]
    public void Un_pair_bloque_n_est_plus_autorise()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Gêneur", [Place]);
        book.Block(code.Id);

        Assert.False(book.IsAuthorized(theirKey));
        Assert.Empty(book.Active);
    }

    [Fact]
    public void Un_pair_en_pause_reste_autorise_mais_sort_des_actifs()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);
        book.SetPaused(code.Id, true);

        Assert.True(book.IsAuthorized(theirKey));
        Assert.Empty(book.Active);
    }

    [Fact]
    public void Un_pair_retire_quitte_la_liste_mais_reste_a_prevenir()
    {
        // Retiré sans prévenir, il nous verrait encore comme pairé : c'est
        // l'entrée gardée qui permet au moteur de le lui dire.
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        book.Revoke(code.Id);

        Assert.Empty(book.Listed);
        Assert.Empty(book.Active);
        Assert.Equal(code.Id, Assert.Single(book.Revoked).Id);
    }

    [Fact]
    public void Un_retrait_jamais_remis_est_oublie_au_bout_d_un_mois()
    {
        var clock = new MovableClock();
        var book = new PairBook(clock);
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);
        book.Revoke(code.Id);

        clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, book.ForgetStaleRevocations());

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, book.ForgetStaleRevocations());
        Assert.Null(book.Find(code.Id));
    }

    [Fact]
    public void Un_nouveau_pairage_remplace_un_retrait_en_attente()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);
        book.Revoke(code.Id);

        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        Assert.Single(book.Active);
        Assert.Empty(book.Revoked);
    }

    [Fact]
    public void Un_inconnu_n_est_jamais_autorise()
    {
        using var stranger = CryptoPrimitives.GenerateIdentity();
        Assert.False(New().IsAuthorized(CryptoPrimitives.ExportPublicPoint(stranger)));
    }

    [Fact]
    public void Les_deux_pairs_derivent_le_meme_secret_depuis_le_meme_code()
    {
        // Celui qui invite et celui qui colle doivent tomber sur le même secret,
        // sans quoi leurs jetons de rendez-vous ne coïncideraient jamais.
        using var alice = CryptoPrimitives.GenerateIdentity();
        using var bob = CryptoPrimitives.GenerateIdentity();
        var aliceId = PeerId.Of(CryptoPrimitives.ExportPublicPoint(alice));
        var bobId = PeerId.Of(CryptoPrimitives.ExportPublicPoint(bob));

        var codeFromAlice = PairingCode.Create(aliceId, "rdv.exemple.ch");

        var bobBook = new PairBook(new MovableClock());
        var chezBob = bobBook.Add(
            aliceId, CryptoPrimitives.ExportPublicPoint(alice), codeFromAlice.PairingNonce,
            bobId, "Alice", [Place]);

        var chezAlice = PairSecret.Derive(codeFromAlice.PairingNonce, aliceId, bobId);

        Assert.Equal(chezAlice, chezBob.PairSecret);
    }

    [Fact]
    public void La_cle_complete_est_apprise_au_premier_handshake()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();

        // Un pair peut exister sans clé connue, par exemple après une migration.
        book.Load([new PairRecord
        {
            Id = code.Id, PairSecret = new byte[32], DisplayName = "Amie",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)], Trust = PairTrust.Accepted, PairedAt = default,
        }]);

        Assert.Null(book.Find(code.Id)!.PublicKey);
        Assert.True(book.IsAuthorized(theirKey));
        Assert.Equal(theirKey, book.Find(code.Id)!.PublicKey);
    }

    [Fact]
    public void Une_cle_qui_changerait_apres_avoir_ete_apprise_est_refusee()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        // Deux clés différentes de même empreinte seraient une contradiction.
        var falsifiee = theirKey.ToArray();
        falsifiee[10] ^= 0x01;

        Assert.False(book.IsAuthorized(falsifiee));
    }

    [Fact]
    public void Un_pair_garde_plusieurs_lieux_de_rendez_vous()
    {
        // C'est ce qui fait survivre un pairage à la mort d'un service : les
        // deux côtés s'annoncent sur tous les lieux partagés et se trouvent sur
        // le premier qui répond.
        var book = New();
        var (code, ours, theirKey) = Invitation();

        var lieux = new[]
        {
            new RendezvousAddress("rdv.ami.ch", 47900),
            new RendezvousAddress("rdv.exemple.ch", 443),
        };

        var record = book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", lieux);

        Assert.Equal(lieux, record.Rendezvous);
    }

    [Fact]
    public void L_ordre_des_lieux_est_conserve()
    {
        // Le premier sert de relais préféré : le réordonner silencieusement
        // changerait par qui transitent les octets.
        var book = New();
        var (code, ours, theirKey) = Invitation();

        var lieux = new[]
        {
            new RendezvousAddress("second.exemple.ch", 47900),
            new RendezvousAddress("premier.exemple.ch", 47900),
        };

        var record = book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", lieux);

        Assert.Equal("second.exemple.ch", record.Rendezvous[0].Host);
    }

    [Fact]
    public void L_empreinte_du_personnage_s_epingle()
    {
        var book = New();
        var (code, ours, theirKey) = Invitation();
        book.Add(code.Id, theirKey, code.PairingNonce, ours, "Amie", [Place]);

        book.PinFingerprint(code.Id, PlayerFingerprint.Of("amie", 21));

        Assert.Equal(PlayerFingerprint.Of("amie", 21), book.Find(code.Id)!.PinnedFingerprint);
    }
}

public class PinnedBlobsTests
{
    private static BlobHash H(string content) => BlobHash.OfContent(System.Text.Encoding.UTF8.GetBytes(content));

    private static CharacterManifest With(params string[] contents)
        => new(CharacterManifest.CurrentVersion,
               contents.Select(c => new FileReplacement([$"chara/x/{c}.tex"], H(c), 1)).ToArray(),
               string.Empty, null);

    [Fact]
    public void Tous_les_blobs_des_manifestes_sont_epingles_sans_doublon()
    {
        var pinned = PinnedBlobs.Of([With("a", "b"), null, With("b", "c")]);

        Assert.Equal(3, pinned.Count);
        Assert.Contains(H("a"), pinned);
        Assert.Contains(H("c"), pinned);
    }

    private static FakeBlobStore Holding(params (string Content, long Size)[] blobs)
    {
        var store = new FakeBlobStore();

        foreach (var (content, size) in blobs)
            store.Add(H(content), size);

        return store;
    }

    [Fact]
    public void Ce_qui_est_a_l_ecran_reste_epingle_meme_au_dela_du_budget()
    {
        // Penumbra lit ces fichiers : les retirer casserait un personnage affiché.
        var store = Holding(("a", 500), ("b", 500));

        var pinned = PinnedBlobs.Within([With("a", "b")], [], store, budget: 100);

        Assert.Contains(H("a"), pinned);
        Assert.Contains(H("b"), pinned);
    }

    [Fact]
    public void Une_reception_qui_depasse_le_budget_n_est_pas_epinglee()
    {
        // Sans quoi un pair qui annonce plus que le quota, ou dix qui arrivent
        // ensemble, empêchaient l'éviction de rien retirer.
        var store = Holding(("ecran", 300), ("leger", 100), ("lourd1", 400), ("lourd2", 400));

        var pinned = PinnedBlobs.Within(
            [With("ecran")], [With("lourd1", "lourd2"), With("leger")], store, budget: 500);

        Assert.Contains(H("ecran"), pinned);
        Assert.Contains(H("leger"), pinned);
        Assert.DoesNotContain(H("lourd1"), pinned);
        Assert.DoesNotContain(H("lourd2"), pinned);
    }

    [Fact]
    public void Un_blob_deja_epingle_ne_compte_pas_deux_fois()
    {
        // La réception partage « a » avec l'écran : seul « b » lui coûte.
        var store = Holding(("a", 400), ("b", 100));

        var pinned = PinnedBlobs.Within([With("a")], [With("a", "b")], store, budget: 500);

        Assert.Contains(H("b"), pinned);
    }
}
