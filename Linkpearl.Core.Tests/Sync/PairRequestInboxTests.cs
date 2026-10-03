using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class PairRequestInboxTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed record Request(string Label);

    private static PeerId Key(int n) => PeerId.FromBytes(Enumerable.Repeat((byte)n, PeerId.SizeInBytes).ToArray());

    private static PlayerFingerprint Player(int n) => PlayerFingerprint.Of($"joueur {n}", 21);

    private static byte[] Nonce() => RandomNumberGenerator.GetBytes(PairRequestMessage.NonceLength);

    [Fact]
    public void Une_meme_demande_arrivee_par_deux_services_n_est_vue_qu_une_fois()
    {
        var inbox = new PairRequestInbox<Request>(new FakeClock());
        var nonce = Nonce();

        Assert.True(inbox.Witness(Key(1), nonce));
        Assert.False(inbox.Witness(Key(1), nonce));
        Assert.True(inbox.Witness(Key(1), Nonce()));
    }

    [Fact]
    public void Une_seule_demande_par_expediteur_la_derniere_remplace()
    {
        var inbox = new PairRequestInbox<Request>(new FakeClock());

        Assert.Equal(InboxOutcome.Added, inbox.Offer(Key(1), Player(1), new Request("première")));
        Assert.Equal(InboxOutcome.Replaced, inbox.Offer(Key(1), Player(1), new Request("seconde")));

        Assert.Equal("seconde", Assert.Single(inbox.Peek()).Label);
    }

    [Fact]
    public void Un_meme_nom_sous_une_autre_cle_remplace_aussi()
    {
        // Sans quoi un même personnage annoncé se montrerait sous autant de
        // clés qu'on en tire, une carte par clé.
        var inbox = new PairRequestInbox<Request>(new FakeClock());

        inbox.Offer(Key(1), Player(1), new Request("a"));
        Assert.Equal(InboxOutcome.Replaced, inbox.Offer(Key(2), Player(1), new Request("b")));

        Assert.Equal("b", Assert.Single(inbox.Peek()).Label);
    }

    [Fact]
    public void Au_dela_du_plafond_les_nouvelles_demandes_sont_jetees()
    {
        var inbox = new PairRequestInbox<Request>(new FakeClock());

        for (var i = 0; i < PairRequestInbox<Request>.Capacity; i++)
            Assert.Equal(InboxOutcome.Added, inbox.Offer(Key(i), Player(i), new Request($"{i}")));

        Assert.Equal(InboxOutcome.Full, inbox.Offer(Key(200), Player(200), new Request("de trop")));
        Assert.Equal(PairRequestInbox<Request>.Capacity, inbox.Count);

        // Celles déjà montrées restent, et l'une d'elles peut encore se remplacer.
        Assert.Equal("0", inbox.Peek()[0].Label);
        Assert.Equal(InboxOutcome.Replaced, inbox.Offer(Key(3), Player(3), new Request("3 bis")));
    }

    [Fact]
    public void Une_demande_expire_apres_dix_minutes()
    {
        var clock = new FakeClock();
        var inbox = new PairRequestInbox<Request>(clock);

        inbox.Offer(Key(1), Player(1), new Request("a"));
        clock.UtcNow += TimeSpan.FromMinutes(9);
        Assert.Equal(1, inbox.Count);

        clock.UtcNow += TimeSpan.FromMinutes(1);
        Assert.Equal(0, inbox.Count);
        Assert.Empty(inbox.Peek());
    }

    [Fact]
    public void La_memoire_des_aleas_oublie_avec_le_temps()
    {
        var clock = new FakeClock();
        var inbox = new PairRequestInbox<Request>(clock);
        var nonce = Nonce();

        inbox.Witness(Key(1), nonce);
        clock.UtcNow += PairRequestInbox<Request>.Lifetime;

        Assert.True(inbox.Witness(Key(1), nonce));
    }

    [Fact]
    public void La_memoire_des_aleas_est_bornee()
    {
        var inbox = new PairRequestInbox<Request>(new FakeClock());
        var first = Nonce();

        inbox.Witness(Key(1), first);

        for (var i = 0; i < PairRequestInbox<Request>.SeenCapacity; i++)
            inbox.Witness(Key(1), Nonce());

        // La plus ancienne est partie pour faire de la place : revue, elle
        // passe pour neuve. C'est le prix d'une mémoire qui ne grandit pas.
        Assert.True(inbox.Witness(Key(1), first));
    }

    [Fact]
    public void Retirer_et_oublier()
    {
        var inbox = new PairRequestInbox<Request>(new FakeClock());
        var kept = new Request("gardée");
        var answered = new Request("répondue");

        inbox.Offer(Key(1), Player(1), kept);
        inbox.Offer(Key(2), Player(2), answered);

        Assert.True(inbox.Remove(answered));
        Assert.Same(kept, Assert.Single(inbox.Peek()));

        inbox.Clear();
        Assert.Equal(0, inbox.Count);
    }
}
