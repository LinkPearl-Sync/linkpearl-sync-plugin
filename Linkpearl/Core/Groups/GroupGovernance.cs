using System.Security.Cryptography;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Groups;

/// <summary>Ce qu'on peut faire dans un groupe. Strictement local.</summary>
public enum GroupRole
{
    Member,
    Moderator,
    Owner,
}

public sealed record CreatedGroup(GroupRecord Record, InvitationTicket Code);

/// <summary>
/// Créer un groupe et en modifier la politique.
/// </summary>
/// <remarks>
/// Chaque modification rend la politique encodée, déjà passée par les règles
/// que les autres membres appliqueront : ce qui sort d'ici est ce qu'ils
/// accepteront. L'appelant la donne ensuite à <see cref="GroupBook.OfferPolicy"/>,
/// et le moteur la propage.
/// </remarks>
public static class GroupGovernance
{
    public static CreatedGroup Create(
        string name, string password, RendezvousAddress service, byte[] ownerIdentityKey, DateTimeOffset now)
    {
        if (GroupPolicyCodec.IsCreatableName(name) is false)
            throw new ArgumentException(
                "nom de groupe invalide : 1 à 32 lettres, chiffres ou tirets, sans espace", nameof(name));

        // Une clé propre au groupe, jamais une identité : un propriétaire peut
        // ainsi créer plusieurs groupes, chacun avec son identifiant.
        using var groupKey = CryptoPrimitives.GenerateIdentity();
        var ownerKey = GroupPolicySigning.CompressedKey(groupKey);
        var id = GroupId.Of(ownerKey);
        var code = InvitationTicket.Create();

        var attestation = GroupPolicySigning.SignAttestation(
            new GroupAttestation(
                id, 1, password.Length > 0 ? AdmissionMode.Password : AdmissionMode.Validation,
                CryptoPrimitives.Compress(ownerIdentityKey), [], []),
            groupKey);

        var policy = GroupPolicySigning.Sign(
            new GroupPolicy(id, 1, name, code.ToBytes(), [service], password, [], false, attestation, [], []),
            groupKey);

        Validated(policy, id, ownerKey);

        var record = new GroupRecord
        {
            Id = id,
            Name = name,
            Secret = RandomNumberGenerator.GetBytes(GroupDerivation.SecretSize),
            Rendezvous = [service],
            JoinedAt = now,
            OwnerKey = ownerKey,
            SigningKey = groupKey.ExportPkcs8PrivateKey(),
            Policy = policy,
        };

        return new CreatedGroup(record, code);
    }

    /// <summary>Notre rôle dans ce groupe.</summary>
    /// <param name="ourIdentityKey">Notre clé d'identité, point de 65 octets.</param>
    public static GroupRole RoleOf(GroupRecord group, byte[]? ourIdentityKey)
    {
        if (group.SigningKey is not null)
            return GroupRole.Owner;

        if (ourIdentityKey is null || group.Policy is not { } policy)
            return GroupRole.Member;

        return policy.IsModerator(CryptoPrimitives.Compress(ourIdentityKey)) ? GroupRole.Moderator : GroupRole.Member;
    }

    public static InvitationTicket? CodeOf(GroupRecord group)
        => group.Policy is { } policy ? InvitationTicket.FromBytes(policy.Code) : null;

    public static byte[] Rename(GroupRecord group, string name, ECDsa? moderator)
        => Edit(group, moderator, policy => policy with { Name = name });

    public static byte[] NewCode(GroupRecord group, ECDsa? moderator)
        => Edit(group, moderator, policy => policy with { Code = InvitationTicket.Create().ToBytes() });

    public static byte[] SetPassword(GroupRecord group, string password, ECDsa? moderator)
        => Edit(group, moderator, policy => policy with { Password = password });

    /// <summary>
    /// Bannit. Si <paramref name="moderator"/> est nul (le propriétaire) et que la
    /// clé visée est celle d'un modérateur attesté, le retire d'abord des
    /// modérateurs, dans la même politique : les règles refusent sans exception
    /// un bannissement par clé qui viserait un pair protégé, et rebannir un
    /// modérateur qu'on vient tout juste de démettre exigerait sinon un aller-
    /// retour que rien ne garantit d'obtenir.
    /// </summary>
    public static byte[] Ban(GroupRecord group, GroupBan ban, ECDsa? moderator)
    {
        if (moderator is null && ban.Peer is { } targeted && IsAttestedModerator(group, targeted))
        {
            return AttestAndEdit(
                group,
                attestation => attestation with
                {
                    Moderators = [.. attestation.Moderators.Where(key => PeerOf(key) != targeted)],
                },
                policy => WithBan(policy, ban));
        }

        return Edit(group, moderator, policy => WithBan(policy, ban));
    }

    public static byte[] Unban(GroupRecord group, GroupBan ban, ECDsa? moderator)
        => Edit(group, moderator, policy => policy with { Bans = [.. policy.Bans.Where(existing => existing != ban)] });

    public static byte[] Dissolve(GroupRecord group)
        => Edit(group, null, policy => policy with { Dissolved = true });

    public static byte[] SetAdmission(GroupRecord group, byte mode)
        => Attest(group, attestation => attestation with { Admission = mode });

