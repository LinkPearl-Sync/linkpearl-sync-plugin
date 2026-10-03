using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Groups;

/// <summary>Ce que le handshake demande avant d'accepter la clé d'un pair de groupe.</summary>
public interface IGroupGate
{
    bool Admits(PairRecord pair, byte[] publicKey);
}

/// <summary>Le verdict sur la clé d'un membre. Strictement local.</summary>
public enum GroupAdmission
{
    Admitted,

    /// <summary>Admis, et épinglé à l'instant : il faut enregistrer.</summary>
    Pinned,

    /// <summary>
    /// Une autre clé a déjà été vue pour ce personnage, ou cette clé a déjà
    /// été vue pour un autre personnage.
    /// </summary>
    Disputed,

    /// <summary>La politique bannit cette clé ou ce personnage.</summary>
    Banned,

    UnknownGroup,

    /// <summary>
    /// Un personnage jamais vu, alors que le groupe a atteint son plafond de
    /// membres : refusé plutôt que d'oublier un membre déjà épinglé.
    /// </summary>
    Full,
}

/// <summary>Le sort d'une politique proposée. Strictement local.</summary>
public enum PolicyOffer
{
    Adopted,

    /// <summary>C'est déjà la nôtre.</summary>
    Same,

    /// <summary>La nôtre est plus récente : c'est à nous de la renvoyer.</summary>
    Stale,

    Invalid,
    UnknownGroup,

    /// <summary>Un groupe sans clé : essai ou Public.</summary>
    NotPrivate,
}

/// <summary>Ce que le moteur demande pour propager les politiques.</summary>
public interface IGroupPolicies
{
    byte[]? CurrentPolicy(GroupId group);

    PolicyOffer OfferPolicy(GroupId group, ReadOnlySpan<byte> encoded);
}

/// <summary>
/// Les groupes du personnage connecté.
/// </summary>
/// <remarks>
/// Lu par le fil de rafraîchissement, le tic du moteur et les handshakes, qui
/// tournent chacun sur sa tâche : il se protège seul. Les enregistrements sont
/// immuables, donc ce qui sort d'ici peut être lu sans verrou.
/// </remarks>
public sealed class GroupBook(IClock clock) : IGroupGate, IGroupPolicies
{
    /// <summary>
    /// Chaque groupe coûte quatre boîtes par connexion au changement de fenêtre.
    /// Dix groupes tiennent sous la limite de 64 du service, avec la boîte personnelle.
    /// </summary>
    public const int MaxGroups = 10;

    /// <summary>Plus qu'une compagnie libre, assez peu pour que la liste reste lisible.</summary>
    public const int MaxMembersPerGroup = 256;

    /// <summary>
    /// Le Public garde davantage : chaque inconnu croisé y entre, et un
    /// carnet plein n'y reçoit plus aucun nouveau venu.
    /// </summary>
    /// <remarks>
    /// Aucun membre épinglé n'est jamais oublié pour faire de la place (voir
    /// <see cref="Admit"/>) : le plafond doit donc couvrir des mois de jeu de
    /// rôle dans des lieux fréquentés. Une entrée pèse environ 400 octets
    /// dans <c>groups.json</c>, soit moins d'un mégaoctet au plafond.
    /// </remarks>
    public const int MaxPublicMembers = 2048;

    /// <summary>Le plafond de membres de ce groupe.</summary>
    public static int MemberCap(GroupRecord group) => group.IsPublic ? MaxPublicMembers : MaxMembersPerGroup;

    private readonly Lock _gate = new();
    private readonly Dictionary<GroupId, GroupRecord> _groups = [];

    /// <summary>Levé hors du verrou, quand quelque chose qui s'enregistre a changé.</summary>
    public event Action? Changed;

    /// <summary>
    /// Levé hors du verrou, après <see cref="Changed"/>, quand une politique
    /// est adoptée, avec celle qu'elle remplace.
    /// </summary>
    /// <remarks>
    /// La précédente dit à l'abonné ce qui vient de changer pour lui : une
    /// exclusion qui tombe, ou qui se lève, ne se dit qu'une fois.
    /// </remarks>
    public event Action<GroupId, GroupPolicy?>? PolicyAdopted;

    public IReadOnlyList<GroupRecord> All
    {
        get
        {
            lock (_gate)
                return [.. _groups.Values.Where(group => group.Dormant is false)];
        }
    }

