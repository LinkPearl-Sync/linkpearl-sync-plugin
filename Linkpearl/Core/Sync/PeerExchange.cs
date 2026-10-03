using System.Buffers.Binary;
using System.Threading.Channels;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Manifest;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transfer;
using Linkpearl.Core.Transport;

namespace Linkpearl.Core.Sync;

/// <summary>Ce que l'on sait d'un pair à un instant donné.</summary>
/// <param name="ManifestHash">
/// L'empreinte de <paramref name="Manifest"/>, calculée une fois à la réception.
/// Le moteur la compare à chaque tic et pour chaque pair : la recalculer là
/// réencodait le manifeste entier à chaque passage.
/// </param>
public sealed record PeerView(
    PlayerFingerprint? Fingerprint,
    CharacterManifest? Manifest,
    BlobHash? AnnouncedManifest,
    long MissingBytes,
    long ReceivedBytes,
    bool Ready,
    BlobHash? ManifestHash = null);

/// <summary>
/// Le dialogue avec un pair : présence, manifeste, blobs.
/// </summary>
/// <remarks>
/// Une instance par session. Tout ce qui arrive du réseau passe par ici et
/// n'atteint le jeu qu'une fois le manifeste validé et tous les blobs vérifiés :
/// le moteur ne pose jamais une apparence partielle.
/// </remarks>
public sealed class PeerExchange : IAsyncDisposable
{
    private readonly PeerSession _session;
    private readonly IBlobStore _store;
    private readonly ILocalAppearance _local;
    private readonly RateLimiter _limiter;
    private readonly ChannelPlan _channels;
    private readonly ILogSink _log;
    private readonly Quotas _quotas;
    private readonly int _blockSize;
    private readonly Func<TransientCategories> _receive;
    private readonly Func<bool> _mayShare;

    /// <summary>
    /// Paquets qu'on laisse s'accumuler dans la file d'un canal avant d'attendre.
    /// </summary>
    /// <remarks>
    /// Soit la fenêtre fiable de LiteNetLib, soixante-quatre paquets, ce qui
    /// borne l'avance à environ un mébioctet par canal. Plus haut ne fait pas
    /// aller plus vite, la fenêtre ne s'ouvrant pas davantage, et coûte
    /// directement en mémoire dans le processus du jeu.
    /// </remarks>
    private const int QueuedPacketsPerChannel = 64;

