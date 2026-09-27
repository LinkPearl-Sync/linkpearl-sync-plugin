using Dalamud.Plugin.Services;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Integration;

/// <summary>
/// Redemande la liste signée à l'autorité toutes les six heures.
/// </summary>
/// <remarks>
/// La dernière liste acceptée est gardée sur disque : un démarrage sans
/// réseau, ou avec l'autorité tombée, repart de là tant qu'elle n'a pas
/// expiré. Un échec ne retire jamais la liste en place.
/// </remarks>
public sealed class ConsensusFetcher(OpenCircle circle, string path, IPluginLog log) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Start()
    {
        LoadSaved();
        _ = Task.Run(() => LoopAsync(_life.Token));
    }

    public void RefreshSoon()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Déjà réveillé : une seule récupération suffit.
        }
    }

    private void LoadSaved()
    {
        try
        {
            if (File.Exists(path) && circle.Offer(File.ReadAllBytes(path), out var why) is false)
                log.Information($"Liste signée enregistrée écartée : {why}");
        }
        catch (IOException e)
        {
            log.Warning($"Liste signée enregistrée illisible : {e.Message}");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            await FetchAsync(ct).ConfigureAwait(false);

            try
            {
                await _wake.WaitAsync(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Patience);

            await using var client = new RendezvousClient();
            await client.ConnectAsync(RendezvousList.Authority.Host, RendezvousList.Authority.Port, deadline.Token).ConfigureAwait(false);

            var (document, failure) = await client.QueryConsensusAsync(deadline.Token).ConfigureAwait(false);

            if (document is null)
            {
                log.Information($"Liste signée indisponible : {failure}");
                return;
            }

            if (circle.Offer(document, out var why) is false)
            {
                log.Warning($"Liste signée refusée : {why}");
                return;
            }

            var temporary = path + ".part";
            File.WriteAllBytes(temporary, document);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (ct.IsCancellationRequested is false)
        {
            log.Information($"Autorité du réseau ouvert injoignable : {e.Message}");
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
        _wake.Dispose();
    }
}
