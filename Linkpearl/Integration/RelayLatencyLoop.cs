using Dalamud.Plugin.Services;
using Linkpearl.Core.Sync;

namespace Linkpearl.Integration;

/// <summary>
/// Fait tourner les mesures de latence hors du thread du jeu.
/// </summary>
/// <remarks>
/// Le carnet est lu par la boucle de rafraîchissement du plugin : c'est elle
/// qui calcule les services à mesurer et les confie ici, ce qui réveille la
/// boucle.
/// </remarks>
public sealed class RelayLatencyLoop(RelayLatencies latencies, IPluginLog log) : IDisposable
{
    /// <summary>Tous les combien la boucle du plugin recalcule les services à mesurer.</summary>
    public static readonly TimeSpan TargetsEvery = TimeSpan.FromMinutes(5);

    // Un essai dure au plus une seconde : au-delà, la boucle ne répond plus à
    // l'annulation et on renonce plutôt que de bloquer le déchargement.
    private static readonly TimeSpan StopPatience = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IReadOnlyList<RelayPlace> _targets = [];
    private Task _loop = Task.CompletedTask;

    public void Start() => _loop = Task.Run(() => LoopAsync(_life.Token));

    public void Offer(IReadOnlyList<RelayPlace> targets)
    {
        Volatile.Write(ref _targets, targets);

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Déjà réveillée : elle lira la liste la plus récente.
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            try
            {
                await latencies.RefreshAsync(Volatile.Read(ref _targets), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (ct.IsCancellationRequested is false)
            {
                log.Information($"Mesure des relais interrompue : {e.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await _wake.WaitAsync(TargetsEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _life.Cancel();

        // Le contexte de chargement de Dalamud est collectible : une boucle
        // encore vivante ferait fuir le plugin. On l'attend avant de libérer
        // ce qu'elle tient.
        try
        {
            // Pas encore finie : on lui laisse son sémaphore plutôt que de le
            // libérer sous elle.
            if (_loop.Wait(StopPatience) is false)
                return;
        }
        catch (AggregateException)
        {
            // Annulée ou en échec : dans les deux cas, elle est terminée.
        }

        _life.Dispose();
        _wake.Dispose();
    }
}
