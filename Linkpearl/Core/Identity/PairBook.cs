using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Identity;

/// <summary>Degré de confiance accordé à un pair.</summary>
/// <remarks>
/// Une énumération est acceptable ici parce que cet état ne traverse jamais le
/// réseau : il est strictement local. Ce qui vient du réseau se valide octet par
/// octet, et n'est jamais converti en énumération sans contrôle.
/// </remarks>
public enum PairTrust
{
    Pending,
    Accepted,
    Blocked,

    /// <summary>
    /// Retiré par nous, et pas encore prévenu.
    /// </summary>
    /// <remarks>
    /// L'entrée est gardée pour une seule raison : lui dire que c'est fini.
    /// Sans cela il nous verrait encore comme pairés, avec une ligne « hors
    /// ligne » que rien n'expliquerait. On le joint comme un pair, mais sa
    /// session ne porte que l'avis de retrait, jamais une apparence.
    /// </remarks>
    Revoked,
}

/// <summary>Ce qu'on accepte d'échanger avec un pair, dans chaque sens.</summary>
[Flags]
public enum PairPermissions
{
    None = 0,
    ReceiveAppearance = 1,
    SendAppearance = 2,
    Both = ReceiveAppearance | SendAppearance,
}

/// <summary>Comment on accepte de se connecter à un pair.</summary>
public enum ConnectionPolicy
{
    /// <summary>Direct si possible, relais en secours.</summary>
    Direct,

    /// <summary>
    /// Relais uniquement.
    /// </summary>
    /// <remarks>
    /// Une connexion directe révèle notre adresse IP au pair, donc notre ville
    /// et notre opérateur. Ce mode existe pour les pairs à qui l'on ne fait pas
    /// cette confiance-là, et c'est une propriété que Mare n'offrait pas.
    /// </remarks>
    RelayOnly,
}

/// <summary>Un pair du carnet.</summary>
public sealed record PairRecord
{
    public required PeerId Id { get; init; }

    /// <summary>
    /// La clé publique complète, apprise au premier handshake.
    /// </summary>
    /// <remarks>
    /// Le code d'invitation ne porte que l'empreinte : la clé arrive du pair
    /// lui-même et se vérifie contre cette empreinte, ce qui suffit à interdire
    /// toute substitution et raccourcit le code de moitié.
    /// </remarks>
    public byte[]? PublicKey { get; init; }

    public required byte[] PairSecret { get; init; }

    /// <summary>Nom donné localement. Jamais transmis, ni journalisé.</summary>
    /// <remarks>C'est le vrai nom du personnage : les journaux disent <see cref="LogTag"/>.</remarks>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Ce qui désigne ce pair dans un journal : le début de son identifiant.
    /// </summary>
    /// <remarks>
    /// Un journal se colle dans un salon d'entraide ; un nom de personnage
    /// n'a rien à y faire (CLAUDE.md, « La vie privée »). Huit caractères
    /// hexadécimaux suffisent à suivre un pair d'une ligne à l'autre et ne
    /// disent rien de qui il est : l'identifiant est le haché de sa clé, ou,
    /// pour un membre de groupe, un dérivé du secret du couple.
    /// </remarks>
    public string LogTag => Id.ToHex()[..8];

    /// <summary>
    /// Les lieux où ce pair et nous nous donnons rendez-vous, par ordre de
    /// préférence.
    /// </summary>
    /// <remarks>
    /// Une liste et non un service unique : c'est ce qui fait survivre un
    /// pairage à la disparition de l'un d'eux. Les deux côtés s'annoncent sur
    /// tous ceux qu'ils partagent, et se trouvent sur le premier qui répond.
    /// Le premier de la liste sert aussi de relais préféré.
    /// </remarks>
    public required IReadOnlyList<RendezvousAddress> Rendezvous { get; init; }

    public PairTrust Trust { get; init; } = PairTrust.Pending;
    public PairPermissions Permissions { get; init; } = PairPermissions.Both;
    public ConnectionPolicy Policy { get; init; } = ConnectionPolicy.Direct;
    public bool Paused { get; init; }

    /// <summary>
    /// Ce pair nous a mis en pause, et aucune session ne s'est rouverte depuis.
    /// </summary>
    /// <remarks>
    /// Sans elle, sa pause se lirait comme un départ du jeu. Enregistrée pour
    /// survivre à un rechargement ; la reprise se voit à la session suivante,
    /// qui l'efface.
    /// </remarks>
    public bool PausedByPeer { get; init; }

    /// <summary>Les animations, VFX et sons qu'on accepte de ce pair.</summary>
    /// <remarks>
    /// Tout par défaut : c'est ce qui rend une idle ou une pose assise visibles
    /// sans réglage. Un pair aux effets envahissants se coupe ici, sans toucher
    /// à ses vêtements ni aux autres pairs.
    /// </remarks>
    public TransientCategories Receive { get; init; } = TransientCategories.All;

