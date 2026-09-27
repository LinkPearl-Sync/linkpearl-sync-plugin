using Linkpearl.Core.Identity;
using Linkpearl.Core.Tests.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Identity;

/// <summary>La marque d'un pair qui nous a mis en pause.</summary>
public class PairBookPauseTests
{
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
