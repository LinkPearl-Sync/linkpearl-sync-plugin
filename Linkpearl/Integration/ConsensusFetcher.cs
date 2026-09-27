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
    // Une heure : c'est le délai qu'un joueur perçoit après chaque changement
    // de la liste, et le document tient en quelques centaines d'octets.
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
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

            // La v2 d'abord, qui porte les régions. Une autorité d'avant répond
            // « trame inattendue » et ferme : on redemande alors la v1, sur une
            // connexion neuve.
            //
            // Une coupure brutale après le refus ne doit pas davantage priver du
            // repli, ni une v2 qui ne répond jamais : elle n'a que la moitié de
            // la patience, pour laisser à la v1 le temps d'arriver.
            using (var v2Deadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                v2Deadline.CancelAfter(Patience / 2);

                byte[]? v2Document;
                string? v2Failure;

                try
                {
                    (v2Document, v2Failure) = await QueryAsync(v2: true, v2Deadline.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (deadline.IsCancellationRequested is false)
                {
                    (v2Document, v2Failure) = (null, e.Message);
                }

                // Hors du try : un disque qui refuse l'écriture d'une v2 acceptée
                // n'est pas une raison de redemander la v1.
                if (v2Document is not null)
                {
                    if (Accept(v2Document) is not { } refusal)
                        return;

                    v2Failure = $"refusée : {refusal}";
                }

                log.Information($"Liste signée v2 écartée ({v2Failure}), repli sur la v1.");
            }

            // Une v2 refusée (signature cassée côté service, lecteur qui dérive)
            // ne doit pas priver d'une v1 valide. Aucun risque de régression :
            // la v1 passe par la même vérification de signature et de version,
            // il ne lui manque que les régions.
            var (document, v1Failure) = await QueryAsync(v2: false, deadline.Token).ConfigureAwait(false);

            if (document is null)
            {
                log.Information($"Liste signée indisponible : {v1Failure}");
                return;
            }

            if (Accept(document) is { } why)
                log.Warning($"Liste signée refusée : {why}");
        }
        catch (Exception e) when (ct.IsCancellationRequested is false)
        {
            log.Information($"Autorité du réseau ouvert injoignable : {e.Message}");
        }
    }

    /// <summary>Offre le document au cercle et l'enregistre s'il est pris ; rend la raison d'un refus.</summary>
    private string? Accept(byte[] document)
    {
        if (circle.Offer(document, out var why) is false)
            return why ?? "raison inconnue";

        var temporary = path + ".part";
        File.WriteAllBytes(temporary, document);
        File.Move(temporary, path, overwrite: true);
        return null;
    }

    private static async Task<(byte[]? Document, string? Failure)> QueryAsync(bool v2, CancellationToken ct)
    {
        await using var client = new RendezvousClient();
        await client.ConnectAsync(RendezvousList.Authority.Host, RendezvousList.Authority.Port, ct).ConfigureAwait(false);

        return v2
            ? await client.QueryConsensusV2Async(ct).ConfigureAwait(false)
            : await client.QueryConsensusAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
        _wake.Dispose();
    }
}
