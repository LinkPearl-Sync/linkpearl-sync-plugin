using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Transport;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Où joindre le service de rendez-vous.</summary>
public sealed record RendezvousEndpoint(string Host, int Port);

/// <summary>Ce qui sait s'annoncer auprès d'un service de rendez-vous.</summary>
/// <remarks>
/// Abstrait pour que le choix du lieu se teste sans réseau. L'implémentation
/// réelle ouvre un <see cref="RendezvousClient"/> et attend l'appariement.
/// </remarks>
public interface IRendezvousDialer
{
    Task<byte[]?> AnnounceAsync(RendezvousAddress at, Announcement announcement, CancellationToken ct);
}

/// <summary>Ce qu'une tentative de connexion a donné, et par quel lieu elle est passée.</summary>
public sealed record ConnectionAttempt(IPeerLink? Link, bool PeerWasAbsent, string? Failure, RendezvousAddress? Via = null);

/// <summary>Ce qu'a donné une demande de relais : un lien, ou un refus explicite.</summary>
/// <remarks>Un silence n'est pas un refus : le pair a pu se replier ailleurs avant nous.</remarks>
public sealed record RelayOpening(IPeerLink? Link, bool Refused);

/// <summary>Ouvre un relais sur un service, dans un budget donné.</summary>
public interface IRelayOpener
{
    Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct);
}