    /// <summary>
    /// Demandes de blobs en attente de service, déjà triées.
    /// </summary>
    /// <remarks>
    /// Bornée : un manifeste au plafond de remplacements tient en huit
    /// demandes de 256 empreintes, et le double laisse passer une
    /// réapplication. Au-delà, c'est un pair qui inonde, et ses demandes
    /// tombent au lieu de s'entasser dans le processus du jeu.
    /// </remarks>
    private readonly Channel<IReadOnlyList<BlobHash>> _wanted = Channel.CreateBounded<IReadOnlyList<BlobHash>>(
        new BoundedChannelOptions(WantQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public const int WantQueueCapacity = 16;

    /// <summary>
    /// Fois qu'un même blob peut être mis en service dans une session.
    /// </summary>
    /// <remarks>
    /// Deux et non un : un blob reçu peut se perdre chez le pair (empreinte
    /// fausse après une erreur disque, éviction sous la pression du quota), et
    /// une réapplication le redemande alors légitimement. Sans plafond, un pair
    /// qui redemande en boucle nous ferait téléverser notre apparence sans fin.
    /// </remarks>
    public const int MaxServesPerBlob = 2;

    /// <summary>
    /// Les blobs du dernier manifeste envoyé à ce pair : les seuls qu'on lui sert.
    /// </summary>
    /// <remarks>
    /// Servir tout ce que le cache contient ferait de nous un oracle (« as-tu
    /// croisé le joueur qui porte ce mod ? ») et une source des mods des autres
    /// pairs. Écrit et lu par la seule pompe de la session.
    /// </remarks>
    private HashSet<BlobHash> _shared = [];

    /// <summary>Combien de fois chaque blob a été mis en service dans cette session.</summary>
    private readonly Dictionary<BlobHash, int> _serves = [];

    /// <summary>
    /// Demandes de manifeste envoyées et pas encore servies.
    /// </summary>
    /// <remarks>
    /// Un manifeste non sollicité est ignoré : chacun coûte une décompression,
    /// une validation et un plan, et remettait à zéro la réception en cours.
    /// Un compte et non un drapeau : deux annonces rapprochées font partir deux
    /// demandes, et la seconde réponse, la plus fraîche, doit passer aussi.
    /// Incrémenté par le tic (réapplication) comme par la pompe, d'où
    /// l'accès atomique.
    /// </remarks>
    private int _manifestsAwaited;

    /// <summary>Une demande de manifeste arrivée avant la présence du pair, servie à son arrivée.</summary>
    /// <remarks>Lu et écrit par la seule pompe de la session.</remarks>
    private bool _requestDeferred;

    private BlobReceiver? _receiver;
    private TransferPlan? _plan;
    private long _received;

    /// <summary>Le hash du manifeste tel que le pair l'a envoyé, avant filtrage.</summary>
    /// <remarks>
    /// C'est lui que le pair annonce. Comparer l'annonce au manifeste filtré
    /// ferait redemander le manifeste à chaque présence dès qu'une catégorie
    /// est bloquée.
    /// </remarks>
    private BlobHash? _receivedHash;

    public PeerExchange(
        PeerSession session, IBlobStore store, ILocalAppearance local,
        RateLimiter limiter, int dataChannels, int blockSize, Quotas quotas, ILogSink log,
        Func<TransientCategories>? receive = null, Func<bool>? mayShare = null)
    {
        _session = session;
        _store = store;
        _local = local;
        _limiter = limiter;
        _channels = new ChannelPlan(dataChannels);
        _blockSize = blockSize;
        _quotas = quotas;
        _log = log;
        _receive = receive ?? (() => TransientCategories.All);
        _mayShare = mayShare ?? (() => true);
    }

    public PeerView View { get; private set; } = new(null, null, null, 0, 0, false);

    /// <summary>Annonce notre présence et notre apparence courante.</summary>
    public async Task HelloAsync(CancellationToken ct)
    {
        var manifest = await _local.CurrentAsync(ct).ConfigureAwait(false);
        var fingerprint = _local.Fingerprint;

        var payload = new byte[PlayerFingerprint.SizeInBytes + BlobHash.SizeInBytes];

        (fingerprint ?? default).ToBytes().CopyTo(payload.AsSpan());

        // Sans droit d'envoi, aucune empreinte : même nos changements de
        // tenue ne regardent pas un pair à qui l'on ne montre rien.
        if (manifest is not null && _mayShare())
            ManifestCodec.HashOf(manifest).TryWriteTo(payload.AsSpan(PlayerFingerprint.SizeInBytes));

        await _session.SendAsync(ChannelPlan.ControlChannel, MessageKind.Hello, payload, ct).ConfigureAwait(false);
    }

    /// <summary>Traite une trame reçue du pair.</summary>
    public async Task HandleAsync(PeerMessage message, CancellationToken ct)
    {
        switch (message.Kind)
        {
            case MessageKind.Hello:
            case MessageKind.Presence:
                await OnHelloAsync(message.Payload, ct).ConfigureAwait(false);
                break;

            case MessageKind.ManifestRequest:
                await OnManifestRequestAsync(ct).ConfigureAwait(false);
                break;

            case MessageKind.ManifestData:
                await OnManifestDataAsync(message.Payload, ct).ConfigureAwait(false);
                break;

            case MessageKind.BlobWant:
                // Trié ici, dans l'ordre des trames de contrôle, donc contre le
                // manifeste que le pair a vraiment reçu ; servi ailleurs, voir
                // ServeAsync.
                OnBlobWant(message.Payload);
                break;

            case MessageKind.BlobStart:
            case MessageKind.BlobChunk:
            case MessageKind.BlobEnd:
                await OnBlobFrameAsync(message, ct).ConfigureAwait(false);
                break;

            default:
                _log.Debug($"Trame de type inconnu ignorée ({message.Kind:X2}).");
                break;
        }
    }

    private async Task OnHelloAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length < PlayerFingerprint.SizeInBytes + BlobHash.SizeInBytes)
            return;

        var fingerprint = PlayerFingerprint.FromBytes(payload.AsSpan(0, PlayerFingerprint.SizeInBytes));
        var announced = BlobHash.FromBytes(payload.AsSpan(PlayerFingerprint.SizeInBytes, BlobHash.SizeInBytes));

        View = View with { Fingerprint = fingerprint, AnnouncedManifest = announced };

        // Sa demande est arrivée avant sa présence : maintenant qu'on sait qui
        // il est, on peut décider de lui répondre.
        if (_requestDeferred)
        {
            _requestDeferred = false;
            await OnManifestRequestAsync(ct).ConfigureAwait(false);
        }

        // Rien ne change : inutile de redemander un manifeste identique, et
        // c'est tout l'intérêt de l'adressage par contenu.
        if (View.Manifest is not null && _receivedHash == announced)
            return;

        await RequestManifestAsync(ct).ConfigureAwait(false);
    }

