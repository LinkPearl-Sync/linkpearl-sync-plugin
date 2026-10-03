using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using Linkpearl.Core.Crypto;

namespace Linkpearl.Core.Transport;

/// <summary>Un tuyau d'octets vers un pair, ouvert par le relais d'un rendez-vous.</summary>
/// <remarks>
/// Abstrait pour que le lien relayé se teste sans serveur. Chaque envoi arrive
/// entier et dans l'ordre de l'autre côté : c'est une connexion TCP que le
/// service met bout à bout avec celle du pair.
/// </remarks>
public interface IRelayPipe : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>La prochaine trame, ou <c>null</c> quand le tuyau est fermé.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
}

/// <summary>
/// Un lien vers un pair qui passe par le relais d'un rendez-vous.
/// </summary>
/// <remarks>
/// Le relais ne porte qu'un flux, là où LiteNetLib offre des canaux : le canal
/// voyage donc en tête de chaque fragment. Le flux étant ordonné, l'ordre à
/// l'intérieur d'un canal est garanti, et c'est tout ce que le reste du code
/// suppose.
///
/// Un message est découpé en fragments, et tous les fragments d'un message
/// partent sous le même verrou : le service refuse toute trame de plus de
/// 64 Kio, or un manifeste compressé les dépasse, et deux messages d'un même
/// canal entrelacés seraient indémêlables.
///
/// La file d'envoi est bornée par construction : <see cref="SendAsync"/> ne
/// rend la main qu'une fois le message confié à la socket, donc chaque
/// émetteur a au plus un message en vol. C'est la contre-pression de TCP, et
/// c'est ce qui manque à LiteNetLib.
///
/// Le contenu est déjà scellé par <c>SecureChannel</c> : le service transporte
/// sans pouvoir lire.
/// </remarks>
public sealed class RelayPeerLink : IPeerLink
{
    /// <summary>
    /// Données par fragment.
    /// </summary>
    /// <remarks>
    /// Un bloc de 16 Kio scellé tient dans un seul fragment, avec son en-tête,
    /// et le fragment reste loin des 64 Kio que le service accepte.
    /// </remarks>
    public const int FragmentLength = 32 * 1024;

    /// <summary>
    /// Plafond d'un message reconstitué.
    /// </summary>
    /// <remarks>
    /// Une longueur annoncée par le réseau ne s'alloue jamais sans borne. Le
    /// plus gros message légitime est un manifeste compressé, qui se compte en
    /// centaines de kilo-octets.
    /// </remarks>
    public const int MaxMessageLength = 16 * 1024 * 1024;

    private const byte FragmentMore = 0x00;
    private const byte FragmentLast = 0x01;
    private const byte Ping = 0x02;
    private const byte Pong = 0x03;

    private const int HeaderLength = 2;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    private readonly IRelayPipe _pipe;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly int[] _pending = new int[256];

    /// <summary>
    /// Fragments reçus du message en cours, pris dans le pool.
    /// </summary>
    /// <remarks>
    /// Un seul message à la fois, tous canaux confondus : l'émetteur envoie
    /// tous les fragments d'un message sous un même verrou, donc deux messages
    /// ne s'entrelacent jamais sur le fil. Un réassemblage par canal laissait
    /// un pair hostile en ouvrir 256 de 16 Mio chacun. Et des fragments de
    /// 32 Kio restent hors du LOH, là où un <c>MemoryStream</c> doublait sa
    /// capacité jusqu'à 32 Mio.
    /// </remarks>
    private readonly List<byte[]> _parts = [];

    private int _assemblingChannel = NotAssembling;
    private int _assembledLength;

    private const int NotAssembling = -1;

    private Action<byte, byte[]>? _received;
    private int _started;
    private int _closed;
    private int _roundTripMs;

    public RelayPeerLink(IRelayPipe pipe, EndPoint? remote)
    {
        _pipe = pipe;
        Remote = remote;
    }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public bool IsRelayed => true;

