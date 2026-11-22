using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class OutgoingPairRequestsTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    }

    private static readonly PlayerFingerprint Target = PlayerFingerprint.Of("jhalen tavari", 21);

    private static PeerId SomeKey()
    {
        using var identity = CryptoPrimitives.GenerateIdentity();
        return PeerId.Of(CryptoPrimitives.ExportPublicPoint(identity));
    }

    private static byte[] TheirEphemeral()
    {
        using var ephemeral = CryptoPrimitives.GenerateEphemeral();
        return CryptoPrimitives.ExportPublicPoint(ephemeral);
    }

    private static (OutgoingPairRequests Requests, FakeClock Clock, byte[] Nonce) Sent()
    {
        var clock = new FakeClock();
        var requests = new OutgoingPairRequests(clock);
        var nonce = RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength);
        requests.Add(Target, nonce, CryptoPrimitives.GenerateEphemeral());
        return (requests, clock, nonce);
    }

    [Fact]
    public void La_premiere_acceptation_d_un_expediteur_visible_conclut()
    {
        var (requests, _, nonce) = Sent();

        Assert.True(requests.IsPending(Target));

        var verdict = requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true);

        Assert.NotEmpty(Assert.IsType<AcceptanceVerdict.Concluded>(verdict).PairingMaterial);
        Assert.False(requests.IsPending(Target));
    }

    [Fact]
    public void Une_seconde_acceptation_sous_la_meme_cle_mais_un_autre_ephemere_annule()
    {
        // Le constat de l'audit : un tiers qui a lu la demande et connaît la
        // clé publique de la cible répond le premier sous cette clé, avec son
        // propre éphémère. Le secret de paire devenait le sien, et la vraie
        // acceptation passait pour un écho.
        var (requests, _, nonce) = Sent();
        var target = SomeKey();

        Assert.IsType<AcceptanceVerdict.Concluded>(
            requests.Settle(Target, target, nonce, TheirEphemeral(), senderVisible: true));

        Assert.Equal(target, Assert.IsType<AcceptanceVerdict.Conflict>(
            requests.Settle(Target, target, nonce, TheirEphemeral(), senderVisible: true)).First);
    }

    [Fact]
    public void Une_acceptation_qui_ne_porte_pas_l_ephemere_de_l_accuse_annule()
    {
        // La cible accuse réception d'elle-même, avec l'éphémère qu'elle
        // gardera pour accepter. Un tiers qui a lu la demande ailleurs et
        // répond le premier ne peut pas porter cet éphémère.
        var (requests, _, nonce) = Sent();
        var genuine = TheirEphemeral();

        Assert.IsType<AcceptanceVerdict.Repeated>(requests.NoteReceipt(nonce, genuine));

        var verdict = requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true);

        Assert.Null(Assert.IsType<AcceptanceVerdict.Conflict>(verdict).First);
        Assert.IsType<AcceptanceVerdict.Repeated>(requests.Settle(Target, SomeKey(), nonce, genuine, senderVisible: true));
    }

    [Fact]
    public void Un_accuse_qui_contredit_une_acceptation_deja_conclue_l_annule()
    {
        // L'intrus a répondu avant que l'accusé n'arrive : la cible, elle, n'a
        // pas besoin d'accepter pour le démasquer.
        var (requests, _, nonce) = Sent();
        var intruder = SomeKey();

        Assert.IsType<AcceptanceVerdict.Concluded>(requests.Settle(Target, intruder, nonce, TheirEphemeral(), senderVisible: true));

        Assert.Equal(intruder, Assert.IsType<AcceptanceVerdict.Conflict>(requests.NoteReceipt(nonce, TheirEphemeral())).First);
    }

    [Fact]
    public void L_acceptation_qui_porte_l_ephemere_de_l_accuse_conclut()
    {
        var (requests, _, nonce) = Sent();
        var genuine = TheirEphemeral();

        requests.NoteReceipt(nonce, genuine);

        Assert.IsType<AcceptanceVerdict.Concluded>(requests.Settle(Target, SomeKey(), nonce, genuine, senderVisible: true));
    }

    [Fact]
    public void Deux_accuses_differents_annulent_la_demande()
    {
        var (requests, _, nonce) = Sent();

        requests.NoteReceipt(nonce, TheirEphemeral());

        Assert.IsType<AcceptanceVerdict.Conflict>(requests.NoteReceipt(nonce, TheirEphemeral()));
        Assert.IsType<AcceptanceVerdict.Repeated>(requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true));
    }

    [Fact]
    public void Un_accuse_sans_demande_est_inconnu()
        => Assert.IsType<AcceptanceVerdict.Unknown>(
            Sent().Requests.NoteReceipt(RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength), TheirEphemeral()));

    [Fact]
    public void Un_expediteur_hors_de_vue_ne_conclut_pas_et_laisse_la_demande_ouverte()
    {
        var (requests, _, nonce) = Sent();

        Assert.IsType<AcceptanceVerdict.NotVisible>(
            requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: false));

        // Le vrai destinataire, lui, est devant nous : sa réponse conclut encore.
        Assert.True(requests.IsPending(Target));
        Assert.IsType<AcceptanceVerdict.Concluded>(
            requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true));
    }

    [Fact]
    public void Une_seconde_acceptation_sous_une_autre_cle_annule_la_premiere()
    {
        var (requests, clock, nonce) = Sent();
        var intruder = SomeKey();

        Assert.IsType<AcceptanceVerdict.Concluded>(
            requests.Settle(Target, intruder, nonce, TheirEphemeral(), senderVisible: true));

        clock.UtcNow += TimeSpan.FromMinutes(3);

        var verdict = requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true);

        Assert.Equal(intruder, Assert.IsType<AcceptanceVerdict.Conflict>(verdict).First);
    }

    [Fact]
    public void La_seconde_cle_est_signalee_meme_hors_de_vue()
    {
        // La seconde réponse prouve à elle seule que l'aléa a fui : peu
        // importe où se tient celui qui l'envoie.
        var (requests, _, nonce) = Sent();
        var first = SomeKey();

        requests.Settle(Target, first, nonce, TheirEphemeral(), senderVisible: true);

        Assert.IsType<AcceptanceVerdict.Conflict>(
            requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: false));
    }

    [Fact]
    public void Apres_l_annulation_plus_rien_ne_conclut_sur_cet_alea()
    {
        var (requests, _, nonce) = Sent();

        requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true);
        requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true);

        Assert.IsType<AcceptanceVerdict.Repeated>(
            requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true));
    }

    [Fact]
    public void La_meme_cle_qui_repond_deux_fois_n_est_qu_un_echo()
    {
        // Un écho, c'est la même trame reçue par un autre service : même clé,
        // même éphémère. Un éphémère différent est un second répondant.
        var (requests, _, nonce) = Sent();
        var key = SomeKey();
        var ephemeral = TheirEphemeral();

        requests.Settle(Target, key, nonce, ephemeral, senderVisible: true);

        Assert.IsType<AcceptanceVerdict.Repeated>(
            requests.Settle(Target, key, nonce, ephemeral, senderVisible: true));
    }

    [Fact]
    public void La_surveillance_dure_dix_minutes_puis_l_alea_est_oublie()
    {
        var (requests, clock, nonce) = Sent();

        requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true);

        clock.UtcNow += TimeSpan.FromMinutes(9);
        Assert.True(requests.Concerns(Target, nonce));

        clock.UtcNow += TimeSpan.FromMinutes(1);
        Assert.False(requests.Concerns(Target, nonce));
        Assert.IsType<AcceptanceVerdict.Unknown>(
            requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true));
    }

    [Fact]
    public void Un_autre_alea_ou_un_autre_expediteur_ne_concerne_aucune_demande()
    {
        var (requests, _, nonce) = Sent();
        var other = PlayerFingerprint.Of("jhalen tavari", 22);

        Assert.False(requests.Concerns(Target, RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength)));
        Assert.False(requests.Concerns(other, nonce));
        Assert.IsType<AcceptanceVerdict.Unknown>(
            requests.Settle(other, SomeKey(), nonce, TheirEphemeral(), senderVisible: true));
    }

    [Fact]
    public void Une_nouvelle_demande_remplace_l_ancienne()
    {
        var (requests, _, first) = Sent();
        var second = RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength);

        requests.Add(Target, second, CryptoPrimitives.GenerateEphemeral());

        Assert.False(requests.Concerns(Target, first));
        Assert.True(requests.Concerns(Target, second));
    }

    [Fact]
    public void Oublier_au_changement_de_personnage_ne_laisse_rien_conclure()
    {
        var (requests, _, nonce) = Sent();

        requests.Clear();

        Assert.False(requests.IsPending(Target));
        Assert.IsType<AcceptanceVerdict.Unknown>(
            requests.Settle(Target, SomeKey(), nonce, TheirEphemeral(), senderVisible: true));
    }

    [Fact]
    public void Les_deux_cotes_obtiennent_le_meme_materiau()
    {
        var clock = new FakeClock();
        var requests = new OutgoingPairRequests(clock);
        var nonce = RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength);
        var ours = CryptoPrimitives.GenerateEphemeral();
        var oursPublic = CryptoPrimitives.ExportPublicPoint(ours);
        requests.Add(Target, nonce, ours);

        using var theirs = CryptoPrimitives.GenerateEphemeral();

        var verdict = requests.Settle(
            Target, SomeKey(), nonce, CryptoPrimitives.ExportPublicPoint(theirs), senderVisible: true);

        Assert.Equal(
            PairRequestMessage.AgreeOnPairing(theirs, oursPublic, nonce),
            Assert.IsType<AcceptanceVerdict.Concluded>(verdict).PairingMaterial);
    }
}
