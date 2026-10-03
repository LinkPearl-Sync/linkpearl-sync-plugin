using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Groups;

/// <summary>Une question de présence de groupe : ce joueur tient-il la boîte de ce groupe ?</summary>
public readonly record struct GroupPresenceQuestion(PlayerFingerprint Member, GroupId Group, byte[] Address);

/// <summary>
/// Ce qu'on demande à chaque service pour savoir quels joueurs visibles sont de nos groupes.
/// </summary>
/// <remarks>
/// Deux règles, selon ce qu'est le service.
///
/// Un service de nos réglages fait aussi la détection par boîte personnelle :
/// on ne l'interroge que sur les joueurs qu'il a détectés, puisqu'un passant
/// sans le plugin n'a pas de boîte de groupe. Il entend parler de tous nos
/// groupes, comme avant.
///
/// Un service connu par la seule politique d'un groupe ne fait pas de
/// détection : personne n'y tient sa boîte personnelle, et lui faire calculer
/// les adresses personnelles des passants lui apprendrait leurs noms. On l'y
/// interroge donc sur tous les joueurs visibles, mais seulement par les
/// adresses de présence des groupes qui le nomment. Elles dérivent du secret
/// du groupe : un opérateur qui n'en est pas membre n'en tire aucun nom. Sans
/// cela, un cercle qui héberge son propre service ne trouvait plus ses membres
/// dès qu'ils ne partageaient aucun service des réglages.
/// </remarks>
public static class GroupPresenceQueries
{
    public static List<GroupPresenceQuestion> For(
        RendezvousAddress service, bool settings,
        IReadOnlyList<GroupRecord> groups,
        IReadOnlyList<PlayerFingerprint> visible,
        IReadOnlySet<PlayerFingerprint> detected,
        DateTimeOffset now)
    {
        var questions = new List<GroupPresenceQuestion>();

        foreach (var group in groups)
        {
            var named = group.Rendezvous.Contains(service);

            // Un service de groupe n'entend parler que de ses groupes : les
            // adresses des autres lui apprendraient au moins combien nous en avons.
            if (named is false && settings is false)
                continue;

            foreach (var player in visible)
            {
                if (settings && detected.Contains(player) is false)
                    continue;

                questions.Add(new GroupPresenceQuestion(
                    player, group.Id, GroupDerivation.PresenceAddress(group.Secret, player, now).ToBytes()));
            }
        }

        return questions;
    }
}