    private ValueTask RequestManifestAsync(CancellationToken ct)
    {
        // Levé avant l'envoi : la réponse peut arriver avant que SendAsync rende.
        Interlocked.Increment(ref _manifestsAwaited);
        return _session.SendAsync(ChannelPlan.ControlChannel, MessageKind.ManifestRequest, ReadOnlyMemory<byte>.Empty, ct);
    }

    /// <summary>Décompte une demande en attente, s'il y en a une.</summary>
    private bool TryConsumeAwaitedManifest()
    {
        while (true)
        {
            var awaited = Volatile.Read(ref _manifestsAwaited);

            if (awaited <= 0)
                return false;

            if (Interlocked.CompareExchange(ref _manifestsAwaited, awaited - 1, awaited) == awaited)
                return true;
        }
    }

    /// <summary>Redemande le manifeste courant du pair, quoi qu'on en sache.</summary>
    /// <remarks>
    /// Pour « réappliquer » : le pair répond avec ce qu'il montre maintenant, et
    /// la réception repart de zéro, blobs manquants compris. Ce que le cache a
    /// déjà ne se retransfère pas.
    /// </remarks>
    public ValueTask RefreshAsync(CancellationToken ct) => RequestManifestAsync(ct);

    private async Task OnManifestRequestAsync(CancellationToken ct)
    {
        // Pas avant sa présence : c'est elle qui dit quel personnage il est,
        // et un personnage listé par un service ne reçoit rien. Le moteur
        // ouvre la réception avant d'annoncer, donc la demande d'un client
        // honnête devance parfois sa présence : elle attend, elle n'est pas
        // perdue. Une empreinte nulle compte pour absente : c'est celle d'un
        // client hors personnage, qui ne peut rien afficher, et un personnage
        // listé échapperait sinon au contrôle en taisant la sienne.
        if (View.Fingerprint is not { } known || known == default)
        {
            _requestDeferred = true;
            return;
        }

        // Pas de permission d'envoi, ou un personnage listé par un service :
        // rien ne part, et le pair ne peut pas distinguer ce silence de celui
        // d'un client qui n'a encore rien construit.
        if (_mayShare() is false)
        {
            _shared = [];
            _log.Debug("Demande de manifeste ignorée : envoi non autorisé vers ce pair.");
            return;
        }

        var manifest = await _local.CurrentAsync(ct).ConfigureAwait(false);

        if (manifest is null)
            return;

        // Retenu avant l'envoi : les demandes de blobs qui suivent la réponse
        // se trient contre ce manifeste-ci, pas contre le précédent.
        _shared = manifest.Replacements.Select(r => r.Hash).ToHashSet();

        await _session.SendAsync(
            ChannelPlan.ControlChannel, MessageKind.ManifestData, ManifestCodec.Compress(manifest), ct)
            .ConfigureAwait(false);
    }

    private async Task OnManifestDataAsync(byte[] payload, CancellationToken ct)
    {
        if (TryConsumeAwaitedManifest() is false)
        {
            _log.Debug("Manifeste non sollicité ignoré.");
            return;
        }

        if (ManifestCodec.TryDecompress(payload, _quotas, out var manifest, out var why) is false)
        {
            _log.Warning($"Manifeste illisible : {why}");
            return;
        }

        if (ManifestValidator.TryAccept(manifest!, _quotas, out var refus) is false)
        {
            _log.Warning($"Manifeste refusé : {refus}");
            return;
        }

        var receivedHash = ManifestCodec.HashOf(manifest!);

        // Avant le plan : ce qu'on a bloqué n'est ni téléchargé ni posé.
        var allowed = _receive();
        var kept = TransientPolicy.Filter(manifest!, allowed);
        var keptHash = receivedHash;

        if (ReferenceEquals(kept, manifest) is false)
        {
            _log.Info($"Manifeste filtré : {manifest!.Replacements.Count - kept.Replacements.Count} entrées écartées ({allowed}).");
            keptHash = ManifestCodec.HashOf(kept);
        }

        manifest = kept;

        var plan = BlobRequestPlanner.Plan(manifest, _store);

        // Rapporté au quota de ce cache-ci, que le plafond absolu du validateur
        // ignore : une apparence reçue est épinglée tant qu'elle se télécharge
        // ou s'affiche, et elle doit tenir avec la nôtre sous le quota, sans
        // quoi l'éviction n'aurait plus rien à retirer.
        if (plan.MissingBytes + plan.CachedBytes > _store.QuotaBytes / 2)
        {
            _log.Warning($"Manifeste refusé : {(plan.MissingBytes + plan.CachedBytes) / 1024 / 1024} Mo, "
                       + $"plus de la moitié du quota du cache ({_store.QuotaBytes / 1024 / 1024} Mo).");
            return;
        }

        _receivedHash = receivedHash;
        _plan = plan;
        _received = 0;

        View = View with
        {
            Manifest = manifest,
            ManifestHash = keptHash,
            MissingBytes = plan.MissingBytes,
            ReceivedBytes = 0,
            Ready = plan.Missing.Count == 0,
        };

        _log.Info($"Manifeste reçu : {plan.Missing.Count} blobs manquants, "
                + $"{plan.MissingBytes / 1024 / 1024} Mo, {plan.CachedBytes / 1024 / 1024} Mo déjà en cache.");

        if (plan.Missing.Count == 0)
            return;

        await using var receiver = _receiver;
        _receiver = new BlobReceiver(_store, _quotas, plan.Missing.ToDictionary(m => m.Hash, m => m.Size));

        await RequestMissingAsync(plan, ct).ConfigureAwait(false);
    }

