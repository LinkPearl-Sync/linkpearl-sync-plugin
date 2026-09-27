using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Ce qui sait où une paire se retrouve dans le cercle ouvert.</summary>
public interface IOpenCircle
{
    /// <summary>Les services ouverts de cette paire, ou rien si elle doit rester dans l'ancrage.</summary>
    IReadOnlyList<RendezvousAddress> PlacesFor(PairRecord pair);

    /// <summary>La liste où puiser les relais de cette paire, vide si elle n'a pas droit au cercle ouvert.</summary>
    IReadOnlyList<ConsensusEntry> RelayEntriesFor(PairRecord pair) => [];
}

/// <summary>
/// La liste signée que ce client tient pour valable, et le droit d'y passer.
/// </summary>
/// <remarks>
/// Le cercle ouvert est un gain, jamais une dépendance : sans liste, avec une
/// liste expirée ou désactivé, il ne donne rien, et tout passe par l'ancrage
/// comme avant son existence.
/// </remarks>
public sealed class OpenCircle(IReadOnlyList<byte[]> trustedKeys, IClock clock) : IOpenCircle
{
    private readonly Lock _gate = new();
    private ServiceConsensus? _list;

    public bool Enabled { get; set; } = true;

    public ServiceConsensus? Current
    {
        get
        {
            lock (_gate)
                return _list is { } list && clock.UtcNow.ToUnixTimeSeconds() < list.Expires ? list : null;
        }
    }

    public bool Offer(ReadOnlySpan<byte> document, out string? rejection)
    {
        if (ServiceConsensus.TryVerify(document, trustedKeys, clock.UtcNow.ToUnixTimeSeconds(), out var list, out rejection) is false)
            return false;

        lock (_gate)
        {
            // Une liste plus ancienne que celle qu'on tient ne remplace rien :
            // sans cette règle, rejouer une vieille liste signée ramènerait
            // des services que l'autorité a depuis écartés. Une liste détenue
            // mais expirée ne protège plus rien : la faire valoir bloquerait
            // une autorité repartie plus bas jusqu'au rechargement du plugin.
            if (_list is { } held && clock.UtcNow.ToUnixTimeSeconds() < held.Expires && list!.Version < held.Version)
            {
                rejection = $"version {list.Version} antérieure à celle détenue ({held.Version})";
                return false;
            }

            _list = list;
        }

        return true;
    }

    public IReadOnlyList<RendezvousAddress> PlacesFor(PairRecord pair)
        => Enabled && MayUse(pair) && Current is { } list ? ServicePlacement.Choose(pair.PairSecret, list.Entries) : [];

    public IReadOnlyList<ConsensusEntry> RelayEntriesFor(PairRecord pair)
        => Enabled && MayUse(pair) && Current is { } list ? list.Entries : [];

    /// <summary>
    /// Vrai si la clé de ce pair ne peut plus être substituée par un service.
    /// </summary>
    /// <remarks>
    /// Une paire du carnet l'est toujours : son identifiant est l'empreinte de
    /// sa clé, qu'une session vérifie. Un membre de groupe ne l'est qu'une
    /// fois épinglé, et jamais dans le groupe Public, dont le secret connu de
    /// tous rend la place de chacun calculable par n'importe qui.
    /// </remarks>
    public static bool MayUse(PairRecord pair)
        => pair.Group is not { } origin || (origin.Pinned && origin.Public is false);
}
