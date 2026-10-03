using Linkpearl.Core.Identity;
using Linkpearl.Core.Tests.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Identity;

/// <summary>La marque d'un pair qui nous a mis en pause.</summary>
public class PairBookPauseTests
{
    [Fact]
    public async Task Une_pause_posee_pendant_que_le_moteur_ecrit_n_est_pas_perdue()
    {
        // L'interface, la présence et le tic écrivent le carnet chacun depuis
        // son fil. Sans verrou, le tic relisait l'entrée, l'interface y posait
        // la pause, et le tic réécrivait l'entrée d'avant : la pause disparaissait.
        var book = Book();

        for (var round = 0; round < 50; round++)
        {
            book.SetPaused(Them, false);

            using var go = new ManualResetEventSlim();

            var engine = Task.Run(() =>
            {
                go.Wait();

                for (var i = 0; i < 2_000; i++)
                    book.Seen(Them);
            });

            var interfaceThread = Task.Run(() =>
            {
                go.Wait();
                book.SetPaused(Them, true);
            });

            go.Set();
            await Task.WhenAll(engine, interfaceThread);

            Assert.True(book.Find(Them)!.Paused, $"pause perdue au tour {round}");
        }
    }

    [Fact]
    public async Task Les_lectures_rendent_des_copies_qu_une_ecriture_ne_trouble_pas()
    {
        var book = Book();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 5_000; i++)
            {
                var id = PeerId.FromBytes(BitConverter.GetBytes(i).Concat(new byte[12]).ToArray());
                book.Load([.. book.All, (book.Find(Them)! with { Id = id })]);
                book.Remove(id);
            }
        });

        // Énumérer le dictionnaire lui-même levait « collection modifiée ».
        while (writer.IsCompleted is false)
        {
            _ = book.Active.Count();
            _ = book.Listed.Count;
        }

        await writer;
    }

    private static readonly PeerId Them = PeerId.FromBytes(Enumerable.Repeat((byte)7, 16).ToArray());

    private static PairBook Book()
    {
        var book = new PairBook(new MovableClock());

        book.Load([new PairRecord
        {
            Id = Them,
            PairSecret = new byte[32],
            DisplayName = "Pair",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            Trust = PairTrust.Accepted,
            PairedAt = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero),
        }]);

        return book;
    }

    [Fact]
    public void La_marque_se_pose_et_s_efface()
    {
        var book = Book();

        book.SetPausedByPeer(Them, true);
        Assert.True(book.Find(Them)!.PausedByPeer);

        book.SetPausedByPeer(Them, false);
        Assert.False(book.Find(Them)!.PausedByPeer);
    }

    [Fact]
    public void Un_pair_qui_nous_a_mis_en_pause_reste_actif()
    {
        // Il faut continuer à le chercher : c'est ainsi que sa reprise se voit.
        var book = Book();

        book.SetPausedByPeer(Them, true);

        Assert.Contains(book.Active, pair => pair.Id == Them);
    }
}