    /// <summary>Tout ce qui s'enregistre, Public dormant compris.</summary>
    /// <remarks>
    /// <see cref="All"/> écarte le Public désactivé : l'enregistrer à partir
    /// de là perdrait ses blocages à chaque désactivation, ce que l'état
    /// dormant existe justement pour éviter.
    /// </remarks>
    public IReadOnlyList<GroupRecord> Stored
    {
        get
        {
            lock (_gate)
                return [.. _groups.Values];
        }
    }

    public GroupRecord? Find(GroupId id)
    {
        lock (_gate)
            return TryLive(id, out var group) ? group : null;
    }

    public bool TryAdd(GroupRecord group, out string? refusal)
    {
        lock (_gate)
        {
            // Le Public s'active par SetPublic : l'ajouter comme un groupe le
            // ferait compter dans les dix, et le rendrait impossible à endormir.
            if (group.IsPublic)
            {
                refusal = "le Public s'active, il ne s'ajoute pas";
                return false;
            }

            if (_groups.ContainsKey(group.Id))
            {
                refusal = "déjà membre de ce groupe";
                return false;
            }

            if (_groups.Values.Count(known => known.IsPublic is false) >= MaxGroups)
            {
                refusal = $"au plus {MaxGroups} groupes par personnage";
                return false;
            }

            _groups[group.Id] = group;
        }

        refusal = null;
        Changed?.Invoke();
        return true;
    }

    public bool Remove(GroupId id)
    {
        bool removed;

        lock (_gate)
            removed = _groups.Remove(id);

        if (removed)
            Changed?.Invoke();

        return removed;
    }

    /// <summary>Remplace tout, au chargement. Ne lève pas <see cref="Changed"/> : rien n'est à réécrire.</summary>
    public void Load(IEnumerable<GroupRecord> groups)
    {
        lock (_gate)
        {
            _groups.Clear();

            var loaded = groups.ToList();

            // Comme le codec : dix groupes privés au plus, et un Public à part.
            foreach (var group in loaded.Where(group => group.IsPublic is false).Take(MaxGroups)
                         .Concat(loaded.Where(group => group.IsPublic).Take(1)))
                _groups[group.Id] = group;
        }
    }

    /// <summary>Oublie tout, au changement de personnage.</summary>
    public void Clear()
    {
        lock (_gate)
            _groups.Clear();
    }

    public byte[]? CurrentPolicy(GroupId id)
    {
        lock (_gate)
            return TryLive(id, out var group) && group.Policy is { } policy ? GroupPolicyCodec.Encode(policy) : null;
    }

    /// <summary>
    /// Adopte une politique si elle est valide et plus récente que la nôtre.
    /// </summary>
    /// <remarks>
    /// Le nom et les services du groupe suivent la politique : c'est elle qui
    /// fait foi, pas ce qu'on avait reçu à l'entrée.
    /// </remarks>
    public PolicyOffer OfferPolicy(GroupId id, ReadOnlySpan<byte> encoded)
    {
        GroupPolicy? previous;

        lock (_gate)
        {
            if (TryLive(id, out var group) is false)
                return PolicyOffer.UnknownGroup;

            if (group.OwnerKey is not { } ownerKey)
                return PolicyOffer.NotPrivate;

            if (GroupPolicyRules.TryAccept(encoded, id, ownerKey, out var candidate, out _) is false)
                return PolicyOffer.Invalid;

            previous = group.Policy;

            if (group.Policy is { } current && GroupPolicyRules.IsNewer(candidate!, current) is false)
            {
                // Deux signatures du même contenu ne doivent jamais compter pour
                // des politiques différentes : ECDSA signe au hasard, donc la
                // comparer par signature ferait que deux membres se renvoient
                // sans fin « leur » exemplaire du même contenu. C'est le contenu
                // signé, et non la signature, qui départage ici comme dans
                // GroupPolicyRules.IsNewer.
                var same = GroupPolicyCodec.SignedPortion(candidate!).AsSpan()
                    .SequenceEqual(GroupPolicyCodec.SignedPortion(current));
                return same ? PolicyOffer.Same : PolicyOffer.Stale;
            }

            _groups[id] = group with { Policy = candidate, Name = candidate!.Name, Rendezvous = candidate.Rendezvous };
        }

        Changed?.Invoke();
        PolicyAdopted?.Invoke(id, previous);
        return PolicyOffer.Adopted;
    }

