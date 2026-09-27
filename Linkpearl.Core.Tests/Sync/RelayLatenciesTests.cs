using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayLatenciesTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    }

    private static RelayPlace Place(string host) => new(new RendezvousAddress(host, 47900), null, true);

    private readonly FakeClock _clock = new();
    private readonly Dictionary<string, Queue<TimeSpan?>> _answers = [];
    private readonly List<string> _pinged = [];

    private RelayLatencies Latencies() => new(_clock, (place, _, _) =>
    {
        _pinged.Add(place.At.Host);
        return Task.FromResult(_answers.TryGetValue(place.At.Host, out var queue) && queue.Count > 0 ? queue.Dequeue() : null);
    });

    [Fact]
    public async Task Le_meilleur_de_trois_essais_est_garde()
    {
        _answers["a"] = new([TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(12), null]);
        var latencies = Latencies();

        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);

        Assert.Equal((ushort)12, Assert.Single(latencies.MeasurementsFor([Place("a")])).RttMs);
        Assert.Equal(3, _pinged.Count);
    }

    [Fact]
    public async Task Trois_silences_font_un_service_injoignable()
    {
        var latencies = Latencies();

        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(RelayMeasurement.Unreachable, Assert.Single(latencies.MeasurementsFor([Place("a")])).RttMs);
    }

    [Fact]
    public async Task Une_mesure_fraiche_n_est_pas_refaite_et_une_vieille_disparait()
    {
        _answers["a"] = new([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);
        var latencies = Latencies();

        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);
        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);
        Assert.Equal(3, _pinged.Count);

        _clock.UtcNow += RelayLatencies.Freshness + TimeSpan.FromSeconds(1);
        Assert.Empty(latencies.MeasurementsFor([Place("a")]));
    }

    [Fact]
    public void Un_service_jamais_mesure_est_absent()
        => Assert.Empty(Latencies().MeasurementsFor([Place("a")]));

    [Fact]
    public async Task Un_refus_vaut_injoignable_pendant_vingt_quatre_heures()
    {
        _answers["a"] = new([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);
        var latencies = Latencies();
        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);

        latencies.MarkRefused(Place("a").At);
        Assert.Equal(RelayMeasurement.Unreachable, Assert.Single(latencies.MeasurementsFor([Place("a")])).RttMs);

        _clock.UtcNow += RelayLatencies.RefusalMemory + TimeSpan.FromSeconds(1);
        Assert.Empty(latencies.MeasurementsFor([Place("a")]));
    }

    [Fact]
    public async Task Un_cycle_ne_mesure_pas_plus_de_soixante_quatre_services()
    {
        var latencies = Latencies();

        await latencies.RefreshAsync(
            Enumerable.Range(0, 100).Select(i => Place($"s{i}")), CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(RelayLatencies.MaxPerRound * RelayLatencies.Attempts, _pinged.Count);
    }

    [Fact]
    public async Task Au_plus_seize_mesures_partent_les_plus_proches_d_abord()
    {
        var places = Enumerable.Range(0, 20).Select(i => Place($"s{i}")).ToList();

        foreach (var (place, i) in places.Select((place, i) => (place, i)))
            _answers[place.At.Host] = new([TimeSpan.FromMilliseconds(100 - i), null, null]);

        var latencies = Latencies();
        await latencies.RefreshAsync(places, CancellationToken.None, TimeSpan.Zero);

        var sent = latencies.MeasurementsFor(places);

        Assert.Equal(RelayMeasurements.MaxMeasurements, sent.Count);
        Assert.Equal(sent.OrderBy(m => m.RttMs).Select(m => m.RttMs), sent.Select(m => m.RttMs));
        Assert.Equal((ushort)81, sent[0].RttMs);
    }
}