/// <summary>
/// Enchaîne ce qu'il faut pour joindre un pair : réflexion, annonce, perçage, relais.
/// </summary>
/// <remarks>
/// Le rendez-vous n'intervient que pour échanger des adresses, et il ne les lit
/// même pas : le bloc de candidats est scellé sous une clé dérivée du secret de
/// paire. Il ne peut donc pas apprendre les adresses locales d'un réseau
/// domestique, seulement les adresses publiques qu'il voit de toute façon.
/// </remarks>
public sealed class PeerConnector(
    PeerLinkFactory links, RendezvousEndpoint rendezvous, IClock clock, ILogSink log,
    TimeSpan? announceBudget = null, IOpenCircle? circle = null, Func<IPAddress, bool>? acceptOpenAddress = null,
    RelayLatencies? latencies = null) : IPeerDialer, IRelayOpener
{
    /// <summary>
    /// L'adresse à joindre pour un nom du cercle ouvert, ou null s'il ne mène qu'au réseau local.
    /// </summary>
    /// <remarks>
    /// On se connecte à l'adresse filtrée et non au nom, pour qu'une seconde
    /// résolution ne puisse pas rendre autre chose que ce qui a été vérifié.
    /// </remarks>
    public static async Task<string?> PublicHostAsync(
        string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, bool> accept, CancellationToken ct)
    {
        IPAddress[] resolved = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await resolve(host, ct).ConfigureAwait(false);

        return resolved
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .FirstOrDefault(accept)?
            .ToString();
    }

    /// <summary>
    /// Combien de temps une annonce reste tenue au rendez-vous.
    /// </summary>
    /// <remarks>
    /// Le service n'apparie que deux annonces présentes en même temps. À cinq
    /// secondes d'attente pour trente entre deux essais, deux pairs pourtant en
    /// ligne se manquaient presque toujours, chacun annonçant pendant que
    /// l'autre patientait. Tenue vingt-cinq secondes sur trente, l'annonce de
    /// l'un couvre forcément un essai de l'autre.
    /// </remarks>
    public static readonly TimeSpan DefaultAnnounceBudget = TimeSpan.FromSeconds(25);

    private readonly TimeSpan _announceBudget = announceBudget ?? DefaultAnnounceBudget;

    /// <summary>
    /// L'avance laissée au cercle ouvert avant d'annoncer aussi sur l'ancrage.
    /// </summary>
    /// <remarks>
    /// Assez pour que deux clients récents s'y trouvent, et que l'ancrage ne
    /// voie pas passer leur annonce. Trop peu pour qu'un client d'avant le
    /// cercle ouvert, qui n'annonce que sur l'ancrage, soit manqué : les
    /// annonces ouvertes restent tenues tout le budget, et celles de l'ancrage
    /// en couvrent encore quinze secondes.
    /// </remarks>
    public static readonly TimeSpan OpenHead = TimeSpan.FromSeconds(10);

    private static ReadOnlySpan<byte> CandidateKeyInfo => "linkpearl:candidates:v2"u8;
    private static ReadOnlySpan<byte> TokenInfo => "linkpearl:token:v1"u8;
    private static ReadOnlySpan<byte> RelayInfo => "linkpearl:relay:v1"u8;

    /// <summary>Temps laissé au perçage avant de se rabattre sur le relais.</summary>
    private static readonly TimeSpan PunchBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Attente du pair au relais.
    /// </summary>
    /// <remarks>
    /// Sous les trente secondes que le service accorde, pour que ce soit nous
    /// qui abandonnions et non lui qui nous coupe sans rien dire.
    /// </remarks>
    public static readonly TimeSpan RelayBudget = TimeSpan.FromSeconds(20);

    /// <summary>Attente au relais choisi, quand ce n'est pas le service d'appariement.</summary>
    /// <remarks>
    /// Courte : le service garde une demande trente secondes, et celui des deux
    /// pairs qui échoue vite attend déjà l'autre au service d'appariement.
    /// </remarks>
    public static readonly TimeSpan ChosenRelayBudget = TimeSpan.FromSeconds(8);

    /// <summary>Attente au service d'appariement après l'échec du relais choisi.</summary>
    public static readonly TimeSpan FallbackRelayBudget = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Relaie par le service choisi, et à défaut par celui qui a apparié.
    /// </summary>
    /// <remarks>
    /// Les deux côtés ont décidé le même service ; s'il échoue pour l'un, il
    /// échoue d'ordinaire pour l'autre, et les deux se retrouvent au service
    /// d'appariement, que tous deux ont déjà atteint.
    /// </remarks>
    public static async Task<(IPeerLink? Link, RendezvousAddress Via)> RelayWithFallbackAsync(
        IRelayOpener opener, RelayPlace chosen, RelayPlace matched, byte[] ticket, ILogSink log,
        Action<RendezvousAddress>? refused, CancellationToken ct)
    {
        if (chosen.Fingerprint == matched.Fingerprint)
            return Opened((await opener.OpenAsync(matched, ticket, RelayBudget, ct).ConfigureAwait(false)).Link, matched.At, log);

        var first = await opener.OpenAsync(chosen, ticket, ChosenRelayBudget, ct).ConfigureAwait(false);

        if (first.Link is not null)
            return Opened(first.Link, chosen.At, log);

        // Une annulation pendant le premier essai arrête tout : le repli
        // ouvrirait une connexion de plus sous un jeton déjà annulé.
        ct.ThrowIfCancellationRequested();

        if (first.Refused)
            refused?.Invoke(chosen.At);

        log.Info($"Relais choisi {ServiceConsensus.Canonical(chosen.At)} {(first.Refused ? "refusé" : "sans réponse")}, "
                 + $"repli sur {ServiceConsensus.Canonical(matched.At)}.");

        return Opened((await opener.OpenAsync(matched, ticket, FallbackRelayBudget, ct).ConfigureAwait(false)).Link, matched.At, log);
    }

    private static (IPeerLink? Link, RendezvousAddress Via) Opened(IPeerLink? link, RendezvousAddress via, ILogSink log)
    {
        if (link is not null)
            log.Info($"Relais ouvert par {ServiceConsensus.Canonical(via)}.");

        return (link, via);
    }

    /// <summary>
    /// Le jeton qu'un pair doit présenter pour ouvrir une session avec nous.
    /// </summary>
    /// <remarks>
    /// Dérivé du secret de paire, donc inconnu de quiconque n'est pas ce pair.
    /// Il n'authentifie rien par lui-même, le handshake s'en charge : il évite
    /// seulement qu'un inconnu qui a trouvé notre adresse puisse ouvrir des
    /// sessions à volonté.
    /// </remarks>
    public static string TokenFor(ReadOnlySpan<byte> pairSecret)
    {
        var token = new byte[16];
        HKDF.Expand(HashAlgorithmName.SHA256, pairSecret, token, TokenInfo);
        return Convert.ToHexStringLower(token);
    }

    /// <summary>
    /// S'annonce sur tous les lieux à la fois, et rend le premier appariement.
    /// </summary>
    /// <remarks>
    /// En parallèle et non l'un après l'autre : en séquence, deux personnes
    /// pourtant en ligne se manquent dès qu'elles essaient les services dans un
    /// ordre différent, l'une attendant sur le premier pendant que l'autre
    /// attend sur le second.
    ///
    /// Le lieu rendu est celui qui a apparié : c'est par lui que passera le
    /// relais si le direct échoue, puisque c'est le seul que les deux ont
    /// atteint.
    /// </remarks>
    public static Task<(RendezvousAddress At, byte[] Theirs)?> AnnounceEverywhereAsync(
        IRendezvousDialer dialer, IReadOnlyList<RendezvousAddress> places,
        Announcement announcement, TimeSpan budget, CancellationToken ct)
        => AnnounceInCirclesAsync(dialer, places, [], announcement, TimeSpan.Zero, budget, ct);

    /// <summary>
    /// S'annonce sur le cercle ouvert tout de suite, et sur l'ancrage après
    /// <paramref name="head"/>, ou dès que tous les services ouverts ont
    /// échoué. Le premier appariement gagne.
    /// </summary>
    public static async Task<(RendezvousAddress At, byte[] Theirs)?> AnnounceInCirclesAsync(
        IRendezvousDialer dialer, IReadOnlyList<RendezvousAddress> open, IReadOnlyList<RendezvousAddress> anchor,
        Announcement announcement, TimeSpan head, TimeSpan budget, CancellationToken ct)
    {
        // Un service des deux cercles n'est annoncé qu'une fois, et côté
        // ouvert : deux annonces d'un même client chez lui s'apparieraient
        // entre elles.
        var openKeys = open.Select(ServiceConsensus.Canonical).ToHashSet(StringComparer.Ordinal);
        var fallback = anchor.Where(place => openKeys.Contains(ServiceConsensus.Canonical(place)) is false).ToList();

        if (open.Count == 0 && fallback.Count == 0)
            return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);

        var openExhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openLeft = new StrongBox<int>(open.Count);

        if (open.Count == 0)
            openExhausted.TrySetResult();

        async Task<(RendezvousAddress At, byte[] Theirs)?> TryAnnounceAsync(RendezvousAddress place, bool isOpen)
        {
            try
            {
                if (isOpen is false)
                {
                    await Task.WhenAny(Task.Delay(head, deadline.Token), openExhausted.Task).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                }

                var theirs = await dialer.AnnounceAsync(place, announcement, deadline.Token).ConfigureAwait(false);

                // Notre propre bloc, que son aléa rend unique : un service qui a
                // apparié nos deux annonces entre elles, pas un pair.
                if (theirs is null || theirs.AsSpan().SequenceEqual(announcement.SealedCandidates))
                    return null;

                return (place, theirs);
            }
            catch (Exception)
            {
                // Un service injoignable n'est pas une erreur : c'est
                // précisément ce à quoi sert d'en avoir plusieurs. Rattrapé ici
                // pour qu'aucune tâche ne se termine en faute, dont l'exception
                // resterait non observée après qu'une autre a gagné.
                return null;
            }
        }

        async Task<(RendezvousAddress At, byte[] Theirs)?> AttemptAsync(RendezvousAddress place, bool isOpen)
        {
            var result = await TryAnnounceAsync(place, isOpen).ConfigureAwait(false);

            // Seul un échec libère l'ancrage. Un appariement ouvert ne doit
            // pas le faire : l'ancrage partirait avant que l'annulation qui
            // suit la victoire ait eu le temps de l'arrêter.
            if (isOpen && result is null && Interlocked.Decrement(ref openLeft.Value) == 0)
                openExhausted.TrySetResult();

            return result;
        }

        var attempts = open.Select(place => AttemptAsync(place, isOpen: true))
            .Concat(fallback.Select(place => AttemptAsync(place, isOpen: false)))
            .ToList();

        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(finished);

            if (await finished.ConfigureAwait(false) is { } match)
            {
                // Le premier gagne : les autres n'ont plus lieu d'être, et leur
                // annulation ferme leurs connexions.
                await deadline.CancelAsync().ConfigureAwait(false);
                return match;
            }
        }

        return null;
    }

    public async Task<ConnectionAttempt> ConnectAsync(PairRecord pair, CancellationToken ct)
    {
        var open = circle?.PlacesFor(pair) ?? [];

        if (pair.Rendezvous.Count == 0 && open.Count == 0)
            return new ConnectionAttempt(null, false, "aucun service connu pour ce pair");

        // Un pair en relais seul ne reçoit aucune de nos adresses : c'est tout
        // l'objet de ce mode. Il nous trouve quand même, par le relais.
        var relayOnly = pair.Policy is ConnectionPolicy.RelayOnly;

        IReadOnlyList<IPEndPoint> candidates = relayOnly
            ? []
            : await GatherCandidatesAsync(ct).ConfigureAwait(false);

        // Les services où l'on pourrait relayer, et ce qu'on en mesure. Un pair
        // en relais seul ne livre que sa région : ses RTT vers plusieurs
        // continents laisseraient trianguler l'adresse que ce mode protège.
        var eligible = RelayPlacement.Eligible(pair.PairSecret, circle?.RelayEntriesFor(pair) ?? [], pair.Rendezvous);
        var measured = latencies?.MeasurementsFor(eligible);

        if (measured is not null && relayOnly)
            measured = RelayMeasurements.Synthetic(eligible, measured);

        var sealedCandidates = SealCandidates(pair.PairSecret, candidates, measured);

        var announcement = new Announcement(
            new RendezvousTicket(clock).Announce(pair.PairSecret), sealedCandidates);

        // Le pair n'est peut-être pas en ligne, et son absence n'est pas une
        // erreur. Mais on attend assez pour qu'il nous trouve s'il essaie.
        var dialer = new LiveDialer(open, acceptOpenAddress ?? ServiceConsensus.IsPublicAddress);

        var match = await AnnounceInCirclesAsync(
            dialer, open, pair.Rendezvous, announcement, OpenHead, _announceBudget, ct).ConfigureAwait(false);

        if (match is null)
        {
            // Deux silences très différents. Si un service nous a reçus, le pair
            // n'était simplement pas là et il reviendra. Si aucun ne nous a
            // reçus, la réparation est d'ajouter un service ou de se repairer,
            // et dire « hors ligne » enverrait attendre pour rien.
            return dialer.Reached > 0
                ? new ConnectionAttempt(null, true, null)
                : new ConnectionAttempt(null, false, "aucun service commun disponible");
        }

        var viaOpen = open.Contains(match.Value.At);
        log.Info($"{pair.LogTag} : apparié sur {match.Value.At}{(viaOpen ? " (réseau ouvert)" : "")}.");

        if (TryOpenCandidates(pair.PairSecret, match.Value.Theirs, out var plain) is false)
            return new ConnectionAttempt(
                null, false, "bloc de candidats illisible : secret de paire différent, ou pair à mettre à jour ?");

        if (CandidateSet.TryDecode(plain, out var theirCandidates, out var consumed, out var why) is false)
            return new ConnectionAttempt(null, false, $"candidats refusés : {why}");

        // Un pair d'avant les mesures n'a rien après ses adresses, et une
        // extension illisible vaut pour absente : dans les deux cas, on relaie
        // par le service d'appariement comme avant.
        var theirMeasures = RelayMeasurements.TryRead(plain.AsSpan(consumed));

        var ordered = CandidateSet.InPriorityOrder(theirCandidates);

        // Décidé sur ce que les deux côtés voient à l'identique, les deux jeux
        // de candidats : l'un tenterait sinon le perçage pendant que l'autre
        // l'attend déjà au relais, et chacun attendrait l'autre pour rien.
        if (candidates.Count > 0 && ordered.Count > 0)
        {
            log.Info($"{pair.LogTag} : {ordered.Count} adresse(s) à essayer.");

            var token = TokenFor(pair.PairSecret);
            links.Allow(token, pair.DisplayName);

            var link = await links.ConnectAsync(ordered, token, PunchBudget, ct).ConfigureAwait(false);

            if (link is not null)
                return new ConnectionAttempt(link, false, null, Via: match.Value.At);

            log.Info($"{pair.LogTag} : perçage sans réponse, passage au relais.");
        }
        else
        {
            log.Info($"{pair.LogTag} : relais seul, l'un des deux n'offre aucune adresse.");
        }

        var ticket = RelayTicketFor(pair.PairSecret, sealedCandidates, match.Value.Theirs);

        // Le relais ne sort jamais de nos éligibles : une empreinte que nous ne
        // connaissons pas ne peut venir que d'un pair qui ment, et l'on reste
        // alors au service d'appariement.
        var matchedPlace = eligible.FirstOrDefault(place => place.Fingerprint == RelayPlace.FingerprintOf(match.Value.At))
            ?? new RelayPlace(match.Value.At, null, viaOpen);
        var decision = RelayChoice.Decide(measured, theirMeasures, matchedPlace.Fingerprint);
        var chosenPlace = eligible.FirstOrDefault(place => place.Fingerprint == decision.Service) ?? matchedPlace;

        if (chosenPlace.Fingerprint != matchedPlace.Fingerprint)
            log.Info($"{pair.LogTag} : relais par {ServiceConsensus.Canonical(chosenPlace.At)} ({decision.WorstMs} ms"
                     + (decision.MatchedWorstMs is { } before ? $" contre {before} ms au service d'appariement)." : ")."));

        var (relayed, via) = await RelayWithFallbackAsync(
            this, chosenPlace, matchedPlace, ticket, log, latencies is null ? null : latencies.MarkRefused, ct).ConfigureAwait(false);

        return relayed is null
            ? new ConnectionAttempt(null, false, "ni perçage ni relais : le service refuse peut-être de relayer")
            : new ConnectionAttempt(relayed, false, null, Via: via);
    }

    /// <summary>
    /// Le jeton sous lequel les deux pairs se retrouvent au relais.
    /// </summary>
    /// <remarks>
    /// Dérivé du secret de paire et des deux blocs de candidats que l'échange
    /// vient de faire circuler, dans un ordre qui ne dépend pas de qui calcule :
    /// les deux côtés obtiennent le même jeton sans échanger un octet de plus.
    /// Il change à chaque tentative, donc le service ne peut pas relier deux
    /// relais d'une même paire par leur jeton.
    /// </remarks>
    public static byte[] RelayTicketFor(ReadOnlySpan<byte> pairSecret, ReadOnlySpan<byte> one, ReadOnlySpan<byte> other)
    {
        var inOrder = one.SequenceCompareTo(other) <= 0;
        var low = inOrder ? one : other;
        var high = inOrder ? other : one;

        var message = new byte[RelayInfo.Length + low.Length + high.Length];
        RelayInfo.CopyTo(message);
        low.CopyTo(message.AsSpan(RelayInfo.Length));
        high.CopyTo(message.AsSpan(RelayInfo.Length + low.Length));

        return HMACSHA256.HashData(pairSecret, message)[..RendezvousTicket.SizeInBytes];
    }

    /// <summary>
    /// Ouvre le relais sur un service, le choisi ou celui qui a apparié.
    /// </summary>
    /// <remarks>
    /// Le service garde la première demande en attente jusqu'à trente
    /// secondes ; les deux côtés arrivent ici à quelques secondes d'écart,
    /// après le même budget de perçage.
    /// </remarks>
    public async Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct)
    {
        var client = new RendezvousClient();

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(budget);

            // Un service du cercle ouvert ne se joint qu'à une adresse publique,
            // comme pour l'annonce ; l'ancrage garde la confiance qu'il a toujours eue.
            var host = place.Open
                ? await PublicHostAsync(place.At.Host, Dns.GetHostAddressesAsync, acceptOpenAddress ?? ServiceConsensus.IsPublicAddress, deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException($"{place.At.Host} ne mène à aucune adresse publique")
                : place.At.Host;

            await client.ConnectAsync(host, place.At.Port, deadline.Token).ConfigureAwait(false);

            // Les deux côtés arrivent ici après le même budget de perçage. Un
            // service d'avant le 24 septembre 2026 garait alors les deux
            // demandes sans les apparier, une fois sur deux au banc : quelques
            // centaines de millisecondes d'écart suffisent à l'éviter.
            await Task.Delay(Random.Shared.Next(0, 400), deadline.Token).ConfigureAwait(false);

            var answer = await client.RequestRelayAsync(ticket, deadline.Token).ConfigureAwait(false);

            if (answer is RelayAnswer.Ready)
                return new RelayOpening(new RelayPeerLink(new RendezvousRelayPipe(client), new DnsEndPoint(place.At.Host, place.At.Port)), false);

            log.Info(answer is RelayAnswer.Disabled
                ? $"Relais refusé par {place.At.Host}."
                : $"Relais par {place.At.Host} indisponible pour l'instant.");
            await client.DisposeAsync().ConfigureAwait(false);
            return new RelayOpening(null, answer is RelayAnswer.Disabled);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Annulé de l'extérieur, pas par le budget : on s'arrête, sans
            // prétendre à un silence qui enverrait tenter le repli.
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            log.Info($"Relais par {place.At.Host} sans réponse : {e.Message}");
        }

        await client.DisposeAsync().ConfigureAwait(false);
        return new RelayOpening(null, false);
    }

    /// <summary>Nos adresses : celle que le rendez-vous voit, et nos adresses locales.</summary>
    private async Task<IReadOnlyList<IPEndPoint>> GatherCandidatesAsync(CancellationToken ct)
    {
        var candidates = new List<IPEndPoint>();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(rendezvous.Host, ct).ConfigureAwait(false);

            foreach (var address in addresses.Take(2))
            {
                var reflected = await links.ReflectAsync(
                    new IPEndPoint(address, rendezvous.Port), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

                if (reflected is not null && candidates.Contains(reflected) is false)
                    candidates.Add(reflected);
            }
        }
        catch (Exception e)
        {
            log.Warning("Réflexion d'adresse en échec.", e);
        }

        // Les adresses locales servent quand les deux joueurs sont sous le même
        // toit, cas où l'adresse publique ne fonctionne pas toujours.
        candidates.AddRange(links.LocalCandidates());

        return candidates;
    }

    /// <summary>L'annonceur réel : une connexion par lieu, le temps d'attendre.</summary>
    /// <remarks>
    /// Il compte les services atteints, ce dont l'appelant a besoin pour
    /// distinguer un pair absent d'un réseau sans lieu commun. Une instance par
    /// tentative, donc le compteur n'a pas à se remettre à zéro.
    /// </remarks>
    private sealed class LiveDialer(IReadOnlyList<RendezvousAddress> open, Func<IPAddress, bool> acceptOpen) : IRendezvousDialer
    {
        private int _reached;

        public int Reached => Volatile.Read(ref _reached);

        public async Task<byte[]?> AnnounceAsync(
            RendezvousAddress at, Announcement announcement, CancellationToken ct)
        {
            // Un lieu du cercle ouvert n'a pas été choisi par l'utilisateur :
            // on résout son nom nous-mêmes et on ne joint qu'une adresse
            // publique. L'ancrage garde la confiance qu'il a toujours eue.
            var host = open.Contains(at)
                ? await PublicHostAsync(at.Host, Dns.GetHostAddressesAsync, acceptOpen, ct).ConfigureAwait(false)
                    ?? throw new IOException($"{at} ne mène à aucune adresse publique")
                : at.Host;

            await using var client = new RendezvousClient();
            await client.ConnectAsync(host, at.Port, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _reached);

            return await client.AnnounceAndWaitAsync(announcement, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Scelle nos adresses pour le pair : aléa(12) || chiffré || étiquette(16).
    /// </summary>
    /// <remarks>
    /// Un aléa neuf à chaque annonce. La version 1 scellait sous un nonce nul,
    /// avec une clé qui ne change pas de toute la vie de la paire, et les deux
    /// pairs scellaient chacun le leur : en AES-GCM, un couple clé-nonce
    /// réutilisé livre le XOR des clairs et de quoi forger des étiquettes, et le
    /// rendez-vous voit passer chaque bloc. L'étiquette de dérivation change
    /// avec le format, pour que la clé dont la version 1 a pu laisser fuir de
    /// quoi forger ne serve plus.
    /// </remarks>
    public static byte[] SealCandidates(
        ReadOnlySpan<byte> pairSecret, IReadOnlyList<IPEndPoint> candidates, IReadOnlyList<RelayMeasurement>? measurements = null)
    {
        byte[] plain = measurements is null
            ? CandidateSet.Encode(candidates)
            : [.. CandidateSet.Encode(candidates), .. RelayMeasurements.Encode(measurements)];

        var nonce = RandomNumberGenerator.GetBytes(CryptoPrimitives.NonceLength);
        var sealedBody = CryptoPrimitives.Seal(CandidateKey(pairSecret), nonce, plain, CandidateKeyInfo);

        return [.. nonce, .. sealedBody];
    }

    public static bool TryOpenCandidates(ReadOnlySpan<byte> pairSecret, ReadOnlySpan<byte> sealedBlock, out byte[] plain)
    {
        plain = [];

        if (sealedBlock.Length < CryptoPrimitives.NonceLength + CryptoPrimitives.TagLength)
            return false;

        return CryptoPrimitives.TryOpen(
            CandidateKey(pairSecret), sealedBlock[..CryptoPrimitives.NonceLength],
            sealedBlock[CryptoPrimitives.NonceLength..], CandidateKeyInfo, out plain);
    }

    private static byte[] CandidateKey(ReadOnlySpan<byte> pairSecret)
    {
        var key = new byte[CryptoPrimitives.KeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, pairSecret, key, CandidateKeyInfo);
        return key;
    }
}
