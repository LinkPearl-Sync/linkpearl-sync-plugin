using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

public class MailboxClaimTests
{
    private static byte[] Address(byte fill) => Enumerable.Repeat(fill, RendezvousWire.MailboxAddressSize).ToArray();

    [Fact]
    public void Chaque_bit_dit_si_la_boite_est_a_nous()
    {
        var outcome = RendezvousClient.ClaimOutcomeOf(RendezvousWire.MailboxClaimed([true, false, true]), 3);

        Assert.Equal([true, false, true], Assert.IsType<MailboxClaimOutcome.Held>(outcome).Mine);
    }

    [Fact]
    public void Un_bit_qui_manque_compte_comme_une_boite_qui_n_est_pas_a_nous()
    {
        var outcome = RendezvousClient.ClaimOutcomeOf(RendezvousWire.MailboxClaimed([true]), 3);

        Assert.Equal([true, false, false], Assert.IsType<MailboxClaimOutcome.Held>(outcome).Mine);
    }

    [Fact]
    public void Un_service_d_avant_ne_prend_pas_en_charge_la_reclamation()
        => Assert.IsType<MailboxClaimOutcome.NotSupported>(
            RendezvousClient.ClaimOutcomeOf(RendezvousWire.Error("trame inattendue"), 2));

    [Theory]
    [InlineData("trop de boîtes pour cette session")]
    [InlineData("demande malformée")]
    public void Une_autre_erreur_est_un_echec_et_non_un_repli(string reason)
    {
        // Se replier sur une ouverture partagée abandonnerait l'exclusivité
        // auprès d'un service qui la connaît.
        var outcome = RendezvousClient.ClaimOutcomeOf(RendezvousWire.Error(reason), 2);

        Assert.Equal(reason, Assert.IsType<MailboxClaimOutcome.Failed>(outcome).Reason);
    }

    [Fact]
    public void Une_coupure_ou_une_reponse_etrangere_est_un_echec()
    {
        Assert.IsType<MailboxClaimOutcome.Failed>(RendezvousClient.ClaimOutcomeOf(null, 2));
        Assert.IsType<MailboxClaimOutcome.Failed>(RendezvousClient.ClaimOutcomeOf([RendezvousKind.Matched], 2));
        Assert.IsType<MailboxClaimOutcome.Failed>(RendezvousClient.ClaimOutcomeOf([RendezvousKind.MailboxClaimed], 2));
    }

    [Fact]
    public async Task La_reclamation_envoie_la_bonne_trame_et_lit_la_reponse()
    {
        using var service = new FakeService(_ => [RendezvousWire.MailboxClaimed([true, false])]);
        await using var client = new RendezvousClient();
        await client.ConnectAsync("127.0.0.1", service.Port, CancellationToken.None);

        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outcome = await client.ClaimMailboxesAsync([Address(1), Address(2)], bounded.Token);

        Assert.Equal([true, false], Assert.IsType<MailboxClaimOutcome.Held>(outcome).Mine);

        var received = await service.FirstFrame;
        Assert.Equal(RendezvousKind.MailboxClaim, received[0]);
        Assert.True(RendezvousWire.TryReadAddresses(received, out var addresses, out _));
        Assert.Equal([Address(1), Address(2)], addresses);
    }

    [Fact]
    public async Task Un_depot_arrive_avant_la_reponse_n_est_pas_perdu()
    {
        using var service = new FakeService(_ =>
            [RendezvousWire.MailboxDelivery([0x42]), RendezvousWire.MailboxClaimed([true])]);
        await using var client = new RendezvousClient();
        await client.ConnectAsync("127.0.0.1", service.Port, CancellationToken.None);

        var delivered = new List<byte[]>();
        client.Delivered += delivered.Add;

        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outcome = await client.ClaimMailboxesAsync([Address(1)], bounded.Token);

        Assert.IsType<MailboxClaimOutcome.Held>(outcome);
        Assert.Equal([0x42], Assert.Single(delivered));
    }

    [Fact]
    public async Task Avec_un_ecouteur_la_reponse_passe_par_lui()
    {
        using var service = new FakeService(frame => frame[0] == RendezvousKind.MailboxClaim
            ? [RendezvousWire.MailboxClaimed([false])]
            : []);
        await using var client = new RendezvousClient();
        await client.ConnectAsync("127.0.0.1", service.Port, CancellationToken.None);

        using var life = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listening = client.ListenAsync(life.Token);

        // L'écouteur doit avoir pris la main avant la question.
        await Task.Delay(100);

        var outcome = await client.ClaimMailboxesAsync([Address(1)], life.Token);

        Assert.Equal([false], Assert.IsType<MailboxClaimOutcome.Held>(outcome).Mine);

        await life.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening);
    }

    [Fact]
    public async Task Un_service_d_avant_repond_trame_inattendue_et_coupe()
    {
        using var service = new FakeService(_ => [RendezvousWire.Error("trame inattendue")], closeAfter: true);
        await using var client = new RendezvousClient();
        await client.ConnectAsync("127.0.0.1", service.Port, CancellationToken.None);

        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.IsType<MailboxClaimOutcome.NotSupported>(await client.ClaimMailboxesAsync([Address(1)], bounded.Token));
    }

    /// <summary>Un service qui répond à chaque trame reçue par les trames qu'on lui dicte.</summary>
    private sealed class FakeService : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<byte[]> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeService(Func<byte[], byte[][]> answer, bool closeAfter = false)
        {
            _listener.Start();
            _ = ServeAsync(answer, closeAfter);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<byte[]> FirstFrame => _first.Task;

        private async Task ServeAsync(Func<byte[], byte[][]> answer, bool closeAfter)
        {
            try
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = socket.GetStream();

                while (true)
                {
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(frame, _stop.Token);
                    _first.TrySetResult(frame);

                    foreach (var reply in answer(frame))
                        await stream.WriteAsync(RendezvousWire.Frame(reply), _stop.Token);

                    if (closeAfter)
                        return;
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // Arrêté par le test.
            }
            catch (EndOfStreamException)
            {
                // Le client est parti.
            }
            catch (IOException)
            {
                // Le client est parti.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
