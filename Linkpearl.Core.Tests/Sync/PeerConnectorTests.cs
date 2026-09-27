using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

/// <summary>Un annonceur qui répond selon le lieu, sans réseau.</summary>
internal sealed class ScriptedDialer(Dictionary<string, byte[]?> answers) : IRendezvousDialer
{
    public List<string> Asked { get; } = [];

    public async Task<byte[]?> AnnounceAsync(
        RendezvousAddress at, Announcement announcement, CancellationToken ct)
    {
        lock (Asked)
            Asked.Add(at.Host);

        if (answers.TryGetValue(at.Host, out var answer) is false)
        {
            // Service muet : il ne répond jamais, il ne refuse pas. C'est le cas
            // qui piège une annonce séquentielle.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }

        return answer;
    }
}

/// <summary>Un annonceur qui date chaque appel, et dont certains lieux lèvent aussitôt.</summary>
internal sealed class TimedDialer(Dictionary<string, byte[]?> answers, HashSet<string>? failing = null) : IRendezvousDialer
{
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();

    public List<(string Host, TimeSpan At)> Asked { get; } = [];

    public async Task<byte[]?> AnnounceAsync(RendezvousAddress at, Announcement announcement, CancellationToken ct)
    {
        lock (Asked)
            Asked.Add((at.Host, _watch.Elapsed));

        if (failing?.Contains(at.Host) is true)
            throw new IOException("service injoignable");

        if (answers.TryGetValue(at.Host, out var answer) is false)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        }

        return answer;
    }
}

/// <summary>Un ouvreur de relais scripté : chaque service accepte, refuse ou se tait.</summary>
/// <remarks>Le lien rendu est le faux lien en mémoire du moteur, dont seule la présence compte ici.</remarks>
internal sealed class ScriptedRelays(Dictionary<string, string> behaviour) : IRelayOpener
{
    public List<(string Host, TimeSpan Budget)> Opened { get; } = [];

    public Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct)
    {
        Opened.Add((place.At.Host, budget));

        return Task.FromResult(behaviour.GetValueOrDefault(place.At.Host) switch
        {
            "accepte" => new RelayOpening(MemoryLink.Pair().A, false),
            "refuse" => new RelayOpening(null, true),
            _ => new RelayOpening(null, false),
        });
    }
}

public class PeerConnectorTests
{
    private static Announcement Some() => new([new byte[16]], [9]);

    private static RendezvousAddress At(string host) => new(host, 47900);