    /// <summary>
    /// Décide si cette clé peut parler pour ce personnage dans ce groupe.
    /// </summary>
    /// <remarks>
    /// Tous les membres connaissent le secret, donc les jetons de n'importe quel
    /// couple : un membre pourrait se présenter à la place d'un autre. La
    /// première clé vue pour un personnage l'emporte, et toute autre est
    /// contestée. Le premier contact reste gagnable par qui arrive avant le
    /// vrai ; c'est un prix énoncé dans la spec. Dans le Public, dont le secret
    /// est une constante, ce « vrai » peut même être un passant qui n'a pas le
    /// plugin : voir la section « Groupe Public » de <c>docs/protocol.md</c>.
    ///
    /// Deux règles bornent ce que ce premier contact rapporte. Une clé ne parle
    /// que pour un personnage : déjà épinglée sous une autre empreinte, elle
    /// est contestée, sans quoi un seul usurpateur se poserait sur chaque
    /// passant l'un après l'autre sous la même identité. Et un membre épinglé
    /// n'est jamais oublié pour faire de la place : l'oublier rouvrirait son
    /// premier contact à qui sait remplir le carnet. Au plafond, c'est le
    /// nouveau venu qui est refusé.
    /// </remarks>
    public GroupAdmission Admit(GroupId id, PlayerFingerprint member, byte[] publicKey, string displayName)
    {
        var key = PeerId.Of(publicKey);
        GroupAdmission verdict;
        var write = false;

        lock (_gate)
        {
            if (TryLive(id, out var group) is false)
                return GroupAdmission.UnknownGroup;

            // Un banni ne laisse aucune trace : ni épinglage, ni dernière vue.
            if (group.Refuses(key, member))
                return GroupAdmission.Banned;

            // LivePin et non Id : une clé qu'on a bloquée ne tient plus le
            // personnage, et le carnet relu d'avant Unblock doit finir au
            // même endroit que celui qu'Unblock vient d'écrire.
            if (group.Members.TryGetValue(member, out var known) && group.LivePin(known) is { } pinned)
            {
                // Une clé contestée ne prouve rien : n'importe quel membre connaît le
                // secret partagé et peut prétendre être n'importe quelle empreinte
                // avec une clé bidon, sans jamais réussir le handshake. La laisser
                // rafraîchir LastSeenAt ne servirait qu'à tromper l'interface.
                if (pinned != key)
                    return GroupAdmission.Disputed;

                verdict = GroupAdmission.Admitted;

                // La clé complète peut manquer sur un enregistrement épinglé
                // avant cette version (ou relu depuis le disque) : la compléter
                // est un vrai changement, à enregistrer, mais rien d'autre ne
                // bouge tant que la clé est déjà connue.
                if (known.PublicKey is null)
                {
                    var membersWithKey = new Dictionary<PlayerFingerprint, GroupMember>(group.Members)
                    {
                        [member] = known with { PublicKey = publicKey },
                    };
                    _groups[id] = group with { Members = membersWithKey };
                    write = true;
                }
            }
            else
            {
                // Une clé, un personnage : voir la remarque.
                if (group.MemberPinnedTo(key) is not null)
                    return GroupAdmission.Disputed;

                if (known is null && group.Members.Count >= MemberCap(group))
                    return GroupAdmission.Full;

                var members = new Dictionary<PlayerFingerprint, GroupMember>(group.Members);
                verdict = GroupAdmission.Pinned;

                members[member] = known is not null
                    ? known with { Id = key, PublicKey = publicKey, LastSeenAt = clock.UtcNow }
                    : new GroupMember
                    {
                        Fingerprint = member,
                        Id = key,
                        DisplayName = displayName,
                        PublicKey = publicKey,
                        LastSeenAt = clock.UtcNow,
                    };

                _groups[id] = group with { Members = members };
                write = true;
            }
        }

        if (write)
            Changed?.Invoke();

        return verdict;
    }

    public void SetPaused(GroupId id, PlayerFingerprint member, bool paused)
        => UpdateMember(id, member, known => known with { Paused = paused });

    public void SetReceive(GroupId id, PlayerFingerprint member, TransientCategories? receive)
        => UpdateMember(id, member, known => known with { Receive = receive });

    public GroupRecord? Public
    {
        get
        {
            lock (_gate)
                return _groups.GetValueOrDefault(PublicGroup.Id);
        }
    }

    /// <summary>
    /// Active ou désactive le Public.
    /// </summary>
    /// <remarks>
    /// Désactivé, il reste au carnet, dormant : ses blocages et ses réglages
    /// survivent, et on les retrouve en le réactivant.
    /// </remarks>
    public void SetPublic(bool enabled, IReadOnlyList<RendezvousAddress> services)
    {
        lock (_gate)
        {
            _groups[PublicGroup.Id] = _groups.TryGetValue(PublicGroup.Id, out var known)
                ? known with { Dormant = enabled is false, Rendezvous = services }
                : PublicGroup.Create(services, clock.UtcNow) with { Dormant = enabled is false };
        }

        Changed?.Invoke();
    }

