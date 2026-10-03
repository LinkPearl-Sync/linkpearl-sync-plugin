using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Transport;

/// <summary>
/// Ouvre et accepte les liens avec les pairs, sur une seule socket.
/// </summary>
/// <remarks>
/// Une seule socket pour tous les pairs, et c'est nécessaire : c'est son
/// adresse publique que le rendez-vous nous a rendue, et c'est donc elle qui a
/// percé le NAT. Ouvrir une socket par pair rendrait cette adresse inutile.
///
/// Le jeton de connexion est dérivé du secret de paire : un inconnu ne peut pas
/// ouvrir de session, même en connaissant l'adresse. Il voyage en clair, donc il
/// n'ouvre la porte que le temps d'une tentative, et seulement vers elle.
/// </remarks>
public sealed class PeerLinkFactory : IDisposable
{
    private readonly NetManager _manager;
    private readonly EventBasedNetListener _listener = new();
    private readonly ILogSink _log;

    private readonly ConcurrentDictionary<NetPeer, LiteNetPeerLink> _links = new();

    /// <summary>
    /// Tient ensemble les tentatives, les jetons et l'attribution des pairs.
    /// </summary>
    /// <remarks>
    /// <c>Connect</c> part d'un fil du pool alors que <c>PeerConnectedEvent</c>
    /// est levé sur celui du jeu : sans verrou commun, une connexion en boucle
    /// locale pourrait être signalée avant que le pair rendu par
    /// <c>Connect</c> ne soit rattaché à son jeton, et serait alors refusée.
    /// </remarks>
    private readonly Lock _gate = new();

    /// <summary>Les tentatives sortantes en cours, par jeton.</summary>
    private readonly Dictionary<string, TaskCompletionSource<IPeerLink>> _pending = new(StringComparer.Ordinal);

    /// <summary>Les jetons attendus sans tentative sortante, chacun pour une seule connexion.</summary>
    private readonly Dictionary<string, string> _passive = new(StringComparer.Ordinal);

    /// <summary>
    /// Le jeton sous lequel chaque pair est entré ou sorti.
    /// </summary>
    /// <remarks>
    /// C'est ce qui relie une connexion établie à la tentative qui l'attendait.
    /// Sans lui, la première tentative en attente recevait n'importe quelle
    /// connexion : un pair qui présentait son propre jeton captait celle
    /// destinée à un autre, et pour un membre de groupe pas encore épinglé, sa
    /// clé aurait été épinglée sous l'empreinte de l'autre.
    /// </remarks>
    private readonly Dictionary<NetPeer, string> _tokens = [];

