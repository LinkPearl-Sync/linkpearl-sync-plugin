using System.Security.Cryptography;
using System.Threading.Channels;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Transport;

namespace Linkpearl.Core.Sync;

/// <summary>Où en est une session avec un pair.</summary>
public enum PeerSessionState
{
    Disconnected,
    Connecting,
    Handshaking,

    /// <summary>Lien établi et authentifié, mais le pair n'est pas dans notre champ.</summary>
    Connected,

    /// <summary>Le pair est visible et son apparence a été posée.</summary>
    Applied,
}

/// <summary>Une trame applicative reçue d'un pair, déjà déchiffrée.</summary>
public sealed record PeerMessage(byte Kind, byte Channel, byte[] Payload);

/// <summary>
/// Une session authentifiée avec un pair.
/// </summary>
/// <remarks>
/// Qui initie le handshake se décide par comparaison des identifiants, et non
/// par « celui qui a appelé » : les deux côtés tentent de se joindre en même
/// temps, et sans règle déterministe on obtiendrait deux initiateurs ou deux
/// répondeurs.
/// </remarks>
public sealed class PeerSession : IAsyncDisposable
{
    /// <summary>
    /// Trames scellées admises avant la fin de notre handshake.
    /// </summary>
    /// <remarks>
    /// Ce qui arrive là est ce que le pair envoie dès sa propre session
    /// établie, avant la nôtre : une présentation, une politique de groupe, une
    /// demande de manifeste, soit quelques trames de quelques centaines
    /// d'octets. Le plafond laisse une marge large, mais un pair qui déverse
    /// sans attendre n'emplit plus la mémoire du jeu.
    /// </remarks>
    private const int MaxEarlyFrames = 64;

    private const int MaxEarlyBytes = 1024 * 1024;

    private readonly IPeerLink _link;
    private readonly ILogSink _log;
    private readonly Channel<PeerMessage> _incoming = Channel.CreateUnbounded<PeerMessage>();

    /// <summary>
    /// Les trames du handshake, bornées par construction : n'y entrent que
    /// celles que <see cref="_handshakeLengths"/> attend encore.
    /// </summary>
    private readonly Channel<byte[]> _rawIncoming = Channel.CreateUnbounded<byte[]>();

    /// <summary>
    /// La taille de chaque trame du handshake que l'on doit encore recevoir,
    /// dans l'ordre.
    /// </summary>
    /// <remarks>
    /// Le handshake ne passe que par le canal de contrôle, ordonné, et chacun
    /// de ses messages a une taille fixe. Toute autre trame de ce canal avant
    /// la fin est une violation, et ferme la session au lieu de s'accumuler.
    /// </remarks>
    private readonly Queue<int> _handshakeLengths;

    /// <summary>Ce qui est arrivé scellé avant notre passage au mode scellé.</summary>
    private readonly List<byte[]> _early = [];

    private int _earlyBytes;

    /// <summary>
    /// Tient ensemble le passage au mode scellé et le rangement d'une trame reçue.
    /// </summary>
    private readonly Lock _sealing = new();

    /// <summary>
    /// Un verrou par canal, pour sceller et remettre au lien d'un seul geste.
    /// </summary>
    /// <remarks>
    /// Le compteur se prend atomiquement au scellement, mais deux envois
    /// concurrents sur un même canal pouvaient partir dans l'ordre inverse de
    /// leurs compteurs. Le receveur jetait alors le plus ancien comme un
    /// rejeu : une présentation ou un bloc perdu, sans erreur nulle part. Un
    /// sémaphore et non un verrou : l'envoi relayé attend sa socket, et
    /// l'attente ne doit pas bloquer le thread du jeu.
    /// </remarks>
    private readonly SemaphoreSlim[] _sending =
        [.. Enumerable.Range(0, SecureChannel.MaxChannels).Select(_ => new SemaphoreSlim(1, 1))];

    private SecureChannel? _secure;

    /// <summary>Vrai dès qu'une trame a été refusée : plus rien de ce lien n'est lu.</summary>
    private bool _broken;

    /// <summary>Ce qui admet la clé d'un pair de groupe. Null quand le moteur n'a pas de groupes.</summary>
    private IGroupGate? _groups;

