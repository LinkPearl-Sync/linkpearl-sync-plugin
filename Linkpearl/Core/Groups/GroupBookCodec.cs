using System.Text.Json;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Groups;

/// <summary>
/// La forme sur disque des groupes, sans le chiffrement.
/// </summary>
/// <remarks>
/// Dans le noyau pour se tester sous Linux ; le chiffrement DPAPI reste dans
/// l'adaptateur. Un format explicite plutôt que la sérialisation des types du
/// noyau, pour la même raison que le carnet : ceux-ci vont changer, et des
/// groupes illisibles après une mise à jour seraient perdus. Les champs à venir
/// (politique, clé de signature) s'ajouteront en fin d'enregistrement, nullables.
///
/// Le fichier se relit comme un message venu d'ailleurs, puisqu'il voyagera
/// dans la sauvegarde : une entrée hors des règles est ignorée, pas le
/// fichier entier.
/// </remarks>
public static class GroupBookCodec
{
    private sealed record MemberDto(
        string Fingerprint, string? Id, string DisplayName, long? LastSeenAt, bool Paused, int Receive,
        string? PublicKey = null);

    private sealed record BanDto(string? Peer, string? Fingerprint);

    private sealed record GroupDto(
        string Id, string Name, string Secret, string[] Rendezvous, long JoinedAt, MemberDto[] Members,
        string? OwnerKey = null, string? SigningKey = null, string? Policy = null,
        bool Dormant = false, BanDto[]? Blocked = null, int? DefaultReceive = null);

    /// <summary>Un membre sans réglage propre, qui suit le groupe.</summary>
    private const int ReceiveFollowsGroup = -1;

    /// <summary>Même borne que les bannis d'une politique.</summary>
    private const int MaxBlocked = 256;

    /// <summary>Même borne que la clé PKCS#8 qu'une identité ECDSA P-256 exporte.</summary>
    private const int MaxSigningKeyLength = 1024;

    private const int ReceiveAnimations = 1;
    private const int ReceiveVfx = 2;
    private const int ReceiveSounds = 4;

    /// <summary>Même borne que le nom de personnage dans une demande de pairage.</summary>
    private const int MaxNameLength = 64;

    public static byte[] Encode(IEnumerable<GroupRecord> groups)
        => JsonSerializer.SerializeToUtf8Bytes(groups.Select(group => new GroupDto(
            Convert.ToHexStringLower(group.Id.ToBytes()),
            group.Name,
            Convert.ToHexStringLower(group.Secret),
            [.. group.Rendezvous.Select(place => place.ToString())],
            group.JoinedAt.ToUnixTimeSeconds(),
            [.. group.Members.Values.Select(member => new MemberDto(
                Convert.ToHexStringLower(member.Fingerprint.ToBytes()),
                member.Id is { } id ? Convert.ToHexStringLower(id.ToBytes()) : null,
                member.DisplayName,
                member.LastSeenAt?.ToUnixTimeSeconds(),
                member.Paused,
                member.Receive is { } receive ? ToBits(receive) : ReceiveFollowsGroup,
                member.PublicKey is { } publicKey ? Convert.ToHexStringLower(publicKey) : null))],
            group.OwnerKey is { } ownerKey ? Convert.ToHexStringLower(ownerKey) : null,
            group.SigningKey is { } signingKey ? Convert.ToHexStringLower(signingKey) : null,
            group.Policy is { } policy ? Convert.ToHexStringLower(GroupPolicyCodec.Encode(policy)) : null,
            group.IsPublic && group.Dormant,
            [.. group.Blocked.Select(ban => new BanDto(
                ban.Peer is { } peer ? Convert.ToHexStringLower(peer.ToBytes()) : null,
                ban.Fingerprint is { } print ? Convert.ToHexStringLower(print.ToBytes()) : null))],
            ToBits(group.DefaultReceive))).ToList());

    public static IReadOnlyList<GroupRecord> Decode(ReadOnlySpan<byte> json)
    {
        var dtos = JsonSerializer.Deserialize<List<GroupDto?>>(json) ?? [];
        var groups = dtos.Select(Rehydrate).OfType<GroupRecord>().ToList();

        // Le Public ne compte pas dans les dix : il n'ouvre qu'une boîte de
        // présence par fenêtre, et aucune d'admission.
        return [.. groups.Where(group => group.IsPublic is false).Take(GroupBook.MaxGroups),
                .. groups.Where(group => group.IsPublic).Take(1)];
    }

