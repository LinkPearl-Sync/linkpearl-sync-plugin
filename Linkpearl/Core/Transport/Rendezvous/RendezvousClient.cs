using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Safety;

namespace Linkpearl.Core.Transport.Rendezvous;

/// <summary>
/// Client du service de rendez-vous.
/// </summary>
/// <remarks>
/// Le rendez-vous n'est pas une autorité : tout ce qu'il rend est soit opaque,
/// soit vérifié ailleurs. Un serveur malveillant peut refuser son service ou
/// mentir sur une adresse, ce qui produit un échec de handshake, jamais une
/// usurpation : l'autorisation vient du carnet local et la clé publique du code
/// d'invitation.
/// </remarks>
public sealed class RendezvousClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;

    /// <summary>Une seule trame écrite à la fois.</summary>
    /// <remarks>
    /// La présence dépose depuis plusieurs tâches (réponses d'admission, redépôts,
    /// interrogations) sur la même connexion : deux écritures concurrentes sur un
    /// flux peuvent s'entrelacer, et le service lirait alors une trame corrompue
    /// puis fermerait la connexion.
    /// </remarks>
    private readonly SemaphoreSlim _writing = new(1, 1);

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    /// <summary>Annonce ses jetons et attend qu'un pair présente le même.</summary>
    public async Task<byte[]?> AnnounceAndWaitAsync(Announcement announcement, CancellationToken ct)
    {
        await SendAsync(RendezvousWire.Announce(announcement), ct).ConfigureAwait(false);

        while (true)
        {
            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return null;

            switch (frame[0])
            {
                case RendezvousKind.Matched:
                    return frame[1..];

                case RendezvousKind.Error:
                    throw new InvalidOperationException(
                        $"rendez-vous : {System.Text.Encoding.UTF8.GetString(frame, 1, frame.Length - 1)}");
            }
        }
    }

    /// <summary>
    /// Ouvre ses boîtes et reste connecté pour recevoir les demandes.
    /// </summary>
    /// <remarks>
    /// La connexion tient lieu de présence : la fermer vaut déclaration
    /// d'absence, sans battement de cœur ni délai d'expiration à régler.
    /// </remarks>
    public Task OpenMailboxesAsync(IReadOnlyList<byte[]> addresses, CancellationToken ct)
        => SendAsync(RendezvousWire.MailboxOpen(addresses), ct);

    /// <summary>
    /// Demande lesquelles de ces adresses sont présentes.
    /// </summary>
    /// <remarks>
    /// Deux chemins, parce qu'il y a deux usages. Sans écouteur, l'appelant est
    /// seul à lire et attend sa réponse lui-même. Avec un écouteur, c'est lui
    /// qui lit : lire ici en même temps ferait deux lecteurs sur un flux, et
    /// celui qui attrape la trame n'est pas celui qui l'attendait. La présence
    /// s'est arrêtée exactement ainsi, sans qu'aucun état n'ait l'air fautif.
    /// </remarks>
    public async Task<bool[]?> QueryPresenceAsync(IReadOnlyList<byte[]> addresses, CancellationToken ct)
    {
        if (_listening)
            return await QueryThroughListenerAsync(addresses, ct).ConfigureAwait(false);

        await SendAsync(RendezvousWire.MailboxQuery(addresses), ct).ConfigureAwait(false);

        while (true)
        {
            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return null;

            switch (frame[0])
            {
                case RendezvousKind.MailboxPresence:
                    return RendezvousWire.TryReadPresence(frame, out var present) ? present : null;

                case RendezvousKind.MailboxDelivery:
                    // Une demande arrivée pendant l'attente : on la met de côté
                    // plutôt que de la perdre, elle n'a aucune raison d'attendre
                    // qu'on ait fini d'interroger.
                    Delivered?.Invoke(frame[1..]);
                    break;

                case RendezvousKind.Error:
                    return null;
            }
        }
    }

    /// <summary>L'interrogation qui laisse l'écouteur lire pour elle.</summary>
    private async Task<bool[]?> QueryThroughListenerAsync(IReadOnlyList<byte[]> addresses, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<bool[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Une seule interrogation à la fois sur une connexion : la précédente,
        // si elle existe encore, n'aura jamais sa réponse.
        Interlocked.Exchange(ref _presenceWaiter, waiter)?.TrySetResult(null);

        await SendAsync(RendezvousWire.MailboxQuery(addresses), ct).ConfigureAwait(false);

        await using var give = ct.Register(() => waiter.TrySetResult(null)).ConfigureAwait(false);

        return await waiter.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Demande à ce service la liste de ceux qu'il connaît.
    /// </summary>
    /// <remarks>
    /// Il publie ce que son opérateur a écrit, rien de plus : il n'interroge
    /// aucun autre service et ne relaie rien. Ce qui revient est une
    /// proposition, jamais un ajout : c'est l'utilisateur qui coche.
    /// </remarks>
    public async Task<IReadOnlyList<DirectoryEntry>?> QueryDirectoryAsync(CancellationToken ct)
    {
        await SendAsync(RendezvousWire.Simple(RendezvousKind.DirectoryQuery), ct).ConfigureAwait(false);

        while (true)
        {
            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return null;

            switch (frame[0])
            {
                case RendezvousKind.DirectoryList:
                    return RendezvousWire.TryReadDirectory(frame, out var entries, out _) ? entries : null;

                case RendezvousKind.MailboxDelivery:
                    // Une demande arrivée pendant l'attente : on la met de côté
                    // plutôt que de la perdre.
                    Delivered?.Invoke(frame[1..]);
                    break;

                case RendezvousKind.Error:
                    return null;
            }
        }
    }

    /// <summary>
    /// Télécharge la liste de bannissement de ce service, toutes pages.
    /// </summary>
    /// <remarks>
    /// Sur une connexion à part, qui n'écoute pas de boîte : la boucle de
    /// présence lit déjà la sienne, et deux lecteurs sur un flux se volent les
    /// trames. Un service d'avant ces trames répond « trame inattendue » :
    /// l'échec est rendu, jamais levé, et l'appelant garde ce qu'il avait.
    /// </remarks>
    public async Task<(BanList? List, string? Failure)> QueryBanListAsync(CancellationToken ct)
    {
        var pages = new List<BanList>();
        var total = 1;

        for (var page = 0; page < total; page++)
        {
            await SendAsync(RendezvousWire.BanListQuery(page), ct).ConfigureAwait(false);

            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return (null, "connexion fermée par le service");

            if (frame[0] == RendezvousKind.Error)
                return (null, System.Text.Encoding.UTF8.GetString(frame.AsSpan(1)));

            if (RendezvousWire.TryReadBanListData(frame, out var index, out var count, out var json, out var why) is false)
                return (null, why);

            if (index != page || (page > 0 && count != total))
                return (null, "pages incohérentes");

            total = count;

            if (BanList.TryParse(json, out var list, out why) is false)
                return (null, why);

            pages.Add(list!);
        }

        return BanListPages.TryMerge(pages, out var merged, out var rejection) ? (merged, null) : (null, rejection);
    }

    /// <summary>
    /// Récupère la liste signée du cercle ouvert, page après page.
    /// </summary>
    /// <remarks>
    /// Rend les octets bruts : rien n'est cru avant la vérification de la
    /// signature, faite par l'appelant. Une page manquante ou incohérente
    /// rend un échec lisible, jamais une exception.
    /// </remarks>
    public async Task<(byte[]? Document, string? Failure)> QueryConsensusAsync(CancellationToken ct)
    {
        using var document = new MemoryStream();
        var total = 1;

        for (var page = 0; page < total; page++)
        {
            await SendAsync(RendezvousWire.ConsensusQuery(page), ct).ConfigureAwait(false);

            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return (null, "connexion fermée par le service");

            if (frame[0] == RendezvousKind.Error)
                return (null, System.Text.Encoding.UTF8.GetString(frame.AsSpan(1)));

            if (RendezvousWire.TryReadConsensusPage(frame, out var index, out var count, out var chunk, out var why) is false)
                return (null, why);

            if (index != page || (page > 0 && count != total))
                return (null, "pages incohérentes");

            total = count;
            document.Write(chunk);
        }

        return (document.ToArray(), null);
    }

    /// <summary>Dépose une demande dans la boîte de quelqu'un.</summary>
    public Task DepositAsync(ReadOnlyMemory<byte> address, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => SendAsync(RendezvousWire.MailboxDeposit(address.Span, payload.Span), ct);

    /// <summary>Appelé quand une demande nous est poussée.</summary>
    public event Action<byte[]>? Delivered;

    private volatile bool _listening;

    private TaskCompletionSource<bool[]?>? _presenceWaiter;

    /// <summary>
    /// Boucle de réception des remises.
    /// </summary>
    /// <remarks>
    /// À ne lancer que sur une connexion dédiée à la présence : deux lecteurs
    /// concurrents sur un même flux se volent les trames, ce qui s'est déjà vu
    /// sur le relais.
    /// </remarks>
    public async Task ListenAsync(CancellationToken ct)
    {
        _listening = true;

        try
        {
            while (ct.IsCancellationRequested is false)
            {
                var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

                if (frame is null)
                    return;

                switch (frame[0])
                {
                    case RendezvousKind.MailboxDelivery:
                        Delivered?.Invoke(frame[1..]);
                        break;

                    // La réponse appartient à celui qui a posé la question.
                    case RendezvousKind.MailboxPresence:
                        Interlocked.Exchange(ref _presenceWaiter, null)?.TrySetResult(
                            RendezvousWire.TryReadPresence(frame, out var present) ? present : null);
                        break;

                    // Le service répond « destinataire absent » à un dépôt vers
                    // une boîte que personne ne tient ouverte. Personne n'attend
                    // cette réponse : les redépôts d'admission, qui visent une
                    // boîte souvent fermée faute de membre en ligne, la rendent
                    // fréquente, et la prendre pour la réponse de l'interrogation
                    // en cours faisait passer celle-ci pour « sans réponse ».
                    case RendezvousKind.Error when IsAbsentRecipient(frame):
                        break;

                    case RendezvousKind.Error:
                        Interlocked.Exchange(ref _presenceWaiter, null)?.TrySetResult(null);
                        break;
                }
            }
        }
        finally
        {
            _listening = false;

            // La connexion s'en va : celui qui attendait doit l'apprendre plutôt
            // que d'attendre une trame qui ne viendra plus.
            Interlocked.Exchange(ref _presenceWaiter, null)?.TrySetResult(null);
        }
    }

    /// <summary>Le texte exact que le service renvoie pour un dépôt sans destinataire.</summary>
    private const string AbsentRecipient = "destinataire absent";

    private static bool IsAbsentRecipient(byte[] frame)
        => frame is [RendezvousKind.Error, .. var reason]
           && System.Text.Encoding.UTF8.GetString(reason) == AbsentRecipient;

    /// <summary>Dépose une invitation, que le rendez-vous rendra une seule fois.</summary>
    public async Task<string?> RegisterInvitationAsync(
        ReadOnlyMemory<byte> ticket, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await SendAsync(RendezvousWire.TicketRegister(ticket.Span, payload.Span), ct).ConfigureAwait(false);

        var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

        return frame switch
        {
            null => "le service n'a pas répondu",
            [RendezvousKind.TicketAccepted, ..] => null,
            [RendezvousKind.Error, .. var reason] => System.Text.Encoding.UTF8.GetString(reason),
            _ => "réponse inattendue du service",
        };
    }

    /// <summary>Retire une invitation. Elle disparaît du serveur au premier retrait.</summary>
    public async Task<(byte[]? Payload, string? Rejection)> RedeemInvitationAsync(
        ReadOnlyMemory<byte> ticket, CancellationToken ct)
    {
        await SendAsync(RendezvousWire.TicketRedeem(ticket.Span), ct).ConfigureAwait(false);

        var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

        return frame switch
        {
            null => (null, "le service n'a pas répondu"),
            [RendezvousKind.TicketPayload, .. var payload] => (payload, null),
            [RendezvousKind.Error, .. var reason] => (null, System.Text.Encoding.UTF8.GetString(reason)),
            _ => (null, "réponse inattendue du service"),
        };
    }

    /// <summary>Demande un relais et attend que le pair en fasse autant.</summary>
    public async Task<bool> OpenRelayAsync(ReadOnlyMemory<byte> ticket, CancellationToken ct)
    {
        await SendAsync(RendezvousWire.RelayOpen(ticket.Span), ct).ConfigureAwait(false);

        var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
        return frame is not null && frame[0] == RendezvousKind.RelayReady;
    }

    public Task SendRelayAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => SendAsync(RendezvousWire.RelayData(payload.Span), ct);

    public async Task<byte[]?> ReceiveRelayAsync(CancellationToken ct)
    {
        var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
        return frame is not null && frame[0] == RendezvousKind.RelayData ? frame[1..] : null;
    }

    /// <summary>
    /// Demande au serveur l'adresse d'où il nous voit.
    /// </summary>
    /// <remarks>
    /// La socket UDP est fournie par l'appelant, et c'est essentiel : c'est
    /// celle qui servira ensuite au trou de NAT, donc c'est son adresse
    /// publique que l'on veut connaître, pas celle d'une autre.
    /// </remarks>
    public static async Task<IPEndPoint?> ReflectAsync(
        Socket socket, EndPoint server, TimeSpan timeout, CancellationToken ct)
    {
        await socket.SendToAsync(new byte[] { RendezvousKind.Reflect }, server, ct).ConfigureAwait(false);

        var buffer = new byte[64];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            var received = await socket.ReceiveFromAsync(buffer, server, deadline.Token).ConfigureAwait(false);

            if (received.ReceivedBytes < 4 || buffer[0] != RendezvousKind.Reflected)
                return null;

            var length = buffer[1];

            if (received.ReceivedBytes < 2 + length + 2)
                return null;

            var address = new IPAddress(buffer.AsSpan(2, length));
            var port = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2 + length));
            return new IPEndPoint(address, port);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task SendAsync(byte[] body, CancellationToken ct)
    {
        var frame = RendezvousWire.Frame(body);
        await _writing.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await _stream!.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWriting();
        }
    }

    private void ReleaseWriting()
    {
        try
        {
            _writing.Release();
        }
        catch (ObjectDisposedException)
        {
            // Le client a été disposé pendant l'écriture, qui a elle-même levé
            // sur le flux fermé : c'est cette exception-là que l'appelant doit voir.
        }
    }

    private async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[sizeof(int)];

        if (await ReadExactlyAsync(header, ct).ConfigureAwait(false) is false)
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        // Une longueur venant du réseau ne s'alloue jamais telle quelle.
        if (length is <= 0 or > RendezvousWire.MaxFrameLength)
            return null;

        var body = new byte[length];
        return await ReadExactlyAsync(body, ct).ConfigureAwait(false) ? body : null;
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await _stream!.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);

            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        // Le flux d'abord : une écriture en cours lève et rend le sémaphore,
        // qu'on peut alors libérer sans laisser d'attente suspendue.
        _stream?.Dispose();
        _tcp.Dispose();
        _writing.Dispose();
        return ValueTask.CompletedTask;
    }
}