    private PeerSession(IPeerLink link, PairRecord pair, ILogSink log, bool weInitiate)
    {
        _link = link;
        Pair = pair;
        _log = log;

        _handshakeLengths = new Queue<int>(weInitiate
            ? [HandshakeFormat.Message2Length]
            : [HandshakeFormat.Message1Length, HandshakeFormat.Message3Length]);

        link.Received += OnReceived;
        link.Closed += reason => State = PeerSessionState.Disconnected;
    }

    public PairRecord Pair { get; }

    /// <summary>
    /// Ce qui désigne le pair dans le journal.
    /// </summary>
    /// <remarks>
    /// Un début d'empreinte et jamais le nom affiché, qui est souvent celui du
    /// personnage : le journal par défaut ne doit en porter aucun.
    /// </remarks>
    private string Label => $"pair {Pair.Id.ToHex()[..8]}";

    /// <summary>Le lien qui porte la session, pour qui doit en observer la santé.</summary>
    public IPeerLink Link => _link;

    public PeerSessionState State { get; private set; } = PeerSessionState.Connecting;

    public byte[]? SessionId { get; private set; }

    public ChannelReader<PeerMessage> Messages => _incoming.Reader;

    /// <summary>
    /// Établit une session : handshake puis canal chiffré.
    /// </summary>
    /// <remarks>
    /// L'autorisation vient du carnet local et de lui seul. La clé publique
    /// reçue doit correspondre à l'empreinte du pair attendu, faute de quoi la
    /// session est refusée : c'est ce qui rend un rendez-vous malveillant
    /// incapable d'imposer quelqu'un d'autre.
    /// </remarks>
    public static async Task<PeerSession?> EstablishAsync(
        IPeerLink link, PairRecord pair, PeerId ourId, ECDsa identity, IClock clock, ILogSink log,
        CancellationToken ct, IGroupGate? groups = null)
    {
        var weInitiate = WeInitiate(pair, ourId);
        var session = new PeerSession(link, pair, log, weInitiate) { State = PeerSessionState.Handshaking, _groups = groups };

        try
        {
            var keys = weInitiate
                ? await session.InitiateAsync(identity, pair, clock, ct).ConfigureAwait(false)
                : await session.RespondAsync(identity, pair, clock, ct).ConfigureAwait(false);

            if (keys is null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            session.Seal(new SecureChannel(keys.SendKey, keys.ReceiveKey, keys.SessionId));
            session.SessionId = keys.SessionId;
            session.State = PeerSessionState.Connected;

            _ = Task.Run(() => session.PumpAsync(ct), ct);

            log.Info($"{session.Label} : session établie, {(weInitiate ? "initiateur" : "répondeur")}.");
            return session;
        }
        catch (Exception e)
        {
            log.Warning($"{session.Label} : handshake en échec.", e);
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    public async ValueTask SendAsync(byte channel, byte kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_secure is not { } secure)
            throw new InvalidOperationException("session non établie");

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, SecureChannel.MaxChannels);

        var gate = _sending[channel];
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await _link.SendAsync(channel, secure.Seal(channel, kind, payload.Span), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void MarkApplied() => State = PeerSessionState.Applied;

    public void MarkOutOfSight()
    {
        // On revient à « connecté » sans couper la session ni jeter le cache :
        // le pair va revenir, et tout refaire coûterait un transfert complet.
        if (State is PeerSessionState.Applied)
            State = PeerSessionState.Connected;
    }

    /// <summary>
    /// Qui des deux envoie le premier message.
    /// </summary>
    /// <remarks>
    /// Pour un pair de groupe, l'identifiant du pair est tiré du secret du
    /// couple et vaut la même chose des deux côtés : le comparer au nôtre
    /// pourrait donner deux initiateurs. Les deux empreintes, elles, sont
    /// connues des deux et différentes.
    /// </remarks>
    private static bool WeInitiate(PairRecord pair, PeerId ourId)
        => pair.Group is { } origin
            ? string.CompareOrdinal(origin.Ours.ToString(), origin.Theirs.ToString()) < 0
            : string.CompareOrdinal(ourId.ToHex(), pair.Id.ToHex()) < 0;

    private async Task<SessionKeys?> InitiateAsync(ECDsa identity, PairRecord pair, IClock clock, CancellationToken ct)
    {
        var initiator = new HandshakeInitiator(identity, clock);

        await _link.SendAsync(0, initiator.CreateMessage1(), ct).ConfigureAwait(false);

        var message2 = await NextRawAsync(ct).ConfigureAwait(false);

        if (initiator.TryHandleMessage2(message2, key => Authorizes(pair, key, _groups), out var message3, out var keys, out var why) is false)
        {
            _log.Warning($"{Label} : message 2 refusé, {why}");
            return null;
        }

        await _link.SendAsync(0, message3!, ct).ConfigureAwait(false);
        return keys;
    }

    private async Task<SessionKeys?> RespondAsync(ECDsa identity, PairRecord pair, IClock clock, CancellationToken ct)
    {
        var responder = new HandshakeResponder(identity, clock);

        var message1 = await NextRawAsync(ct).ConfigureAwait(false);

        if (responder.TryHandleMessage1(message1, out var message2, out var why1) is false)
        {
            _log.Warning($"{Label} : message 1 refusé, {why1}");
            return null;
        }

        await _link.SendAsync(0, message2!, ct).ConfigureAwait(false);

        var message3 = await NextRawAsync(ct).ConfigureAwait(false);

        if (responder.TryHandleMessage3(message3, key => Authorizes(pair, key, _groups), out var keys, out var why2) is false)
        {
            _log.Warning($"{Label} : message 3 refusé, {why2}");
            return null;
        }

        return keys;
    }

    /// <summary>
    /// La clé reçue doit être celle du pair attendu, et de personne d'autre.
    /// </summary>
    /// <remarks>
    /// Pour une paire, l'empreinte de la clé sert de comparaison : une clé
    /// substituée donne une autre empreinte, donc un refus. Pour un pair de
    /// groupe, on ne connaît pas sa clé d'avance : c'est le groupe qui décide,
    /// par l'épinglage du premier vu. Sans groupe pour trancher, on refuse.
    /// </remarks>
    private static bool Authorizes(PairRecord pair, byte[] publicKey, IGroupGate? groups)
    {
        if (pair.Group is not null)
            return groups?.Admits(pair, publicKey) ?? false;

        if (PeerId.Of(publicKey) != pair.Id)
            return false;

        return pair.PublicKey is not { } known || known.AsSpan().SequenceEqual(publicKey);
    }

    private async Task<byte[]> NextRawAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        return await _rawIncoming.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
    }

    private void OnReceived(byte channel, byte[] payload)
    {
        // Avant l'établissement, les trames sont celles du handshake et passent
        // en clair ; après, tout est scellé. Le test et le rangement se font sous
        // le même verrou que le passage au mode scellé : sinon une trame lue
        // « avant » pourrait être rangée « après », dans une file que plus
        // personne ne lit.
        string? rejection;
        bool established;

        lock (_sealing)
        {
            if (_broken)
                return;

            established = _secure is not null;
            rejection = _secure is { } secure ? Open(secure, payload) : Hold(channel, payload);

            if (rejection is not null)
                Break();
        }

        if (rejection is null)
            return;

        _log.Warning($"{Label} : trame refusée, {rejection}. Lien fermé.");

        // Avant l'établissement, c'est le handshake, débloqué en échec, qui
        // referme le lien en libérant la session.
        if (established)
            Drop();
    }

    /// <summary>
    /// Range une trame arrivée avant notre passage au mode scellé, ou dit
    /// pourquoi elle condamne la session.
    /// </summary>
    /// <remarks>
    /// Sur le canal de contrôle, tant que le handshake attend une trame, c'en
    /// est une, et de la taille fixée par le protocole. Tout le reste ne peut
    /// être qu'une trame scellée envoyée par un pair qui a fini avant nous : on
    /// la garde pour l'ouvrir au passage au mode scellé, dans une limite.
    /// </remarks>
    private string? Hold(byte channel, byte[] payload)
    {
        if (channel == ChannelPlan.ControlChannel && _handshakeLengths.TryPeek(out var expected))
        {
            if (payload.Length != expected)
                return $"trame de handshake de {payload.Length} octets, {expected} attendus";

            _handshakeLengths.Dequeue();
            _rawIncoming.Writer.TryWrite(payload);
            return null;
        }

        if (_early.Count >= MaxEarlyFrames || _earlyBytes + payload.Length > MaxEarlyBytes)
            return "trop de trames avant la fin du handshake";

        _early.Add(payload);
        _earlyBytes += payload.Length;
        return null;
    }

    /// <summary>
    /// Ne plus rien lire de ce lien, et débloquer qui attend encore.
    /// </summary>
    /// <remarks>
    /// Sous <see cref="_sealing"/>. Le handshake en cours échoue au lieu
    /// d'attendre son délai, et les lecteurs des messages s'arrêtent.
    /// </remarks>
    private void Break()
    {
        _broken = true;
        _early.Clear();
        _earlyBytes = 0;
        _rawIncoming.Writer.TryComplete(new InvalidDataException("session rompue par une trame refusée"));
        _incoming.Writer.TryComplete();
    }

    /// <summary>
    /// Ferme le lien après une trame refusée.
    /// </summary>
    /// <remarks>
    /// Le transport livre chaque canal dans l'ordre et sans perte, et chaque
    /// canal n'a qu'un émetteur à la fois : un pair honnête ne produit jamais
    /// une trame qui ne s'ouvre pas. En jeter une en silence laissait la
    /// session vivante, mais amputée d'une présentation ou d'un bloc, sans
    /// rien pour le dire. Mieux vaut tomber et se rejoindre.
    /// </remarks>
    private void Drop()
    {
        State = PeerSessionState.Disconnected;

        // Hors du fil du transport : on y est appelé, et fermer un lien relayé
        // attend sa socket.
        _ = Task.Run(async () =>
        {
            try
            {
                await _link.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Debug($"{Label} : fermeture du lien en échec, {e.Message}");
            }
        });
    }

    /// <summary>
    /// Passe au mode scellé, et reprend ce qui est arrivé trop tôt.
    /// </summary>
    /// <remarks>
    /// L'initiateur termine le handshake dès son dernier message envoyé et
    /// envoie aussitôt son premier message scellé. Chez le répondeur, ce message
    /// peut arriver avant la vérification du dernier message du handshake : il
    /// était alors rangé avec les trames du handshake, que plus personne ne lit,
    /// et le répondeur ne recevait jamais l'identité de l'autre ni son
    /// manifeste. Vu dans les tests du moteur sous charge, une fois sur trois ;
    /// reproduit à coup sûr par PeerSessionTests.
    /// </remarks>
    private void Seal(SecureChannel secure)
    {
        string? rejection = null;

        lock (_sealing)
        {
            if (_broken)
                throw new InvalidDataException("session rompue avant la fin du handshake");

            _secure = secure;

            foreach (var early in _early)
            {
                if ((rejection = Open(secure, early)) is not null)
                    break;
            }

            _early.Clear();
            _earlyBytes = 0;

            if (rejection is not null)
                Break();
        }

        if (rejection is not null)
            throw new InvalidDataException($"trame scellée précoce refusée, {rejection}");
    }

    /// <summary>Ouvre une trame scellée et la range, ou dit pourquoi elle est refusée.</summary>
    private string? Open(SecureChannel secure, byte[] payload)
    {
        if (secure.TryOpen(payload, out var kind, out var fromChannel, out var plain, out var rejection) is false)
            return rejection;

        _incoming.Writer.TryWrite(new PeerMessage(kind, fromChannel, plain));
        return null;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // Le lien pousse déjà dans la file : cette boucle ne sert qu'à fermer la
        // file quand le lien tombe, pour que les lecteurs se débloquent.
        while (ct.IsCancellationRequested is false && _link.IsOpen)
            await Task.Delay(500, ct).ConfigureAwait(false);

        _incoming.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        State = PeerSessionState.Disconnected;
        _incoming.Writer.TryComplete();
        _rawIncoming.Writer.TryComplete();
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}