    /// <summary>
    /// Vrai quand les six mots ont été comparés de vive voix.
    /// </summary>
    /// <remarks>
    /// Le ticket de douze caractères ne porte aucune identité : c'est le
    /// rendez-vous qui a dit à qui l'on parle, et il pouvait mentir. Tant que
    /// cette comparaison n'a pas eu lieu, le pairage n'est pas prouvé.
    /// </remarks>
    public bool KeyVerified { get; init; }

    public required DateTimeOffset PairedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Quand on l'a retiré, pour oublier un avis jamais remis.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Empreinte du personnage du pair, épinglée à la première rencontre.</summary>
    /// <remarks>
    /// Sans épinglage, un pair pourrait annoncer l'empreinte d'un tiers et nous
    /// faire appliquer ses fichiers sur ce tiers, visible chez nous seuls.
    /// </remarks>
    public PlayerFingerprint? PinnedFingerprint { get; init; }

    /// <summary>
    /// Le groupe par lequel on joint ce pair, ou null pour une paire du carnet.
    /// </summary>
    /// <remarks>
    /// Un pair de groupe n'est jamais écrit dans le carnet : il naît quand on le
    /// voit, et disparaît cinq minutes après. Son <see cref="Id"/> n'est pas le
    /// hash de sa clé, qu'on ne connaît pas encore, mais un identifiant tiré du
    /// secret du couple (<see cref="GroupDerivation.RuntimeId"/>) : c'est
    /// pourquoi le handshake l'autorise autrement.
    /// </remarks>
    public GroupOrigin? Group { get; init; }
}

/// <summary>
/// Le carnet de pairs. C'est lui, et lui seul, qui autorise.
/// </summary>
/// <remarks>
/// Le service de rendez-vous n'a aucun rôle dans cette décision. Il peut
/// refuser son service ou mentir sur une adresse, ce qui produit un échec de
/// connexion ; il ne peut pas faire accepter un pair.
///
/// Lu et écrit depuis l'interface, la boucle de présence, le pool et le tic du
/// moteur. Chaque accès passe donc par un verrou, et chaque lecture rend une
/// copie : une pause posée depuis l'interface pendant que le tic note un
/// passage était sinon perdue, l'un réécrivant l'entrée lue avant l'autre, et
/// deux écritures simultanées pouvaient corrompre le dictionnaire.
/// </remarks>
public sealed class PairBook(IClock clock)
{
    private readonly Dictionary<PeerId, PairRecord> _pairs = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Durée pendant laquelle on cherche à prévenir un pair retiré.
    /// </summary>
    /// <remarks>
    /// Un mois : assez pour un joueur qui fait une pause, et au-delà l'entrée
    /// ne serait plus qu'une tentative de connexion toutes les trente secondes
    /// vers quelqu'un qui ne reviendra pas.
    /// </remarks>
    public static readonly TimeSpan RevocationLifetime = TimeSpan.FromDays(30);

    /// <summary>Tout le carnet, retraits en attente compris : ce qui s'enregistre.</summary>
    public IReadOnlyCollection<PairRecord> All => Snapshot(_ => true);

    /// <summary>Ce que l'utilisateur voit : un pair retiré n'est plus le sien.</summary>
    public IReadOnlyList<PairRecord> Listed => Snapshot(p => p.Trust is not PairTrust.Revoked);

    public IEnumerable<PairRecord> Active => Snapshot(p => p.Trust is PairTrust.Accepted && p.Paused is false);

    /// <summary>Les pairs retirés qu'il reste à prévenir.</summary>
    public IEnumerable<PairRecord> Revoked => Snapshot(p => p.Trust is PairTrust.Revoked);

    public PairRecord? Find(PeerId id)
    {
        lock (_gate)
            return _pairs.GetValueOrDefault(id);
    }

    /// <summary>Une copie filtrée, prise sous le verrou : l'appelant l'énumère sans lui.</summary>
    private PairRecord[] Snapshot(Func<PairRecord, bool> keep)
    {
        lock (_gate)
            return [.. _pairs.Values.Where(keep)];
    }

    public PairRecord? Find(ReadOnlySpan<byte> publicKey) => Find(PeerId.Of(publicKey));

    /// <summary>
    /// Ajoute un pair depuis un code d'invitation, en attente de confirmation.
    /// </summary>
    /// <summary>Ajoute un pair dont on vient d'apprendre la clé.</summary>
    public PairRecord Add(
        PeerId theirId, byte[] theirPublicKey, ReadOnlySpan<byte> pairingMaterial,
        PeerId ourId, string displayName, IReadOnlyList<RendezvousAddress> rendezvous)
    {
        var record = new PairRecord
        {
            Id = theirId,
            PublicKey = theirPublicKey,
            PairSecret = PairSecret.Derive(pairingMaterial, ourId, theirId),
            DisplayName = displayName,
            Rendezvous = rendezvous,
            Trust = PairTrust.Accepted,
            PairedAt = clock.UtcNow,
        };

        lock (_gate)
            _pairs[record.Id] = record;

        return record;
    }