    /// <summary>Les services du Public sont ceux de la configuration, recopiés à chaque ronde.</summary>
    public void SetPublicServices(IReadOnlyList<RendezvousAddress> services)
    {
        lock (_gate)
        {
            if (_groups.TryGetValue(PublicGroup.Id, out var known) is false || known.Rendezvous.SequenceEqual(services))
                return;

            _groups[PublicGroup.Id] = known with { Rendezvous = services };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Bloque un membre, par son personnage et, s'il est épinglé, par sa clé.
    /// </summary>
    /// <remarks>
    /// Les deux : la clé seule laisserait revenir le même joueur sous une clé
    /// neuve, le personnage seul le laisserait revenir sous un autre. Dans le
    /// Public, celui qu'on bloque peut usurper le personnage d'un autre : c'est
    /// <see cref="Unblock"/> qui rend le personnage sans rendre la clé.
    /// </remarks>
    public void Block(GroupId id, PlayerFingerprint member)
    {
        lock (_gate)
        {
            if (_groups.TryGetValue(id, out var group) is false || group.Blocked.Any(ban => ban.Fingerprint == member))
                return;

            var ban = new GroupBan(group.Members.GetValueOrDefault(member)?.Id, member);
            _groups[id] = group with { Blocked = [.. group.Blocked, ban] };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Débloque un personnage, ou une identité dont le personnage est déjà débloqué.
    /// </summary>
    /// <remarks>
    /// En deux temps quand le blocage porte les deux. Débloquer veut dire
    /// « je veux revoir ce personnage », pas « je refais confiance à cette
    /// clé » : dans le Public, la clé bloquée a pu être celle d'un usurpateur,
    /// et le personnage celui d'un passant qui n'y est pour rien. Le personnage
    /// est donc libéré, et son épinglage avec, pour que la prochaine clé qui
    /// se présente sous lui fasse un premier contact neuf ; la clé, elle, reste
    /// bloquée sous tout personnage, jusqu'à un second déblocage. Sans cela,
    /// débloquer rendait le personnage à la clé épinglée, donc à l'usurpateur,
    /// et le vrai joueur restait contesté pour toujours.
    /// </remarks>
    public void Unblock(GroupId id, GroupBan ban)
    {
        lock (_gate)
        {
            if (_groups.TryGetValue(id, out var group) is false || group.Blocked.Contains(ban) is false)
                return;

            var blocked = group.Blocked.Where(known => known != ban).ToList();
            var members = group.Members;

            if (ban is { Peer: { } peer, Fingerprint: { } print })
            {
                blocked.Add(new GroupBan(peer, null));

                if (members.TryGetValue(print, out var member) && member.Id == peer)
                {
                    members = new Dictionary<PlayerFingerprint, GroupMember>(members)
                    {
                        [print] = member with { Id = null, PublicKey = null },
                    };
                }
            }

            _groups[id] = group with { Blocked = blocked, Members = members };
        }

        Changed?.Invoke();
    }

    public void SetDefaultReceive(GroupId id, TransientCategories receive)
    {
        lock (_gate)
        {
            if (_groups.TryGetValue(id, out var group) is false)
                return;

            _groups[id] = group with { DefaultReceive = receive };
        }

        Changed?.Invoke();
    }

    /// <summary>Un groupe vivant : un Public désactivé ne répond à rien.</summary>
    private bool TryLive(GroupId id, out GroupRecord group)
        => _groups.TryGetValue(id, out group!) && group.Dormant is false;

    bool IGroupGate.Admits(PairRecord pair, byte[] publicKey)
        => pair.Group is { } origin
           && Admit(origin.Group, origin.Theirs, publicKey, pair.DisplayName)
               is GroupAdmission.Admitted or GroupAdmission.Pinned;

    private void UpdateMember(GroupId id, PlayerFingerprint member, Func<GroupMember, GroupMember> change)
    {
        lock (_gate)
        {
            if (TryLive(id, out var group) is false
                || group.Members.TryGetValue(member, out var known) is false)
                return;

            var members = new Dictionary<PlayerFingerprint, GroupMember>(group.Members) { [member] = change(known) };
            _groups[id] = group with { Members = members };
        }

        Changed?.Invoke();
    }
}
