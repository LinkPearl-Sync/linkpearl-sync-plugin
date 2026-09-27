namespace Linkpearl.Core.Sync;

/// <summary>Où en est un pair, du point de vue de celui qui regarde la liste.</summary>
/// <remarks>
/// État local, jamais transmis : c'est ce qui autorise une énumération ici.
/// La pause et le blocage n'y figurent pas, ils viennent du carnet et non du
/// moteur.
/// </remarks>
public enum PeerPhase
{
    /// <summary>On le cherche au rendez-vous, ou la tentative est sur le point de partir.</summary>
    Searching,

    /// <summary>Il n'était pas au rendez-vous : hors ligne, ou il nous a mis en pause.</summary>
    Absent,

    /// <summary>La dernière tentative a échoué pour une vraie raison.</summary>
    Failing,

    /// <summary>Il nous a mis en pause : il reviendra de lui-même en nous reprenant.</summary>
    PausedByPeer,

    /// <summary>Session ouverte, son apparence n'est pas encore arrivée.</summary>
    AwaitingAppearance,

    /// <summary>Des fichiers de son apparence sont en route.</summary>
    Receiving,

    /// <summary>Son apparence est complète, elle se posera quand il sera en vue.</summary>
    OutOfView,

    /// <summary>Son apparence est posée.</summary>
    Applied,
}

public static class PeerPhases
{
    /// <summary>La phase d'un pair, d'après ce que le moteur en sait.</summary>
    /// <param name="dialing">Une tentative est en cours.</param>
    /// <param name="nextAttempt">L'heure de la prochaine tentative, sans objet quand une session est ouverte.</param>
    /// <param name="lastFailure">La raison du dernier échec ; nulle quand le pair était simplement absent.</param>
    /// <param name="wasAbsent">Le pair n'était pas au rendez-vous lors du dernier essai.</param>
    /// <param name="pausedByPeer">Il nous a envoyé un avis de pause, qu'aucune session n'a encore effacé.</param>
    /// <remarks>
    /// Calculée par le moteur plutôt que devinée par l'interface : jusqu'au 26
    /// septembre, l'interface ne regardait que l'existence d'une session, et
    /// un pair recherché au rendez-vous s'affichait « hors ligne ».
    /// </remarks>
    public static PeerPhase Of(bool dialing, bool hasSession, DateTimeOffset now, DateTimeOffset nextAttempt,
                               string? lastFailure, bool wasAbsent, bool pausedByPeer, PeerView view, bool applied)
    {
        if (hasSession is false)
        {
            // La pause avant tout : pendant les essais qui la suivent, il est
            // tour à tour recherché, absent ou en échec, et c'est elle seule
            // qui dit pourquoi.
            if (pausedByPeer)
                return PeerPhase.PausedByPeer;

            // Absent jusqu'à preuve du contraire, même pendant l'essai suivant :
            // un ami hors ligne est recherché vingt-cinq secondes sur trente, et
            // sa ligne clignoterait sinon entre « recherche » et « absent ».
            if (wasAbsent)
                return PeerPhase.Absent;

            if (dialing || nextAttempt <= now || lastFailure is null)
                return PeerPhase.Searching;

            return PeerPhase.Failing;
        }

        // Avant « posée » : pendant un changement de tenue, l'ancienne reste en
        // place et c'est la nouvelle que l'on veut voir avancer. Même ordre que
        // le badge en jeu.
        if (view is { MissingBytes: > 0, Ready: false })
            return PeerPhase.Receiving;

        if (applied)
            return PeerPhase.Applied;

        return view.Ready ? PeerPhase.OutOfView : PeerPhase.AwaitingAppearance;
    }
}