    /// <summary>
    /// Remplace les modérateurs. Un modérateur nommé ici qui était banni par
    /// clé voit ce bannissement levé dans la même politique : sans cela, les
    /// règles rejetteraient la politique elle-même, puisqu'un modérateur
    /// fraîchement attesté devient un pair protégé qu'aucun bannissement par
    /// clé ne peut viser. Lever un bannissement par seule empreinte n'est pas
    /// nécessaire : la protection d'un pair attesté joue déjà à l'application.
    /// </summary>
    /// <param name="identityKeys">Les clés d'identité des modérateurs, points de 65 octets.</param>
    public static byte[] SetModerators(GroupRecord group, IReadOnlyList<byte[]> identityKeys)
    {
        var promoted = identityKeys.Select(key => PeerId.Of(key)).ToHashSet();

        return AttestAndEdit(
            group,
            attestation => attestation with { Moderators = [.. identityKeys.Select(key => CryptoPrimitives.Compress(key))] },
            policy => policy with
            {
                Bans = [.. policy.Bans.Where(ban => ban.Peer is not { } peer || promoted.Contains(peer) is false)],
            });
    }

    /// <summary>
    /// Une modification du corps de la politique.
    /// </summary>
    /// <remarks>
    /// Signée par le propriétaire (<paramref name="moderator"/> nul), elle passe
    /// par <see cref="AttestAndEdit"/> : l'attestation est re-signée, sa version
    /// haussée, même à contenu inchangé. L'ordre des politiques compare d'abord
    /// la version d'attestation, donc une modification du propriétaire
    /// l'emporte toujours, y compris sur un modérateur hostile qui aurait poussé
    /// la version du <em>corps</em> à <see cref="ulong.MaxValue"/> : lui n'a
    /// jamais la clé du groupe, donc jamais moyen de hausser l'attestation.
    ///
    /// Signée par un modérateur, la version du corps avance de un, sous
    /// contrôle : un modérateur qui l'aurait épuisée ne bloque pas le
    /// propriétaire, dont le chemin ne dépend jamais de cette version.
    /// </remarks>
    private static byte[] Edit(GroupRecord group, ECDsa? moderator, Func<GroupPolicy, GroupPolicy> change)
    {
        if (moderator is null)
            return AttestAndEdit(group, attestation => attestation, change);

        var current = Current(group);
        ulong nextVersion;

        try
        {
            nextVersion = checked(current.Version + 1);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("version de politique épuisée : le propriétaire doit intervenir");
        }

        var next = GroupPolicySigning.Sign(change(current) with { Version = nextVersion }, moderator);
        return Validated(next, group.Id, group.OwnerKey!);
    }

    private static byte[] Attest(GroupRecord group, Func<GroupAttestation, GroupAttestation> change)
        => AttestAndEdit(group, change, policy => policy);

    /// <summary>
    /// Re-signe l'attestation (version haussée) et le corps de la politique
    /// (version haussée) en une seule politique, tous deux signés par la clé du
    /// groupe : seul le propriétaire y a accès.
    /// </summary>
    private static byte[] AttestAndEdit(
        GroupRecord group, Func<GroupAttestation, GroupAttestation> attestationChange, Func<GroupPolicy, GroupPolicy> policyChange)
    {
        var current = Current(group);
        using var owner = ImportGroupKey(group);

        // Sans checked : c'est le chemin du propriétaire, dont la version
        // d'attestation prime toujours sur celle du corps (GroupPolicyRules.
        // IsNewer). Un modérateur peut avoir poussé le corps jusqu'à
        // ulong.MaxValue (voir Edit) ; ici on ne veut jamais que ce dépassement
        // bloque le propriétaire, seulement que la valeur avance.
        var attestation = GroupPolicySigning.SignAttestation(
            attestationChange(current.Attestation) with { Version = current.Attestation.Version + 1 }, owner);

        var next = GroupPolicySigning.Sign(
            policyChange(current) with { Version = current.Version + 1, Attestation = attestation }, owner);

        return Validated(next, group.Id, group.OwnerKey!);
    }

    private static GroupPolicy WithBan(GroupPolicy policy, GroupBan ban)
        => policy with { Bans = [.. policy.Bans.Where(existing => existing != ban), ban] };

    private static bool IsAttestedModerator(GroupRecord group, PeerId peer)
        => Current(group).Attestation.Moderators.Any(key => PeerOf(key) == peer);

    /// <summary>Le pair d'une clé de modérateur compressée, telle qu'attestée.</summary>
    private static PeerId PeerOf(byte[] compressedKey) => PeerId.Of(CryptoPrimitives.Decompress(compressedKey));

    private static GroupPolicy Current(GroupRecord group)
        => group.Policy ?? throw new InvalidOperationException("ce groupe n'a pas encore reçu sa politique");

    private static ECDsa ImportGroupKey(GroupRecord group)
    {
        var signing = group.SigningKey ?? throw new InvalidOperationException("seul le propriétaire peut faire cela");

        // La clé arrive aussi d'une sauvegarde : une autre courbe que P-256 y
        // serait lisible par .NET, puis ferait lever plus loin, à l'encodage
        // compressé de sa partie publique.
        return IdentityKeyPair.TryImport(signing, out var key, out _)
            ? key
            : throw new InvalidOperationException("clé du groupe illisible");
    }

    private static byte[] Validated(GroupPolicy policy, GroupId id, byte[] groupKey)
    {
        var encoded = GroupPolicyCodec.Encode(policy);

        if (GroupPolicyRules.TryAccept(encoded, id, groupKey, out _, out var why) is false)
            throw new InvalidOperationException(why);

        return encoded;
    }
}
