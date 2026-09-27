using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Harness;

public sealed record OpenCircleSettings(
    IReadOnlyList<RendezvousAddress> Open, RendezvousAddress Anchor, RendezvousAddress Authority,
    byte[] AuthorityKey, int TimeoutSeconds);

/// <summary>Journal de console qui compte les appariements passés par le cercle ouvert.</summary>
internal sealed class CountingLog(string who) : ILogSink
{
    private readonly ConsoleLog _inner = new(who);
    private int _open;
    private readonly List<string> _relays = [];
    private readonly List<string> _matches = [];

    public int OpenMatches => Volatile.Read(ref _open);

    /// <summary>Les services par lesquels un relais s'est ouvert, en « hôte:port ».</summary>
    public IReadOnlyList<string> Relays
    {
        get
        {
            lock (_relays)
                return [.. _relays];
        }
    }

    /// <summary>Les services qui ont apparié, en « hôte:port » canonique.</summary>
    public IReadOnlyList<string> Matches
    {
        get
        {
            lock (_matches)
                return [.. _matches];
        }
    }

    public void Debug(string message) => _inner.Debug(message);

    public void Info(string message)
    {
        if (message.Contains("(réseau ouvert)", StringComparison.Ordinal))
            Interlocked.Increment(ref _open);

        const string opened = "Relais ouvert par ";

        if (message.StartsWith(opened, StringComparison.Ordinal))
            lock (_relays)
                _relays.Add(message[opened.Length..].TrimEnd('.'));

        const string matched = " : apparié sur ";
        var at = message.IndexOf(matched, StringComparison.Ordinal);

        if (at >= 0)
        {
            var service = message[(at + matched.Length)..].Replace(" (réseau ouvert)", "", StringComparison.Ordinal).TrimEnd('.');

            lock (_matches)
                _matches.Add(RendezvousAddress.TryParse(service, out var parsed, out _) ? ServiceConsensus.Canonical(parsed) : service);
        }

        _inner.Info(message);
    }

    public void Warning(string message, Exception? exception = null) => _inner.Warning(message, exception);
}

/// <summary>
/// La preuve que le cercle ouvert marche, et que son repli aussi.
/// </summary>
/// <remarks>
/// Les services tournent à côté, lancés à la main, comme pour la fédération.
/// Une autorité réelle ne liste personne avant soixante-douze heures : sa
/// liste n'est donc vérifiée qu'en signature. Les deux scénarios suivants
/// emploient une liste signée par une clé du harnais, qu'un <see cref="OpenCircle"/>
/// accepte comme il accepterait celle de l'autorité.
///
/// Le second scénario ne liste que des services morts : l'apparence doit
/// passer quand même, par l'ancrage. Un cercle ouvert qui deviendrait une
/// dépendance ferait échouer ce cas.
/// </remarks>
public static class OpenCircleRun
{
    public static async Task<bool> ExecuteAsync(OpenCircleSettings settings, CancellationToken ct)
    {
        Console.WriteLine($"Cercle ouvert : {string.Join(", ", settings.Open)}");
        Console.WriteLine($"Ancrage : {settings.Anchor}");
        Console.WriteLine($"Autorité : {settings.Authority}");
        Console.WriteLine();

        var ok = await CheckAuthorityAsync(settings, ct).ConfigureAwait(false);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        ok &= await ScenarioAsync(
            "cercle ouvert", Sign(key, settings.Open), key, settings, expectOpen: true, ct).ConfigureAwait(false);

        ok &= await ScenarioAsync(
            "repli sur l'ancrage",
            Sign(key, [new RendezvousAddress("127.0.0.1", 47998), new RendezvousAddress("127.0.0.1", 47997)]),
            key, settings, expectOpen: false, ct).ConfigureAwait(false);

        ok &= await ScenarioAsync(
            "relais le plus proche",
            SignWithRegions(key, [.. settings.Open, new RendezvousAddress("127.0.0.1", 47996)]),
            key, settings, expectOpen: true, ct,
            nearestRelay: settings.Open[0]).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(ok ? "TOUT EST PASSÉ" : "ÉCHEC : voir ci-dessus.");
        return ok;
    }

