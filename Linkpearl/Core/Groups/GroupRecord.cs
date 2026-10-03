using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Groups;

/// <summary>D'où vient un pair de groupe : quel groupe, et quelles empreintes.</summary>
/// <remarks>
/// Les deux empreintes servent à choisir l'initiateur du handshake : l'identifiant
/// du pair n'est pas connu d'avance, et les deux côtés doivent faire le même choix.
/// </remarks>
public sealed record GroupOrigin(GroupId Group, PlayerFingerprint Ours, PlayerFingerprint Theirs)
{
    /// <summary>Vrai si la clé du membre est déjà épinglée dans le carnet de groupe.</summary>
    /// <remarks>
    /// Avant l'épinglage, le premier handshake est une confiance au premier
    /// contact que ne lie pas le secret du groupe : un service du cercle ouvert
    /// pourrait le gagner. Ce drapeau garde ces membres dans l'ancrage.
    /// </remarks>
    public bool Pinned { get; init; }

    /// <summary>Vrai pour le groupe Public, dont le secret est connu de tous.</summary>
    public bool Public { get; init; }
}

/// <summary>Un membre rencontré.</summary>
/// <remarks>
/// Indexé par empreinte et non par clé : c'est le personnage qu'on voit à
/// l'écran, et la clé ne s'apprend qu'au premier handshake, où elle s'épingle.
/// </remarks>
public sealed record GroupMember
{
    public required PlayerFingerprint Fingerprint { get; init; }

    /// <summary>La première clé vue pour ce personnage dans ce groupe. Null avant le premier handshake.</summary>
    public PeerId? Id { get; init; }

    /// <summary>Nom affiché, pour l'interface. Jamais transmis.</summary>
    public required string DisplayName { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public bool Paused { get; init; }

    /// <summary>
    /// Les animations, VFX et sons acceptés de ce membre, ou null pour suivre le groupe.
    /// </summary>
    /// <remarks>
    /// Null tant qu'on n'a rien réglé pour lui : il suit alors
    /// <see cref="GroupRecord.DefaultReceive"/>, et « tout réactiver pour le
    /// Public » le libère avec les autres. Un réglage posé d'un clic l'emporte.
    /// </remarks>
    public TransientCategories? Receive { get; init; }

    /// <summary>
    /// La clé d'identité complète épinglée, point de 65 octets.
    /// </summary>
    /// <remarks>
    /// L'identifiant seul ne suffit pas pour nommer un modérateur : la politique
    /// porte des clés, pas des empreintes de clés.
    /// </remarks>
    public byte[]? PublicKey { get; init; }
}

/// <summary>Un groupe dont le personnage est membre.</summary>
public sealed record GroupRecord
{
    public required GroupId Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Le secret partagé, 32 octets. Tout ce que le service voit en dérive.</summary>
    public required byte[] Secret { get; init; }

    public required IReadOnlyList<RendezvousAddress> Rendezvous { get; init; }

    public required DateTimeOffset JoinedAt { get; init; }

    /// <summary>Les membres rencontrés, et eux seuls : personne ne tient la liste complète.</summary>
    public IReadOnlyDictionary<PlayerFingerprint, GroupMember> Members { get; init; }
        = new Dictionary<PlayerFingerprint, GroupMember>();

    /// <summary>
    /// La clé publique compressée du groupe, dont dérive son identifiant.
    /// </summary>
    /// <remarks>
    /// Nulle pour un groupe fabriqué à partir d'un secret seul (essai, Public) :
    /// sans elle, aucune politique ne peut se vérifier, donc aucune ne s'applique.
    /// </remarks>
    public byte[]? OwnerKey { get; init; }

    /// <summary>La clé privée du groupe, PKCS#8, chez le seul propriétaire.</summary>
    public byte[]? SigningKey { get; init; }

    /// <summary>La politique vérifiée la plus récente. Nulle tant qu'aucun membre ne l'a transmise.</summary>
    public GroupPolicy? Policy { get; init; }

    /// <summary>
    /// Vrai pour un Public désactivé, gardé pour ses blocages et ses réglages.
    /// </summary>
    /// <remarks>
    /// Il n'ouvre aucune boîte et ne compose personne. Le retirer du carnet
    /// ferait perdre, à chaque désactivation, les joueurs qu'on avait bloqués.
    /// Toujours faux pour un groupe privé.
    /// </remarks>
    public bool Dormant { get; init; }

    /// <summary>
    /// Les joueurs qu'on a bloqués dans ce groupe. Local, jamais transmis.
    /// </summary>
    /// <remarks>Le Public n'a pas de politique : c'est sa seule modération avec les listes des services.</remarks>
    public IReadOnlyList<GroupBan> Blocked { get; init; } = [];

    /// <summary>Ce qu'on accepte d'un membre qu'on n'a pas réglé un par un.</summary>
    public TransientCategories DefaultReceive { get; init; } = TransientCategories.All;

    public bool IsPublic => PublicGroup.Is(Id);

    /// <summary>Vrai si la politique bannit ce membre ou si on l'a bloqué.</summary>
    public bool Refuses(PeerId? key, PlayerFingerprint? member)
        => Policy?.IsBanned(key, member) is true || Blocked.Any(ban => ban.Matches(key, member));

    /// <summary>
    /// Vrai si la politique courante nous exclut de ce groupe.
    /// </summary>
    /// <remarks>
    /// Un état qu'on déduit de la politique, jamais un drapeau enregistré : une
    /// politique plus récente qui lève le bannissement nous réintègre sans
    /// rien d'autre à défaire, et le carnet sur disque n'a pas à changer de
    /// forme. Un groupe dissous n'exclut plus personne : il se retire.
    /// </remarks>
    public bool Excludes(PeerId? ourKey, PlayerFingerprint? ourFingerprint)
        => Policy is { Dissolved: false } policy && policy.IsBanned(ourKey, ourFingerprint);

    /// <summary>
    /// La clé qui tient ce personnage, ou null s'il attend un premier contact.
    /// </summary>
    /// <remarks>
    /// Une clé qu'on a bloquée ne tient plus le personnage sous lequel elle
    /// s'était épinglée : bloquée avec lui, c'est le personnage qui refuse ;
    /// bloquée seule, après qu'on a débloqué le personnage, elle le laisse à
    /// la prochaine clé qui se présente (voir <see cref="GroupBook.Unblock"/>).
    /// </remarks>
    public PeerId? LivePin(GroupMember? member)
        => member?.Id is { } pinned && Blocked.Any(ban => ban.Peer == pinned) is false ? pinned : null;

    /// <summary>Le membre dont la clé épinglée est celle-ci, s'il y en a un.</summary>
    public GroupMember? MemberPinnedTo(PeerId key)
        => Members.Values.FirstOrDefault(member => member.Id == key);

    public TransientCategories ReceiveOf(GroupMember? member) => member?.Receive ?? DefaultReceive;
}
