using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>
/// Le RTT vers chaque service éligible, mesuré en tâche de fond.
/// </summary>
/// <remarks>
/// Hors du chemin de connexion : on n'attend jamais un ping pour joindre un
/// pair. Un service pas encore mesuré n'est simplement pas proposé, et la
/// décision retombe sur le service d'appariement.
///
/// Le RTT vers un serveur bouge peu, d'où une demi-heure de fraîcheur. Un
/// service qui a refusé de relayer est déclaré injoignable une journée : la
/// liste ne dit pas qui relaie, et c'est ainsi que les deux pairs apprennent
/// à l'éviter.
/// </remarks>
public sealed class RelayLatencies(IClock clock, Func<RelayPlace, TimeSpan, CancellationToken, Task<TimeSpan?>> ping)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RefusalMemory = TimeSpan.FromHours(24);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);
    public const int Attempts = 3;
    public const int MaxPerRound = 64;

    /// <summary>L'écart entre deux services mesurés, pour étaler les pings.</summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, (ushort RttMs, DateTimeOffset At)> _measured = [];
    private readonly Dictionary<ulong, DateTimeOffset> _refused = [];

    public async Task RefreshAsync(IEnumerable<RelayPlace> targets, CancellationToken ct, TimeSpan? spacing = null)
    {
        var pause = spacing ?? Spacing;
        var due = targets.DistinctBy(place => place.Fingerprint).Where(IsStale).Take(MaxPerRound).ToList();

        foreach (var place in due)
        {
            TimeSpan? best = null;

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                TimeSpan? rtt;

                try
                {
                    rtt = await ping(place, AttemptTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested is false)
                {
                    // Un nom qui ne résout pas, une pile réseau absente : ce
                    // service est injoignable d'ici, ce n'est pas une panne.
                    rtt = null;
                }

                if (rtt is { } value && (best is null || value < best))
                    best = value;
            }

            lock (_gate)
                _measured[place.Fingerprint] = (RelayMeasurement.Of(place.Fingerprint, best).RttMs, clock.UtcNow);

            if (pause > TimeSpan.Zero)
                await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<RelayMeasurement> MeasurementsFor(IReadOnlyList<RelayPlace> eligible)
    {
        var now = clock.UtcNow;
        var found = new List<RelayMeasurement>();

        lock (_gate)
        {
            foreach (var place in eligible.DistinctBy(place => place.Fingerprint))
            {
                if (_refused.TryGetValue(place.Fingerprint, out var refusedAt) && now - refusedAt < RefusalMemory)
                    found.Add(new RelayMeasurement(place.Fingerprint, RelayMeasurement.Unreachable));
                else if (_measured.TryGetValue(place.Fingerprint, out var measured) && now - measured.At < Freshness)
                    found.Add(new RelayMeasurement(place.Fingerprint, measured.RttMs));
            }
        }

        return [.. found.OrderBy(m => m.RttMs).ThenBy(m => m.Service).Take(RelayMeasurements.MaxMeasurements)];
    }

    public void MarkRefused(RendezvousAddress at)
    {
        lock (_gate)
            _refused[RelayPlace.FingerprintOf(at)] = clock.UtcNow;
    }

    private bool IsStale(RelayPlace place)
    {
        lock (_gate)
            return _measured.TryGetValue(place.Fingerprint, out var measured) is false
                || clock.UtcNow - measured.At >= Freshness;
    }
}