    private static async Task<bool> CheckAuthorityAsync(OpenCircleSettings settings, CancellationToken ct)
    {
        Console.WriteLine("── liste de l'autorité ──");

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));

            await using var client = new RendezvousClient();
            await client.ConnectAsync(settings.Authority.Host, settings.Authority.Port, deadline.Token).ConfigureAwait(false);

            var (document, failure) = await client.QueryConsensusAsync(deadline.Token).ConfigureAwait(false);

            if (document is null)
            {
                Console.WriteLine($"   ÉCHEC : {failure}");
                return false;
            }

            if (ServiceConsensus.TryVerify(
                    document, [settings.AuthorityKey], DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var list, out var why) is false)
            {
                Console.WriteLine($"   ÉCHEC : {why}");
                return false;
            }

            Console.WriteLine($"   liste de l'autorité vérifiée : version {list!.Version}, {list.Entries.Count} service(s)");
            return true;
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            Console.WriteLine($"   ÉCHEC : autorité injoignable ({e.Message})");
            return false;
        }
    }

    private static byte[] Sign(ECDsa key, IReadOnlyList<RendezvousAddress> services)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entries = services
            .Select((at, i) => new ConsensusEntry(ServiceConsensus.Canonical(at), $"ouvert {i + 1}", [.. Enumerable.Repeat((byte)(i + 1), 8)]))
            .ToList();

        return ServiceConsensus.Sign(
            new ServiceConsensus(1, issued, issued + (long)ServiceConsensus.Lifetime.TotalSeconds, entries), key);
    }

    /// <summary>
    /// Une liste v2 : les deux premiers services en Europe, les suivants en
    /// Amérique. L'Europe compte deux familles, donc un tirage régional.
    /// </summary>
    /// <remarks>
    /// Le quatrième service, éteint, n'est là que pour que le placement puisse
    /// retenir deux services sans le premier : avec trois familles seulement,
    /// le meilleur d'Europe est toujours aussi l'un des deux meilleurs scores,
    /// donc un lieu d'annonce qui peut gagner l'appariement.
    /// </remarks>
    private static byte[] SignWithRegions(ECDsa key, IReadOnlyList<RendezvousAddress> services)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[] regions = ["EU", "EU", "NA", "NA"];
        var entries = services
            .Select((at, i) => new ConsensusEntry(
                ServiceConsensus.Canonical(at), $"ouvert {i + 1}", [.. Enumerable.Repeat((byte)(i + 1), 8)], regions[i % regions.Length]))
            .ToList();

        return ServiceConsensus.SignV2(
            new ServiceConsensus(1, issued, issued + (long)ServiceConsensus.Lifetime.TotalSeconds, entries), key);
    }

    private static async Task<bool> ScenarioAsync(
        string name, byte[] document, ECDsa key, OpenCircleSettings settings, bool expectOpen, CancellationToken ct,
        RendezvousAddress? nearestRelay = null)
    {
        Console.WriteLine($"── {name} ──");

        var clock = new RealClock();
        var root = Path.Combine(Path.GetTempPath(), "linkpearl-cercle-" + Guid.NewGuid().ToString("N"));

        using var aliceIdentity = CryptoPrimitives.GenerateIdentity();
        using var bobIdentity = CryptoPrimitives.GenerateIdentity();

        var alicePublic = CryptoPrimitives.ExportPublicPoint(aliceIdentity);
        var bobPublic = CryptoPrimitives.ExportPublicPoint(bobIdentity);
        var aliceId = PeerId.Of(alicePublic);
        var bobId = PeerId.Of(bobPublic);

        var aliceStore = new FileSystemBlobStore(
            Path.Combine(root, "alice"), new CacheSettings(), clock, _ => long.MaxValue);
        var bobStore = new FileSystemBlobStore(
            Path.Combine(root, "bob"), new CacheSettings(), clock, _ => long.MaxValue);

        var manifest = await FederationRun.TinyAppearanceAsync(aliceStore, ct).ConfigureAwait(false);
        // Les services « ouverts » tournent sur la boucle locale, que le client
        // refuse d'ordinaire pour un lieu du cercle ouvert : le harnais lève ce
        // filtre, et lui seul.
        //
        // Chacun son cercle, comme deux plugins distincts qui auraient reçu la
        // même liste de l'autorité.
        var aliceCircle = new OpenCircle([ServiceConsensus.PublicPoint(key)], clock);
        var bobCircle = new OpenCircle([ServiceConsensus.PublicPoint(key)], clock);

        if (aliceCircle.Offer(document, out var why) is false || bobCircle.Offer(document, out why) is false)
        {
            Console.WriteLine($"   ÉCHEC : liste du harnais refusée ({why})");
            return false;
        }

        IReadOnlyList<RendezvousAddress> anchor = [settings.Anchor];
        var pairSecret = RandomNumberGenerator.GetBytes(32);

        // Le secret de paire décide qui est éligible au relais, la latence ne
        // fait que départager : un service le plus proche mais non éligible ne
        // serait jamais retenu. Et s'il était éligible par le placement, il
        // serait aussi lieu d'annonce, pourrait gagner l'appariement, et le
        // relais y resterait sans rien prouver. On tire donc un secret qui ne
        // le rende éligible que par le tirage régional.
        const int maxDraws = 10_000;
        var draws = 0;

        bool OnlyRegional(byte[] secret, RendezvousAddress target)
        {
            var entries = aliceCircle.RelayEntriesFor(FederationRun.Pair(bobId, bobPublic, secret, "Bob", anchor));

            return ServicePlacement.Choose(secret, entries).Contains(target) is false
                && RelayPlacement.Eligible(secret, entries, anchor).Any(place => place.At == target);
        }

        while (nearestRelay is { } target && OnlyRegional(pairSecret, target) is false)
        {
            if (++draws >= maxDraws)
            {
                Console.WriteLine($"   ÉCHEC : aucun secret de paire en {maxDraws} tirages ne rend {target} éligible par sa seule région");
                return false;
            }

            pairSecret = RandomNumberGenerator.GetBytes(32);
        }

        var aliceBook = new PairBook(clock);
        // Seule Alice met Bob en relais seul : elle n'envoie alors que des RTT
        // synthétiques, 0 pour l'Europe, dont le plus proche est nearestRelay.
        // Bob envoie de vrais RTT. Les deux en relais seul donneraient 0 à
        // deux services européens, et l'empreinte trancherait au hasard.
        var bobForAlice = FederationRun.Pair(bobId, bobPublic, pairSecret, "Bob", anchor);
        aliceBook.Load([nearestRelay is null ? bobForAlice : bobForAlice with { Policy = ConnectionPolicy.RelayOnly }]);

        var bobBook = new PairBook(clock);
        bobBook.Load([FederationRun.Pair(aliceId, alicePublic, pairSecret, "Alice", anchor)]);

        // Tout service autre que nearestRelay paraît 200 ms plus loin : sur la
        // boucle locale, les vrais RTT sont trop proches pour départager.
        RelayLatencies? Latencies() => nearestRelay is not { } near ? null : new RelayLatencies(clock, async (place, timeout, token) =>
        {
            var rtt = await RelayPing.PingAsync(place, _ => true, timeout, token).ConfigureAwait(false);
            return rtt is null ? null : place.At == near ? rtt : rtt + TimeSpan.FromMilliseconds(200);
        });

        var aliceLatencies = Latencies();
        var bobLatencies = Latencies();
        var places = RelayPlacement.Eligible(pairSecret, aliceCircle.RelayEntriesFor(aliceBook.Listed[0]), anchor);

        if (aliceLatencies is not null)
            await aliceLatencies.RefreshAsync(places, ct, TimeSpan.Zero).ConfigureAwait(false);

        if (bobLatencies is not null)
            await bobLatencies.RefreshAsync(places, ct, TimeSpan.Zero).ConfigureAwait(false);

        var engineSettings = new SyncEngineSettings { DataChannels = 4 };

        using var aliceLinks = new PeerLinkFactory(engineSettings.DataChannels + 1, new ConsoleLog("Alice"));
        using var bobLinks = new PeerLinkFactory(engineSettings.DataChannels + 1, new ConsoleLog("Bob"));

        using var polling = new CancellationTokenSource();
        var pumps = new[] { FederationRun.Poll(aliceLinks, polling.Token), FederationRun.Poll(bobLinks, polling.Token) };

        var alicePrint = PlayerFingerprint.Of("alice", 21);
        var bobPrint = PlayerFingerprint.Of("bob", 21);

        var aliceLog = new CountingLog("Alice");
        var bobLog = new CountingLog("Bob");
        var narrator = new NarratingApplicator(bobStore);

        await using var alice = new SyncEngine(
            aliceBook,
            new PeerConnector(aliceLinks, FederationRun.Endpoint(settings.Anchor), clock, aliceLog, circle: aliceCircle, acceptOpenAddress: _ => true,
                latencies: aliceLatencies),
            new StaticAppearance(manifest, alicePrint), new NarratingApplicator(aliceStore),
            aliceStore, aliceId, aliceIdentity, clock, new ConsoleLog("Alice"), engineSettings);

        await using var bob = new SyncEngine(
            bobBook,
            new PeerConnector(bobLinks, FederationRun.Endpoint(settings.Anchor), clock, bobLog, circle: bobCircle, acceptOpenAddress: _ => true,
                latencies: bobLatencies),
            new StaticAppearance(null, bobPrint), narrator,
            bobStore, bobId, bobIdentity, clock, new ConsoleLog("Bob"), engineSettings);

        IReadOnlyList<VisiblePlayer> aliceSees = [new VisiblePlayer(new GameObjectRef(7, 0xB0B), bobPrint)];
        IReadOnlyList<VisiblePlayer> bobSees = [new VisiblePlayer(new GameObjectRef(4, 0xA11CE), alicePrint)];

        var deadline = DateTime.UtcNow.AddSeconds(settings.TimeoutSeconds);

        while (narrator.Applications == 0 && DateTime.UtcNow < deadline && ct.IsCancellationRequested is false)
        {
            await alice.TickAsync(aliceSees, ct).ConfigureAwait(false);
            await bob.TickAsync(bobSees, ct).ConfigureAwait(false);
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        await polling.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(pumps).ConfigureAwait(false);

        var applied = narrator.Applications > 0;
        var viaOpen = aliceLog.OpenMatches + bobLog.OpenMatches > 0;
        // Le relais doit avoir quitté le service d'appariement pour le plus
        // proche, des deux côtés : un relais resté là où l'on s'est apparié ne
        // prouverait rien du choix.
        var matches = aliceLog.Matches.Concat(bobLog.Matches).Distinct(StringComparer.Ordinal).ToList();
        var relays = aliceLog.Relays.Concat(bobLog.Relays).Distinct(StringComparer.Ordinal).ToList();
        var nearestUsed = nearestRelay is not { } wanted
            || (relays.Count == 1 && relays[0] == ServiceConsensus.Canonical(wanted)
                && matches.Count > 0 && matches.Contains(relays[0], StringComparer.Ordinal) is false);

        if (nearestRelay is not null)
            Console.WriteLine($"   appariement sur {string.Join(", ", matches)}, relais par {string.Join(", ", relays)}");

        var good = applied && viaOpen == expectOpen && nearestUsed;

        Console.WriteLine($"   {(applied ? "apparence posée" : "aucun appariement")}, "
                        + $"{(viaOpen ? "par le cercle ouvert" : "par l'ancrage")}"
                        + (nearestRelay is null ? "" : nearestUsed ? ", relais déplacé vers le service le plus proche" : ", relais non déplacé vers le plus proche")
                        + $", attendu : posée {(expectOpen ? "par le cercle ouvert" : "par l'ancrage")} → {(good ? "conforme" : "ÉCHEC")}");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }

        return good;
    }
}
