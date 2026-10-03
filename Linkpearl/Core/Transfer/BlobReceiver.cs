using System.Buffers.Binary;
using Linkpearl.Core.Cache;
using Linkpearl.Core.Protocol;
using Linkpearl.Core.Safety;

namespace Linkpearl.Core.Transfer;

/// <summary>Ce qu'une trame reçue a produit.</summary>
public sealed record ReceiveOutcome(
    bool Accepted, bool BlobCompleted = false, bool AlreadyPresent = false,
    BlobHash? Hash = null, string? Rejection = null)
{
    public static ReceiveOutcome Ok { get; } = new(true);

    public static ReceiveOutcome Refused(string reason) => new(false, Rejection: reason);
}

/// <summary>
/// Reçoit les blobs d'un pair, par tronçons.
/// </summary>
/// <remarks>
/// L'état d'un tronçon est tenu <em>par canal</em>, et c'est une contrainte du
/// transport et non un choix : LiteNetLib ne garantit l'ordre qu'à l'intérieur
/// d'un canal. L'annonce, les blocs et la clôture d'un tronçon voyagent donc
/// ensemble. Les tronçons d'un même blob, eux, arrivent par des canaux
/// différents et dans n'importe quel ordre : ils se rejoignent dans un
/// assemblage, qui n'est validé qu'une fois tous présents (voir
/// <see cref="BlobSegments"/>).
///
/// C'est aussi l'endroit où atterrit tout ce qu'un pair envoie, donc l'endroit
/// où l'on dit non :
///
/// <list type="number">
/// <item>seul ce que notre plan a demandé est accepté : un pair ne décide pas de
/// ce que l'on stocke ;</item>
/// <item>un tronçon à la fois par canal, et un nombre total plafonné, sinon un
/// pair ouvre mille écritures et épuise la mémoire et les descripteurs ;</item>
/// <item>un tronçon n'est accepté que s'il est exactement l'un de ceux du
/// découpage, une seule fois, et pour une taille de blob constante : aucune
/// zone ne peut être écrite deux fois ;</item>
/// <item>tout ce qui dépasse la longueur annoncée est refusé, sinon annoncer un
/// octet et en envoyer un gigaoctet remplirait le disque.</item>
/// </list>
///
/// Une faute sur un tronçon abandonne le blob entier : jamais d'apparence
/// partielle, ni de fichier à moitié juste.
/// </remarks>
/// <param name="requested">
/// Ce que notre plan a demandé, et la taille que le manifeste annonçait pour
/// chacun. Une annonce de blob doit porter exactement cette taille : c'est sur
/// elle que le manifeste a été jugé, et un pair qui la changerait en route
/// ferait réserver bien plus que ce que la validation a accepté.
/// </param>
public sealed class BlobReceiver(
    IBlobStore store, Quotas quotas, IReadOnlyDictionary<BlobHash, long> requested, int maxConcurrent = 64)
    : IAsyncDisposable
{
    private sealed class Assembly
    {
        public required IBlobAssembly Target { get; init; }
        public required BlobHash Hash { get; init; }
        public required long Size { get; init; }
        public required bool[] Done { get; init; }
        public HashSet<int> InFlight { get; } = [];
        public int Remaining { get; set; }
    }

    private sealed class Segment
    {
        public required BlobHash Hash { get; init; }
        public required long Offset { get; init; }
        public required long Length { get; init; }
        public required int Index { get; init; }

        /// <summary>Null quand le blob est déjà là ou a été abandonné : les blocs sont alors ignorés.</summary>
        public Assembly? Into { get; set; }

        public long Received { get; set; }
    }

    private readonly Dictionary<byte, Segment> _byChannel = [];
    private readonly Dictionary<BlobHash, Assembly> _assemblies = [];

    public int ActiveTransfers => _byChannel.Count;

    public async ValueTask<ReceiveOutcome> HandleAsync(
        byte channel, byte kind, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => kind switch
        {
            MessageKind.BlobStart => await StartAsync(channel, payload, ct).ConfigureAwait(false),
            MessageKind.BlobChunk => await ChunkAsync(channel, payload, ct).ConfigureAwait(false),
            MessageKind.BlobEnd => await EndAsync(channel, payload, ct).ConfigureAwait(false),
            _ => ReceiveOutcome.Refused($"type de message inattendu dans un transfert ({kind:X2})"),
        };

    private async ValueTask<ReceiveOutcome> StartAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // Le pair tourne une version d'avant les tronçons : le dire, plutôt
        // qu'un « malformé » que personne ne saurait relier à une mise à jour.
        if (payload.Length == BlobSegments.LegacyStartLength)
            return ReceiveOutcome.Refused("annonce d'un format antérieur : ce pair doit mettre Linkpearl à jour");

        if (payload.Length != BlobSegments.StartLength)
            return ReceiveOutcome.Refused("annonce de blob malformée");

        if (_byChannel.ContainsKey(channel))
            return ReceiveOutcome.Refused($"un transfert est déjà en cours sur le canal {channel}");

        if (_byChannel.Count >= maxConcurrent)
            return ReceiveOutcome.Refused($"trop de transferts simultanés (plafond {maxConcurrent})");

        var (hash, size, offset, length) = BlobSegments.ReadStart(payload.Span);

        if (requested.TryGetValue(hash, out var expectedSize) is false)
            return ReceiveOutcome.Refused($"blob non demandé : {hash}");

        if (size < 0 || size > quotas.MaxBlobBytes)
            return ReceiveOutcome.Refused($"taille hors plafond ({size}, plafond {quotas.MaxBlobBytes})");

        if (size != expectedSize)
            return ReceiveOutcome.Refused($"taille annoncée {size} pour {hash}, le manifeste disait {expectedSize}");

        if (BlobSegments.IsValid(size, offset, length) is false)
            return ReceiveOutcome.Refused($"tronçon hors découpage ({offset}+{length} pour {size} octets)");

        var index = (int)(offset / BlobSegments.SegmentSize);

        // Déjà en cache : il n'y a rien à écrire. Le tronçon est suivi pour que
        // ses blocs soient ignorés sans bruit, et le dire évite au pair de nous
        // envoyer le reste pour rien.
        if (store.TryGetSize(hash, out _))
        {
            _byChannel[channel] = new Segment { Hash = hash, Offset = offset, Length = length, Index = index };
            return new ReceiveOutcome(true, AlreadyPresent: true, Hash: hash);
        }

        if (_assemblies.TryGetValue(hash, out var assembly))
        {
            if (assembly.Size != size)
            {
                await AbandonAsync(assembly).ConfigureAwait(false);
                return ReceiveOutcome.Refused($"taille annoncée changeante pour {hash} ({size} après {assembly.Size})");
            }
        }
        else
        {
            if (_assemblies.Count >= maxConcurrent)
                return ReceiveOutcome.Refused($"trop de blobs en cours d'assemblage (plafond {maxConcurrent})");

            var count = BlobSegments.CountFor(size);

            assembly = new Assembly
            {
                Target = await store.BeginAssemblyAsync(hash, size, ct).ConfigureAwait(false),
                Hash = hash,
                Size = size,
                Done = new bool[count],
                Remaining = count,
            };

            _assemblies[hash] = assembly;
        }

        if (assembly.Done[index] || assembly.InFlight.Add(index) is false)
            return ReceiveOutcome.Refused($"tronçon {index} de {hash} déjà reçu ou en cours");

        _byChannel[channel] = new Segment { Hash = hash, Offset = offset, Length = length, Index = index, Into = assembly };

        return ReceiveOutcome.Ok;
    }

    private async ValueTask<ReceiveOutcome> ChunkAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length < sizeof(uint))
            return ReceiveOutcome.Refused("bloc malformé");

        if (_byChannel.TryGetValue(channel, out var segment) is false)
            return ReceiveOutcome.Refused($"bloc reçu sans annonce préalable sur le canal {channel}");

        var data = payload[sizeof(uint)..];

        if (segment.Received + data.Length > segment.Length)
        {
            _byChannel.Remove(channel);

            if (segment.Into is { } overflowing)
                await AbandonAsync(overflowing).ConfigureAwait(false);

            return ReceiveOutcome.Refused(
                $"envoi au-delà de la taille annoncée ({segment.Received + data.Length} pour {segment.Length})");
        }

        if (segment.Into is { } assembly)
            await assembly.Target.WriteAtAsync(segment.Offset + segment.Received, data, ct).ConfigureAwait(false);

        segment.Received += data.Length;

        return ReceiveOutcome.Ok;
    }

    private async ValueTask<ReceiveOutcome> EndAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length != BlobHash.SizeInBytes)
            return ReceiveOutcome.Refused("clôture de blob malformée");

        if (_byChannel.Remove(channel, out var segment) is false)
            return ReceiveOutcome.Refused($"clôture reçue sans annonce préalable sur le canal {channel}");

        var hash = BlobHash.FromBytes(payload.Span);

        if (hash != segment.Hash)
        {
            if (segment.Into is { } mismatched)
                await AbandonAsync(mismatched).ConfigureAwait(false);

            return ReceiveOutcome.Refused("clôture portant une autre empreinte que l'annonce");
        }

        // Déjà en cache, ou abandonné en route : rien à valider.
        if (segment.Into is not { } assembly)
            return ReceiveOutcome.Ok;

        if (segment.Received != segment.Length)
        {
            await AbandonAsync(assembly).ConfigureAwait(false);
            return ReceiveOutcome.Refused($"tronçon incomplet ({segment.Received} octets pour {segment.Length})");
        }

        assembly.InFlight.Remove(segment.Index);
        assembly.Done[segment.Index] = true;
        assembly.Remaining--;

        if (assembly.Remaining > 0)
            return ReceiveOutcome.Ok;

        _assemblies.Remove(hash);

        // C'est le cache qui vérifie l'empreinte du fichier complet : un blob
        // n'est publié que s'il correspond à ce qui était annoncé.
        var result = await assembly.Target.CommitAsync(ct).ConfigureAwait(false);
        await assembly.Target.DisposeAsync().ConfigureAwait(false);

        return result.Accepted
            ? new ReceiveOutcome(true, BlobCompleted: true, Hash: hash)
            : ReceiveOutcome.Refused(result.Rejection!);
    }

    /// <summary>
    /// Abandonne un blob entier. Ses autres tronçons en vol restent suivis, sans
    /// cible : leurs blocs sont ignorés au lieu de produire un refus chacun.
    /// </summary>
    private async ValueTask AbandonAsync(Assembly assembly)
    {
        _assemblies.Remove(assembly.Hash);

        foreach (var segment in _byChannel.Values)
        {
            if (ReferenceEquals(segment.Into, assembly))
                segment.Into = null;
        }

        assembly.Target.Abort();
        await assembly.Target.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var assembly in _assemblies.Values.ToList())
            await AbandonAsync(assembly).ConfigureAwait(false);

        _byChannel.Clear();
    }
}
