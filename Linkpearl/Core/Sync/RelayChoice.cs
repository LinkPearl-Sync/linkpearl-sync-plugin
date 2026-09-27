namespace Linkpearl.Core.Sync;

/// <summary>Le relais retenu, et de quoi dire pourquoi.</summary>
public readonly record struct RelayDecision(ulong Service, int? WorstMs, int? MatchedWorstMs);

/// <summary>
/// Le relais d'une paire, décidé à l'identique des deux côtés.
/// </summary>
/// <remarks>
/// Chaque côté a ses mesures et celles du pair : la décision n'en dépend que
/// par des opérations symétriques (max, somme, intersection), donc les deux
/// calculent le même service sans se consulter, comme pour le choix entre
/// perçage et relais.
///
/// L'hystérésis tient à ce qu'un relais ne se quitte que pour mieux : deux
/// services équivalents se disputeraient sinon au gré du bruit des mesures.
/// </remarks>
public static class RelayChoice
{
    public const int MinimumGainMs = 20;
    public const double MinimumGainRatio = 0.2;

    public static RelayDecision Decide(
        IReadOnlyList<RelayMeasurement>? mine, IReadOnlyList<RelayMeasurement>? theirs, ulong matched)
    {
        if (mine is null || theirs is null)
            return new RelayDecision(matched, null, null);

        var their = new Dictionary<ulong, ushort>();

        foreach (var measure in theirs)
            their.TryAdd(measure.Service, measure.RttMs);

        var seen = new HashSet<ulong>();
        var candidates = new List<(ulong Service, int Worst, int Total)>();

        foreach (var measure in mine)
        {
            if (seen.Add(measure.Service) is false || their.TryGetValue(measure.Service, out var other) is false)
                continue;

            if (measure.RttMs == RelayMeasurement.Unreachable || other == RelayMeasurement.Unreachable)
                continue;

            candidates.Add((measure.Service, Math.Max(measure.RttMs, other), measure.RttMs + other));
        }

        if (candidates.Count == 0)
            return new RelayDecision(matched, null, null);

        var best = candidates.OrderBy(c => c.Worst).ThenBy(c => c.Total).ThenBy(c => c.Service).First();
        var current = candidates.FindIndex(c => c.Service == matched);

        if (current < 0)
            return new RelayDecision(best.Service, best.Worst, null);

        var worst = candidates[current].Worst;
        var gain = worst - best.Worst;

        return gain >= MinimumGainMs && gain >= worst * MinimumGainRatio
            ? new RelayDecision(best.Service, best.Worst, worst)
            : new RelayDecision(matched, worst, worst);
    }
}