    public int RoundTripMs => Volatile.Read(ref _roundTripMs);

    /// <summary>Aucune perte visible : TCP retransmet sous le relais.</summary>
    public float PacketLossPercent => 0f;

    /// <summary>Le service qui relaie, pas le pair : son adresse, justement, reste cachée.</summary>
    public EndPoint? Remote { get; }

    public int PendingOn(byte channel) => Volatile.Read(ref _pending[channel]);

    /// <summary>
    /// Une trame reçue.
    /// </summary>
    /// <remarks>
    /// La lecture ne commence qu'au premier abonné. Avant, un message du pair
    /// arrivé tôt serait levé vers personne et perdu : c'est le premier message
    /// du handshake qui part le plus vite.
    /// </remarks>
    public event Action<byte, byte[]>? Received
    {
        add
        {
            _received += value;
            Start();
        }
        remove => _received -= value;
    }

    public event Action<string>? Closed;

    public async ValueTask SendAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (IsOpen is false)
            throw new InvalidOperationException("lien fermé");

        if (payload.Length > MaxMessageLength)
            throw new ArgumentException($"message de {payload.Length} octets, plafond {MaxMessageLength}", nameof(payload));

        // Le pair refuserait le fragment et couperait le lien.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, SecureChannel.MaxChannels);

        Interlocked.Increment(ref _pending[channel]);

