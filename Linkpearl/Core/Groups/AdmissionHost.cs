using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Groups;

/// <summary>Un dépôt à faire dans la boîte personnelle du candidat, sur ces services.</summary>
/// <remarks>Le candidat est désigné par nom et monde : c'est l'adaptateur qui en tire l'adresse.</remarks>
public sealed record AdmissionOutbound(string CharacterName, ushort WorldId, IReadOnlyList<RendezvousAddress> Via, byte[] Payload);

/// <summary>Une demande qui attend qu'un modérateur tranche.</summary>
public sealed record PendingValidation(
    GroupId Group, string GroupName, byte[] Nonce, string CharacterName, ushort WorldId, DateTimeOffset LastSeen);

/// <summary>
/// Le côté membre de l'admission.
/// </summary>
/// <remarks>
/// Appelé depuis le fil qui lit les boîtes et depuis l'interface : tout passe
/// sous un verrou. Le verrou du carnet de groupes est pris après le nôtre,
/// jamais l'inverse.
/// </remarks>
public sealed class AdmissionHost(
    GroupBook book, Func<byte[]?> ourIdentityKey, IClock clock,
    Func<AdmissionRequest, PlayerFingerprint, bool>? refuses = null,
    Func<PlayerFingerprint?>? ourFingerprint = null) : IDisposable
{
    /// <summary>Au-delà, on répond « trop d'essais » sans vérifier.</summary>
    /// <remarks>
    /// Le compte est par clé de candidat : il freine l'erreur honnête répétée,
    /// pas la devinette, qu'une clé d'identité neuve par essai contourne sans
    /// peine. Contre la devinette, la seule protection est l'entropie du mot
    /// de passe.
    /// </remarks>
    public const int MaxFailures = 5;

    /// <summary>Au-delà, une demande nouvelle ne reçoit aucun défi.</summary>
    /// <remarks>
    /// La boîte d'admission est ouverte à quiconque tient le code : sans
    /// plafond, un porteur du code ferait créer un éphémère et garder une
    /// entrée par demande forgée, sans limite. 64 dépasse de loin les
    /// candidatures simultanées plausibles d'un groupe de joueurs.
    /// </remarks>
    public const int MaxLiveChallenges = 64;

    /// <summary>Au-delà, une demande nouvelle n'est pas proposée aux modérateurs.</summary>
    /// <remarks>Même raison que <see cref="MaxLiveChallenges"/> : une page Demandes inondée ne sert plus à personne.</remarks>
    public const int MaxPendingValidations = 64;

    /// <summary>
    /// Combien de réponses et de comptes d'échecs on garde au plus ; les plus
    /// anciens sortent d'abord.
    /// </summary>
    /// <remarks>
    /// Chaque aléa ou clé neuve y ajoute une entrée, et n'importe qui en
    /// fabrique à volonté. Oublier une vieille réponse coûte au pire un défi
    /// de plus ; oublier un vieux compte d'échecs ne coûte rien de plus que
    /// ce que la clé neuve permet déjà (voir <see cref="MaxFailures"/>).
    /// </remarks>
    private const int MaxRemembered = 256;

    /// <summary>Le temps pour un candidat de répondre à un défi.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Délai minimal entre deux renvois du même défi, par aléa.</summary>
    /// <remarks>
    /// Chaque renvoi est un dépôt, et le service compte 60 trames par minute
    /// et par adresse IP (RendezvousLimits.AnnouncementsPerMinute) : un porteur
    /// du code qui rejoue la même demande en boucle, ou la même demande reçue
    /// par plusieurs services, épuiserait sinon ce quota et ferait déconnecter
    /// le membre. Le candidat honnête ne redépose qu'une fois par minute
    /// (AdmissionCandidate.RedepositInterval) : trente secondes ne le freinent pas.
    /// </remarks>
    public static readonly TimeSpan ChallengeResendInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Une demande en validation disparaît si elle n'est plus redéposée.
    /// </summary>
    /// <remarks>Le candidat redépose chaque minute : trois minutes de silence veulent dire qu'il a abandonné.</remarks>
    public static readonly TimeSpan ValidationSilence = TimeSpan.FromMinutes(3);

    /// <summary>Combien de temps on se souvient d'avoir déjà répondu à une demande.</summary>
    private static readonly TimeSpan AnswerMemory = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Challenge> _challenges = [];
    private readonly Dictionary<string, Waiting> _pending = [];
    private readonly Dictionary<string, DateTimeOffset> _answered = [];
    private readonly Dictionary<PeerId, Failures> _failures = [];
    private bool _disposed;

    /// <param name="LastSent">Le dernier envoi de ce défi, pour espacer les renvois (voir <see cref="ChallengeResendInterval"/>).</param>
    private sealed record Challenge(
        GroupId Group, AdmissionRequest Request, PlayerFingerprint Candidate, ECDiffieHellman Ephemeral,
        AdmissionOutbound Outbound, DateTimeOffset Created, DateTimeOffset LastSent);

    private sealed record Waiting(GroupId Group, AdmissionRequest Request, PlayerFingerprint Candidate, DateTimeOffset LastSeen);

    private readonly record struct Failures(int Count, long Window, DateTimeOffset Last);

    public IReadOnlyList<PendingValidation> Pending
    {
        get
        {
            lock (_gate)
            {
                Prune();

                // Ce qu'on ne peut plus trancher ne se propose plus : Approve
                // le refuserait de toute façon (voir StillValidates).
                return [.. _pending.Values
                    .Where(waiting => book.Find(waiting.Group) is { } group && StillValidates(group, waiting))
                    .OrderBy(waiting => waiting.LastSeen)
                    .Select(waiting => new PendingValidation(
                        waiting.Group, book.Find(waiting.Group)?.Name ?? "?", waiting.Request.Nonce,
                        waiting.Request.CharacterName, waiting.Request.WorldId, waiting.LastSeen))];
            }
        }
    }

    /// <summary>Les codes dont on ouvre la boîte d'admission : ceux des groupes qu'on peut admettre.</summary>
    /// <remarks>
    /// En mode validation, un simple membre n'y peut rien : il n'ouvre pas la
    /// boîte, et ne fait pas payer au service des dépôts qu'il jetterait.
    /// </remarks>
    public IReadOnlyList<byte[]> AdmissionCodes
        => [.. book.All.Where(CanAdmit).Select(group => group.Policy!.Code)];

    public IReadOnlyList<AdmissionOutbound> OnRequest(AdmissionRequest request, PlayerFingerprint candidate)
    {
        // Le filtre des services dérive en PBKDF2, des dixièmes de seconde :
        // il ne tourne jamais sous notre verrou, que l'interface prend à chaque
        // image depuis le thread du jeu. Une première passe dit s'il faut
        // trancher, le filtre tourne hors du verrou, puis la décision est
        // rejouée avec le verdict, sur l'état tel qu'il est devenu entre-temps.
        if (Answer(request, candidate, screened: null) is { } answers)
            return answers;

        return Answer(request, candidate, refuses!(request, candidate)) ?? [];
    }

    /// <summary>La décision, ou null s'il faut d'abord consulter le filtre des services.</summary>
    private IReadOnlyList<AdmissionOutbound>? Answer(AdmissionRequest request, PlayerFingerprint candidate, bool? screened)
    {
        var group = Admitting(request.Code);

        if (group?.Policy is not { } policy)
            return [];

        var ours = ourIdentityKey();

        if (ours is not null && ours.AsSpan().SequenceEqual(request.PublicKey))
            return [];

        if (policy.IsBanned(PeerId.Of(request.PublicKey), candidate))
            return [];

        var key = Convert.ToHexStringLower(request.Nonce);
        var now = clock.UtcNow;

        lock (_gate)
        {
            if (_disposed)
                return [];

            Prune();

            if (_answered.ContainsKey(key))
                return [];

            if (policy.Attestation.Admission == AdmissionMode.Validation)
            {
                // L'aléa circule en clair dans la demande : un porteur du code
                // peut le rejouer avec son propre éphémère. Seule la demande
                // identique rafraîchit l'attente, sans quoi la bienvenue que le
                // modérateur approuve serait scellée pour l'intrus.
                if (_pending.TryGetValue(key, out var waiting))
                {
                    if (SameRequest(waiting.Request, request))
                        _pending[key] = waiting with { LastSeen = now };

                    return [];
                }

                if (_pending.Count >= MaxPendingValidations)
                    return [];

                // Listé par un service actif : aucune réponse, comme un banni du
                // groupe. Après les plafonds et pour une demande neuve seulement :
                // le filtre coûte une dérivation, et une inondation de demandes
                // forgées ne doit pas la payer au-delà de ce que le plafond laisse.
                if (refuses is not null && screened is null)
                    return null;

                if (screened is true)
                {
                    _answered[key] = now;
                    return [];
                }

                _pending[key] = new Waiting(group.Id, request, candidate, now);
                return [];
            }

            // Un redépôt de la même demande veut dire que le candidat n'a rien
            // reçu, ou que sa preuve s'est perdue : lui renvoyer le même défi
            // le laisse la rejouer. Toute autre demande de même aléa est un
            // rejeu, qui n'obtient rien.
            if (_challenges.TryGetValue(key, out var live))
            {
                if (SameRequest(live.Request, request) is false || now - live.LastSent < ChallengeResendInterval)
                    return [];

                _challenges[key] = live with { LastSent = now };
                return [live.Outbound with { Payload = [.. live.Outbound.Payload] }];
            }

            if (_challenges.Count >= MaxLiveChallenges)
                return [];

            // Listé par un service actif : aucune réponse, comme un banni du
            // groupe. Après les plafonds et pour une demande neuve seulement :
            // le filtre coûte une dérivation, et une inondation de demandes
            // forgées ne doit pas la payer au-delà de ce que le plafond laisse.
            if (refuses is not null && screened is null)
                return null;

            if (screened is true)
            {
                _answered[key] = now;
                return [];
            }

            var ephemeral = CryptoPrimitives.GenerateEphemeral();
            var challenge = new AdmissionChallenge(request.Nonce, CryptoPrimitives.ExportPublicPoint(ephemeral));
            var outbound = new AdmissionOutbound(request.CharacterName, request.WorldId, group.Rendezvous, AdmissionCodec.Encode(challenge));
            _challenges[key] = new Challenge(group.Id, request, candidate, ephemeral, outbound, now, now);

            return [outbound with { Payload = [.. outbound.Payload] }];
        }
    }

    public IReadOnlyList<AdmissionOutbound> OnProof(AdmissionProof proof)
    {
        var key = Convert.ToHexStringLower(proof.Nonce);
        Challenge? challenge;

        lock (_gate)
        {
            if (_disposed)
                return [];

            Prune();

            // Plusieurs membres défient le même candidat : la preuve désigne
            // celui qu'il a retenu par son éphémère, les autres l'ignorent.
            if (_challenges.TryGetValue(key, out challenge) is false
                || CryptoPrimitives.ExportPublicPoint(challenge.Ephemeral).AsSpan().SequenceEqual(proof.MemberEphemeral) is false)
                return [];

            _challenges.Remove(key);
            _answered[key] = clock.UtcNow;
            KeepNewest(_answered, at => at);
        }

        using (challenge.Ephemeral)
        {
            // Le groupe vient du défi, jamais de proof.Code : ce dernier ne
            // figure dans aucune donnée associée à l'étiquette, donc un porteur
            // du réseau peut le modifier en route sans l'invalider. La politique
            // a pu changer pendant les dix minutes du défi : on revérifie donc
            // tout ce qu'OnRequest avait vérifié, contre le code de la demande
            // mémorisée. Un code renouvelé révoque ainsi les défis en cours, et
            // un groupe passé en validation n'admet plus sur simple mot de
            // passe. Chaque cas se tait, comme OnRequest devant la même demande.
            var request = challenge.Request;
            var candidate = PeerId.Of(request.PublicKey);
            var group = book.Find(challenge.Group);

            if (group?.Policy is not { Dissolved: false } policy
                || CanAdmit(group) is false
                || policy.Attestation.Admission != AdmissionMode.Password
                || policy.Code.AsSpan().SequenceEqual(request.Code) is false
                || policy.IsBanned(candidate, challenge.Candidate))
                return [];

            var window = MailboxAddress.IndexAt(clock.UtcNow);

            lock (_gate)
            {
                if (_failures.TryGetValue(candidate, out var failures) && failures.Window == window && failures.Count >= MaxFailures)
                    return [Refuse(request, group, RefusalReason.TooManyAttempts)];
            }

            var associated = AdmissionSealing.Associated(AdmissionKind.Proof, request.Nonce, proof.MemberEphemeral);
            var expected = AdmissionSealing.ProofTag(challenge.Ephemeral, request.Ephemeral, request.Nonce, policy.Password, associated);

            if (CryptographicOperations.FixedTimeEquals(expected, proof.Tag) is false)
            {
                lock (_gate)
                {
                    var previous = _failures.TryGetValue(candidate, out var f) && f.Window == window ? f.Count : 0;
                    _failures[candidate] = new Failures(previous + 1, window, clock.UtcNow);
                    KeepNewest(_failures, entry => entry.Last);
                }

                return [Refuse(request, group, RefusalReason.WrongPassword)];
            }

            return [Welcome(request, group, challenge.Ephemeral)];
        }
    }

    /// <summary>Valide une demande en attente, si on peut encore la valider.</summary>
    /// <remarks>
    /// Tout se revérifie au moment du clic, comme dans <see cref="OnProof"/> :
    /// la demande a pu attendre des minutes, et la politique changer entre-temps.
    /// Un modérateur retiré, un groupe repassé en mot de passe ou un code
    /// renouvelé ne valident plus rien, sans quoi un ancien modérateur ferait
    /// encore entrer qui il veut par les demandes restées en attente chez lui.
    /// </remarks>
    public AdmissionOutbound? Approve(byte[] nonce)
    {
        if (TakePending(nonce) is not { } waiting
            || book.Find(waiting.Group) is not { } group
            || StillValidates(group, waiting) is false)
            return null;

        using var ephemeral = CryptoPrimitives.GenerateEphemeral();
        return Welcome(waiting.Request, group, ephemeral);
    }

    /// <summary>Refuse une demande en attente, aux mêmes conditions que la valider.</summary>
    /// <remarks>Un refus parle aussi au nom du groupe : un modérateur retiré n'a plus à le donner.</remarks>
    public AdmissionOutbound? Decline(byte[] nonce)
    {
        if (TakePending(nonce) is not { } waiting
            || book.Find(waiting.Group) is not { } group
            || StillValidates(group, waiting) is false)
            return null;

        return Refuse(waiting.Request, group, RefusalReason.Declined);
    }

    /// <summary>Vrai si l'on peut encore trancher cette demande, sur la politique courante.</summary>
    /// <remarks>Le bannissement du candidat a pu tomber pendant qu'elle attendait.</remarks>
    private bool StillValidates(GroupRecord group, Waiting waiting)
        => CanAdmit(group)
           && group.Policy is { } policy
           && policy.Attestation.Admission == AdmissionMode.Validation
           && GroupGovernance.RoleOf(group, ourIdentityKey()) is not GroupRole.Member
           && policy.Code.AsSpan().SequenceEqual(waiting.Request.Code)
           && policy.IsBanned(PeerId.Of(waiting.Request.PublicKey), waiting.Candidate) is false;

    private Waiting? TakePending(byte[] nonce)
    {
        var key = Convert.ToHexStringLower(nonce);

        lock (_gate)
        {
            if (_disposed || _pending.Remove(key, out var waiting) is false)
                return null;

            _answered[key] = clock.UtcNow;
            KeepNewest(_answered, at => at);
            return waiting;
        }
    }

    private GroupRecord? Admitting(byte[] code)
        => book.All.FirstOrDefault(group => CanAdmit(group) && group.Policy!.Code.AsSpan().SequenceEqual(code));

    /// <remarks>
    /// Un exclu garde le groupe en attendant qu'une politique plus récente le
    /// réintègre, et garde donc son code et son mot de passe : il n'admet
    /// personne pour autant, sans quoi l'exclusion se contournerait en
    /// faisant entrer un autre personnage.
    /// </remarks>
    private bool CanAdmit(GroupRecord group)
    {
        if (group is not { OwnerKey: not null, Policy: { Dissolved: false } policy })
            return false;

        var ours = ourIdentityKey();

        if (group.Excludes(ours is null ? null : PeerId.Of(ours), ourFingerprint?.Invoke()))
            return false;

        return policy.Attestation.Admission == AdmissionMode.Password
               || GroupGovernance.RoleOf(group, ours) is not GroupRole.Member;
    }

    private static bool SameRequest(AdmissionRequest kept, AdmissionRequest incoming)
        => kept.Code.AsSpan().SequenceEqual(incoming.Code)
           && kept.PublicKey.AsSpan().SequenceEqual(incoming.PublicKey)
           && kept.Ephemeral.AsSpan().SequenceEqual(incoming.Ephemeral)
           && kept.WorldId == incoming.WorldId
           && string.Equals(kept.CharacterName, incoming.CharacterName, StringComparison.Ordinal);

    private static AdmissionOutbound Welcome(AdmissionRequest request, GroupRecord group, ECDiffieHellman ephemeral)
    {
        var memberPoint = CryptoPrimitives.ExportPublicPoint(ephemeral);
        var key = AdmissionSealing.Key(ephemeral, request.Ephemeral, request.Nonce, AdmissionSealing.Welcome);
        var grant = GroupGrantCodec.Encode(new GroupGrant(group.Id, group.Secret, group.OwnerKey!, group.Name));
        var sealedGrant = AdmissionSealing.Seal(key, grant, AdmissionSealing.Associated(AdmissionKind.Welcome, request.Nonce, memberPoint));

        return new AdmissionOutbound(
            request.CharacterName, request.WorldId, group.Rendezvous,
            AdmissionCodec.Encode(new AdmissionWelcome(request.Nonce, memberPoint, sealedGrant)));
    }

    private static AdmissionOutbound Refuse(AdmissionRequest request, GroupRecord group, byte reason)
        => new(request.CharacterName, request.WorldId, group.Rendezvous, AdmissionCodec.Encode(new AdmissionRefusal(request.Nonce, reason)));

    private static void KeepNewest<TKey, TValue>(Dictionary<TKey, TValue> map, Func<TValue, DateTimeOffset> at)
        where TKey : notnull
    {
        while (map.Count > MaxRemembered)
            map.Remove(map.MinBy(entry => at(entry.Value)).Key);
    }

    private void Prune()
    {
        var now = clock.UtcNow;

        foreach (var (key, challenge) in _challenges.ToList())
        {
            if (now - challenge.Created <= ChallengeLifetime)
                continue;

            challenge.Ephemeral.Dispose();
            _challenges.Remove(key);
        }

        foreach (var (key, waiting) in _pending.ToList())
            if (now - waiting.LastSeen > ValidationSilence)
                _pending.Remove(key);

        foreach (var (key, at) in _answered.ToList())
            if (now - at > AnswerMemory)
                _answered.Remove(key);

        var window = MailboxAddress.IndexAt(now);

        foreach (var (candidate, failures) in _failures.ToList())
            if (failures.Window != window)
                _failures.Remove(candidate);
    }

    /// <summary>
    /// Oublie tout ce qui est en cours, au changement de personnage.
    /// </summary>
    /// <remarks>
    /// Un défi ou une attente du personnage précédent qui aboutirait chez le
    /// suivant ferait admettre au nom de l'un par le carnet de l'autre, et les
    /// relierait. L'hôte reste utilisable : il vit aussi longtemps que le plugin.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
            Forget();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Forget();
        }
    }

    private void Forget()
    {
        foreach (var challenge in _challenges.Values)
            challenge.Ephemeral.Dispose();

        _challenges.Clear();
        _pending.Clear();
        _answered.Clear();
        _failures.Clear();
    }
}
