using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Crypto;
using Linkpearl.Core.Groups;
using Linkpearl.Core.Identity;
using Linkpearl.Core.Tests.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Groups;

public sealed class AdmissionTests : IDisposable
{
    private readonly ECDsa _owner = CryptoPrimitives.GenerateIdentity();
    private readonly ECDsa _candidateIdentity = CryptoPrimitives.GenerateIdentity();
    private readonly MovableClock _clock = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        _owner.Dispose();
        _candidateIdentity.Dispose();

        foreach (var disposable in _disposables)
            disposable.Dispose();
    }

    private static readonly PlayerFingerprint Candidate = PlayerFingerprint.Of("jhalen tavari", 21);

    private byte[] OwnerPoint => CryptoPrimitives.ExportPublicPoint(_owner);
    private byte[] CandidatePoint => CryptoPrimitives.ExportPublicPoint(_candidateIdentity);

    /// <summary>Un membre qui tient le groupe, et son hôte.</summary>
    private (AdmissionHost Host, GroupBook Book, CreatedGroup Created) Member(
        string password = "lune", bool asOwner = false, Func<AdmissionRequest, PlayerFingerprint, bool>? refuses = null)
    {
        var created = GroupGovernance.Create("Compagnie", password, PolicyFixture.Service, OwnerPoint, _clock.UtcNow);
        var book = new GroupBook(_clock);
        book.Load([asOwner ? created.Record : created.Record with { SigningKey = null }]);

        using var stranger = CryptoPrimitives.GenerateIdentity();
        var ours = asOwner ? OwnerPoint : CryptoPrimitives.ExportPublicPoint(stranger);
        var host = new AdmissionHost(book, () => ours, _clock, refuses);
        _disposables.Add(host);
        return (host, book, created);
    }

    private AdmissionCandidate NewCandidate()
    {
        var candidate = new AdmissionCandidate(_clock);
        _disposables.Add(candidate);
        return candidate;
    }

    private static T Decode<T>(byte[] payload) where T : AdmissionMessage
    {
        Assert.True(AdmissionCodec.TryDecode(payload, out var message, out var why), why);
        return Assert.IsType<T>(message);
    }

    private byte[] Start(AdmissionCandidate candidate, CreatedGroup created, string password)
        => candidate.Start(created.Code, PolicyFixture.Service, password, CandidatePoint, "Jhalen Tavari", 21);

    [Fact]
    public void Le_bon_mot_de_passe_fait_entrer()
    {
        var (host, _, created) = Member();
        var candidate = NewCandidate();

        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));
        var challenge = Assert.Single(host.OnRequest(request, Candidate));
        Assert.Equal("Jhalen Tavari", challenge.CharacterName);
        Assert.Equal([PolicyFixture.Service], challenge.Via);

        var proof = candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload));
        Assert.Equal(CandidacyState.Proving, candidate.State);

        var welcome = Assert.Single(host.OnProof(Decode<AdmissionProof>(proof!)));
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
        Assert.Equal(CandidacyState.Joined, candidate.State);

        var record = candidate.TakeJoined(_clock.UtcNow);
        Assert.Equal(created.Record.Id, record!.Id);
        Assert.Equal(created.Record.Secret, record.Secret);
        Assert.Equal(created.Record.OwnerKey, record.OwnerKey);
        Assert.Equal([PolicyFixture.Service], record.Rendezvous);
        Assert.Null(record.Policy);
        Assert.Equal(CandidacyState.Idle, candidate.State);
        Assert.Equal("Compagnie", candidate.LastJoinedName);
    }

    [Fact]
    public void Un_mauvais_mot_de_passe_est_refuse_puis_le_sixieme_essai_est_bloque()
    {
        var (host, _, created) = Member();

        for (var attempt = 1; attempt <= AdmissionHost.MaxFailures + 1; attempt++)
        {
            var candidate = NewCandidate();
            var request = Decode<AdmissionRequest>(Start(candidate, created, "soleil"));
            var challenge = Assert.Single(host.OnRequest(request, Candidate));
            var proof = candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload));
            var refusal = Decode<AdmissionRefusal>(Assert.Single(host.OnProof(Decode<AdmissionProof>(proof!))).Payload);

            candidate.OnRefusal(refusal);
            Assert.Equal(CandidacyState.Refused, candidate.State);
            Assert.Equal(
                attempt <= AdmissionHost.MaxFailures ? RefusalReason.WrongPassword : RefusalReason.TooManyAttempts,
                refusal.Reason);
        }
    }

    [Fact]
    public void Un_candidat_banni_ne_recoit_aucune_reponse()
    {
        var created = GroupGovernance.Create("Compagnie", "lune", PolicyFixture.Service, OwnerPoint, _clock.UtcNow);
        var book = new GroupBook(_clock);
        book.Load([created.Record]);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id,
            GroupGovernance.Ban(created.Record, new GroupBan(null, Candidate), null)));

        using var host = new AdmissionHost(book, () => OwnerPoint, _clock);
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune"));

        Assert.Empty(host.OnRequest(request, Candidate));
    }

    [Fact]
    public void Une_demande_redeposee_recoit_le_meme_defi()
    {
        var (host, _, created) = Member();
        var candidate = NewCandidate();
        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));

        var first = Assert.Single(host.OnRequest(request, Candidate));

        // Au rythme réel du candidat, qui redépose chaque minute.
        _clock.Advance(AdmissionCandidate.RedepositInterval);
        var again = Assert.Single(host.OnRequest(request, Candidate));

        Assert.Equal(first.Payload, again.Payload);
        Assert.Equal(first.CharacterName, again.CharacterName);
    }

    [Fact]
    public void Une_demande_rejouee_avec_un_autre_ephemere_ne_recoit_rien()
    {
        var (host, _, created) = Member();
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune"));
        Assert.Single(host.OnRequest(request, Candidate));

        using var intruder = CryptoPrimitives.GenerateEphemeral();
        var forged = request with { Ephemeral = CryptoPrimitives.ExportPublicPoint(intruder) };

        Assert.Empty(host.OnRequest(forged, Candidate));
    }

    [Fact]
    public void En_validation_la_demande_attend_un_moderateur()
    {
        var (host, _, created) = Member(password: "", asOwner: true);
        var candidate = NewCandidate();
        var request = Decode<AdmissionRequest>(Start(candidate, created, ""));

        Assert.Empty(host.OnRequest(request, Candidate));
        var pending = Assert.Single(host.Pending);
        Assert.Equal("Jhalen Tavari", pending.CharacterName);
        Assert.Equal("Compagnie", pending.GroupName);

        var welcome = host.Approve(pending.Nonce);
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome!.Payload)));
        Assert.Empty(host.Pending);
    }

    [Fact]
    public void En_validation_un_simple_membre_ne_voit_rien()
    {
        var (host, _, created) = Member(password: "");
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, ""));

        Assert.Empty(host.OnRequest(request, Candidate));
        Assert.Empty(host.Pending);
        Assert.Empty(host.AdmissionCodes);
    }

    [Fact]
    public void Une_demande_refusee_ne_revient_pas()
    {
        var (host, _, created) = Member(password: "", asOwner: true);
        var candidate = NewCandidate();
        var request = Decode<AdmissionRequest>(Start(candidate, created, ""));

        host.OnRequest(request, Candidate);
        var refusal = host.Decline(Assert.Single(host.Pending).Nonce);
        candidate.OnRefusal(Decode<AdmissionRefusal>(refusal!.Payload));

        Assert.Equal(CandidacyState.Refused, candidate.State);
        Assert.Equal(RefusalReason.Declined, candidate.RefusalReason);

        // Le candidat redépose une minute plus tard, avant d'avoir lu le refus.
        _clock.Advance(TimeSpan.FromMinutes(1));
        host.OnRequest(request, Candidate);
        Assert.Empty(host.Pending);
    }

    [Fact]
    public void Une_demande_qui_n_est_plus_redeposee_disparait()
    {
        var (host, _, created) = Member(password: "", asOwner: true);
        host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "")), Candidate);
        Assert.Single(host.Pending);

        _clock.Advance(AdmissionHost.ValidationSilence + TimeSpan.FromSeconds(1));
        Assert.Empty(host.Pending);
    }

    [Fact]
    public void Le_candidat_redepose_chaque_minute_puis_abandonne()
    {
        var (_, _, created) = Member();
        var candidate = NewCandidate();
        Start(candidate, created, "lune");

        Assert.Null(candidate.DueRedeposit());
        _clock.Advance(AdmissionCandidate.RedepositInterval);
        Assert.NotNull(candidate.DueRedeposit());
        Assert.Null(candidate.DueRedeposit());

        _clock.Advance(AdmissionCandidate.Lifetime);
        Assert.Null(candidate.DueRedeposit());
        Assert.Equal(CandidacyState.Expired, candidate.State);
    }

    [Fact]
    public void Sans_mot_de_passe_un_defi_demande_d_en_saisir_un()
    {
        var (host, _, created) = Member();
        var candidate = NewCandidate();
        var challenge = Assert.Single(host.OnRequest(Decode<AdmissionRequest>(Start(candidate, created, "")), Candidate));

        Assert.Null(candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload)));
        Assert.Equal(CandidacyState.NeedsPassword, candidate.State);
    }

    [Fact]
    public void Un_defi_d_une_autre_demande_est_ignore()
    {
        var (host, _, created) = Member();
        var first = NewCandidate();
        var other = NewCandidate();
        Start(first, created, "lune");
        var otherChallenge = Assert.Single(host.OnRequest(Decode<AdmissionRequest>(Start(other, created, "lune")), Candidate));

        Assert.Null(first.OnChallenge(Decode<AdmissionChallenge>(otherChallenge.Payload)));
        Assert.Equal(CandidacyState.Waiting, first.State);
    }

    [Fact]
    public void Un_groupe_dissous_n_admet_plus()
    {
        var created = GroupGovernance.Create("Compagnie", "lune", PolicyFixture.Service, OwnerPoint, _clock.UtcNow);
        var book = new GroupBook(_clock);
        book.Load([created.Record]);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id, GroupGovernance.Dissolve(created.Record)));

        using var host = new AdmissionHost(book, () => OwnerPoint, _clock);

        Assert.Empty(host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune")), Candidate));
        Assert.Empty(host.AdmissionCodes);
    }

    [Fact]
    public void Un_mot_de_passe_normalise_differemment_donne_la_meme_etiquette()
    {
        // « café » composé (NFC, un seul point de code pour le é) côté
        // candidat, et décomposé (NFD, e + accent combinant) côté membre :
        // deux séquences d'octets UTF-8 différentes pour le même mot lu.
        const string composed = "café";
        const string decomposed = "café";
        Assert.NotEqual(composed, decomposed);

        var (host, _, created) = Member(password: decomposed);
        var candidate = NewCandidate();

        var request = Decode<AdmissionRequest>(Start(candidate, created, composed));
        var challenge = Assert.Single(host.OnRequest(request, Candidate));
        var proof = candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload));

        var welcome = Assert.Single(host.OnProof(Decode<AdmissionProof>(proof!)));
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
        Assert.Equal(CandidacyState.Joined, candidate.State);
    }

    [Fact]
    public void Un_code_de_preuve_modifie_en_route_est_verifie_contre_le_groupe_de_la_demande()
    {
        // Le groupe vient de l'aléa du défi (Challenge.Group), jamais du
        // proof.Code en clair : le modifier en route ne doit ni égarer la
        // vérification vers un autre groupe, ni faire échouer une preuve dont
        // l'étiquette est par ailleurs valide.
        var (host, _, created) = Member();
        var candidate = NewCandidate();

        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));
        var challenge = Assert.Single(host.OnRequest(request, Candidate));
        var proof = Decode<AdmissionProof>(candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload))!);

        var tamperedCode = new byte[proof.Code.Length];
        Array.Fill(tamperedCode, (byte)0xff);
        var tampered = proof with { Code = tamperedCode };

        var welcome = Assert.Single(host.OnProof(tampered));
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
        Assert.Equal(CandidacyState.Joined, candidate.State);
    }

    [Fact]
    public void Deux_essais_successifs_utilisent_chacun_un_ephemere_et_un_alea_neufs()
    {
        var (host, _, created) = Member();
        var candidate = NewCandidate();

        var firstRequest = Decode<AdmissionRequest>(Start(candidate, created, "soleil"));
        var firstChallenge = Assert.Single(host.OnRequest(firstRequest, Candidate));
        var firstProof = candidate.OnChallenge(Decode<AdmissionChallenge>(firstChallenge.Payload));
        var refusal = Decode<AdmissionRefusal>(Assert.Single(host.OnProof(Decode<AdmissionProof>(firstProof!))).Payload);
        candidate.OnRefusal(refusal);
        Assert.Equal(CandidacyState.Refused, candidate.State);

        var secondRequest = Decode<AdmissionRequest>(Start(candidate, created, "lune"));

        Assert.NotEqual(firstRequest.Nonce, secondRequest.Nonce);
        Assert.NotEqual(firstRequest.Ephemeral, secondRequest.Ephemeral);

        var secondChallenge = Assert.Single(host.OnRequest(secondRequest, Candidate));
        var secondProof = candidate.OnChallenge(Decode<AdmissionChallenge>(secondChallenge.Payload));
        var welcome = Assert.Single(host.OnProof(Decode<AdmissionProof>(secondProof!)));
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
        Assert.Equal(CandidacyState.Joined, candidate.State);
    }

    /// <summary>Un second membre du même groupe, avec son propre hôte.</summary>
    private AdmissionHost OtherMember(GroupBook book)
    {
        using var identity = CryptoPrimitives.GenerateIdentity();
        var key = CryptoPrimitives.ExportPublicPoint(identity);
        var host = new AdmissionHost(book, () => key, _clock);
        _disposables.Add(host);
        return host;
    }

    [Fact]
    public void Un_defieur_muet_rend_la_main_et_un_second_defi_est_accepte()
    {
        var (silent, book, created) = Member();
        var other = OtherMember(book);
        var candidate = NewCandidate();

        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));
        var challenge = Assert.Single(silent.OnRequest(request, Candidate));
        Assert.NotNull(candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload)));
        Assert.Equal(CandidacyState.Proving, candidate.State);

        // La preuve se perd : le défieur ne répondra jamais.
        _clock.Advance(AdmissionCandidate.ProofPatience - TimeSpan.FromSeconds(1));
        Assert.Null(candidate.DueRedeposit());
        Assert.Equal(CandidacyState.Proving, candidate.State);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var redeposit = candidate.DueRedeposit();
        Assert.NotNull(redeposit);
        Assert.Equal(CandidacyState.Waiting, candidate.State);

        // Même aléa, même éphémère : c'est la même demande qui revient.
        var again = Decode<AdmissionRequest>(redeposit!);
        Assert.Equal(request.Nonce, again.Nonce);
        Assert.Equal(request.Ephemeral, again.Ephemeral);

        var second = Assert.Single(other.OnRequest(again, Candidate));
        var proof = candidate.OnChallenge(Decode<AdmissionChallenge>(second.Payload));
        var welcome = Assert.Single(other.OnProof(Decode<AdmissionProof>(proof!)));

        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
        Assert.Equal(CandidacyState.Joined, candidate.State);
    }

    [Fact]
    public void Une_preuve_perdue_est_rejouee_sur_le_meme_defi()
    {
        var (host, _, created) = Member();
        var candidate = NewCandidate();

        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));
        var challenge = Assert.Single(host.OnRequest(request, Candidate));
        candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload));

        _clock.Advance(AdmissionCandidate.ProofPatience);
        var again = Assert.Single(host.OnRequest(Decode<AdmissionRequest>(candidate.DueRedeposit()!), Candidate));
        Assert.Equal(challenge.Payload, again.Payload);
        var proof = candidate.OnChallenge(Decode<AdmissionChallenge>(again.Payload));

        var welcome = Assert.Single(host.OnProof(Decode<AdmissionProof>(proof!)));
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
    }

    [Fact]
    public void Un_meme_defi_n_est_renvoye_qu_une_fois_par_intervalle()
    {
        var (host, _, created) = Member();
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune"));
        Assert.Single(host.OnRequest(request, Candidate));

        // Rejouée aussitôt, par un autre service ou par un porteur du code : rien.
        Assert.Empty(host.OnRequest(request, Candidate));

        _clock.Advance(AdmissionHost.ChallengeResendInterval);
        Assert.Single(host.OnRequest(request, Candidate));

        // Deux redépôts à moins de trente secondes : un seul renvoi.
        _clock.Advance(AdmissionHost.ChallengeResendInterval - TimeSpan.FromSeconds(1));
        Assert.Empty(host.OnRequest(request, Candidate));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(host.OnRequest(request, Candidate));
    }

    [Fact]
    public void Une_candidature_expire_aussi_en_attente_de_preuve()
    {
        var (host, _, created) = Member();
        var candidate = NewCandidate();
        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));

        // Le défi arrive tard, quand la candidature touche à sa fin.
        _clock.Advance(AdmissionCandidate.Lifetime - TimeSpan.FromSeconds(10));
        var challenge = Assert.Single(host.OnRequest(request, Candidate));
        candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload));
        Assert.Equal(CandidacyState.Proving, candidate.State);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(candidate.DueRedeposit());
        Assert.Equal(CandidacyState.Expired, candidate.State);
    }

    [Fact]
    public void Une_demande_de_meme_alea_ne_remplace_pas_celle_qui_attend_un_moderateur()
    {
        var (host, _, created) = Member(password: "", asOwner: true);
        var candidate = NewCandidate();
        var request = Decode<AdmissionRequest>(Start(candidate, created, ""));
        host.OnRequest(request, Candidate);
        var seen = Assert.Single(host.Pending).LastSeen;

        // Un porteur du code rejoue l'aléa avec son propre éphémère et un autre nom.
        using var intruder = CryptoPrimitives.GenerateEphemeral();
        _clock.Advance(TimeSpan.FromSeconds(30));
        host.OnRequest(request with { Ephemeral = CryptoPrimitives.ExportPublicPoint(intruder), CharacterName = "Autre Nom" }, Candidate);

        var pending = Assert.Single(host.Pending);
        Assert.Equal("Jhalen Tavari", pending.CharacterName);
        Assert.Equal(seen, pending.LastSeen);

        // La bienvenue approuvée reste scellée pour le vrai candidat.
        var welcome = host.Approve(pending.Nonce);
        Assert.Equal("Jhalen Tavari", welcome!.CharacterName);
        Assert.True(candidate.OnWelcome(Decode<AdmissionWelcome>(welcome.Payload)));
    }

    [Fact]
    public void Une_demande_identique_rafraichit_celle_qui_attend_un_moderateur()
    {
        var (host, _, created) = Member(password: "", asOwner: true);
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, ""));
        host.OnRequest(request, Candidate);

        _clock.Advance(TimeSpan.FromMinutes(1));
        host.OnRequest(request, Candidate);

        Assert.Equal(_clock.UtcNow, Assert.Single(host.Pending).LastSeen);
    }

    private static AdmissionRequest WithFreshNonce(AdmissionRequest request)
        => request with { Nonce = RandomNumberGenerator.GetBytes(AdmissionCodec.NonceLength) };

    [Fact]
    public void Au_dela_de_64_defis_vivants_la_demande_suivante_est_ignoree()
    {
        var (host, _, created) = Member();
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune"));

        for (var i = 0; i < AdmissionHost.MaxLiveChallenges; i++)
            Assert.Single(host.OnRequest(WithFreshNonce(request), Candidate));

        Assert.Empty(host.OnRequest(WithFreshNonce(request), Candidate));
    }

    [Fact]
    public void Au_dela_de_64_demandes_en_validation_la_suivante_est_ignoree()
    {
        var (host, _, created) = Member(password: "", asOwner: true);
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, ""));

        for (var i = 0; i < AdmissionHost.MaxPendingValidations; i++)
            host.OnRequest(WithFreshNonce(request), Candidate);

        var last = WithFreshNonce(request);
        host.OnRequest(last, Candidate);

        Assert.Equal(AdmissionHost.MaxPendingValidations, host.Pending.Count);
        Assert.DoesNotContain(host.Pending, pending => pending.Nonce.AsSpan().SequenceEqual(last.Nonce));
    }

    /// <summary>Un défi en cours, puis un changement de politique avant la preuve.</summary>
    private AdmissionProof ProofThenChange(AdmissionHost host, GroupBook book, CreatedGroup created, Func<GroupRecord, byte[]> change)
    {
        var candidate = NewCandidate();
        var request = Decode<AdmissionRequest>(Start(candidate, created, "lune"));
        var challenge = Assert.Single(host.OnRequest(request, Candidate));
        var proof = Decode<AdmissionProof>(candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload))!);

        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id, change(book.Find(created.Record.Id)!)));
        return proof;
    }

    [Fact]
    public void Un_groupe_dissous_entre_defi_et_preuve_n_admet_pas()
    {
        var (host, book, created) = Member(asOwner: true);
        var proof = ProofThenChange(host, book, created, GroupGovernance.Dissolve);

        Assert.Empty(host.OnProof(proof));
    }

    [Fact]
    public void Un_candidat_banni_entre_defi_et_preuve_n_obtient_rien()
    {
        var (host, book, created) = Member(asOwner: true);
        var proof = ProofThenChange(host, book, created, record => GroupGovernance.Ban(record, new GroupBan(null, Candidate), null));

        Assert.Empty(host.OnProof(proof));
    }

    [Fact]
    public void Un_code_change_entre_defi_et_preuve_n_admet_pas()
    {
        var (host, book, created) = Member(asOwner: true);
        var proof = ProofThenChange(host, book, created, record => GroupGovernance.NewCode(record, null));

        Assert.Empty(host.OnProof(proof));
    }

    [Fact]
    public void Un_passage_en_validation_entre_defi_et_preuve_n_admet_pas()
    {
        var (host, book, created) = Member(asOwner: true);
        var proof = ProofThenChange(host, book, created, record => GroupGovernance.SetAdmission(record, AdmissionMode.Validation));

        Assert.Empty(host.OnProof(proof));
    }

    [Fact]
    public void Un_candidat_banni_avant_l_approbation_n_est_pas_admis()
    {
        var (host, book, created) = Member(password: "", asOwner: true);
        host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "")), Candidate);
        var pending = Assert.Single(host.Pending);

        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id,
            GroupGovernance.Ban(book.Find(created.Record.Id)!, new GroupBan(null, Candidate), null)));

        Assert.Null(host.Approve(pending.Nonce));
    }

    /// <summary>Un modérateur et son hôte, et le carnet du propriétaire qui le nomme.</summary>
    private (AdmissionHost Host, GroupBook Book, GroupBook OwnerBook, CreatedGroup Created) Moderator(ECDsa moderator)
    {
        var created = GroupGovernance.Create("Compagnie", "", PolicyFixture.Service, OwnerPoint, _clock.UtcNow);
        var id = created.Record.Id;
        var point = CryptoPrimitives.ExportPublicPoint(moderator);

        var ownerBook = new GroupBook(_clock);
        ownerBook.Load([created.Record]);
        var named = GroupGovernance.SetModerators(ownerBook.Find(id)!, [point]);
        Assert.Equal(PolicyOffer.Adopted, ownerBook.OfferPolicy(id, named));

        var book = new GroupBook(_clock);
        book.Load([created.Record with { SigningKey = null }]);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(id, named));

        var host = new AdmissionHost(book, () => point, _clock);
        _disposables.Add(host);
        return (host, book, ownerBook, created);
    }

    [Fact]
    public void Un_moderateur_retire_ne_valide_plus_une_demande_restee_en_attente()
    {
        using var moderator = CryptoPrimitives.GenerateIdentity();
        var (host, book, ownerBook, created) = Moderator(moderator);
        var id = created.Record.Id;

        host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "")), Candidate);
        var pending = Assert.Single(host.Pending);

        var demoted = GroupGovernance.SetModerators(ownerBook.Find(id)!, []);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(id, demoted));

        Assert.Empty(host.Pending);
        Assert.Null(host.Approve(pending.Nonce));
    }

    [Fact]
    public void Un_code_renouvele_pendant_l_attente_ne_valide_plus_la_demande()
    {
        var (host, book, created) = Member(password: "", asOwner: true);
        host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "")), Candidate);
        var pending = Assert.Single(host.Pending);

        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id,
            GroupGovernance.NewCode(book.Find(created.Record.Id)!, null)));

        Assert.Null(host.Approve(pending.Nonce));
    }

    [Fact]
    public void Un_passage_en_mot_de_passe_pendant_l_attente_ne_valide_plus_la_demande()
    {
        // En mot de passe, tout membre admet : une validation sans preuve y
        // ferait entrer sans mot de passe.
        var (host, book, created) = Member(password: "", asOwner: true);
        host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "")), Candidate);
        var pending = Assert.Single(host.Pending);

        var withPassword = GroupGovernance.SetPassword(book.Find(created.Record.Id)!, "lune", null);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id, withPassword));
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(created.Record.Id,
            GroupGovernance.SetAdmission(book.Find(created.Record.Id)!, AdmissionMode.Password)));

        Assert.Null(host.Approve(pending.Nonce));
    }

    [Fact]
    public void Un_membre_exclu_n_admet_personne()
    {
        using var member = CryptoPrimitives.GenerateIdentity();
        var point = CryptoPrimitives.ExportPublicPoint(member);
        var created = GroupGovernance.Create("Compagnie", "lune", PolicyFixture.Service, OwnerPoint, _clock.UtcNow);
        var id = created.Record.Id;

        var book = new GroupBook(_clock);
        book.Load([created.Record with { SigningKey = null }]);
        using var host = new AdmissionHost(book, () => point, _clock);
        Assert.Single(host.AdmissionCodes);

        var ban = GroupGovernance.Ban(created.Record, new GroupBan(PeerId.Of(point), null), null);
        Assert.Equal(PolicyOffer.Adopted, book.OfferPolicy(id, ban));

        Assert.NotNull(book.Find(id));
        Assert.Empty(host.AdmissionCodes);
        Assert.Empty(host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune")), Candidate));
    }

    [Fact]
    public void La_remise_a_zero_oublie_defis_et_attentes_sans_fermer_l_hote()
    {
        // Au changement de personnage : ce que le précédent avait en cours ne
        // doit ni aboutir ni s'afficher chez le suivant.
        var (host, _, created) = Member();
        var candidate = NewCandidate();
        var challenge = Assert.Single(host.OnRequest(Decode<AdmissionRequest>(Start(candidate, created, "lune")), Candidate));
        var proof = candidate.OnChallenge(Decode<AdmissionChallenge>(challenge.Payload));

        var (validating, _, open) = Member(password: "", asOwner: true);
        Assert.Empty(validating.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), open, "")), Candidate));
        Assert.Single(validating.Pending);

        host.Reset();
        validating.Reset();

        Assert.Empty(host.OnProof(Decode<AdmissionProof>(proof!)));
        Assert.Empty(validating.Pending);

        // L'hôte sert encore : une demande neuve reçoit son défi.
        Assert.Single(host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune")), Candidate));
    }

    [Fact]
    public void Apres_dispose_l_hote_ne_cree_plus_rien()
    {
        var (host, _, created) = Member();
        host.Dispose();

        Assert.Empty(host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune")), Candidate));
    }

    [Fact]
    public void Un_candidat_liste_ne_recoit_aucune_reponse_en_mode_mot_de_passe()
    {
        var calls = 0;
        var (host, _, created) = Member(refuses: (_, _) => { calls++; return true; });
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune"));

        Assert.Empty(host.OnRequest(request, Candidate));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Un_candidat_liste_n_est_pas_propose_aux_moderateurs()
    {
        // Sans mot de passe, le groupe valide chaque entrée.
        var (host, _, created) = Member(password: "", asOwner: true, refuses: (_, _) => true);
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, ""));

        host.OnRequest(request, Candidate);

        Assert.Empty(host.Pending);
    }

    [Fact]
    public void Le_filtre_n_est_consulte_qu_une_fois_par_demande()
    {
        // Il coûte une dérivation PBKDF2 : un redépôt de la même demande, chaque
        // minute, ne doit pas la refaire.
        var calls = 0;
        var (host, _, created) = Member(refuses: (_, _) => { calls++; return false; });
        var request = Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune"));

        host.OnRequest(request, Candidate);
        _clock.Advance(AdmissionHost.ChallengeResendInterval);
        host.OnRequest(request, Candidate);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Le_filtre_tourne_hors_du_verrou_de_l_hote()
    {
        // Le filtre dérive en PBKDF2 (dixièmes de seconde), et l'interface lit
        // Pending à chaque image depuis le thread du jeu : sous le verrou, le
        // jeu gèlerait le temps de la dérivation.
        AdmissionHost? host = null;
        var readable = false;
        (host, _, var created) = Member(refuses: (_, _) =>
        {
            readable = Task.Run(() => host!.Pending).Wait(TimeSpan.FromSeconds(2));
            return false;
        });

        host.OnRequest(Decode<AdmissionRequest>(Start(NewCandidate(), created, "lune")), Candidate);

        Assert.True(readable, "Pending attendait le verrou pris pendant le filtre");
    }
}