        try
        {
            await _writing.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var offset = 0;

                do
                {
                    var length = Math.Min(FragmentLength, payload.Length - offset);
                    var last = offset + length == payload.Length;

                    var fragment = new byte[HeaderLength + length];
                    fragment[0] = last ? FragmentLast : FragmentMore;
                    fragment[1] = channel;
                    payload.Slice(offset, length).CopyTo(fragment.AsMemory(HeaderLength));

                    // Le jeton du lien et non celui de l'appelant : un message
                    // abandonné à mi-chemin laisserait chez le pair un début
                    // que le message suivant du canal viendrait compléter.
                    await _pipe.SendAsync(fragment, _life.Token).ConfigureAwait(false);
                    offset += length;
                }
                while (offset < payload.Length);
            }
            finally
            {
                _writing.Release();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Close($"envoi en échec : {e.Message}");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _pending[channel]);
        }
    }

    private void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _ = Task.Run(() => ReceiveLoopAsync(_life.Token));
        _ = Task.Run(() => PingLoopAsync(_life.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "relais fermé";

        try
        {
            while (ct.IsCancellationRequested is false)
            {
                var frame = await _pipe.ReceiveAsync(ct).ConfigureAwait(false);

                if (frame is null)
                    break;

                if (Handle(frame) is { } rejection)
                {
                    reason = rejection;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            reason = $"relais en échec : {e.Message}";
        }

        // Seule cette boucle touche aux fragments : les rendre ici, et non dans
        // Close, évite de les libérer sous un réassemblage en cours.
        ReleaseParts();
        Close(reason);
    }

    /// <summary>Range une trame reçue, ou dit pourquoi le lien doit tomber.</summary>
    private string? Handle(byte[] frame)
    {
        if (frame.Length < HeaderLength)
            return "fragment tronqué";

        var kind = frame[0];
        var channel = frame[1];
        var body = frame.AsSpan(HeaderLength);

        switch (kind)
        {
            case Ping:
                if (body.Length != sizeof(long))
                    return "sonde malformée";

                var echo = (byte[])frame.Clone();
                echo[0] = Pong;
                _ = SendControlAsync(echo);
                return null;

            case Pong:
                if (body.Length != sizeof(long))
                    return "écho malformé";

                var elapsed = Stopwatch.GetElapsedTime(BinaryPrimitives.ReadInt64BigEndian(body));

                // Un écho qui ne vient pas de nous donnerait une durée absurde :
                // on l'ignore plutôt que de l'afficher.
                if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(1))
                    Volatile.Write(ref _roundTripMs, (int)elapsed.TotalMilliseconds);

                return null;

            case FragmentMore or FragmentLast:
                return Assemble(kind == FragmentLast, channel, body);

            default:
                return $"fragment de type inconnu {kind:x2}";
        }
    }

    private string? Assemble(bool last, byte channel, ReadOnlySpan<byte> body)
    {
        // Le scellement ne connaît pas d'autre canal : au-delà, la trame ne
        // pourrait de toute façon pas s'ouvrir.
        if (channel >= SecureChannel.MaxChannels)
            return $"canal hors bornes ({channel})";

        // L'émetteur ne coupe qu'en fragments pleins : seul le dernier est plus
        // court. Exiger cette forme borne le nombre de fragments d'un message,
        // là où une pluie de fragments d'un octet ferait enfler la liste.
        if (last ? body.Length > FragmentLength : body.Length != FragmentLength)
            return $"fragment de {body.Length} octets";

        if (_assemblingChannel != NotAssembling && _assemblingChannel != channel)
            return "fragments de deux messages entrelacés";

        // Un message d'un seul fragment, le cas de chaque bloc : aucune copie
        // intermédiaire.
        if (last && _assemblingChannel == NotAssembling)
        {
            _received?.Invoke(channel, body.ToArray());
            return null;
        }

        if (_assembledLength + body.Length > MaxMessageLength)
            return $"message de plus de {MaxMessageLength} octets";

        var part = ArrayPool<byte>.Shared.Rent(FragmentLength);
        body.CopyTo(part);
        _parts.Add(part);
        _assembledLength += body.Length;
        _assemblingChannel = channel;

        if (last is false)
            return null;

        // Seul le message entier, de taille connue, est alloué d'un bloc : le
        // contrat de IPeerLink rend un tableau.
        var message = new byte[_assembledLength];
        var offset = 0;

        foreach (var piece in _parts)
        {
            var length = Math.Min(FragmentLength, message.Length - offset);
            piece.AsSpan(0, length).CopyTo(message.AsSpan(offset));
            offset += length;
        }

        ReleaseParts();
        _received?.Invoke(channel, message);
        return null;
    }

    /// <summary>Rend au pool les fragments du message en cours, et l'oublie.</summary>
    private void ReleaseParts()
    {
        foreach (var piece in _parts)
            ArrayPool<byte>.Shared.Return(piece);

        _parts.Clear();
        _assembledLength = 0;
        _assemblingChannel = NotAssembling;
    }

    /// <summary>
    /// Mesure le temps d'aller-retour.
    /// </summary>
    /// <remarks>
    /// Le limiteur d'envoi s'en sert pour céder quand la liaison sature, et
    /// l'interface l'affiche. Le relais n'en fournit aucun, alors on sonde de
    /// bout en bout : c'est de toute façon le seul délai qui compte, celui qui
    /// traverse le service.
    /// </remarks>
    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (ct.IsCancellationRequested is false && IsOpen)
            {
                var probe = new byte[HeaderLength + sizeof(long)];
                probe[0] = Ping;
                BinaryPrimitives.WriteInt64BigEndian(probe.AsSpan(HeaderLength), Stopwatch.GetTimestamp());

                await SendControlAsync(probe).ConfigureAwait(false);
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Une sonde ou son écho.
    /// </summary>
    /// <remarks>
    /// Sous le même verrou que les messages, pour ne jamais couper un message
    /// fragmenté en deux. Derrière un gros envoi, la sonde attend son tour, et
    /// le temps mesuré inclut cette attente : c'est bien celle que subit un
    /// message du pair.
    /// </remarks>
    private async Task SendControlAsync(byte[] frame)
    {
        try
        {
            await _writing.WaitAsync(_life.Token).ConfigureAwait(false);

            try
            {
                await _pipe.SendAsync(frame, _life.Token).ConfigureAwait(false);
            }
            finally
            {
                _writing.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Close($"envoi en échec : {e.Message}");
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        _life.Cancel();
        Closed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        Close("fermé localement");
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
