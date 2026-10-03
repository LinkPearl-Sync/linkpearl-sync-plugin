using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;

namespace Linkpearl.Core.Groups;

/// <summary>Un joueur visible dont la boîte de présence de groupe répond.</summary>
public sealed record GroupSighting(GroupId Group, PlayerFingerprint Member, string DisplayName);

/// <summary>
/// Transforme ce qu'on voit en pairs à joindre.
/// </summary>
/// <remarks>
/// On ne se connecte qu'aux membres visibles : une compagnie libre de cent
/// personnes ferait sinon cent sessions permanentes par joueur. Un membre qui
/// sort du champ reste cinq minutes, le temps d'un aller-retour dans une autre
/// pièce, sans refaire tout le transfert.
///
/// Appelé depuis le seul fil de rafraîchissement : il n'a pas de verrou.
/// </remarks>
public sealed class GroupDialPlanner(IClock clock)
{
    /// <summary>
    /// Un lieu de RP bondé ne doit pas tenir cent liens, chacun avec trente-deux
    /// canaux et sa fenêtre d'envoi. Quarante-huit couvre largement ce qu'un
    /// joueur voit autour de lui.
    /// </summary>
    public const int MaxSessions = 48;

    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(5);

    private readonly Dictionary<PlayerFingerprint, (GroupId Group, string Name, DateTimeOffset Seen)> _recent = [];

    public IReadOnlyList<PairRecord> Plan(
        PlayerFingerprint ours,
        IReadOnlyList<GroupSighting> sightings,
        IReadOnlyList<GroupRecord> groups,
        IEnumerable<PlayerFingerprint> directlyPaired,
        IServiceBans? bans = null,
        PeerId? ourKey = null)
    {
        // Un listé ne se compose nulle part. Un verdict en attente ne retient
        // que le Public, et seulement pour une composition nouvelle : ses
        // membres sont des inconnus, alors qu'un groupe privé réunit des gens
        // admis. Une session en cours ne se coupe pas pour autant, sans quoi
        // la première entrée d'une liste, qui remet tout le monde en attente,
        // ferait clignoter tout un lieu. Voir la décision 3 du plan de l'incrément 3.
        //
        // Un groupe dont la politique nous exclut ne compose plus que ceux qui
        // peuvent lever l'exclusion : le propriétaire et les modérateurs, par
        // leur clé épinglée. On garde le groupe pour qu'une politique plus
        // récente puisse nous rattraper, et elle ne voyage que dans une
        // session : sans personne à composer, elle n'arriverait jamais. Les
        // autres membres nous refuseraient de toute façon tant qu'ils tiennent
        // la politique qui nous exclut.
        bool Excluded(GroupRecord group, PlayerFingerprint member, bool composing)
        {
            var status = bans?.Status(member) ?? ServiceBanStatus.Clear;
            var pinned = group.LivePin(group.Members.GetValueOrDefault(member));

            if (group.Excludes(ourKey, ours) && (pinned is not { } key || group.Policy!.IsProtected(key) is false))
                return true;

            return group.Refuses(pinned, member)
                   || status.Verdict is BanVerdict.Listed
                   || (composing && status.Verdict is BanVerdict.Pending && group.IsPublic);
        }

        var byId = groups.ToDictionary(group => group.Id);
        var now = clock.UtcNow;

        // Deux groupes en commun : le privé, puis le plus petit identifiant, choisi pareil des
        // deux côtés sans se concerter, et une seule session. L'empreinte se
        // vérifie contre l'Id épinglé du membre quand il est connu, et non
        // contre null : un pair protégé (propriétaire-membre ou modérateur
        // attesté) n'est jamais tenu pour banni tant que sa clé est reconnue,
        // et un membre pas encore épinglé n'a pas encore cette protection à
        // offrir.
        foreach (var seen in sightings
                     .Where(sighting => sighting.Member != ours
                                        && byId.TryGetValue(sighting.Group, out var group)
                                        && Excluded(group, sighting.Member, composing: _recent.ContainsKey(sighting.Member) is false) is false)
                     .GroupBy(sighting => sighting.Member))
        {
            // Un groupe privé commun passe avant le Public : l'ami qu'on y
            // retrouve ne doit pas arriver en inconnu, effets coupés. Et la
            // règle doit donner le même choix des deux côtés même quand l'un
            // a bloqué l'autre dans le Public, ou attend encore son verdict :
            // sinon les secrets de paire diffèrent, et plus rien ne s'ouvre.
            var chosen = seen.MinBy(sighting => (byId[sighting.Group].IsPublic, sighting.Group))!;
            _recent[seen.Key] = (chosen.Group, chosen.DisplayName, now);
        }

        // Même substitution ici : un membre que la politique courante bannit
        // (par empreinte, sauf s'il reste protégé par son Id épinglé) doit
        // voir sa session se refermer au tic suivant, pas seulement ne plus
        // s'en ouvrir une nouvelle.
        foreach (var (member, entry) in _recent.ToList())
            if (now - entry.Seen > Linger
                || byId.TryGetValue(entry.Group, out var group) is false
                || Excluded(group, member, composing: false))
                _recent.Remove(member);

        // Une paire du carnet l'emporte toujours. Le carnet entier, retraits
        // compris : l'autre côté peut encore nous tenir pour une paire.
        var direct = directlyPaired.ToHashSet();

        return [.. _recent
            .Where(entry => direct.Contains(entry.Key) is false)
            .Select(entry => (
                Member: entry.Key,
                entry.Value.Group,
                entry.Value.Name,
                entry.Value.Seen,
                Known: byId[entry.Value.Group].Members.GetValueOrDefault(entry.Key)))
            .Where(candidate => candidate.Known is not { Paused: true })
            .OrderByDescending(candidate => candidate.Seen)
            .ThenBy(candidate => candidate.Member.ToString(), StringComparer.Ordinal)
            .Take(MaxSessions)
            .Select(candidate => Record(
                ours, byId[candidate.Group], candidate.Member, candidate.Known?.DisplayName ?? candidate.Name, candidate.Known))];
    }

    private static PairRecord Record(
        PlayerFingerprint ours, GroupRecord group, PlayerFingerprint theirs, string name, GroupMember? known)
    {
        var secret = GroupDerivation.MemberPairSecret(group.Secret, ours, theirs);

        return new PairRecord
        {
            Id = GroupDerivation.RuntimeId(secret),
            PairSecret = secret,
            DisplayName = name,
            Rendezvous = group.Rendezvous,
            Trust = PairTrust.Accepted,
            PairedAt = group.JoinedAt,
            PinnedFingerprint = theirs,
            Receive = group.ReceiveOf(known),
            Group = new GroupOrigin(group.Id, ours, theirs) { Pinned = group.LivePin(known) is not null, Public = group.IsPublic },
        };
    }
}