    public PeerLinkFactory(int channels, ILogSink log)
    {
        _log = log;

        _manager = new NetManager(_listener)
        {
            ChannelsCount = (byte)channels,
            AutoRecycle = true,
            DisconnectTimeout = 20_000,
            UnsyncedEvents = false,

            // Les accusés de réception partent à ce rythme, et la fenêtre fiable
            // de soixante-quatre paquets par canal ne se libère qu'avec eux. Au
            // défaut de 15 ms, le faux pair plafonnait à 12,5 Mo/s en boucle
            // locale sur une apparence réelle de 405 Mo ; à 1 ms, 36 Mo/s.
            UpdateTime = 1,

            // Des paquets plus grands portent plus par fenêtre. Mesuré à 20 ms
            // de latence et huit canaux : 5,5 Mo/s sans, 7,7 Mo/s avec.
            MtuDiscovery = true,
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkReceiveUnconnectedEvent += OnUnconnected;

        _manager.UnconnectedMessagesEnabled = true;
        _manager.Start();
    }

    public int LocalPort => _manager.LocalPort;

    /// <summary>
    /// Un pair a ouvert une session vers nous sous un jeton autorisé par
    /// <see cref="Allow"/>, sans tentative sortante pour l'attendre.
    /// </summary>
    /// <remarks>
    /// Sans abonné, une telle connexion est refermée aussitôt : un lien que
    /// personne ne prend resterait ouvert sans session, ni fin.
    /// </remarks>
    public event Action<string, IPeerLink>? Accepted;

    /// <summary>
    /// Autorise un jeton pour une seule connexion entrante, remise par
    /// <see cref="Accepted"/>.
    /// </summary>
    /// <remarks>
    /// Une tentative sortante n'en a pas besoin : <see cref="ConnectAsync"/>
    /// accepte d'elle-même son jeton tant qu'elle dure, et le retire en
    /// partant, qu'il ait été autorisé ici ou non.
    /// </remarks>
    public void Allow(string token, string peerLabel)
    {
        lock (_gate)
            _passive[token] = peerLabel;
    }

    public void Forget(string token)
    {
        lock (_gate)
            _passive.Remove(token);
    }

    /// <summary>
    /// Tente de joindre un pair sur chacune de ses adresses candidates.
    /// </summary>
    /// <remarks>
    /// Toutes en parallèle et par rafales répétées : le NatPunchModule de
    /// LiteNetLib n'envoie que deux paquets sans réessai, ce qui échoue dès que
    /// les deux côtés ne sont pas synchronisés à quelques dizaines de
    /// millisecondes près.
    ///
    /// Le lien rendu est celui qui a présenté ce jeton, qu'on l'ait composé ou
    /// que le pair nous ait composés au même moment : le perçage simultané
    /// aboutit tantôt par l'un, tantôt par l'autre.
    /// </remarks>
    public async Task<IPeerLink?> ConnectAsync(
        IReadOnlyList<IPEndPoint> candidates, string token, TimeSpan budget, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return null;

        var completion = new TaskCompletionSource<IPeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            // Deux tentatives sous le même jeton se disputeraient la même
            // connexion : la seconde renonce, l'appelant passera au relais.
            if (_pending.TryAdd(token, completion) is false)
            {
                _log.Debug("Tentative déjà en cours sous ce jeton.");
                return null;
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            deadline.CancelAfter(budget);

            // Pris avant la boucle : la source est libérée en sortant, et une
            // rafale encore en route ne doit pas la relire.
            var stop = deadline.Token;

            _ = Task.Run(async () =>
            {
                while (stop.IsCancellationRequested is false)
                {
                    foreach (var candidate in candidates)
                        Dial(candidate, token, completion);

                    await Task.Delay(500, stop).ConfigureAwait(false);
                }
            }, stop);

            var finished = await Task.WhenAny(completion.Task, Task.Delay(budget, ct)).ConfigureAwait(false);

            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // Les rafales s'arrêtent avant que le jeton ne soit libéré : une
            // rafale tardive rattacherait sinon un pair à un jeton qui n'a plus
            // de tentative.
            await deadline.CancelAsync().ConfigureAwait(false);
            Settle(token, completion);
        }
    }

    /// <summary>Compose vers un candidat, et rattache le pair rendu au jeton.</summary>
    private void Dial(IPEndPoint candidate, string token, TaskCompletionSource<IPeerLink> completion)
    {
        lock (_gate)
        {
            // La tentative a pu se terminer entre deux rafales.
            if (completion.Task.IsCompleted || _pending.GetValueOrDefault(token) != completion)
                return;

            try
            {
                // Null quand une demande entrante de cette adresse attend déjà :
                // elle passera par OnConnectionRequest avec son propre jeton. Un
                // pair déjà rattaché à un autre jeton garde le sien : c'est la
                // connexion d'un autre pair, qu'une tentative ne s'approprie pas.
                if (_manager.Connect(candidate, token) is { } peer)
                    _tokens.TryAdd(peer, token);
            }
            catch (Exception e)
            {
                _log.Debug($"Tentative vers {candidate} refusée : {e.Message}");
            }
        }
    }

    /// <summary>
    /// Retire le jeton d'une tentative terminée, quelle qu'en soit l'issue.
    /// </summary>
    /// <remarks>
    /// Un jeton qui survit à sa tentative resterait une porte ouverte à qui
    /// l'a lu passer en clair. Et les pairs composés sans succès, vers une
    /// adresse locale ou périmée, continueraient de frapper.
    /// </remarks>
    private void Settle(string token, TaskCompletionSource<IPeerLink> completion)
    {
        List<NetPeer> abandoned = [];

        lock (_gate)
        {
            if (_pending.GetValueOrDefault(token) == completion)
                _pending.Remove(token);

            _passive.Remove(token);

            foreach (var (peer, owner) in _tokens)
            {
                if (owner == token && _links.ContainsKey(peer) is false)
                    abandoned.Add(peer);
            }

            foreach (var peer in abandoned)
                _tokens.Remove(peer);
        }

        foreach (var peer in abandoned)
            peer.Disconnect();
    }

    /// <summary>À appeler depuis le thread du jeu, à chaque image.</summary>
    public void Poll() => _manager.PollEvents();

    /// <summary>
    /// Demande au rendez-vous l'adresse publique de <em>cette</em> socket.
    /// </summary>
    /// <remarks>
    /// Par un message hors connexion, donc sur la socket même qui portera les
    /// liens. C'est essentiel : le NAT associe une adresse publique à une socket
    /// précise, et découvrir celle d'une autre socket donnerait une adresse
    /// que le pair ne pourrait pas joindre.
    /// </remarks>
    public async Task<IPEndPoint?> ReflectAsync(IPEndPoint rendezvous, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reflection = completion;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            var writer = new NetDataWriter();
            writer.Put(RendezvousKind.Reflect);

            while (deadline.IsCancellationRequested is false && completion.Task.IsCompleted is false)
            {
                _manager.SendUnconnectedMessage(writer, rendezvous);
                await Task.Delay(300, deadline.Token).ConfigureAwait(false);
            }

            return completion.Task.IsCompletedSuccessfully ? await completion.Task.ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException)
        {
            return completion.Task.IsCompletedSuccessfully ? await completion.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            _reflection = null;
        }
    }

