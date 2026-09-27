namespace Linkpearl.Core.Sync;

/// <summary>
/// Ce que l'on écrit sous un pair visible dont l'apparence n'est pas encore là.
/// </summary>
/// <param name="Label">Le texte, court : il se lit au milieu d'une scène.</param>
/// <param name="Progress">De zéro à un pendant une réception, null sinon.</param>
/// <remarks>
/// Sans lui, un pair appairé qui garde son apparence par défaut est
/// indiscernable d'un pair en panne : on ne sait pas s'il faut attendre ou
/// réappliquer. Rien n'est affiché une fois l'apparence appliquée ni pour un pair
/// hors ligne, sans quoi le badge deviendrait un décor que plus personne ne lit.
/// </remarks>
public sealed record TransferBadge(string Label, float? Progress)
{
    private const long Mo = 1024 * 1024;

    public static TransferBadge? Of(PeerStatus status)
    {
        // Rien ne sera posé : annoncer une attente mentirait.
        if (status.FingerprintDisputed)
            return null;

        if (status.State is PeerSessionState.Disconnected)
            return null;

        if (status.State is PeerSessionState.Connecting or PeerSessionState.Handshaking)
            return new TransferBadge("connexion", null);

        var view = status.View;

        // Avant l'état « posé » : pendant un changement de tenue, l'ancienne
        // reste en place et c'est la nouvelle que l'on veut voir avancer.
        if (view is { MissingBytes: > 0, Ready: false })
        {
            var progress = Math.Clamp((float)view.ReceivedBytes / view.MissingBytes, 0f, 1f);

            return new TransferBadge(
                $"réception {view.ReceivedBytes / Mo} / {view.MissingBytes / Mo} Mo", progress);
        }

        if (status.Applied)
            return null;

        return view.Ready
            ? new TransferBadge("application de l'apparence", null)
            : new TransferBadge("en attente", null);
    }
}