    [Fact]
    public async Task Le_cercle_ouvert_apparie_sans_deranger_l_ancrage()
    {
        var dialer = new TimedDialer(new() { ["ouvert.ch"] = [1] });

        var match = await PeerConnector.AnnounceInCirclesAsync(
            dialer, [At("ouvert.ch")], [At("ancre.ch")], Some(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("ouvert.ch", match!.Value.At.Host);
        Assert.DoesNotContain(dialer.Asked, asked => asked.Host == "ancre.ch");
    }

    [Fact]
    public async Task L_ancrage_attend_la_tete_laissee_au_cercle_ouvert()
    {
        var dialer = new TimedDialer(new() { ["ancre.ch"] = [1] });

        var match = await PeerConnector.AnnounceInCirclesAsync(
            dialer, [At("ouvert.ch")], [At("ancre.ch")], Some(), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("ancre.ch", match!.Value.At.Host);
        Assert.True(dialer.Asked.Single(asked => asked.Host == "ancre.ch").At >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task Des_services_ouverts_injoignables_liberent_l_ancrage_aussitot()
    {
        var dialer = new TimedDialer(new() { ["ancre.ch"] = [1] }, failing: ["ouvert1.ch", "ouvert2.ch"]);

        var match = await PeerConnector.AnnounceInCirclesAsync(
            dialer, [At("ouvert1.ch"), At("ouvert2.ch")], [At("ancre.ch")], Some(),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("ancre.ch", match!.Value.At.Host);
        Assert.True(dialer.Asked.Single(asked => asked.Host == "ancre.ch").At < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Sans_cercle_ouvert_l_ancrage_part_aussitot()
    {
        var dialer = new TimedDialer(new() { ["ancre.ch"] = [1] });

        var match = await PeerConnector.AnnounceInCirclesAsync(
            dialer, [], [At("ancre.ch")], Some(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal("ancre.ch", match!.Value.At.Host);
        Assert.True(dialer.Asked.Single().At < TimeSpan.FromSeconds(2));
    }

    private static Func<string, CancellationToken, Task<System.Net.IPAddress[]>> Resolving(params string[] addresses)
        => (_, _) => Task.FromResult(addresses.Select(System.Net.IPAddress.Parse).ToArray());

    [Fact]
    public async Task Un_nom_ouvert_qui_ne_mene_qu_au_reseau_local_n_est_pas_joint()
        => Assert.Null(await PeerConnector.PublicHostAsync(
            "rdv.piege.ch", Resolving("192.168.1.1", "127.0.0.1", "fd00::1"), ServiceConsensus.IsPublicAddress, CancellationToken.None));

    [Fact]
    public async Task Un_nom_ouvert_mene_a_sa_premiere_adresse_publique()
        => Assert.Equal("83.228.242.221", await PeerConnector.PublicHostAsync(
            "rdv.ami.ch", Resolving("10.0.0.5", "83.228.242.221"), ServiceConsensus.IsPublicAddress, CancellationToken.None));

    [Fact]
    public async Task Une_adresse_litterale_privee_n_est_pas_jointe()
        => Assert.Null(await PeerConnector.PublicHostAsync(
            "127.0.0.1", Resolving(), ServiceConsensus.IsPublicAddress, CancellationToken.None));

    [Fact]
    public async Task Nos_propres_candidats_renvoyes_ne_comptent_pas_comme_un_appariement()
    {
        // Un service d'avant le correctif apparie nos deux annonces entre
        // elles quand il est dans les deux cercles sous deux noms : il nous
        // renvoie alors notre propre bloc, qui n'est pas un pair.
        var dialer = new TimedDialer(new() { ["alias.ch"] = [9] });

        var match = await PeerConnector.AnnounceInCirclesAsync(
            dialer, [At("alias.ch")], [], Some(), TimeSpan.Zero, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task Un_service_des_deux_cercles_n_est_annonce_qu_une_fois()
    {
        var dialer = new TimedDialer([]);

        var match = await PeerConnector.AnnounceInCirclesAsync(
            dialer, [At("rdv.x.ch")], [new RendezvousAddress("RDV.X.CH", 47900)], Some(),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(match);
        Assert.Single(dialer.Asked);
    }

    [Fact]
    public async Task Tous_les_lieux_sont_essayes_en_meme_temps()
    {
        // En séquence, deux personnes en ligne se manqueraient : l'une sur le
        // premier service pendant que l'autre est sur le second.
        var dialer = new ScriptedDialer(new() { ["rapide.ch"] = [1, 2, 3] });

        var result = await PeerConnector.AnnounceEverywhereAsync(
            dialer,
            [new RendezvousAddress("muet.ch", 47900), new RendezvousAddress("rapide.ch", 47900)],
            Some(),
            TimeSpan.FromSeconds(5),
            default);

        Assert.NotNull(result);
        Assert.Equal("rapide.ch", result!.Value.At.Host);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Value.Theirs);
        Assert.Equal(2, dialer.Asked.Count);
    }

    [Fact]
    public async Task Le_lieu_qui_apparie_est_rendu_pour_le_relais()
    {
        // Le relais doit passer par le service que les deux ont atteint.
        var dialer = new ScriptedDialer(new() { ["seul.ch"] = [7] });

        var result = await PeerConnector.AnnounceEverywhereAsync(
            dialer, [new RendezvousAddress("seul.ch", 443)], Some(), TimeSpan.FromSeconds(5), default);

        Assert.Equal(443, result!.Value.At.Port);
    }

    [Fact]
    public async Task Aucun_lieu_joignable_rend_null_sans_lever()
    {
        var dialer = new ScriptedDialer([]);

        var result = await PeerConnector.AnnounceEverywhereAsync(
            dialer, [new RendezvousAddress("muet.ch", 47900)], Some(),
            TimeSpan.FromMilliseconds(200), default);

        Assert.Null(result);
    }

    [Fact]
    public async Task Une_liste_vide_rend_null_sans_rien_demander()
    {
        var dialer = new ScriptedDialer([]);

        Assert.Null(await PeerConnector.AnnounceEverywhereAsync(
            dialer, [], Some(), TimeSpan.FromSeconds(1), default));

        Assert.Empty(dialer.Asked);
    }

    [Fact]
    public async Task Un_service_qui_leve_n_empeche_pas_les_autres()
    {
        // Un service injoignable n'est pas une erreur : c'est précisément ce à
        // quoi sert d'en avoir plusieurs.
        var dialer = new ThrowingDialer();

        var result = await PeerConnector.AnnounceEverywhereAsync(
            dialer,
            [new RendezvousAddress("casse.ch", 47900), new RendezvousAddress("bon.ch", 47900)],
            Some(), TimeSpan.FromSeconds(5), default);

        Assert.Equal("bon.ch", result!.Value.At.Host);
    }

    private static RelayPlace Relay(string host) => new(new RendezvousAddress(host, 47900), null, true);

    [Fact]
    public async Task Le_relais_choisi_sert_quand_il_accepte()
    {
        var relays = new ScriptedRelays(new() { ["proche.us"] = "accepte" });

        var (link, via) = await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), null, CancellationToken.None);

        Assert.NotNull(link);
        Assert.Equal("proche.us", via.Host);
        Assert.Equal([("proche.us", PeerConnector.ChosenRelayBudget)], relays.Opened);
    }

    [Fact]
    public async Task Un_refus_se_replie_sur_le_service_d_appariement_et_se_retient()
    {
        var relays = new ScriptedRelays(new() { ["proche.us"] = "refuse", ["suisse.ch"] = "accepte" });
        var refused = new List<RendezvousAddress>();

        var (link, via) = await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), refused.Add, CancellationToken.None);

        Assert.NotNull(link);
        Assert.Equal("suisse.ch", via.Host);
        Assert.Equal([("proche.us", PeerConnector.ChosenRelayBudget), ("suisse.ch", PeerConnector.FallbackRelayBudget)], relays.Opened);
        Assert.Equal("proche.us", Assert.Single(refused).Host);
    }

    [Fact]
    public async Task Un_silence_se_replie_sans_retenir_de_refus()
    {
        var relays = new ScriptedRelays(new() { ["suisse.ch"] = "accepte" });
        var refused = new List<RendezvousAddress>();

        var (link, _) = await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), refused.Add, CancellationToken.None);

        Assert.NotNull(link);
        Assert.Empty(refused);
    }

    [Fact]
    public async Task Quand_le_choix_est_le_service_d_appariement_un_seul_essai_suffit()
    {
        var relays = new ScriptedRelays(new() { ["suisse.ch"] = "accepte" });

        await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("suisse.ch"), Relay("suisse.ch"), new byte[16], new SilentLog(), null, CancellationToken.None);

        Assert.Equal([("suisse.ch", PeerConnector.RelayBudget)], relays.Opened);
    }

    [Fact]
    public void Les_budgets_tiennent_dans_l_attente_du_service()
    {
        // Un côté qui échoue aussitôt attend au service d'appariement ; l'autre
        // l'y rejoint au bout du budget du relais choisi. Il faut que ce soit
        // avant que le service ne lâche la première demande.
        Assert.True(PeerConnector.ChosenRelayBudget < PeerConnector.FallbackRelayBudget);
        Assert.True(PeerConnector.FallbackRelayBudget < TimeSpan.FromSeconds(30));
        Assert.True(PeerConnector.ChosenRelayBudget + TimeSpan.FromSeconds(1) < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Les_mesures_scellees_se_relisent()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        RelayMeasurement[] measures = [new(7, 12)];

        var sealedBlock = PeerConnector.SealCandidates(secret, [], measures);

        Assert.True(PeerConnector.TryOpenCandidates(secret, sealedBlock, out var plain));
        Assert.True(CandidateSet.TryDecode(plain, out _, out var consumed, out _));
        Assert.Equal(measures, RelayMeasurements.TryRead(plain.AsSpan(consumed)));
    }

    [Fact]
    public async Task Une_annulation_pendant_le_relais_choisi_ne_tente_pas_le_repli()
    {
        using var stop = new CancellationTokenSource();
        var relays = new CancellingRelays(stop);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), null, stop.Token));

        Assert.Equal(["proche.us"], relays.Opened);
    }

    [Theory]
    [InlineData("relais coupé sur ce service", true)]
    [InlineData("trop de demandes de relais", false)]
    [InlineData("autre erreur", false)]
    [InlineData(null, false)]
    public async Task Seul_un_relais_coupe_est_retenu_comme_refus(string? error, bool remembered)
    {
        // Un vrai client contre deux faux services en boucle locale. Le choisi
        // répond l'erreur, ou coupe sans répondre quand il n'y en a pas ; celui
        // d'appariement coupe toujours, pour que le repli finisse aussitôt.
        using var chosenService = new FakeRelayService(error);
        using var matchedService = new FakeRelayService(null);

        using var links = new PeerLinkFactory(2, new SilentLog());
        var connector = new PeerConnector(links, new RendezvousEndpoint("127.0.0.1", chosenService.Port), new MovableClock(), new SilentLog());
        var refused = new List<RendezvousAddress>();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (link, _) = await PeerConnector.RelayWithFallbackAsync(
            connector, chosenService.Place, matchedService.Place, new byte[RendezvousTicket.SizeInBytes],
            new SilentLog(), refused.Add, bounded.Token);

        Assert.Null(link);
        Assert.Equal(remembered ? 1 : 0, refused.Count);
        Assert.Equal(1, matchedService.Served);
    }

    /// <summary>Un service qui lit une demande, répond une erreur ou rien, puis coupe.</summary>
    private sealed class FakeRelayService : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _served;

        public FakeRelayService(string? error)
        {
            _listener.Start();
            _ = ServeAsync(error);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Served => Volatile.Read(ref _served);

        public RelayPlace Place => new(new RendezvousAddress("127.0.0.1", Port), null, false);

        private async Task ServeAsync(string? error)
        {
            try
            {
                while (true)
                {
                    using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = socket.GetStream();
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    await stream.ReadExactlyAsync(new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header)], _stop.Token);

                    if (error is not null)
                        await stream.WriteAsync(RendezvousWire.Frame(RendezvousWire.Error(error)), _stop.Token);

                    Interlocked.Increment(ref _served);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // Arrêté par le test.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    [Fact]
    public void La_reponse_du_service_se_lit_en_trois_issues()
    {
        Assert.Equal(RelayAnswer.Ready, RendezvousClient.RelayAnswerOf([RendezvousKind.RelayReady]));
        Assert.Equal(RelayAnswer.Disabled, RendezvousClient.RelayAnswerOf(RendezvousWire.Error(RendezvousWire.RelayDisabledReason)));
        Assert.Equal(RelayAnswer.Unavailable, RendezvousClient.RelayAnswerOf(RendezvousWire.Error("trop de demandes de relais")));
        Assert.Equal(RelayAnswer.Unavailable, RendezvousClient.RelayAnswerOf(null));
        Assert.Equal(RelayAnswer.Unavailable, RendezvousClient.RelayAnswerOf([RendezvousKind.Matched]));
    }

    /// <summary>Annule pendant le premier essai, puis rend un silence, comme un ouvreur qui avalerait l'annulation.</summary>
    private sealed class CancellingRelays(CancellationTokenSource stop) : IRelayOpener
    {
        public List<string> Opened { get; } = [];

        public async Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct)
        {
            Opened.Add(place.At.Host);
            await stop.CancelAsync();
            return new RelayOpening(null, false);
        }
    }

    private sealed class ThrowingDialer : IRendezvousDialer
    {
        public Task<byte[]?> AnnounceAsync(
            RendezvousAddress at, Announcement announcement, CancellationToken ct)
            => at.Host is "casse.ch"
                ? throw new IOException("connexion refusée")
                : Task.FromResult<byte[]?>([4, 2]);
    }
}

/// <summary>Le bloc de candidats, que le rendez-vous voit passer sans devoir le lire.</summary>
public class CandidateSealingTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static readonly System.Net.IPEndPoint[] Candidates =
    [
        new(System.Net.IPAddress.Parse("203.0.113.7"), 51000),
        new(System.Net.IPAddress.Parse("192.168.1.20"), 51000),
    ];

    [Fact]
    public void Un_bloc_scelle_se_rouvre_avec_le_meme_secret()
    {
        var sealedBlock = PeerConnector.SealCandidates(Secret, Candidates);

        Assert.True(PeerConnector.TryOpenCandidates(Secret, sealedBlock, out var plain));
        Assert.True(CandidateSet.TryDecode(plain, out var decoded, out _));
        Assert.Equal(Candidates, decoded);
    }

    [Fact]
    public void Sans_mesures_le_clair_est_celui_d_avant()
    {
        // Un pair d'avant les mesures lit ce bloc : rien ne doit suivre les adresses.
        Assert.True(PeerConnector.TryOpenCandidates(Secret, PeerConnector.SealCandidates(Secret, Candidates), out var plain));
        Assert.Equal(CandidateSet.Encode(Candidates), plain);
    }

    [Fact]
    public void Deux_scellements_n_emploient_jamais_le_meme_nonce()
    {
        // La version 1 scellait sous un nonce nul et une clé fixe par paire :
        // chaque annonce, des deux côtés, réemployait le même couple.
        var first = PeerConnector.SealCandidates(Secret, Candidates);
        var second = PeerConnector.SealCandidates(Secret, Candidates);

        Assert.NotEqual(first[..12], second[..12]);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Un_autre_secret_ne_rouvre_pas_le_bloc()
    {
        var other = (byte[])Secret.Clone();
        other[0] ^= 1;

        Assert.False(PeerConnector.TryOpenCandidates(other, PeerConnector.SealCandidates(Secret, Candidates), out _));
    }

    [Fact]
    public void Un_bloc_trop_court_est_refuse_sans_lever()
    {
        Assert.False(PeerConnector.TryOpenCandidates(Secret, new byte[20], out _));
    }
}
