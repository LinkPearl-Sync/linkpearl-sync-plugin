using Dalamud.Plugin.Services;
using Linkpearl.Core.Abstractions;

namespace Linkpearl.Integration;

/// <summary>Le journal du noyau, versé dans celui de Dalamud.</summary>
/// <remarks>
/// Le noyau ne connaît pas Dalamud, et c'est ce qui le rend testable sous
/// Linux. Cette classe est le seul point où les deux se rencontrent.
///
/// Le préfixe nomme le sous-système et non le pair. Le noyau, lui, manipule le
/// nom que l'utilisateur a donné à chaque pair (<c>PairRecord.DisplayName</c>,
/// qui est le vrai nom du personnage) : c'est à chaque message de ne pas
/// l'interpoler, et de désigner le pair par <c>PairRecord.LogTag</c>. Rien ici
/// ne le filtre.
///
/// Une exception ne part en entier qu'au niveau Debug : son message porte
/// souvent un chemin complet, donc le nom de session Windows de l'utilisateur,
/// et le journal par défaut finit collé dans un salon d'entraide. Au niveau
/// Warning, le type seul dit déjà de quelle panne il s'agit.
/// </remarks>
public sealed class PluginLogSink(IPluginLog log, string subsystem) : ILogSink
{
    public void Debug(string message) => log.Debug($"[{subsystem}] {message}");

    public void Info(string message) => log.Information($"[{subsystem}] {message}");

    public void Warning(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            log.Warning($"[{subsystem}] {message}");
            return;
        }

        log.Warning($"[{subsystem}] {message} ({exception.GetType().Name})");
        log.Debug(exception, $"[{subsystem}] {message}");
    }
}