    public void Accept(PeerId id) => Update(id, record => record with { Trust = PairTrust.Accepted });

    public void Block(PeerId id) => Update(id, record => record with { Trust = PairTrust.Blocked });

    public void SetPaused(PeerId id, bool paused) => Update(id, record => record with { Paused = paused });

    public void SetPausedByPeer(PeerId id, bool paused)
        => Update(id, record => record with { PausedByPeer = paused });

    public void SetPolicy(PeerId id, ConnectionPolicy policy) => Update(id, record => record with { Policy = policy });

    public void SetPermissions(PeerId id, PairPermissions permissions)
        => Update(id, record => record with { Permissions = permissions });

    public void SetReceive(PeerId id, TransientCategories receive)
        => Update(id, record => record with { Receive = receive });

    public void MarkVerified(PeerId id) => Update(id, record => record with { KeyVerified = true });

    public void PinFingerprint(PeerId id, PlayerFingerprint fingerprint)
        => Update(id, record => record with { PinnedFingerprint = fingerprint });

    public void Seen(PeerId id) => Update(id, record => record with { LastSeenAt = clock.UtcNow });

    public bool Remove(PeerId id)
    {
        lock (_gate)
            return _pairs.Remove(id);
    }

    /// <summary>Retire un pair de la liste, en gardant de quoi le prévenir.</summary>
    public void Revoke(PeerId id)
        => Update(id, record => record with { Trust = PairTrust.Revoked, RevokedAt = clock.UtcNow });

    /// <summary>Oublie les retraits qu'on n'a pas pu remettre à temps.</summary>
    /// <returns>Le nombre d'entrées oubliées, pour savoir s'il faut enregistrer.</returns>
    public int ForgetStaleRevocations()
    {
        lock (_gate)
        {
            var stale = _pairs.Values
                .Where(p => p.Trust is PairTrust.Revoked
                         && clock.UtcNow - (p.RevokedAt ?? p.PairedAt) > RevocationLifetime)
                .Select(p => p.Id)
                .ToList();

            foreach (var id in stale)
                _pairs.Remove(id);

            return stale.Count;
        }
    }

    /// <summary>Oublie tout, au changement de personnage.</summary>
    /// <remarks>
    /// Le carnet est attaché à une identité, et l'identité à un personnage. Se
    /// déconnecter pour en reprendre un autre ne doit pas laisser en mémoire
    /// des pairs que le nouveau n'a jamais rencontrés, ni les réécrire dans son
    /// fichier à lui.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
            _pairs.Clear();
    }

    /// <summary>
    /// Décide si une clé publique reçue dans un handshake est acceptable.
    /// </summary>
    /// <remarks>
    /// Un pair en attente est accepté au niveau cryptographique : c'est ce qui
    /// permet à la première connexion d'aboutir et à l'utilisateur de voir une
    /// demande à confirmer. Rien ne lui sera appliqué tant qu'il n'est pas
    /// accepté pour de bon.
    /// </remarks>
    /// <summary>
    /// Décide si une clé publique reçue dans un handshake est acceptable, et
    /// l'apprend au passage.
    /// </summary>
    /// <remarks>
    /// L'empreinte de la clé reçue sert de clé de recherche dans le carnet :
    /// une clé substituée donne une autre empreinte, donc aucune entrée, donc un
    /// refus. La liaison est ainsi assurée par construction, sans comparaison
    /// explicite à oublier un jour.
    ///
    /// Une clé déjà apprise et qui changerait serait une contradiction, et est
    /// refusée : elle signifierait que deux clés différentes ont la même
    /// empreinte.
    /// </remarks>
    public bool IsAuthorized(byte[] publicKey)
    {
        var id = PeerId.Of(publicKey);

        // Lecture et écriture sous le même verrou : apprendre la clé ne doit pas
        // effacer une pause posée entre les deux.
        lock (_gate)
        {
            if (_pairs.GetValueOrDefault(id) is not { Trust: PairTrust.Pending or PairTrust.Accepted } record)
                return false;

            if (record.PublicKey is { } known)
                return known.AsSpan().SequenceEqual(publicKey);

            _pairs[id] = record with { PublicKey = publicKey };
            return true;
        }
    }

    public void Load(IEnumerable<PairRecord> records)
    {
        // Matérialisé hors du verrou : la source pourrait être une requête
        // paresseuse sur ce carnet même.
        var loaded = records.ToList();

        lock (_gate)
        {
            _pairs.Clear();

            foreach (var record in loaded)
                _pairs[record.Id] = record;
        }
    }

    /// <remarks>
    /// Lire, transformer et réécrire sous un seul verrou : c'est ce qui empêche
    /// deux changements simultanés de la même entrée de s'écraser.
    /// </remarks>
    private void Update(PeerId id, Func<PairRecord, PairRecord> change)
    {
        lock (_gate)
        {
            if (_pairs.TryGetValue(id, out var record))
                _pairs[id] = change(record);
        }
    }
}