    private async Task RequestMissingAsync(TransferPlan plan, CancellationToken ct)
    {
        // Par lots : un manifeste lourd compte des centaines d'entrées, et une
        // trame unique dépasserait la taille utile d'un message.
        foreach (var batch in plan.Missing.Chunk(256))
        {
            var payload = new byte[2 + (batch.Length * BlobHash.SizeInBytes)];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)batch.Length);

            for (var i = 0; i < batch.Length; i++)
                batch[i].Hash.TryWriteTo(payload.AsSpan(2 + (i * BlobHash.SizeInBytes)));

            await _session.SendAsync(ChannelPlan.ControlChannel, MessageKind.BlobWant, payload, ct)
                          .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sert les blobs demandés, sur sa propre tâche.
    /// </summary>
    /// <remarks>
    /// Séparé de la réception, et c'est une condition de fonctionnement et non
    /// un confort : servir une apparence prend des minutes, et tant que cela
    /// dure les trames que le pair nous envoie ne seraient pas lues. Elles
    /// s'accumuleraient en mémoire dans le processus du jeu, à hauteur de ce
    /// qu'il nous transfère en même temps, soit plusieurs centaines de
    /// mégaoctets. Deux joueurs qui se découvrent se servent l'un l'autre en
    /// même temps : c'est le cas courant, pas le cas rare.
    /// </remarks>
    public async Task ServeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var wanted in _wanted.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await ServeWantedAsync(wanted, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Trie une demande de blobs : seuls ceux du manifeste envoyé à ce pair,
    /// et chacun un nombre borné de fois.
    /// </summary>
    private void OnBlobWant(byte[] payload)
    {
        if (payload.Length < 2)
            return;

        var count = BinaryPrimitives.ReadUInt16BigEndian(payload);

        if (payload.Length < 2 + (count * BlobHash.SizeInBytes))
            return;

        // Relu à chaque demande : une permission retirée ou un bannissement
        // arrivé en cours de session coupe le service sans attendre.
        if (_mayShare() is false)
        {
            _log.Debug("Demande de blobs ignorée : envoi non autorisé vers ce pair.");
            return;
        }

        var wanted = new List<BlobHash>(Math.Min((int)count, _shared.Count));
        var refused = 0;

        for (var i = 0; i < count; i++)
        {
            var hash = BlobHash.FromBytes(payload.AsSpan(2 + (i * BlobHash.SizeInBytes), BlobHash.SizeInBytes));
            var served = _serves.GetValueOrDefault(hash);

            if (_shared.Contains(hash) is false || served >= MaxServesPerBlob)
            {
                refused++;
                continue;
            }

            _serves[hash] = served + 1;
            wanted.Add(hash);
        }

        // Sans l'empreinte demandée : la redire dans le journal aiderait qui
        // cherche à savoir ce que contient notre cache.
        if (refused > 0)
            _log.Debug($"Demande de blobs : {refused} empreinte(s) hors de notre manifeste ou déjà servies, ignorée(s).");

        if (wanted.Count == 0)
            return;

        if (_wanted.Writer.TryWrite(wanted) is false)
        {
            // Rendues : elles n'ont pas été servies.
            foreach (var hash in wanted)
                _serves[hash]--;

            _log.Debug("Demande de blobs ignorée : file de service pleine.");
        }
    }

    private async Task ServeWantedAsync(IReadOnlyList<BlobHash> wanted, CancellationToken ct)
    {
        var sender = new BlobSender(_store, _blockSize);

        // Plusieurs blobs de front, un par canal, et jamais plus que de canaux.
        //
        // La fenêtre fiable de LiteNetLib est une constante de soixante-quatre
        // paquets par canal : servir les blobs l'un après l'autre n'en remplit
        // qu'une à la fois, et le transfert plafonne au débit d'un canal unique
        // quel que soit le nombre de canaux ouverts. Mesuré sur une apparence
        // réelle de 405 Mo en boucle locale : 3,3 Mo/s à un canal comme à
        // vingt-quatre, tant que le service restait séquentiel.
        //
        // Et par tronçons plutôt que par blobs : un blob de 85 Mo tenant sur un
        // seul canal en devenait la traîne, vingt secondes sur trente-huit à
        // 20 ms de latence. Découpé, il occupe tous les canaux libres.
        var segments = new List<(BlobHash Hash, long Offset, long Length)>();

        foreach (var hash in wanted)
        {
            if (_store.TryGetSize(hash, out var size) is false)
            {
                _log.Warning($"Blob demandé mais absent de notre cache : {hash}");
                continue;
            }

            foreach (var (offset, length) in BlobSegments.Of(size))
                segments.Add((hash, offset, length));
        }

        await Parallel.ForEachAsync(
            segments,
            new ParallelOptions { MaxDegreeOfParallelism = _channels.DataChannels, CancellationToken = ct },
            async (segment, token) => await ServeSegmentAsync(sender, segment, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>Envoie un tronçon, entier, sur un seul canal.</summary>
    /// <remarks>
    /// Sur un seul canal parce que le transport ne garantit l'ordre qu'à
    /// l'intérieur d'un canal, et parce que le receveur refuse un second
    /// tronçon sur un canal déjà occupé.
    /// </remarks>
    private async Task ServeSegmentAsync(
        BlobSender sender, (BlobHash Hash, long Offset, long Length) segment, CancellationToken ct)
    {
        // Revu à chaque tronçon, pas au milieu : un tronçon commencé se finit,
        // sans quoi le receveur garderait un canal occupé par un envoi mort.
        if (_mayShare() is false)
            return;

        // Au moins un octet imputé : un tronçon vide laisserait sinon son canal
        // paraître libre, et un second y partirait en même temps.
        var weight = (int)Math.Max(1, segment.Length);
        var channel = _channels.Next(weight);

        try
        {
            await foreach (var frame in sender.FramesFor(segment.Hash, segment.Offset, segment.Length, ct)
                                              .ConfigureAwait(false))
            {
                // Deux freins, et ils ne retiennent pas la même chose. Le
                // limiteur borne ce que l'on prend de la liaison montante, pour
                // que le ping du jeu ne parte pas à trois cents millisecondes.
                // La file du canal borne ce que l'on alloue d'avance dans le
                // processus du jeu : celle de LiteNetLib n'est pas bornée, et
                // sans ce frein vingt-quatre canaux servis de front y
                // entasseraient l'apparence entière.
                while (_limiter.TryConsume(frame.Payload.Length) is false
                    || _session.Link.PendingOn(channel) > QueuedPacketsPerChannel)
                {
                    if (_session.State is PeerSessionState.Disconnected)
                        return;

                    await Task.Delay(5, ct).ConfigureAwait(false);
                }

                await _session.SendAsync(channel, frame.Kind, frame.Payload, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _channels.Completed(channel, weight);
        }
    }

    private async Task OnBlobFrameAsync(PeerMessage message, CancellationToken ct)
    {
        if (_receiver is null)
            return;

        var outcome = await _receiver.HandleAsync(message.Channel, message.Kind, message.Payload, ct)
                                     .ConfigureAwait(false);

        if (outcome.Accepted is false)
        {
            _log.Warning($"Réception refusée : {outcome.Rejection}");
            return;
        }

        if (message.Kind == MessageKind.BlobChunk)
        {
            _received += message.Payload.Length - 4;
            View = View with { ReceivedBytes = _received };
        }

        if (outcome.BlobCompleted is false && outcome.AlreadyPresent is false)
            return;

        // Tout n'est prêt que lorsque chaque blob du manifeste est présent et
        // vérifié : on ne pose jamais une apparence partielle.
        if (_plan is { } plan && plan.Missing.All(m => _store.TryGetSize(m.Hash, out _)))
            View = View with { Ready = true };
    }

    public async ValueTask DisposeAsync()
    {
        _wanted.Writer.TryComplete();

        if (_receiver is not null)
            await _receiver.DisposeAsync().ConfigureAwait(false);
    }
}