    private TaskCompletionSource<IPEndPoint>? _reflection;

    private void OnUnconnected(IPEndPoint from, NetPacketReader reader, UnconnectedMessageType type)
    {
        var data = reader.GetRemainingBytes();
        reader.Recycle();

        if (data.Length < 4 || data[0] != RendezvousKind.Reflected)
            return;

        var length = data[1];

        if (data.Length < 2 + length + 2)
            return;

        try
        {
            var address = new IPAddress(data.AsSpan(2, length));
            var port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2 + length));
            _reflection?.TrySetResult(new IPEndPoint(address, port));
        }
        catch (Exception e)
        {
            _log.Debug($"Réflexion illisible : {e.Message}");
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        string token;

        try
        {
            token = request.Data.GetString(128);
        }
        catch (Exception)
        {
            request.Reject();
            return;
        }

        lock (_gate)
        {
            // Seul un jeton qu'une tentative en cours attend, ou autorisé pour
            // une seule connexion, ouvre la porte. Un inconnu ou un jeton
            // périmé est refusé sans rien révéler. Pendant un perçage
            // simultané, la demande du pair arrive parfois avant notre propre
            // tentative : refusée, elle revient à sa rafale suivante.
            var expected = _pending.TryGetValue(token, out var attempt) && attempt.Task.IsCompleted is false;

            if (expected is false && _passive.ContainsKey(token) is false)
            {
                request.Reject();
                return;
            }

            if (request.Accept() is { } peer)
                _tokens[peer] = token;
        }
    }

    private void OnPeerConnected(NetPeer peer)
    {
        LiteNetPeerLink? link = null;
        string? label = null;

        lock (_gate)
        {
            if (_tokens.TryGetValue(peer, out var token))
            {
                link = new LiteNetPeerLink(peer);
                _links[peer] = link;

                // Seule la tentative qui porte ce jeton reçoit ce lien. Une
                // tentative déjà servie, par une autre adresse du même pair,
                // n'en veut pas un second.
                if (_pending.TryGetValue(token, out var attempt) && attempt.TrySetResult(link))
                    return;

                if (Accepted is not null && _passive.Remove(token, out var allowed))
                    label = allowed;
            }
        }

        if (link is not null && label is not null)
        {
            Accepted?.Invoke(label, link);
            return;
        }

        // Personne n'attend ce lien : un pair sans jeton connu, une connexion
        // aboutie après la fin de sa tentative, ou un doublon. Le garder
        // ouvert ne servirait qu'à tenir un lien sans session.
        _log.Debug("Connexion sans tentative pour l'attendre, refermée.");
        Drop(peer);
    }

    private void Drop(NetPeer peer)
    {
        lock (_gate)
            _tokens.Remove(peer);

        _links.TryRemove(peer, out _);
        peer.Disconnect();
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        lock (_gate)
            _tokens.Remove(peer);

        if (_links.TryRemove(peer, out var link))
            link.Close(info.Reason.ToString());
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (_links.TryGetValue(peer, out var link))
            link.Deliver(channel, reader.GetRemainingBytes());

        reader.Recycle();
    }

    /// <summary>Nos adresses locales, à offrir comme candidats au pair.</summary>
    /// <remarks>
    /// Utiles quand les deux joueurs sont sous le même toit : le NAT ne
    /// laisserait pas forcément revenir un paquet parti vers sa propre adresse
    /// publique.
    /// </remarks>
    public IReadOnlyList<IPEndPoint> LocalCandidates()
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus is System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork && IPAddress.IsLoopback(a) is false)
            .Select(a => new IPEndPoint(a, LocalPort))
            .ToList();

    public void Dispose()
    {
        _manager.Stop();
    }
}
