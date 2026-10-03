using Dalamud.Plugin.Services;
using Linkpearl.Core.Safety;

namespace Linkpearl.Integration;

/// <summary>
/// Dérive, sur le pool, l'empreinte de bannissement des joueurs à vérifier.
/// </summary>
/// <remarks>
/// C'est ici, et seulement ici, qu'un nom rencontre une liste : le noyau ne
/// reçoit que le haché. Une dérivation coûte de l'ordre de 0,1 à 0,3 s ; elles
/// s'enchaînent sur une seule tâche, pour ne jamais occuper plus d'un cœur à
/// côté du jeu. Une ronde qui arrive pendant qu'une autre tourne est ignorée :
/// la suivante reprendra ce qui manque encore.
/// </remarks>
public sealed class ServiceBanScreening(ServiceBanBook book, IPluginLog log) : IDisposable
{
    private readonly CancellationTokenSource _life = new();
    private int _running;

    public void Schedule(IReadOnlyList<NearbyPlayer> players)
    {
        var todo = players
            .Select(player => (Player: player, Missing: book.Missing(player.Fingerprint)))
            .Where(entry => entry.Missing.Count > 0)
            .ToList();

        if (todo.Count == 0 || Interlocked.Exchange(ref _running, 1) == 1)
            return;

        var ct = _life.Token;

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var (player, missing) in todo)
                {
                    foreach (var derivation in missing)
                    {
                        ct.ThrowIfCancellationRequested();
                        book.Record(player.Fingerprint, derivation.Key,
                            BanList.Derive(player.Name, player.WorldId, derivation.Salt, derivation.Parameters));
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log.Warning($"Vérification des listes de bannissement en échec ({e.GetType().Name}).");
                log.Debug(e, "Vérification des listes de bannissement en échec.");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }, ct);
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
    }
}