    public static bool IsValid(byte[] json)
    {
        try
        {
            _ = Decode(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GroupRecord? Rehydrate(GroupDto? dto)
    {
        if (dto is null)
            return null;

        try
        {
            var secret = Convert.FromHexString(dto.Secret);

            if (secret.Length != GroupDerivation.SecretSize || dto.Name.Length is 0 or > MaxNameLength)
                return null;

            var places = (dto.Rendezvous ?? [])
                .Select(text => RendezvousAddress.TryParse(text, out var place, out _) ? place : (RendezvousAddress?)null)
                .OfType<RendezvousAddress>()
                .ToList();

            var id = GroupId.FromBytes(Convert.FromHexString(dto.Id));
            var isPublic = PublicGroup.Is(id);

            var members = (dto.Members ?? [])
                .Select(RehydrateMember)
                .OfType<GroupMember>()
                .Take(isPublic ? GroupBook.MaxPublicMembers : GroupBook.MaxMembersPerGroup)
                .GroupBy(member => member.Fingerprint)
                .ToDictionary(same => same.Key, same => same.First());

            // Le Public tire ses services de la configuration : il vit sans en
            // avoir d'enregistré. Un groupe privé sans service est injoignable,
            // comme un pair sans service.
            if (places.Count == 0 && isPublic is false)
                return null;

            // L'identifiant du Public avec un autre secret, ou une clé, ferait
            // composer sous des boîtes que personne n'ouvre, ou prêter une
            // autorité à ce qui n'en a aucune.
            if (isPublic && (secret.AsSpan().SequenceEqual(PublicGroup.Secret) is false
                             || dto.OwnerKey is not null || dto.SigningKey is not null || dto.Policy is not null))
                return null;

            var blocked = (dto.Blocked ?? [])
                .Take(MaxBlocked)
                .Select(RehydrateBan)
                .OfType<GroupBan>()
                .ToList();

            byte[]? ownerKey = null;

            if (dto.OwnerKey is { } ownerKeyHex)
            {
                var candidate = Convert.FromHexString(ownerKeyHex);

                // Une clé qui ne redonne pas l'identifiant du groupe n'a
                // aucune raison qu'on lui prête la moindre autorité : elle
                // ferait accepter n'importe quelle politique prétendument
                // signée par le groupe. L'entrée entière est rejetée plutôt
                // que gardée sans clé, pour ne pas faire disparaître en
                // silence un groupe privé en groupe d'essai.
                if (candidate.Length != CryptoPrimitives.CompressedPointLength || GroupId.Of(candidate) != id)
                    return null;

                ownerKey = candidate;
            }

            byte[]? signingKey = null;

            if (dto.SigningKey is { } signingKeyHex)
            {
                // La clé de signature n'a de sens que pour le propriétaire, et
                // le propriétaire est celui dont la clé redonne l'identifiant :
                // sans OwnerKey, une SigningKey ne prouve rien et rejette
                // l'entrée plutôt que de la garder à moitié.
                if (ownerKey is null)
                    return null;

                var candidate = Convert.FromHexString(signingKeyHex);

                if (candidate.Length > MaxSigningKeyLength)
                    return null;

                signingKey = candidate;
            }

            GroupPolicy? policy = null;

            if (dto.Policy is { Length: > 0 } policyHex && ownerKey is not null)
            {
                var encoded = Convert.FromHexString(policyHex);

                // Une politique qui ne passe plus TryAccept (clé altérée,
                // signature invalide, bornes dépassées) est oubliée : le
                // groupe reste, seule la politique retombe à néant.
                if (GroupPolicyRules.TryAccept(encoded, id, ownerKey, out var accepted, out _))
                    policy = accepted;
            }

            return new GroupRecord
            {
                Id = id,
                Name = dto.Name,
                Secret = secret,
                Rendezvous = places,
                JoinedAt = DateTimeOffset.FromUnixTimeSeconds(dto.JoinedAt),
                Members = members,
                OwnerKey = ownerKey,
                SigningKey = signingKey,
                Policy = policy,
                Dormant = isPublic && dto.Dormant,
                Blocked = blocked,
                DefaultReceive = dto.DefaultReceive is { } bits
                    ? FromBits(bits)
                    : isPublic ? TransientCategories.None : TransientCategories.All,
            };
        }
        catch (Exception e) when (e is FormatException or ArgumentException or NullReferenceException)
        {
            return null;   // une entrée abîmée ne doit pas emporter les autres
        }
    }

    private static GroupMember? RehydrateMember(MemberDto? dto)
    {
        if (dto?.DisplayName is not { Length: <= MaxNameLength })
            return null;

        try
        {
            var id = dto.Id is { } idHex ? PeerId.FromBytes(Convert.FromHexString(idHex)) : (PeerId?)null;

            byte[]? publicKey = null;

            if (dto.PublicKey is { } publicKeyHex)
            {
                var candidate = Convert.FromHexString(publicKeyHex);

                // Une clé complète incohérente (mauvaise taille, épinglage
                // absent, ou qui ne redonne pas l'Id épinglé) ne peut pas
                // servir à nommer un modérateur (tâche 4) : elle attesterait
                // une clé qui n'est pas celle du membre. Ce n'est pas une
                // raison de perdre le membre ni son épinglage pour autant :
                // seule la clé retombe à null, et Admit la recomplétera au
                // prochain contact avec la vraie clé.
                if (candidate.Length == CryptoPrimitives.PublicPointLength
                    && id is { } pinned && PeerId.Of(candidate) == pinned)
                {
                    publicKey = candidate;
                }
            }

            return new GroupMember
            {
                Fingerprint = PlayerFingerprint.FromBytes(Convert.FromHexString(dto.Fingerprint)),
                Id = id,
                DisplayName = dto.DisplayName,
                LastSeenAt = dto.LastSeenAt is { } seen ? DateTimeOffset.FromUnixTimeSeconds(seen) : null,
                Paused = dto.Paused,
                Receive = dto.Receive == ReceiveFollowsGroup ? null : FromBits(dto.Receive),
                PublicKey = publicKey,
            };
        }
        catch (Exception e) when (e is FormatException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    private static GroupBan? RehydrateBan(BanDto? dto)
    {
        try
        {
            var peer = dto?.Peer is { } peerHex ? PeerId.FromBytes(Convert.FromHexString(peerHex)) : (PeerId?)null;
            var print = dto?.Fingerprint is { } printHex ? PlayerFingerprint.FromBytes(Convert.FromHexString(printHex)) : (PlayerFingerprint?)null;

            return peer is null && print is null ? null : new GroupBan(peer, print);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static int ToBits(TransientCategories receive)
        => (receive.Animations ? ReceiveAnimations : 0)
         | (receive.Vfx ? ReceiveVfx : 0)
         | (receive.Sounds ? ReceiveSounds : 0);

    private static TransientCategories FromBits(int bits)
        => new((bits & ReceiveAnimations) != 0, (bits & ReceiveVfx) != 0, (bits & ReceiveSounds) != 0);
}
