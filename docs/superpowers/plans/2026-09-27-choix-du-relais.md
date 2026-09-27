# Choix du relais le plus proche : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Quand deux pairs relaient, passer par le service éligible le plus proche des deux, et non par le premier qui les a appariés.

**Architecture:** L'autorité attribue une région (continent, par GeoIP) à chaque service et la publie dans une liste signée v2, servie à côté de la v1. Le plugin mesure en tâche de fond le RTT vers les services éligibles d'une paire (placement actuel, un service par région, ancrage), glisse ces mesures à la fin du bloc de candidats scellé, et les deux côtés calculent le même relais par une fonction pure symétrique, avec repli sur le service d'appariement.

**Tech Stack:** .NET 10, C#, xUnit ; `MaxMind.Db` (Apache 2.0) côté service pour lire la base DB-IP « IP to Country Lite » (CC BY 4.0).

**Spec:** `docs/superpowers/specs/2026-09-27-choix-du-relais-design.md` (dépôt du plugin).

**Dépôts :** `~/Projects/linkpearl-sync/plugin` (appelé « plugin ») et `~/Projects/linkpearl-sync/rendezvous` (appelé « service »). Chaque tâche dit dans lequel elle travaille. Les commandes sont lancées depuis la racine du dépôt indiqué.

## Global Constraints

- `Protocol/` du service est une copie littérale du plugin : on modifie `RendezvousWire.cs`, `ServiceConsensus.cs`, `rendezvous-vectors.json` et `RendezvousVectorTests.cs` dans le plugin, puis on recopie, puis `diff` doit être vide.
- `ServicePlacement.Choose` et `ServicePlacement.Score` ne changent pas d'un octet.
- Le format v1 de la liste signée, la requête `ConsensusQuery` (0x17) et `CandidateSet.Encode` restent identiques : les clients déjà publiés ne doivent rien voir changer.
- Préfixe signé de la v2 : `linkpearl:consensus:v2` (ASCII, en tête des octets signés).
- Nouvelles trames : `ConsensusV2Query = 0x1B`, `ConsensusV2Page = 0x1C`.
- Région : deux lettres ASCII majuscules, ou `00 00` si absente. Continents retenus : `AF`, `AS`, `EU`, `NA`, `OC`, `SA`.
- Seuil régional : au moins 2 familles distinctes dans la région.
- Extension du bloc : `etiquette(1) || longueur(2, gros-boutiste) || contenu` ; étiquette `0x01` = mesures, `nombre(1) || (service(8) || rtt_ms(2))*`, au plus 16 mesures ; `service` = 8 premiers octets de `SHA-256(ServiceConsensus.Canonical(adresse))` ; `0xFFFF` = injoignable, plafond `0xFFFE`.
- RelayOnly : RTT synthétiques `0` (région la plus proche), `1000` (autres joignables), `0xFFFF` (injoignables).
- Décision : `max(rttA, rttB)`, puis `rttA + rttB`, puis empreinte ; hystérésis 20 ms **et** 20 %.
- Budgets : relais choisi 8 s, repli 25 s ; budget actuel (20 s) quand le choix est le service d'appariement.
- Mesures : 3 essais, minimum gardé, 1 s par essai, fraîcheur 30 min, 64 services par cycle, refus mémorisé 24 h.
- GeoIP : base périmée au-delà de 90 jours = pas de région.
- Pas de tiret cadratin, commentaires en français qui disent le pourquoi, commits Conventional Commits en français, **jamais** de ligne `Co-Authored-By` ni `Claude-Session`.
- Build sans warning : `dotnet build Linkpearl/Linkpearl.csproj -c Release` (plugin), `dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release` (service).

## Review Focus

- Deux pairs qui tiennent des listes de versions différentes (l'un v2 avec régions, l'autre v1 ou une v2 plus ancienne) doivent toujours tomber sur le même relais : couvert par la symétrie de `RelayChoice.Decide` et l'intersection des empreintes (Tâche 8).
- Un pair d'avant la fonction (bloc sans extension) doit se connecter exactement comme aujourd'hui : couvert par `Decide(null, ...)` (Tâche 8) et par le décodage d'un bloc sans extension (Tâche 7).
- Un bloc dont l'extension est tronquée ou dupliquée ne doit ni lever ni casser la connexion : on retombe sur le service d'appariement (Tâche 7).
- Un relais choisi qui refuse, ou qui se tait, doit laisser les deux côtés se retrouver au service d'appariement dans les trente secondes que le service accorde : couvert par les budgets de `RelayWithFallbackAsync` (Tâche 10).
- Une autorité ancienne qui répond « trame inattendue » à la requête v2 ne doit pas faire perdre la liste au plugin : couvert par le repli v1 de `ConsensusFetcher` (Tâche 11) et par le test serveur qui vérifie que la v1 est toujours servie (Tâche 4).

---

### Task 1 : liste signée v2 (plugin)

**Files:**
- Modify: `Linkpearl/Core/Transport/Rendezvous/ServiceConsensus.cs`
- Test: `Linkpearl.Core.Tests/Rendezvous/ServiceConsensusTests.cs`

**Interfaces:**
- Produces :
  - `public sealed record ConsensusEntry(string Address, string Label, byte[] Family, string? Region = null);`
  - `public const int RegionSize = 2;`
  - `public byte[] SignedPortionV2()`
  - `public static byte[] AssembleV2(ServiceConsensus list, IReadOnlyList<ConsensusSignature> signatures)`
  - `public static byte[] SignV2(ServiceConsensus list, ECDsa key)`
  - `TryParse` et `TryVerify` reconnaissent seuls la v2 à son préfixe ; signatures inchangées.

- [ ] **Step 1 : écrire les tests qui échouent**

Ajouter à `ServiceConsensusTests` :

```csharp
    private static ServiceConsensus WithRegions() => new(
        7, Now, Now + (long)ServiceConsensus.Lifetime.TotalSeconds,
        [
            new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)0x0F, 8)], "EU"),
            new ConsensusEntry("rdv.loin.ch:47900", "Loin", [.. Enumerable.Repeat((byte)0x1E, 8)]),
        ]);

    [Fact]
    public void Une_liste_v2_signee_se_verifie_avec_ses_regions()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.SignV2(WithRegions(), key);

        Assert.True(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], Now, out var list, out var why), why);
        Assert.Equal("EU", list!.Entries[0].Region);
        Assert.Null(list.Entries[1].Region);
        Assert.Equal(Enumerable.Repeat((byte)0x1E, 8), list.Entries[1].Family);
    }

    [Fact]
    public void La_v1_ne_porte_pas_les_regions()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.Sign(WithRegions(), key);

        Assert.True(ServiceConsensus.TryVerify(document, [ServiceConsensus.PublicPoint(key)], Now, out var list, out var why), why);
        Assert.All(list!.Entries, entry => Assert.Null(entry.Region));
    }

    [Fact]
    public void Une_signature_v1_ne_vaut_pas_pour_la_v2()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v1 = ServiceConsensus.Sign(WithRegions(), key);
        ServiceConsensus.TryParse(v1, out _, out var signatures, out _, out _);

        var forged = ServiceConsensus.AssembleV2(WithRegions(), signatures);

        Assert.False(ServiceConsensus.TryVerify(forged, [ServiceConsensus.PublicPoint(key)], Now, out _, out _));
    }

    [Fact]
    public void Une_signature_v2_ne_vaut_pas_pour_la_v1()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v2 = ServiceConsensus.SignV2(WithRegions(), key);
        ServiceConsensus.TryParse(v2, out _, out var signatures, out _, out _);

        var forged = ServiceConsensus.Assemble(WithRegions(), signatures);

        Assert.False(ServiceConsensus.TryVerify(forged, [ServiceConsensus.PublicPoint(key)], Now, out _, out _));
    }

    [Fact]
    public void Une_region_hors_des_majuscules_est_refusee_a_la_lecture()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = ServiceConsensus.SignV2(WithRegions(), key);

        // La région de la première entrée suit sa famille : on la retrouve par
        // son contenu plutôt que par un décalage calculé à la main.
        var at = document.AsSpan().IndexOf("EU"u8);
        document[at] = (byte)'e';

        Assert.False(ServiceConsensus.TryParse(document, out _, out _, out _, out var why));
        Assert.Contains("région", why);
    }

    [Fact]
    public void L_ecrivain_refuse_une_region_invalide()
    {
        var list = new ServiceConsensus(1, Now, Now + 60,
            [new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)1, 8)], "eu")]);

        Assert.Throws<ArgumentException>(() => list.SignedPortionV2());
    }

    [Fact]
    public void Une_v2_tronquee_dans_sa_region_est_refusee()
    {
        var list = new ServiceConsensus(1, Now, Now + 60,
            [new ConsensusEntry("rdv.ami.ch:47900", "Ami", [.. Enumerable.Repeat((byte)1, 8)], "EU")]);
        var portion = list.SignedPortionV2();

        Assert.False(ServiceConsensus.TryParse(portion[..^1], out _, out _, out _, out var why));
        Assert.Contains("région", why);
    }
```

- [ ] **Step 2 : lancer les tests, ils doivent échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~ServiceConsensusTests"`
Attendu : échec de compilation (`SignV2`, `AssembleV2`, `SignedPortionV2`, quatrième paramètre de `ConsensusEntry` inconnus).

- [ ] **Step 3 : implémenter**

Dans `ServiceConsensus.cs` :

1. Remplacer le record d'entrée et compléter son commentaire :

```csharp
/// <summary>Un service du cercle ouvert, tel que la liste signée le présente.</summary>
/// <remarks>
/// <see cref="Family"/> est l'empreinte du /24 ou du /48 que l'autorité a vu
/// en le sondant. Le client ne résout rien lui-même, et publier l'empreinte
/// plutôt que l'adresse évite de coller une IP à côté de chaque nom.
///
/// <see cref="Region"/> est le continent que l'autorité déduit de cette même
/// adresse, ou null. Seule la v2 la porte : la v1 l'ignore en écriture et la
/// rend nulle en lecture.
/// </remarks>
public sealed record ConsensusEntry(string Address, string Label, byte[] Family, string? Region = null);
```

2. Dans le commentaire de `ServiceConsensus`, sous le format v1, ajouter :

```
///     liste_v2  = "linkpearl:consensus:v2" || version(4) || emise(8) || expire(8) || nombre(2) || entree_v2*
///     entree_v2 = longueur(1) || adresse || longueur(1) || libelle || famille(8) || region(2)
///
/// Le préfixe de la v2 fait partie des octets signés : une signature de l'une
/// ne vaut jamais pour l'autre. La lecture reconnaît la v2 à ce préfixe, qu'une
/// v1 ne peut pas porter : sa version et sa date d'émission tomberaient des
/// milliards d'années dans le futur.
```

3. Ajouter les constantes :

```csharp
    public const int RegionSize = 2;

    private static ReadOnlySpan<byte> V2Prefix => "linkpearl:consensus:v2"u8;
```

4. Remplacer `SignedPortion()` par :

```csharp
    public byte[] SignedPortion() => Write(withRegions: false);

    public byte[] SignedPortionV2() => Write(withRegions: true);

    private byte[] Write(bool withRegions)
    {
        if (Entries.Count > MaxEntries)
            throw new ArgumentException($"{Entries.Count} entrées, plafond {MaxEntries}");

        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];

        if (withRegions)
            stream.Write(V2Prefix);

        BinaryPrimitives.WriteUInt32BigEndian(number, Version);
        stream.Write(number[..4]);
        BinaryPrimitives.WriteInt64BigEndian(number, Issued);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, Expires);
        stream.Write(number);
        BinaryPrimitives.WriteUInt16BigEndian(number, (ushort)Entries.Count);
        stream.Write(number[..2]);

        foreach (var entry in Entries)
        {
            WriteText(stream, entry.Address, nameof(entry.Address));
            WriteText(stream, entry.Label, nameof(entry.Label));

            if (entry.Family.Length != FamilySize)
                throw new ArgumentException($"famille de {entry.Family.Length} octets, {FamilySize} attendus");

            stream.Write(entry.Family);

            if (withRegions)
                WriteRegion(stream, entry.Region);
        }

        return stream.ToArray();
    }

    private static void WriteRegion(MemoryStream stream, string? region)
    {
        if (region is null)
        {
            stream.WriteByte(0);
            stream.WriteByte(0);
            return;
        }

        if (region.Length != RegionSize || region.Any(letter => letter is < 'A' or > 'Z'))
            throw new ArgumentException($"région « {region} » : deux lettres majuscules attendues");

        stream.WriteByte((byte)region[0]);
        stream.WriteByte((byte)region[1]);
    }
```

5. Remplacer `Assemble` et `Sign` par :

```csharp
    public static byte[] Assemble(ServiceConsensus list, IReadOnlyList<ConsensusSignature> signatures)
        => Assemble(list.SignedPortion(), signatures);

    public static byte[] AssembleV2(ServiceConsensus list, IReadOnlyList<ConsensusSignature> signatures)
        => Assemble(list.SignedPortionV2(), signatures);

    private static byte[] Assemble(byte[] signed, IReadOnlyList<ConsensusSignature> signatures)
    {
        if (signatures.Count is < 1 or > MaxSignatures)
            throw new ArgumentException($"{signatures.Count} signatures, de 1 à {MaxSignatures}", nameof(signatures));

        using var stream = new MemoryStream();
        stream.Write(signed);
        stream.WriteByte((byte)signatures.Count);

        foreach (var signature in signatures)
        {
            if (signature.KeyId.Length != KeyIdSize || signature.Signature.Length != SignatureSize)
                throw new ArgumentException("signature de taille inattendue", nameof(signatures));

            stream.Write(signature.KeyId);
            stream.Write(signature.Signature);
        }

        return stream.ToArray();
    }

    public static byte[] Sign(ServiceConsensus list, ECDsa key) => Sign(list.SignedPortion(), key);

    public static byte[] SignV2(ServiceConsensus list, ECDsa key) => Sign(list.SignedPortionV2(), key);

    private static byte[] Sign(byte[] signed, ECDsa key)
    {
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Assemble(signed, [new ConsensusSignature(KeyId(PublicPoint(key)), signature)]);
    }
```

6. Dans `TryParse`, remplacer le début jusqu'à `var entries = ...` par :

```csharp
        list = null;
        signatures = [];
        signedLength = 0;

        var v2 = document.StartsWith(V2Prefix);
        var start = v2 ? V2Prefix.Length : 0;

        if (document.Length < start + HeaderSize + 1)
        {
            rejection = "liste signée tronquée";
            return false;
        }

        var header = document[start..];
        var version = BinaryPrimitives.ReadUInt32BigEndian(header);
        var issued = BinaryPrimitives.ReadInt64BigEndian(header[4..]);
        var expires = BinaryPrimitives.ReadInt64BigEndian(header[12..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(header[20..]);

        if (count > MaxEntries)
        {
            rejection = $"{count} entrées, plafond {MaxEntries}";
            return false;
        }

        if (expires <= issued)
        {
            rejection = "expiration antérieure à l'émission";
            return false;
        }

        var offset = start + HeaderSize;
        var entries = new List<ConsensusEntry>(count);
```

puis, dans la boucle, remplacer l'ajout de l'entrée par :

```csharp
            var family = document.Slice(offset, FamilySize).ToArray();
            offset += FamilySize;

            string? region = null;

            if (v2)
            {
                if (offset + RegionSize > document.Length)
                {
                    rejection = $"région {i} tronquée";
                    return false;
                }

                var raw = document.Slice(offset, RegionSize);
                offset += RegionSize;

                if (raw[0] != 0 || raw[1] != 0)
                {
                    if (raw[0] is < (byte)'A' or > (byte)'Z' || raw[1] is < (byte)'A' or > (byte)'Z')
                    {
                        rejection = $"région {i} illisible";
                        return false;
                    }

                    region = string.Concat((char)raw[0], (char)raw[1]);
                }
            }

            entries.Add(new ConsensusEntry(address, label, family, region));
```

(supprimer l'ancien `entries.Add(new ConsensusEntry(address, label, document.Slice(offset, FamilySize).ToArray()));` et l'ancien `offset += FamilySize;`).

- [ ] **Step 4 : lancer les tests, ils doivent passer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~ServiceConsensusTests|FullyQualifiedName~ServicePlacementTests|FullyQualifiedName~OpenCircleTests"`
Attendu : PASS, anciens tests compris.

- [ ] **Step 5 : commit**

```bash
git add Linkpearl/Core/Transport/Rendezvous/ServiceConsensus.cs Linkpearl.Core.Tests/Rendezvous/ServiceConsensusTests.cs
git commit -m "feat(wire): liste signée v2, qui porte la région de chaque service"
```

---

### Task 2 : trames v2 et vecteurs figés (plugin)

**Files:**
- Modify: `Linkpearl/Core/Transport/Rendezvous/RendezvousWire.cs`
- Modify: `Linkpearl/Core/Transport/Rendezvous/RendezvousClient.cs:210-246`
- Modify: `Linkpearl.Core.Tests/Rendezvous/RendezvousVectorTests.cs`
- Modify: `Linkpearl.Core.Tests/Fixtures/rendezvous-vectors.json`

**Interfaces:**
- Consumes : `ServiceConsensus.AssembleV2`, `ConsensusEntry(..., Region)` (Tâche 1).
- Produces :
  - `RendezvousKind.ConsensusV2Query = 0x1B`, `RendezvousKind.ConsensusV2Page = 0x1C`
  - `RendezvousWire.ConsensusV2Query(int page)`, `TryReadConsensusV2Query(ReadOnlySpan<byte> frame, out int page)`, `ConsensusV2Page(int page, int pages, ReadOnlySpan<byte> chunk)`, `TryReadConsensusV2Page(ReadOnlySpan<byte> frame, out int page, out int pages, out byte[] chunk, out string? rejection)`
  - `RendezvousClient.QueryConsensusV2Async(CancellationToken ct)` qui rend `(byte[]? Document, string? Failure)`

- [ ] **Step 1 : ajouter les vecteurs attendus (test qui échoue)**

Dans `RendezvousVectorTests.Frames`, après `("etat-reseau-page", ...)`, ajouter :

```csharp
        ("consensus-v2-demande", () => RendezvousWire.ConsensusV2Query(1)),
        ("consensus-v2-page", () => RendezvousWire.ConsensusV2Page(0, 2, new byte[] { 0xAB, 0xCD })),
        ("consensus-v2-document", () => ServiceConsensus.AssembleV2(
            new ServiceConsensus(7, 1_790_000_000, 1_790_604_800,
                [new ConsensusEntry("rdv.ami.ch:47900", "Ami", Repeat(0x0F, 8), "EU"),
                 new ConsensusEntry("rdv.loin.ch:47900", "Loin", Repeat(0x1E, 8))]),
            [new ConsensusSignature(Repeat(0x5A, 8), Repeat(0x5B, 64))])),
```

et dans la théorie des plafonds :

```csharp
    [InlineData("tailleRegion", ServiceConsensus.RegionSize)]
```

Dans `rendezvous-vectors.json`, ajouter à la fin du tableau `trames` les trois entrées avec un `hex` vide, et `"tailleRegion": 2` dans `constantes` :

```json
    { "nom": "consensus-v2-demande", "hex": "" },
    { "nom": "consensus-v2-page", "hex": "" },
    { "nom": "consensus-v2-document", "hex": "" }
```

(respecter l'indentation et les virgules du fichier existant).

- [ ] **Step 2 : lancer le test, il doit échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RendezvousVectorTests"`
Attendu : échec de compilation (`ConsensusV2Query` inconnu).

- [ ] **Step 3 : implémenter les trames**

Dans `RendezvousKind`, après `NetworkStatusPage` :

```csharp
    /// <summary>Demande une page de la liste signée v2, celle qui porte les régions.</summary>
    /// <remarks>
    /// Une requête à part plutôt qu'un drapeau : une autorité d'avant répond
    /// « trame inattendue », et le client sait alors redemander la v1.
    /// </remarks>
    public const byte ConsensusV2Query = 0x1B;

    /// <summary>Une page de la liste signée v2.</summary>
    public const byte ConsensusV2Page = 0x1C;
```

Dans `RendezvousWire`, après `TryReadConsensusPage` :

```csharp
    public static byte[] ConsensusV2Query(int page) => PageQuery(RendezvousKind.ConsensusV2Query, page);

    public static bool TryReadConsensusV2Query(ReadOnlySpan<byte> frame, out int page)
        => TryReadPageQuery(RendezvousKind.ConsensusV2Query, frame, out page);

    public static byte[] ConsensusV2Page(int page, int pages, ReadOnlySpan<byte> chunk)
        => ChunkPage(RendezvousKind.ConsensusV2Page, page, pages, chunk);

    public static bool TryReadConsensusV2Page(
        ReadOnlySpan<byte> frame, out int page, out int pages, out byte[] chunk, out string? rejection)
        => TryReadChunkPage(RendezvousKind.ConsensusV2Page, "page de liste signée v2 malformée", frame, out page, out pages, out chunk, out rejection);
```

Dans `RendezvousClient`, remplacer `QueryConsensusAsync` par :

```csharp
    public Task<(byte[]? Document, string? Failure)> QueryConsensusAsync(CancellationToken ct)
        => QueryConsensusPagesAsync(v2: false, ct);

    /// <summary>La liste signée v2, avec les régions. Une autorité d'avant la refuse.</summary>
    public Task<(byte[]? Document, string? Failure)> QueryConsensusV2Async(CancellationToken ct)
        => QueryConsensusPagesAsync(v2: true, ct);

    private async Task<(byte[]? Document, string? Failure)> QueryConsensusPagesAsync(bool v2, CancellationToken ct)
    {
        using var document = new MemoryStream();
        var total = 1;

        for (var page = 0; page < total; page++)
        {
            await SendAsync(v2 ? RendezvousWire.ConsensusV2Query(page) : RendezvousWire.ConsensusQuery(page), ct).ConfigureAwait(false);

            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return (null, "connexion fermée par le service");

            if (frame[0] == RendezvousKind.Error)
                return (null, System.Text.Encoding.UTF8.GetString(frame.AsSpan(1)));

            var read = v2
                ? RendezvousWire.TryReadConsensusV2Page(frame, out var index, out var count, out var chunk, out var why)
                : RendezvousWire.TryReadConsensusPage(frame, out index, out count, out chunk, out why);

            if (read is false)
                return (null, why);

            if (index != page || (page > 0 && count != total))
                return (null, "pages incohérentes");

            total = count;
            document.Write(chunk);
        }

        return (document.ToArray(), null);
    }
```

(garder le commentaire `<summary>` existant au-dessus de `QueryConsensusAsync`).

- [ ] **Step 4 : relever les hex attendus**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~Chaque_trame_se_reproduit"`
Attendu : FAIL, `Expected: ""` et `Actual: "1b0001"` pour la première trame. Recopier chaque `Actual` dans le `hex` correspondant du JSON, relancer jusqu'à ce que les trois soient remplis. `consensus-v2-demande` doit valoir `1b0001` et `consensus-v2-page` `1c00000002abcd` (même forme que `190001` et `1a00000002abcd`) ; `consensus-v2-document` doit commencer par `6c696e6b706561726c3a636f6e73656e7375733a7632` (le préfixe en ASCII).

- [ ] **Step 5 : lancer toute la suite**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj`
Attendu : PASS.

- [ ] **Step 6 : commit**

```bash
git add Linkpearl/Core/Transport/Rendezvous/RendezvousWire.cs Linkpearl/Core/Transport/Rendezvous/RendezvousClient.cs Linkpearl.Core.Tests/Rendezvous/RendezvousVectorTests.cs Linkpearl.Core.Tests/Fixtures/rendezvous-vectors.json
git commit -m "feat(wire): trames de la liste signée v2, et leurs vecteurs figés"
```

---

### Task 3 : recopier le protocole dans le service

**Files (dépôt service) :**
- Modify: `Protocol/Core/Transport/Rendezvous/RendezvousWire.cs`
- Modify: `Protocol/Core/Transport/Rendezvous/ServiceConsensus.cs`
- Modify: `Protocol/rendezvous-vectors.json`
- Modify: `Linkpearl.Rendezvous.Tests/RendezvousVectorTests.cs`

**Interfaces:**
- Produces : dans le service, les mêmes types que les Tâches 1 et 2.

- [ ] **Step 1 : recopier**

```bash
cd ~/Projects/linkpearl-sync
cp plugin/Linkpearl/Core/Transport/Rendezvous/RendezvousWire.cs rendezvous/Protocol/Core/Transport/Rendezvous/RendezvousWire.cs
cp plugin/Linkpearl/Core/Transport/Rendezvous/ServiceConsensus.cs rendezvous/Protocol/Core/Transport/Rendezvous/ServiceConsensus.cs
cp plugin/Linkpearl.Core.Tests/Fixtures/rendezvous-vectors.json rendezvous/Protocol/rendezvous-vectors.json
cp plugin/Linkpearl.Core.Tests/Rendezvous/RendezvousVectorTests.cs rendezvous/Linkpearl.Rendezvous.Tests/RendezvousVectorTests.cs
```

- [ ] **Step 2 : vérifier que la copie est littérale**

```bash
cd ~/Projects/linkpearl-sync
for f in RendezvousWire ServiceConsensus RendezvousAddress RendezvousTicket; do diff plugin/Linkpearl/Core/Transport/Rendezvous/$f.cs rendezvous/Protocol/Core/Transport/Rendezvous/$f.cs; done
diff plugin/Linkpearl.Core.Tests/Fixtures/rendezvous-vectors.json rendezvous/Protocol/rendezvous-vectors.json
diff plugin/Linkpearl.Core.Tests/Rendezvous/RendezvousVectorTests.cs rendezvous/Linkpearl.Rendezvous.Tests/RendezvousVectorTests.cs
```

Attendu : aucune sortie.

- [ ] **Step 3 : build et tests du service**

Run (dépôt service) : `dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release && dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj`
Attendu : build sans warning, tests PASS.

- [ ] **Step 4 : commit (dépôt service)**

```bash
git add Protocol Linkpearl.Rendezvous.Tests/RendezvousVectorTests.cs
git commit -m "chore(protocole): recopie de la liste signée v2 et de ses trames"
```

---

### Task 4 : l'autorité émet et sert la v2 (service)

**Files (dépôt service) :**
- Create: `Linkpearl.Rendezvous/IRegionLookup.cs`
- Modify: `Linkpearl.Rendezvous/AuthorityLedger.cs` (`Record`, `Listed`, `Snapshot`, `SaveLocked`, `Read`, classe `Service`, record `TrackedService`)
- Modify: `Linkpearl.Rendezvous/AuthorityService.cs`
- Modify: `Linkpearl.Rendezvous/RendezvousServer.cs:480` et `:586`
- Modify: `Linkpearl.Rendezvous/AdminServer.cs:536-546`, `Linkpearl.Rendezvous/AdminPage.cs:649`
- Test: `Linkpearl.Rendezvous.Tests/AuthorityServiceTests.cs`, `Linkpearl.Rendezvous.Tests/AuthorityLedgerTests.cs`

**Interfaces:**
- Consumes : `ServiceConsensus.SignV2`, `RendezvousWire.ConsensusV2Page`, `TryReadConsensusV2Query` (Tâche 3).
- Produces :
  - `public interface IRegionLookup { string? RegionOf(IPAddress address); }`
  - `AuthorityService(AuthorityLedger ledger, IServiceProbe probe, ECDsa key, IClock clock, IRegionLookup? regions = null)`
  - `AuthorityService.DocumentV2` et `IConsensusSource.DocumentV2 => null` par défaut
  - `AuthorityLedger.Record(string address, ProbeResult result, string? region = null)`
  - `TrackedService(..., string? Region = null)` en dernier paramètre

- [ ] **Step 1 : écrire les tests qui échouent**

Dans `AuthorityServiceTests.cs`, ajouter à côté de `ScriptedProbe` :

```csharp
/// <summary>Une géolocalisation scriptée : chaque adresse citée reçoit sa région.</summary>
internal sealed class ScriptedRegions : IRegionLookup
{
    public Dictionary<IPAddress, string> Known { get; } = [];

    public string? RegionOf(IPAddress address) => Known.GetValueOrDefault(address);
}
```

étendre `FixedConsensus` :

```csharp
internal sealed class FixedConsensus(byte[]? document, byte[]? status = null, byte[]? documentV2 = null) : IConsensusSource
{
    public byte[]? Document => document;

    public byte[]? Status => status;

    public byte[]? DocumentV2 => documentV2;
}
```

remplacer le constructeur de la classe de test pour injecter les régions :

```csharp
    private readonly ScriptedRegions _regions = new();

    public AuthorityServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _authority = new AuthorityService(
            AuthorityLedger.Load(Path.Combine(_dir, "authority.json"), _clock), _probe, _key, _clock, _regions)
        {
            Log = TextWriter.Null,
        };
    }
```

et ajouter :

```csharp
    [Fact]
    public async Task Un_service_liste_porte_sa_region_dans_la_v2_et_pas_dans_la_v1()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
        _probe.Up["rdv.candidat.ch:47900"] = IPAddress.Parse("203.0.113.7");
        _regions.Known[IPAddress.Parse("203.0.113.7")] = "NA";

        for (var round = 0; round <= 432; round++)
        {
            await _authority.RoundAsync(CancellationToken.None);
            _clock.Advance(AuthorityService.Interval);
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();

        Assert.True(ServiceConsensus.TryVerify(_authority.DocumentV2!, [_authority.PublicPoint], now, out var v2, out var why), why);
        Assert.Equal("NA", Assert.Single(v2!.Entries).Region);

        Assert.True(ServiceConsensus.TryVerify(_authority.Document!, [_authority.PublicPoint], now, out var v1, out why), why);
        Assert.Null(Assert.Single(v1!.Entries).Region);
        Assert.Equal(v1.Version, v2.Version);
    }

    [Fact]
    public async Task Une_region_qui_change_fait_reemettre_la_liste()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
        _probe.Up["rdv.candidat.ch:47900"] = IPAddress.Parse("203.0.113.7");

        for (var round = 0; round <= 432; round++)
        {
            await _authority.RoundAsync(CancellationToken.None);
            _clock.Advance(AuthorityService.Interval);
        }

        var before = _authority.Current!.Version;
        _regions.Known[IPAddress.Parse("203.0.113.7")] = "EU";
        await _authority.RoundAsync(CancellationToken.None);

        Assert.True(_authority.Current!.Version > before);
        Assert.Equal("EU", Assert.Single(_authority.Current.Entries).Region);
    }

    [Fact]
    public async Task Le_service_sert_la_v2_par_pages_et_toujours_la_v1()
    {
        var v1 = RandomNumberGenerator.GetBytes(40_000);
        var v2 = RandomNumberGenerator.GetBytes(40_000);
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(v1, documentV2: v2));
        using var client = await harness.ConnectAsync();
        var received = new List<byte>();

        for (var page = 0; page < 2; page++)
        {
            await client.SendAsync(RendezvousWire.ConsensusV2Query(page));
            var frame = await client.ReadFrameAsync();

            Assert.True(RendezvousWire.TryReadConsensusV2Page(frame!, out var index, out var pages, out var chunk, out var why), why);
            Assert.Equal(page, index);
            Assert.Equal(2, pages);
            received.AddRange(chunk);
        }

        Assert.Equal(v2, received.ToArray());

        await client.SendAsync(RendezvousWire.ConsensusQuery(0));
        Assert.True(RendezvousWire.TryReadConsensusPage((await client.ReadFrameAsync())!, out _, out _, out var first, out _));
        Assert.Equal(v1.AsSpan(0, first.Length).ToArray(), first);
    }

    [Fact]
    public async Task Sans_v2_le_service_refuse_poliment_la_demande_v2()
    {
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(new byte[] { 1 }));
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.ConsensusV2Query(0));

        Assert.Equal(RendezvousKind.Error, (await client.ReadFrameAsync())![0]);
    }
```

Dans `AuthorityLedgerTests.cs`, ajouter (en reprenant la construction de registre déjà employée dans ce fichier, `AuthorityLedger.Load(path, clock)`) :

```csharp
    [Fact]
    public void La_region_survit_a_un_redemarrage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lprdv-ledger-{Guid.NewGuid():N}.json");
        var clock = new ManualClock();

        try
        {
            var ledger = AuthorityLedger.Load(path, clock);
            ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
            ledger.Record("rdv.candidat.ch", new ProbeResult(true, IPAddress.Parse("203.0.113.7")), "OC");
            ledger.Settle();

            var reloaded = AuthorityLedger.Load(path, clock);

            Assert.Equal("OC", Assert.Single(reloaded.Snapshot()).Region);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

(Si `Settle()` ne sauve pas un service encore en probation, appeler la méthode qui persiste dans ce fichier, en suivant les tests existants de `AuthorityLedgerTests`.)

- [ ] **Step 2 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter "FullyQualifiedName~AuthorityServiceTests|FullyQualifiedName~AuthorityLedgerTests"`
Attendu : échec de compilation (`IRegionLookup`, `DocumentV2`, `Region` inconnus).

- [ ] **Step 3 : implémenter**

`Linkpearl.Rendezvous/IRegionLookup.cs` :

```csharp
using System.Net;

namespace Linkpearl.Rendezvous;

/// <summary>Le continent d'une adresse de service, ou null.</summary>
/// <remarks>
/// Déduit par l'autorité, jamais déclaré par l'opérateur : un service qui se
/// dirait seul dans une région creuse y prendrait tous les relais.
/// </remarks>
public interface IRegionLookup
{
    string? RegionOf(IPAddress address);
}
```

`AuthorityLedger.cs` :
- `TrackedService` : ajouter `string? Region = null` après `double? Availability24h = null`.
- Classe `Service` : `public string? Region { get; set; }`.
- `Record(string address, ProbeResult result, string? region = null)` ; dans le bloc `if (reached && result.Address is { } reachedAt)`, après `service.Family = ...` :

```csharp
                // Relue à chaque sonde réussie : une base GeoIP absente ou
                // périmée efface la région, et le service retombe dans le
                // tirage global plutôt que de garder une région qu'on ne sait
                // plus justifier.
                service.Region = region;
```

- `Listed()` : `new ConsensusEntry(service.Address, service.Label, service.Family!, service.Region)`.
- `Snapshot()` : passer `service.Region` en dernier argument de `TrackedService`.
- `SaveLocked()` : `["region"] = service.Region,`.
- `Read()` : `Region = item["region"]?.GetValue<string>(),` dans l'initialiseur de `Service` (absent d'un registre d'avant : null).

`AuthorityService.cs` :
- `IConsensusSource` : ajouter

```csharp
    /// <summary>La liste signée v2, avec les régions, s'il y en a une.</summary>
    byte[]? DocumentV2 => null;
```

- constructeur primaire : `AuthorityLedger ledger, IServiceProbe probe, ECDsa key, IClock clock, IRegionLookup? regions = null`.
- champ `private byte[]? _documentV2;` et propriété :

```csharp
    public byte[]? DocumentV2
    {
        get
        {
            lock (_gate)
                return _documentV2;
        }
    }
```

- dans `RoundAsync`, remplacer le corps de la lambda par :

```csharp
                if (RendezvousAddress.TryParse(address, out var at, out _) is false)
                    return;

                var result = await probe.ProbeAsync(at, token).ConfigureAwait(false);
                ledger.Record(address, result, result.Address is { } reached ? regions?.RegionOf(reached) : null);
```

- dans `IssueIfNeeded`, après `_document = ServiceConsensus.Sign(list, key);` : `_documentV2 = ServiceConsensus.SignV2(list, key);`
- dans `Same`, ajouter `&& pair.First.Region == pair.Second.Region`.

`RendezvousServer.cs` : dans le `switch`, après `ConsensusQuery` :

```csharp
                    RendezvousKind.ConsensusV2Query => await HandleConsensusV2QueryAsync(session, frame, ct).ConfigureAwait(false),
```

et après `HandleConsensusQueryAsync` :

```csharp
    private Task<bool> HandleConsensusV2QueryAsync(PeerSession session, byte[] frame, CancellationToken ct)
        => RendezvousWire.TryReadConsensusV2Query(frame, out var page)
            ? ServeChunksAsync(session, page, Consensus?.DocumentV2, "ce service ne publie pas de liste signée v2", RendezvousWire.ConsensusV2Page, ct)
            : RefuseAsync(session, "demande de liste signée v2 malformée", ct);
```

`AdminServer.cs` (`AuthorityStatus`) : ajouter `["region"] = service.Region,` après `["label"]`.

`AdminPage.cs:649` : remplacer `p.address, p.label || "", etatAutorite(p),` par `p.address, (p.label || "") + (p.region ? " · " + p.region : ""), etatAutorite(p),`.

- [ ] **Step 4 : lancer les tests**

Run : `dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release && dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj`
Attendu : build sans warning, PASS.

- [ ] **Step 5 : commit (dépôt service)**

```bash
git add Linkpearl.Rendezvous Linkpearl.Rendezvous.Tests
git commit -m "feat(autorité): région de chaque service, liste signée v2 servie à côté de la v1"
```

---

### Task 5 : base GeoIP de l'autorité (service)

**Files (dépôt service) :**
- Modify: `Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj` (paquet `MaxMind.Db`)
- Create: `Linkpearl.Rendezvous/GeoIpRegions.cs`
- Create: `Linkpearl.Rendezvous/GeoIpUpdater.cs`
- Modify: `Linkpearl.Rendezvous/Program.cs:74-81` (aide) et `:154-180` (câblage)
- Create: `Linkpearl.Rendezvous.Tests/Fixtures/GeoIP2-Country-Test.mmdb` et `Linkpearl.Rendezvous.Tests/Fixtures/README.md`
- Modify: `Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj` (copie de la fixture)
- Test: `Linkpearl.Rendezvous.Tests/GeoIpRegionsTests.cs`, `Linkpearl.Rendezvous.Tests/GeoIpUpdaterTests.cs`
- Modify: `README.md` (attribution DB-IP), `CLAUDE.md` (fichier `geoip.mmdb`)

**Interfaces:**
- Consumes : `IRegionLookup` (Tâche 4).
- Produces :
  - `public sealed class GeoIpRegions(string path, IClock clock) : IRegionLookup, IDisposable` avec `static readonly TimeSpan MaxAge`, `void Reload()`, `DateTimeOffset? BuildDate { get; }`, `static string? Retained(string? code)`
  - `public sealed class GeoIpUpdater(GeoIpRegions regions, string path, HttpClient http, IClock clock)` avec `static readonly TimeSpan RefreshAfter`, `Task RunAsync(CancellationToken ct)`, `Task<bool> RefreshIfNeededAsync(CancellationToken ct)`, `static Uri SourceFor(DateTimeOffset month)`

- [ ] **Step 1 : ajouter la dépendance et vérifier le schéma de la base**

```bash
dotnet add Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj package MaxMind.Db
mkdir -p Linkpearl.Rendezvous.Tests/Fixtures
curl -fsSL -o Linkpearl.Rendezvous.Tests/Fixtures/GeoIP2-Country-Test.mmdb \
  https://github.com/maxmind/MaxMind-DB/raw/main/test-data/GeoIP2-Country-Test.mmdb
curl -fsSL https://download.db-ip.com/free/dbip-country-lite-$(date +%Y-%m).mmdb.gz | gunzip > "$TMPDIR/dbip.mmdb" || \
curl -fsSL https://download.db-ip.com/free/dbip-country-lite-$(date -d '-1 month' +%Y-%m).mmdb.gz | gunzip > "$TMPDIR/dbip.mmdb"
```

Puis, avec un petit programme jetable (`dotnet new console` dans le scratchpad, `dotnet add package MaxMind.Db`), afficher `reader.Find<Dictionary<string, object>>(IPAddress.Parse("83.228.242.221"))` sur la base DB-IP, et sur la fixture pour `81.2.69.160`. Attendu : un dictionnaire avec une clé `continent` contenant `code`, qui vaut `EU` dans les deux cas. **Si `continent.code` manque dans la base DB-IP, s'arrêter et le signaler** : la conception suppose ce champ. Si `81.2.69.160` ne rend pas `EU` dans la fixture, choisir une autre adresse européenne dans `test-data/source-data/GeoIP2-Country-Test.json` du même dépôt et l'employer dans le test de l'étape 2.

Écrire `Linkpearl.Rendezvous.Tests/Fixtures/README.md` :

```markdown
`GeoIP2-Country-Test.mmdb` vient du dépôt MaxMind-DB (test-data), sous licence
Creative Commons Attribution-ShareAlike 4.0. Il ne sert qu'aux tests de
`GeoIpRegions` : une petite base au même schéma que celle de DB-IP.
```

Dans `Linkpearl.Rendezvous.Tests.csproj`, ajouter :

```xml
  <ItemGroup>
    <None Include="Fixtures\GeoIP2-Country-Test.mmdb" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2 : écrire les tests qui échouent**

Si `ManualClock` (dans `Linkpearl.Rendezvous.Tests/ManualClock.cs`) n'a pas de méthode pour fixer l'heure, en ajouter une, en suivant le nom du champ ou de la propriété qu'il emploie déjà :

```csharp
    public void Set(DateTimeOffset at) => UtcNow = at;
```

`Linkpearl.Rendezvous.Tests/GeoIpRegionsTests.cs` :

```csharp
using System.Net;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

public sealed class GeoIpRegionsTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "GeoIP2-Country-Test.mmdb");

    [Fact]
    public void Une_adresse_connue_rend_son_continent()
    {
        var clock = new ManualClock();
        using var regions = new GeoIpRegions(Fixture, clock);
        clock.Set(regions.BuildDate!.Value.AddDays(1));

        Assert.Equal("EU", regions.RegionOf(IPAddress.Parse("81.2.69.160")));
    }

    [Fact]
    public void Une_adresse_inconnue_ne_rend_rien()
    {
        var clock = new ManualClock();
        using var regions = new GeoIpRegions(Fixture, clock);
        clock.Set(regions.BuildDate!.Value.AddDays(1));

        Assert.Null(regions.RegionOf(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Une_base_perimee_ne_rend_rien()
    {
        var clock = new ManualClock();
        using var regions = new GeoIpRegions(Fixture, clock);
        clock.Set(regions.BuildDate!.Value + GeoIpRegions.MaxAge + TimeSpan.FromDays(1));

        Assert.Null(regions.RegionOf(IPAddress.Parse("81.2.69.160")));
    }

    [Fact]
    public void Sans_fichier_rien_ne_leve()
    {
        using var regions = new GeoIpRegions(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.mmdb"), new ManualClock());

        Assert.Null(regions.BuildDate);
        Assert.Null(regions.RegionOf(IPAddress.Parse("81.2.69.160")));
    }

    [Theory]
    [InlineData("EU", "EU")]
    [InlineData("NA", "NA")]
    [InlineData("AN", null)]
    [InlineData("eu", null)]
    [InlineData(null, null)]
    public void Seuls_les_six_continents_habites_sont_retenus(string? code, string? expected)
        => Assert.Equal(expected, GeoIpRegions.Retained(code));
}
```

`Linkpearl.Rendezvous.Tests/GeoIpUpdaterTests.cs` :

```csharp
using System.IO.Compression;
using System.Net;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Un serveur HTTP scripté : chaque URL citée rend son contenu, les autres un 404.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Pages { get; } = [];

    public List<string> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Asked.Add(request.RequestUri!.ToString());

        return Task.FromResult(Pages.TryGetValue(request.RequestUri.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class GeoIpUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-geoip-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new();
    private readonly ScriptedHandler _web = new();

    public GeoIpUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Gzipped(byte[] raw)
    {
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(raw);

        return output.ToArray();
    }

    private static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GeoIP2-Country-Test.mmdb"));

    [Fact]
    public void L_adresse_de_la_base_suit_le_mois()
        => Assert.Equal(
            "https://download.db-ip.com/free/dbip-country-lite-2026-09.mmdb.gz",
            GeoIpUpdater.SourceFor(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)).ToString());

    [Fact]
    public async Task Une_base_absente_est_telechargee_et_rechargee()
    {
        _clock.Set(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow).ToString()] = Gzipped(Fixture);

        var path = Path.Combine(_dir, "geoip.mmdb");
        using var regions = new GeoIpRegions(path, _clock);
        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.True(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.NotNull(regions.BuildDate);
    }

    [Fact]
    public async Task Le_mois_precedent_sert_quand_le_courant_n_est_pas_encore_publie()
    {
        _clock.Set(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow.AddMonths(-1)).ToString()] = Gzipped(Fixture);

        var path = Path.Combine(_dir, "geoip.mmdb");
        using var regions = new GeoIpRegions(path, _clock);
        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.True(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Equal(2, _web.Asked.Count);
    }

    [Fact]
    public async Task Un_telechargement_corrompu_ne_remplace_pas_la_base_en_place()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value + GeoIpUpdater.RefreshAfter + TimeSpan.FromDays(1));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow).ToString()] = Gzipped([1, 2, 3]);

        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.False(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Equal(Fixture, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Un_gzip_illisible_ne_remplace_pas_la_base_en_place()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value + GeoIpUpdater.RefreshAfter + TimeSpan.FromDays(1));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow).ToString()] = [0x1F, 0x8B, 0x00, 0x42];

        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.False(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Equal(Fixture, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Une_base_recente_n_est_pas_retelechargee()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value.AddDays(1));

        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.False(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Empty(_web.Asked);
    }
}
```

- [ ] **Step 3 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter "FullyQualifiedName~GeoIp"`
Attendu : échec de compilation (`GeoIpRegions`, `GeoIpUpdater` inconnus).

- [ ] **Step 4 : implémenter `GeoIpRegions`**

```csharp
using System.Net;
using Linkpearl.Core.Abstractions;
using MaxMind.Db;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Le continent d'une adresse, lu dans la base DB-IP « IP to Country Lite ».
/// </summary>
/// <remarks>
/// Seule l'autorité s'en sert, et seulement pour les adresses de services,
/// déjà publiques : aucun client n'y passe. Une base absente ou trop vieille
/// ne rend aucune région plutôt qu'une région fausse, et les services
/// retombent dans le tirage global, comme avant les régions.
/// </remarks>
public sealed class GeoIpRegions(string path, IClock clock) : IRegionLookup, IDisposable
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    private static readonly HashSet<string> Continents = new(StringComparer.Ordinal) { "AF", "AS", "EU", "NA", "OC", "SA" };

    private readonly Lock _gate = new();
    private Reader? _reader = Open(path);

    public DateTimeOffset? BuildDate
    {
        get
        {
            lock (_gate)
                return _reader is { } reader ? new DateTimeOffset(reader.Metadata.BuildDate, TimeSpan.Zero) : null;
        }
    }

    public void Reload()
    {
        var fresh = Open(path);

        lock (_gate)
        {
            _reader?.Dispose();
            _reader = fresh;
        }
    }

    public string? RegionOf(IPAddress address)
    {
        lock (_gate)
        {
            if (_reader is not { } reader || clock.UtcNow - new DateTimeOffset(reader.Metadata.BuildDate, TimeSpan.Zero) > MaxAge)
                return null;

            var data = reader.Find<Dictionary<string, object>>(address);

            return data?.GetValueOrDefault("continent") is Dictionary<string, object> continent
                ? Retained(continent.GetValueOrDefault("code") as string)
                : null;
        }
    }

    /// <summary>Le code tel quel s'il désigne un continent habité, sinon null.</summary>
    /// <remarks>L'Antarctique n'a pas de joueurs : un service qui s'y dirait n'aurait pas de région.</remarks>
    public static string? Retained(string? code) => code is not null && Continents.Contains(code) ? code : null;

    public void Dispose()
    {
        lock (_gate)
            _reader?.Dispose();
    }

    private static Reader? Open(string path)
    {
        if (File.Exists(path) is false)
            return null;

        try
        {
            return new Reader(path);
        }
        catch (InvalidDatabaseException)
        {
            // Une base illisible vaut une base absente : pas de région, et le
            // prochain téléchargement la remplace.
            return null;
        }
    }
}
```

- [ ] **Step 5 : implémenter `GeoIpUpdater`**

```csharp
using System.IO.Compression;
using System.Net;
using Linkpearl.Core.Abstractions;
using MaxMind.Db;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Retélécharge la base GeoIP quand elle a plus d'un mois.
/// </summary>
/// <remarks>
/// DB-IP publie une base par mois, sans compte ni clé. Le fichier du mois
/// n'existe pas encore les premiers jours : on retente alors le précédent.
/// Un fichier reçu n'écrase la base en place qu'une fois relu avec succès.
/// </remarks>
public sealed class GeoIpUpdater(GeoIpRegions regions, string path, HttpClient http, IClock clock)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(32);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const long MaxBytes = 200L * 1024 * 1024;

    public TextWriter Log { get; init; } = Console.Out;

    public static Uri SourceFor(DateTimeOffset month)
        => new($"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.mmdb.gz");

    public async Task RunAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            try
            {
                await RefreshIfNeededAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && ct.IsCancellationRequested is false)
            {
                Log.WriteLine($"Base GeoIP non renouvelée : {e.Message}");
            }

            try
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<bool> RefreshIfNeededAsync(CancellationToken ct)
    {
        if (regions.BuildDate is { } built && clock.UtcNow - built < RefreshAfter)
            return false;

        foreach (var month in new[] { clock.UtcNow, clock.UtcNow.AddMonths(-1) })
        {
            using var response = await http.GetAsync(SourceFor(month), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound)
                continue;

            response.EnsureSuccessStatusCode();

            var temporary = path + ".tmp";

            try
            {
                await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var gzip = new GZipStream(body, CompressionMode.Decompress))
                await using (var file = File.Create(temporary))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;

                    while ((read = await gzip.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;

                        if (total > MaxBytes)
                            throw new InvalidDataException($"base GeoIP de plus de {MaxBytes} octets");

                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }

                if (Readable(temporary) is false)
                    throw new InvalidDataException("base GeoIP reçue illisible");
            }
            catch (InvalidDataException e)
            {
                // Un fichier tronqué, corrompu ou démesuré ne remplace jamais
                // une base qui fonctionne : on la garde jusqu'au prochain essai.
                File.Delete(temporary);
                Log.WriteLine($"{e.Message}, la base en place est gardée.");
                return false;
            }

            File.Move(temporary, path, overwrite: true);
            regions.Reload();
            Log.WriteLine($"Base GeoIP renouvelée : {SourceFor(month)}.");
            return true;
        }

        Log.WriteLine("Base GeoIP introuvable pour ce mois comme pour le précédent.");
        return false;
    }

    private static bool Readable(string file)
    {
        try
        {
            using var reader = new Reader(file);
            return true;
        }
        catch (Exception e) when (e is InvalidDatabaseException or IOException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 6 : câbler dans `Program.cs`**

Dans l'aide, après `--authority-state` :

```
        --geoip                base GeoIP de l'autorité (geoip.mmdb). Téléchargée
                               chaque mois depuis DB-IP (CC BY 4.0) ; se
                               régénère, contrairement à directory.key.
```

Avant `if (args.Contains("--directory-authority"))`, déclarer :

```csharp
GeoIpRegions? regions = null;
GeoIpUpdater? geoip = null;
```

Dans ce bloc, remplacer la construction de `authority` par :

```csharp
    var geoipPath = ArgString("--geoip", "geoip.mmdb");
    regions = new GeoIpRegions(geoipPath, clock);
    geoip = new GeoIpUpdater(regions, geoipPath, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, clock);
    authority = new AuthorityService(ledger, new ServiceProbe(), key, clock, regions);
```

Après `running.Add(authority.RunAsync(stopping.Token));` :

```csharp
if (geoip is not null)
    running.Add(geoip.RunAsync(stopping.Token));
```

- [ ] **Step 7 : documenter**

`README.md` (service) : dans la section de l'autorité, ajouter :

```markdown
L'autorité attribue à chaque service le continent de son adresse, d'après la
base [IP to Country Lite](https://db-ip.com) de DB-IP, sous licence
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Elle la télécharge
seule, une fois par mois, dans `geoip.mmdb`.
```

`CLAUDE.md` (service), section Déploiement, après le point sur `directory.key` :

```markdown
- `geoip.mmdb` y vit aussi, pour le rôle d'autorité. Contrairement à `bans.json`
  et `directory.key`, il se régénère : le supprimer ne coûte qu'un téléchargement.
```

- [ ] **Step 8 : lancer les tests et le build**

Run : `dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release && dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj`
Attendu : build sans warning, PASS.

- [ ] **Step 9 : commit (dépôt service)**

```bash
git add Linkpearl.Rendezvous Linkpearl.Rendezvous.Tests README.md CLAUDE.md
git commit -m "feat(autorité): région des services par la base GeoIP de DB-IP, renouvelée chaque mois"
```

---

### Task 6 : services éligibles au relais (plugin)

**Files:**
- Create: `Linkpearl/Core/Sync/RelayPlacement.cs`
- Modify: `Linkpearl/Core/Sync/OpenCircle.cs`
- Test: `Linkpearl.Core.Tests/Sync/RelayPlacementTests.cs`

**Interfaces:**
- Consumes : `ConsensusEntry.Region` (Tâche 1), `ServicePlacement.Choose`, `ServicePlacement.Score`.
- Produces :
  - `public sealed record RelayPlace(RendezvousAddress At, string? Region, bool Open)` avec `ulong Fingerprint` et `static ulong FingerprintOf(RendezvousAddress at)`
  - `public static class RelayPlacement` avec `const int MinimumFamilies = 2` et `static IReadOnlyList<RelayPlace> Eligible(ReadOnlySpan<byte> pairSecret, IReadOnlyList<ConsensusEntry> entries, IReadOnlyList<RendezvousAddress> anchors)`
  - `IOpenCircle.RelayEntriesFor(PairRecord pair)` rendant `IReadOnlyList<ConsensusEntry>` (implémentation par défaut `[]`)

- [ ] **Step 1 : écrire les tests qui échouent**

`Linkpearl.Core.Tests/Sync/RelayPlacementTests.cs` :

```csharp
using System.Security.Cryptography;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayPlacementTests
{
    private static readonly byte[] Secret = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private static ConsensusEntry Entry(string address, byte family, string? region = null)
        => new(address, "", [.. Enumerable.Repeat(family, 8)], region);

    private static string[] Hosts(IReadOnlyList<RelayPlace> places) => [.. places.Select(place => ServiceConsensus.Canonical(place.At))];

    [Fact]
    public void L_empreinte_est_celle_de_l_adresse_canonique()
        => Assert.Equal(
            Convert.ToUInt64(Convert.ToHexString(SHA256.HashData("rdv.a.ch:47900"u8.ToArray())[..8]), 16),
            RelayPlace.FingerprintOf(new RendezvousAddress("RDV.A.CH", 47900)));

    [Fact]
    public void Le_placement_d_appariement_est_toujours_eligible()
    {
        IReadOnlyList<ConsensusEntry> entries =
            [Entry("rdv.a.ch:47900", 1), Entry("rdv.b.ch:47900", 2), Entry("rdv.c.ch:443", 3), Entry("rdv.d.ch:47900", 4)];

        var places = RelayPlacement.Eligible(Secret, entries, []);

        Assert.Equal(ServicePlacement.Choose(Secret, entries).Select(ServiceConsensus.Canonical), Hosts(places));
        Assert.All(places, place => Assert.True(place.Open));
    }

    [Fact]
    public void Une_region_a_deux_familles_ajoute_son_meilleur_service()
    {
        IReadOnlyList<ConsensusEntry> entries =
        [
            Entry("rdv.a.ch:47900", 1, "EU"), Entry("rdv.b.ch:47900", 2, "EU"),
            Entry("rdv.na1.us:47900", 3, "NA"), Entry("rdv.na2.us:47900", 4, "NA"),
        ];

        var places = RelayPlacement.Eligible(Secret, entries, []);

        Assert.Contains(places, place => place.Region == "NA");
        Assert.Contains(places, place => place.Region == "EU");
        Assert.Equal(places.Count, places.Select(place => place.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Une_region_a_une_seule_famille_n_ajoute_rien()
    {
        IReadOnlyList<ConsensusEntry> entries =
        [
            Entry("rdv.a.ch:47900", 1, "EU"), Entry("rdv.b.ch:47900", 2, "EU"), Entry("rdv.c.ch:47900", 5, "EU"),
            Entry("rdv.na1.us:47900", 3, "NA"), Entry("rdv.na2.us:47900", 3, "NA"),
        ];

        var placement = ServicePlacement.Choose(Secret, entries).Select(ServiceConsensus.Canonical).ToHashSet();
        var places = RelayPlacement.Eligible(Secret, entries, []);

        // Deux services NA d'une même famille : pas de tirage NA. Un service NA
        // n'est éligible que s'il est déjà du placement.
        Assert.All(places.Where(place => place.Region == "NA"),
            place => Assert.Contains(ServiceConsensus.Canonical(place.At), placement));
    }

    [Fact]
    public void L_ancrage_s_ajoute_sans_doublon()
    {
        IReadOnlyList<ConsensusEntry> entries = [Entry("rdv.a.ch:47900", 1), Entry("rdv.b.ch:47900", 2)];

        var places = RelayPlacement.Eligible(Secret, entries,
            [new RendezvousAddress("rdv.a.ch", 47900), new RendezvousAddress("ancre.ch", 47900)]);

        Assert.Equal(3, places.Count);
        Assert.False(places.Single(place => place.At.Host == "ancre.ch").Open);
        Assert.True(places.Single(place => place.At.Host == "rdv.a.ch").Open);
    }

    [Fact]
    public void Sans_liste_il_ne_reste_que_l_ancrage()
    {
        var places = RelayPlacement.Eligible(Secret, [], [new RendezvousAddress("ancre.ch", 47900)]);

        Assert.Equal(["ancre.ch:47900"], Hosts(places));
    }

    [Fact]
    public void Le_resultat_ne_depend_pas_de_l_ordre_de_la_liste()
    {
        List<ConsensusEntry> entries =
        [
            Entry("rdv.a.ch:47900", 1, "EU"), Entry("rdv.b.ch:47900", 2, "EU"),
            Entry("rdv.na1.us:47900", 3, "NA"), Entry("rdv.na2.us:47900", 4, "NA"),
            Entry("rdv.jp1.jp:47900", 5, "AS"), Entry("rdv.jp2.jp:47900", 6, "AS"),
        ];

        var forward = Hosts(RelayPlacement.Eligible(Secret, entries, [])).Order();
        entries.Reverse();
        var backward = Hosts(RelayPlacement.Eligible(Secret, entries, [])).Order();

        Assert.Equal(forward, backward);
    }
}
```

- [ ] **Step 2 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayPlacementTests"`
Attendu : échec de compilation.

- [ ] **Step 3 : implémenter**

`Linkpearl/Core/Sync/RelayPlacement.cs` :

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Un service par lequel une paire peut relayer.</summary>
/// <remarks>
/// <see cref="Open"/> dit qu'il vient de la liste signée : on ne le joint alors
/// qu'à une adresse publique, comme pour l'annonce.
/// </remarks>
public sealed record RelayPlace(RendezvousAddress At, string? Region, bool Open)
{
    public ulong Fingerprint => FingerprintOf(At);

    /// <summary>Les huit premiers octets de SHA-256 de l'adresse canonique.</summary>
    /// <remarks>
    /// Ce que les deux pairs se disent pour désigner un service : compact, et
    /// identique des deux côtés quelle que soit la graphie de l'adresse.
    /// </remarks>
    public static ulong FingerprintOf(RendezvousAddress at)
        => BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(ServiceConsensus.Canonical(at))));
}

/// <summary>
/// Les services où une paire a le droit de relayer.
/// </summary>
/// <remarks>
/// Le hasard du secret de paire décide qui est éligible, la latence ne fait
/// que départager. Choisir librement le plus proche de toute la liste
/// laisserait un opérateur qui pose des serveurs partout aspirer les relais
/// de régions entières.
///
/// Une région ne compte qu'avec deux familles au moins : un service seul
/// dans la sienne y prendrait sinon tous les relais.
/// </remarks>
public static class RelayPlacement
{
    public const int MinimumFamilies = 2;

    private static readonly IComparer<byte[]> Descending =
        Comparer<byte[]>.Create((one, other) => other.AsSpan().SequenceCompareTo(one));

    public static IReadOnlyList<RelayPlace> Eligible(
        ReadOnlySpan<byte> pairSecret, IReadOnlyList<ConsensusEntry> entries, IReadOnlyList<RendezvousAddress> anchors)
    {
        var places = new List<RelayPlace>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        void Add(RendezvousAddress at, string? region, bool open)
        {
            if (taken.Add(ServiceConsensus.Canonical(at)))
                places.Add(new RelayPlace(at, region, open));
        }

        var listed = new List<(RendezvousAddress At, ConsensusEntry Entry, byte[] Score)>();

        foreach (var entry in entries)
            if (RendezvousAddress.TryParse(entry.Address, out var at, out _))
                listed.Add((at, entry, ServicePlacement.Score(pairSecret, ServiceConsensus.Canonical(at))));

        var regionOf = listed
            .GroupBy(item => ServiceConsensus.Canonical(item.At), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Entry.Region, StringComparer.Ordinal);

        foreach (var at in ServicePlacement.Choose(pairSecret, entries))
            Add(at, regionOf.GetValueOrDefault(ServiceConsensus.Canonical(at)), open: true);

        foreach (var region in listed
                     .Where(item => item.Entry.Region is not null)
                     .GroupBy(item => item.Entry.Region!, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var families = region.Select(item => Convert.ToHexString(item.Entry.Family)).Distinct(StringComparer.Ordinal).Count();

            if (families < MinimumFamilies)
                continue;

            var best = region.OrderBy(item => item.Score, Descending).First();
            Add(best.At, region.Key, open: true);
        }

        foreach (var at in anchors)
            Add(at, null, open: false);

        return places;
    }
}
```

`OpenCircle.cs` : dans `IOpenCircle`, ajouter

```csharp
    /// <summary>La liste où puiser les relais de cette paire, vide si elle n'a pas droit au cercle ouvert.</summary>
    IReadOnlyList<ConsensusEntry> RelayEntriesFor(PairRecord pair) => [];
```

et dans `OpenCircle` :

```csharp
    public IReadOnlyList<ConsensusEntry> RelayEntriesFor(PairRecord pair)
        => Enabled && MayUse(pair) && Current is { } list ? list.Entries : [];
```

- [ ] **Step 4 : lancer les tests**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayPlacementTests|FullyQualifiedName~OpenCircleTests"`
Attendu : PASS.

- [ ] **Step 5 : commit**

```bash
git add Linkpearl/Core/Sync/RelayPlacement.cs Linkpearl/Core/Sync/OpenCircle.cs Linkpearl.Core.Tests/Sync/RelayPlacementTests.cs
git commit -m "feat(relais): services éligibles au relais, un par région en plus du placement"
```

---

### Task 7 : mesures à la fin du bloc de candidats (plugin)

**Files:**
- Create: `Linkpearl/Core/Sync/RelayMeasurements.cs`
- Modify: `Linkpearl/Core/Sync/CandidateSet.cs`
- Test: `Linkpearl.Core.Tests/Sync/RelayMeasurementsTests.cs`

**Interfaces:**
- Consumes : `RelayPlace` (Tâche 6).
- Produces :
  - `public readonly record struct RelayMeasurement(ulong Service, ushort RttMs)` avec `const ushort Unreachable = 0xFFFF`, `const ushort Ceiling = 0xFFFE`, `static RelayMeasurement Of(ulong service, TimeSpan? rtt)`
  - `RelayMeasurements.Tag = 0x01`, `MaxMeasurements = 16`, `Far = 1000`
  - `static byte[] RelayMeasurements.Encode(IReadOnlyList<RelayMeasurement> measurements)`
  - `static IReadOnlyList<RelayMeasurement>? RelayMeasurements.TryRead(ReadOnlySpan<byte> extensions)` (null = pas de mesures lisibles)
  - `static IReadOnlyList<RelayMeasurement> RelayMeasurements.Synthetic(IReadOnlyList<RelayPlace> eligible, IReadOnlyList<RelayMeasurement> measured)`
  - `CandidateSet.TryDecode(ReadOnlySpan<byte> body, out List<IPEndPoint> candidates, out int consumed, out string? rejection)`

- [ ] **Step 1 : écrire les tests qui échouent**

`Linkpearl.Core.Tests/Sync/RelayMeasurementsTests.cs` :

```csharp
using System.Net;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayMeasurementsTests
{
    private static readonly IPEndPoint[] Addresses = [new(IPAddress.Parse("203.0.113.7"), 40000)];

    private static RelayMeasurement[] Sample => [new(0x0102030405060708, 12), new(0xA0A0A0A0A0A0A0A0, RelayMeasurement.Unreachable)];

    [Fact]
    public void Le_format_est_fige()
        // Longueur : 1 + 2 × 10 = 21 = 0x0015.
        => Assert.Equal(
            "010015" + "02" + "0102030405060708" + "000c" + "a0a0a0a0a0a0a0a0" + "ffff",
            Convert.ToHexStringLower(RelayMeasurements.Encode(Sample)));

    [Fact]
    public void Les_mesures_se_relisent_apres_les_adresses()
    {
        byte[] block = [.. CandidateSet.Encode(Addresses), .. RelayMeasurements.Encode(Sample)];

        Assert.True(CandidateSet.TryDecode(block, out var candidates, out var consumed, out var why), why);
        Assert.Equal(Addresses, candidates);
        Assert.Equal(Sample, RelayMeasurements.TryRead(block.AsSpan(consumed)));
    }

    [Fact]
    public void Un_decodeur_d_aujourd_hui_ignore_les_mesures()
    {
        byte[] block = [.. CandidateSet.Encode(Addresses), .. RelayMeasurements.Encode(Sample)];

        Assert.True(CandidateSet.TryDecode(block, out var candidates, out var why), why);
        Assert.Equal(Addresses, candidates);
    }

    [Fact]
    public void Un_bloc_d_avant_n_a_pas_de_mesures()
    {
        var block = CandidateSet.Encode(Addresses);

        Assert.True(CandidateSet.TryDecode(block, out _, out var consumed, out _));
        Assert.Null(RelayMeasurements.TryRead(block.AsSpan(consumed)));
    }

    [Fact]
    public void Une_etiquette_inconnue_est_sautee()
    {
        byte[] extensions = [0x7F, 0x00, 0x02, 0xEE, 0xEE, .. RelayMeasurements.Encode(Sample)];

        Assert.Equal(Sample, RelayMeasurements.TryRead(extensions));
    }

    [Theory]
    [InlineData("01")]                         // en-tête tronqué
    [InlineData("010015")]                     // contenu absent
    [InlineData("0100010a")]                   // dix mesures annoncées, aucune présente
    [InlineData("01000100" + "01000100")]      // deux blocs de mesures
    [InlineData("010001" + "11")]              // dix-sept mesures
    public void Une_extension_malformee_ne_donne_aucune_mesure(string hex)
        => Assert.Null(RelayMeasurements.TryRead(Convert.FromHexString(hex)));

    [Fact]
    public void Au_plus_seize_mesures_sont_ecrites()
    {
        var many = Enumerable.Range(0, 20).Select(i => new RelayMeasurement((ulong)i, 10)).ToArray();

        Assert.Equal(RelayMeasurements.MaxMeasurements, RelayMeasurements.TryRead(RelayMeasurements.Encode(many))!.Count);
    }

    [Fact]
    public void Un_rtt_se_borne_au_plafond()
    {
        Assert.Equal(RelayMeasurement.Ceiling, RelayMeasurement.Of(1, TimeSpan.FromMinutes(5)).RttMs);
        Assert.Equal(RelayMeasurement.Unreachable, RelayMeasurement.Of(1, null).RttMs);
        Assert.Equal((ushort)13, RelayMeasurement.Of(1, TimeSpan.FromMilliseconds(12.2)).RttMs);
    }

    [Fact]
    public void En_relais_seul_seule_la_region_la_plus_proche_est_a_zero()
    {
        var eu = new RelayPlace(new RendezvousAddress("rdv.eu.ch", 47900), "EU", true);
        var na = new RelayPlace(new RendezvousAddress("rdv.na.us", 47900), "NA", true);
        var anchor = new RelayPlace(new RendezvousAddress("ancre.ch", 47900), null, false);
        var dead = new RelayPlace(new RendezvousAddress("rdv.mort.ch", 47900), "EU", true);

        var synthetic = RelayMeasurements.Synthetic(
            [eu, na, anchor, dead],
            [new(anchor.Fingerprint, 3), new(na.Fingerprint, 90), new(eu.Fingerprint, 15), new(dead.Fingerprint, RelayMeasurement.Unreachable)]);

        Assert.Equal(RelayMeasurements.Far, synthetic.Single(m => m.Service == anchor.Fingerprint).RttMs);
        Assert.Equal(RelayMeasurements.Far, synthetic.Single(m => m.Service == na.Fingerprint).RttMs);
        Assert.Equal((ushort)0, synthetic.Single(m => m.Service == eu.Fingerprint).RttMs);
        Assert.Equal(RelayMeasurement.Unreachable, synthetic.Single(m => m.Service == dead.Fingerprint).RttMs);
    }

    [Fact]
    public void En_relais_seul_sans_region_rien_n_est_a_zero()
    {
        var anchor = new RelayPlace(new RendezvousAddress("ancre.ch", 47900), null, false);

        var synthetic = RelayMeasurements.Synthetic([anchor], [new(anchor.Fingerprint, 3)]);

        Assert.Equal(RelayMeasurements.Far, Assert.Single(synthetic).RttMs);
    }
}
```

- [ ] **Step 2 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayMeasurementsTests"`
Attendu : échec de compilation.

- [ ] **Step 3 : `CandidateSet` rend ce qu'il a lu**

Remplacer `TryDecode` par la paire :

```csharp
    public static bool TryDecode(ReadOnlySpan<byte> body, out List<IPEndPoint> candidates, out string? rejection)
        => TryDecode(body, out candidates, out _, out rejection);

    /// <summary>Comme l'autre, et rend le nombre d'octets lus.</summary>
    /// <remarks>
    /// Ce qui suit les adresses est laissé aux extensions : un décodeur d'avant
    /// ne le lisait pas, ce qui permet d'y ajouter sans casser personne.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> body, out List<IPEndPoint> candidates, out int consumed, out string? rejection)
```

avec le corps actuel, en ajoutant `consumed = 0;` en tête et `consumed = offset;` juste avant le `rejection = null; return true;` final.

- [ ] **Step 4 : implémenter `RelayMeasurements.cs`**

```csharp
using System.Buffers.Binary;

namespace Linkpearl.Core.Sync;

/// <summary>Le RTT d'un pair vers un service, désigné par son empreinte.</summary>
public readonly record struct RelayMeasurement(ulong Service, ushort RttMs)
{
    public const ushort Unreachable = 0xFFFF;
    public const ushort Ceiling = 0xFFFE;

    public static RelayMeasurement Of(ulong service, TimeSpan? rtt)
        => new(service, rtt is { } value ? (ushort)Math.Min(Math.Ceiling(value.TotalMilliseconds), Ceiling) : Unreachable);
}

/// <summary>
/// Les mesures glissées après les adresses, dans le bloc scellé.
/// </summary>
/// <remarks>
///     extension = etiquette(1) || longueur(2) || contenu
///     0x01      : nombre(1) || (service(8) || rtt_ms(2))*
///
/// Scellées avec les adresses : le rendez-vous ne voit ni mesure, ni position.
/// Une étiquette inconnue se saute grâce à sa longueur, ce qui laisse la place
/// à une extension future. Une extension malformée ne fait jamais échouer la
/// connexion : sans mesures lisibles, on relaie comme avant, par le service
/// d'appariement.
/// </remarks>
public static class RelayMeasurements
{
    public const byte Tag = 0x01;
    public const int MaxMeasurements = 16;

    /// <summary>Le RTT synthétique d'un service hors de la région d'un pair en relais seul.</summary>
    public const ushort Far = 1000;

    private const int EntrySize = 8 + 2;

    public static byte[] Encode(IReadOnlyList<RelayMeasurement> measurements)
    {
        var kept = measurements.Take(MaxMeasurements).ToList();
        var length = 1 + kept.Count * EntrySize;
        var block = new byte[3 + length];

        block[0] = Tag;
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(1), (ushort)length);
        block[3] = (byte)kept.Count;

        for (var i = 0; i < kept.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(4 + i * EntrySize), kept[i].Service);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(12 + i * EntrySize), kept[i].RttMs);
        }

        return block;
    }

    public static IReadOnlyList<RelayMeasurement>? TryRead(ReadOnlySpan<byte> extensions)
    {
        List<RelayMeasurement>? found = null;
        var offset = 0;

        while (offset < extensions.Length)
        {
            if (extensions.Length - offset < 3)
                return null;

            var tag = extensions[offset];
            var length = BinaryPrimitives.ReadUInt16BigEndian(extensions[(offset + 1)..]);
            offset += 3;

            if (extensions.Length - offset < length)
                return null;

            var content = extensions.Slice(offset, length);
            offset += length;

            if (tag != Tag)
                continue;

            // Deux blocs de mesures : on ne saurait lequel croire, et les deux
            // pairs pourraient ne pas choisir le même.
            if (found is not null || content.Length < 1)
                return null;

            var count = content[0];

            if (count > MaxMeasurements || content.Length != 1 + count * EntrySize)
                return null;

            found = new List<RelayMeasurement>(count);

            for (var i = 0; i < count; i++)
                found.Add(new RelayMeasurement(
                    BinaryPrimitives.ReadUInt64BigEndian(content[(1 + i * EntrySize)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(content[(9 + i * EntrySize)..])));
        }

        return found;
    }

    /// <summary>
    /// Ce qu'un pair en relais seul envoie à la place de ses RTT.
    /// </summary>
    /// <remarks>
    /// Ce mode existe pour cacher son adresse au pair. Des RTT vers plusieurs
    /// continents la laisseraient trianguler ; des zéros pour sa région et
    /// <see cref="Far"/> ailleurs ne lui apprennent que le continent.
    /// </remarks>
    public static IReadOnlyList<RelayMeasurement> Synthetic(IReadOnlyList<RelayPlace> eligible, IReadOnlyList<RelayMeasurement> measured)
    {
        var regionOf = eligible
            .GroupBy(place => place.Fingerprint)
            .ToDictionary(group => group.Key, group => group.First().Region);

        var nearest = measured
            .Where(m => m.RttMs != RelayMeasurement.Unreachable && regionOf.GetValueOrDefault(m.Service) is not null)
            .OrderBy(m => m.RttMs)
            .ThenBy(m => m.Service)
            .Select(m => regionOf[m.Service])
            .FirstOrDefault();

        return [.. measured.Select(m => new RelayMeasurement(
            m.Service,
            m.RttMs == RelayMeasurement.Unreachable ? RelayMeasurement.Unreachable
            : nearest is not null && regionOf.GetValueOrDefault(m.Service) == nearest ? (ushort)0
            : Far))];
    }
}
```

- [ ] **Step 5 : lancer les tests**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayMeasurementsTests|FullyQualifiedName~CandidateSet"`
Attendu : PASS.

- [ ] **Step 6 : commit**

```bash
git add Linkpearl/Core/Sync/RelayMeasurements.cs Linkpearl/Core/Sync/CandidateSet.cs Linkpearl.Core.Tests/Sync/RelayMeasurementsTests.cs
git commit -m "feat(relais): mesures de latence scellées après les adresses candidates"
```

---

### Task 8 : la décision symétrique (plugin)

**Files:**
- Create: `Linkpearl/Core/Sync/RelayChoice.cs`
- Test: `Linkpearl.Core.Tests/Sync/RelayChoiceTests.cs`

**Interfaces:**
- Consumes : `RelayMeasurement` (Tâche 7).
- Produces :
  - `public readonly record struct RelayDecision(ulong Service, int? WorstMs, int? MatchedWorstMs)`
  - `public static class RelayChoice` avec `const int MinimumGainMs = 20`, `const double MinimumGainRatio = 0.2`, `static RelayDecision Decide(IReadOnlyList<RelayMeasurement>? mine, IReadOnlyList<RelayMeasurement>? theirs, ulong matched)`

- [ ] **Step 1 : écrire les tests qui échouent**

`Linkpearl.Core.Tests/Sync/RelayChoiceTests.cs` :

```csharp
using Linkpearl.Core.Sync;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayChoiceTests
{
    private const ulong Swiss = 0x1111, America = 0x2222, Japan = 0x3333;

    private static RelayMeasurement M(ulong service, int rtt) => new(service, (ushort)rtt);

    [Fact]
    public void Deux_joueurs_americains_quittent_le_service_suisse()
    {
        var decision = RelayChoice.Decide([M(Swiss, 95), M(America, 12)], [M(Swiss, 98), M(America, 30)], Swiss);

        Assert.Equal(America, decision.Service);
        Assert.Equal(30, decision.WorstMs);
        Assert.Equal(98, decision.MatchedWorstMs);
    }

    [Fact]
    public void Sans_mesures_d_un_cote_on_garde_le_service_d_appariement()
    {
        Assert.Equal(Swiss, RelayChoice.Decide(null, [M(America, 1)], Swiss).Service);
        Assert.Equal(Swiss, RelayChoice.Decide([M(America, 1)], null, Swiss).Service);
    }

    [Fact]
    public void Une_intersection_vide_garde_le_service_d_appariement()
        => Assert.Equal(Swiss, RelayChoice.Decide([M(America, 10)], [M(Japan, 10)], Swiss).Service);

    [Fact]
    public void Un_service_injoignable_d_un_cote_n_est_pas_candidat()
        => Assert.Equal(Swiss, RelayChoice.Decide(
            [M(Swiss, 95), M(America, 12)], [M(Swiss, 98), M(America, RelayMeasurement.Unreachable)], Swiss).Service);

    [Theory]
    [InlineData(40, 25, false)]   // 15 ms de gain : sous le seuil absolu
    [InlineData(200, 175, false)] // 25 ms mais 12,5 % : sous le seuil relatif
    [InlineData(100, 79, true)]   // 21 ms et 21 % : on change
    public void L_hysteresis_garde_le_service_d_appariement_pour_un_petit_gain(int matchedRtt, int otherRtt, bool moves)
    {
        var decision = RelayChoice.Decide([M(Swiss, matchedRtt), M(America, otherRtt)], [M(Swiss, matchedRtt), M(America, otherRtt)], Swiss);

        Assert.Equal(moves ? America : Swiss, decision.Service);
    }

    [Fact]
    public void Un_service_d_appariement_non_mesure_laisse_le_meilleur_gagner()
        => Assert.Equal(Japan, RelayChoice.Decide([M(America, 80), M(Japan, 40)], [M(America, 80), M(Japan, 45)], Swiss).Service);

    [Fact]
    public void A_pire_egal_la_somme_puis_l_empreinte_departagent()
    {
        Assert.Equal(Japan, RelayChoice.Decide([M(America, 50), M(Japan, 10)], [M(America, 50), M(Japan, 50)], Swiss).Service);
        Assert.Equal(America, RelayChoice.Decide([M(Japan, 50), M(America, 50)], [M(Japan, 50), M(America, 50)], Swiss).Service);
    }

    [Fact]
    public void Le_relais_seul_se_resout_par_la_region()
    {
        // Le pair en relais seul envoie 0 pour l'Amérique et 1000 ailleurs.
        var decision = RelayChoice.Decide([M(Swiss, 1000), M(America, 0)], [M(Swiss, 98), M(America, 30)], Swiss);

        Assert.Equal(America, decision.Service);
    }

    [Fact]
    public void La_decision_est_la_meme_des_deux_cotes()
    {
        var random = new Random(20260927);
        ulong[] services = [Swiss, America, Japan, 0x4444, 0x5555];

        for (var run = 0; run < 2000; run++)
        {
            var mine = RandomMeasures(random, services);
            var theirs = RandomMeasures(random, services);
            var matched = services[random.Next(services.Length)];

            Assert.Equal(RelayChoice.Decide(mine, theirs, matched), RelayChoice.Decide(theirs, mine, matched));
        }
    }

    private static List<RelayMeasurement> RandomMeasures(Random random, ulong[] services)
        => [.. services
            .Where(_ => random.Next(4) > 0)
            .Select(service => M(service, random.Next(6) == 0 ? RelayMeasurement.Unreachable : random.Next(0, 300)))];
}
```

- [ ] **Step 2 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayChoiceTests"`
Attendu : échec de compilation.

- [ ] **Step 3 : implémenter**

```csharp
namespace Linkpearl.Core.Sync;

/// <summary>Le relais retenu, et de quoi dire pourquoi.</summary>
public readonly record struct RelayDecision(ulong Service, int? WorstMs, int? MatchedWorstMs);

/// <summary>
/// Le relais d'une paire, décidé à l'identique des deux côtés.
/// </summary>
/// <remarks>
/// Chaque côté a ses mesures et celles du pair : la décision n'en dépend que
/// par des opérations symétriques (max, somme, intersection), donc les deux
/// calculent le même service sans se consulter, comme pour le choix entre
/// perçage et relais.
///
/// L'hystérésis tient à ce qu'un relais ne se quitte que pour mieux : deux
/// services équivalents se disputeraient sinon au gré du bruit des mesures.
/// </remarks>
public static class RelayChoice
{
    public const int MinimumGainMs = 20;
    public const double MinimumGainRatio = 0.2;

    public static RelayDecision Decide(
        IReadOnlyList<RelayMeasurement>? mine, IReadOnlyList<RelayMeasurement>? theirs, ulong matched)
    {
        if (mine is null || theirs is null)
            return new RelayDecision(matched, null, null);

        var their = new Dictionary<ulong, ushort>();

        foreach (var measure in theirs)
            their.TryAdd(measure.Service, measure.RttMs);

        var seen = new HashSet<ulong>();
        var candidates = new List<(ulong Service, int Worst, int Total)>();

        foreach (var measure in mine)
        {
            if (seen.Add(measure.Service) is false || their.TryGetValue(measure.Service, out var other) is false)
                continue;

            if (measure.RttMs == RelayMeasurement.Unreachable || other == RelayMeasurement.Unreachable)
                continue;

            candidates.Add((measure.Service, Math.Max(measure.RttMs, other), measure.RttMs + other));
        }

        if (candidates.Count == 0)
            return new RelayDecision(matched, null, null);

        var best = candidates.OrderBy(c => c.Worst).ThenBy(c => c.Total).ThenBy(c => c.Service).First();
        var current = candidates.FindIndex(c => c.Service == matched);

        if (current < 0)
            return new RelayDecision(best.Service, best.Worst, null);

        var worst = candidates[current].Worst;
        var gain = worst - best.Worst;

        return gain >= MinimumGainMs && gain >= worst * MinimumGainRatio
            ? new RelayDecision(best.Service, best.Worst, worst)
            : new RelayDecision(matched, worst, worst);
    }
}
```

- [ ] **Step 4 : lancer les tests**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayChoiceTests"`
Attendu : PASS.

- [ ] **Step 5 : commit**

```bash
git add Linkpearl/Core/Sync/RelayChoice.cs Linkpearl.Core.Tests/Sync/RelayChoiceTests.cs
git commit -m "feat(relais): choix symétrique du relais, avec hystérésis"
```

---

### Task 9 : mesurer en tâche de fond (plugin)

**Files:**
- Create: `Linkpearl/Core/Sync/RelayLatencies.cs`
- Create: `Linkpearl/Core/Sync/RelayPing.cs`
- Test: `Linkpearl.Core.Tests/Sync/RelayLatenciesTests.cs`

**Interfaces:**
- Consumes : `RelayPlace` (Tâche 6), `RelayMeasurement`, `RelayMeasurements.MaxMeasurements` (Tâche 7), `PeerConnector.PublicHostAsync`, `RendezvousClient.ReflectAsync`.
- Produces :
  - `public sealed class RelayLatencies(IClock clock, Func<RelayPlace, TimeSpan, CancellationToken, Task<TimeSpan?>> ping)` avec `Freshness` (30 min), `RefusalMemory` (24 h), `AttemptTimeout` (1 s), `Attempts` (3), `MaxPerRound` (64), `Task RefreshAsync(IEnumerable<RelayPlace> targets, CancellationToken ct, TimeSpan? spacing = null)`, `IReadOnlyList<RelayMeasurement> MeasurementsFor(IReadOnlyList<RelayPlace> eligible)`, `void MarkRefused(RendezvousAddress at)`
  - `public static class RelayPing` avec `static Task<TimeSpan?> PingAsync(RelayPlace place, Func<IPAddress, bool> acceptOpen, TimeSpan timeout, CancellationToken ct)`

- [ ] **Step 1 : écrire les tests qui échouent**

`Linkpearl.Core.Tests/Sync/RelayLatenciesTests.cs` :

```csharp
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Sync;

public class RelayLatenciesTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    }

    private static RelayPlace Place(string host) => new(new RendezvousAddress(host, 47900), null, true);

    private readonly FakeClock _clock = new();
    private readonly Dictionary<string, Queue<TimeSpan?>> _answers = [];
    private readonly List<string> _pinged = [];

    private RelayLatencies Latencies() => new(_clock, (place, _, _) =>
    {
        _pinged.Add(place.At.Host);
        return Task.FromResult(_answers.TryGetValue(place.At.Host, out var queue) && queue.Count > 0 ? queue.Dequeue() : null);
    });

    [Fact]
    public async Task Le_meilleur_de_trois_essais_est_garde()
    {
        _answers["a"] = new([TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(12), null]);
        var latencies = Latencies();

        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);

        Assert.Equal((ushort)12, Assert.Single(latencies.MeasurementsFor([Place("a")])).RttMs);
        Assert.Equal(3, _pinged.Count);
    }

    [Fact]
    public async Task Trois_silences_font_un_service_injoignable()
    {
        var latencies = Latencies();

        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(RelayMeasurement.Unreachable, Assert.Single(latencies.MeasurementsFor([Place("a")])).RttMs);
    }

    [Fact]
    public async Task Une_mesure_fraiche_n_est_pas_refaite_et_une_vieille_disparait()
    {
        _answers["a"] = new([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);
        var latencies = Latencies();

        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);
        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);
        Assert.Equal(3, _pinged.Count);

        _clock.UtcNow += RelayLatencies.Freshness + TimeSpan.FromSeconds(1);
        Assert.Empty(latencies.MeasurementsFor([Place("a")]));
    }

    [Fact]
    public void Un_service_jamais_mesure_est_absent()
        => Assert.Empty(Latencies().MeasurementsFor([Place("a")]));

    [Fact]
    public async Task Un_refus_vaut_injoignable_pendant_vingt_quatre_heures()
    {
        _answers["a"] = new([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]);
        var latencies = Latencies();
        await latencies.RefreshAsync([Place("a")], CancellationToken.None, TimeSpan.Zero);

        latencies.MarkRefused(Place("a").At);
        Assert.Equal(RelayMeasurement.Unreachable, Assert.Single(latencies.MeasurementsFor([Place("a")])).RttMs);

        _clock.UtcNow += RelayLatencies.RefusalMemory + TimeSpan.FromSeconds(1);
        Assert.Empty(latencies.MeasurementsFor([Place("a")]));
    }

    [Fact]
    public async Task Un_cycle_ne_mesure_pas_plus_de_soixante_quatre_services()
    {
        var latencies = Latencies();

        await latencies.RefreshAsync(
            Enumerable.Range(0, 100).Select(i => Place($"s{i}")), CancellationToken.None, TimeSpan.Zero);

        Assert.Equal(RelayLatencies.MaxPerRound * RelayLatencies.Attempts, _pinged.Count);
    }

    [Fact]
    public async Task Au_plus_seize_mesures_partent_les_plus_proches_d_abord()
    {
        var places = Enumerable.Range(0, 20).Select(i => Place($"s{i}")).ToList();

        foreach (var (place, i) in places.Select((place, i) => (place, i)))
            _answers[place.At.Host] = new([TimeSpan.FromMilliseconds(100 - i), null, null]);

        var latencies = Latencies();
        await latencies.RefreshAsync(places, CancellationToken.None, TimeSpan.Zero);

        var sent = latencies.MeasurementsFor(places);

        Assert.Equal(RelayMeasurements.MaxMeasurements, sent.Count);
        Assert.Equal(sent.OrderBy(m => m.RttMs).Select(m => m.RttMs), sent.Select(m => m.RttMs));
        Assert.Equal((ushort)81, sent[0].RttMs);
    }
}
```

- [ ] **Step 2 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayLatenciesTests"`
Attendu : échec de compilation.

- [ ] **Step 3 : implémenter `RelayLatencies`**

```csharp
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>
/// Le RTT vers chaque service éligible, mesuré en tâche de fond.
/// </summary>
/// <remarks>
/// Hors du chemin de connexion : on n'attend jamais un ping pour joindre un
/// pair. Un service pas encore mesuré n'est simplement pas proposé, et la
/// décision retombe sur le service d'appariement.
///
/// Le RTT vers un serveur bouge peu, d'où une demi-heure de fraîcheur. Un
/// service qui a refusé de relayer est déclaré injoignable une journée : la
/// liste ne dit pas qui relaie, et c'est ainsi que les deux pairs apprennent
/// à l'éviter.
/// </remarks>
public sealed class RelayLatencies(IClock clock, Func<RelayPlace, TimeSpan, CancellationToken, Task<TimeSpan?>> ping)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RefusalMemory = TimeSpan.FromHours(24);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);
    public const int Attempts = 3;
    public const int MaxPerRound = 64;

    /// <summary>L'écart entre deux services mesurés, pour étaler les pings.</summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, (ushort RttMs, DateTimeOffset At)> _measured = [];
    private readonly Dictionary<ulong, DateTimeOffset> _refused = [];

    public async Task RefreshAsync(IEnumerable<RelayPlace> targets, CancellationToken ct, TimeSpan? spacing = null)
    {
        var pause = spacing ?? Spacing;
        var due = targets.DistinctBy(place => place.Fingerprint).Where(IsStale).Take(MaxPerRound).ToList();

        foreach (var place in due)
        {
            TimeSpan? best = null;

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                TimeSpan? rtt;

                try
                {
                    rtt = await ping(place, AttemptTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested is false)
                {
                    // Un nom qui ne résout pas, une pile réseau absente : ce
                    // service est injoignable d'ici, ce n'est pas une panne.
                    rtt = null;
                }

                if (rtt is { } value && (best is null || value < best))
                    best = value;
            }

            lock (_gate)
                _measured[place.Fingerprint] = (RelayMeasurement.Of(place.Fingerprint, best).RttMs, clock.UtcNow);

            if (pause > TimeSpan.Zero)
                await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<RelayMeasurement> MeasurementsFor(IReadOnlyList<RelayPlace> eligible)
    {
        var now = clock.UtcNow;
        var found = new List<RelayMeasurement>();

        lock (_gate)
        {
            foreach (var place in eligible.DistinctBy(place => place.Fingerprint))
            {
                if (_refused.TryGetValue(place.Fingerprint, out var refusedAt) && now - refusedAt < RefusalMemory)
                    found.Add(new RelayMeasurement(place.Fingerprint, RelayMeasurement.Unreachable));
                else if (_measured.TryGetValue(place.Fingerprint, out var measured) && now - measured.At < Freshness)
                    found.Add(new RelayMeasurement(place.Fingerprint, measured.RttMs));
            }
        }

        return [.. found.OrderBy(m => m.RttMs).ThenBy(m => m.Service).Take(RelayMeasurements.MaxMeasurements)];
    }

    public void MarkRefused(RendezvousAddress at)
    {
        lock (_gate)
            _refused[RelayPlace.FingerprintOf(at)] = clock.UtcNow;
    }

    private bool IsStale(RelayPlace place)
    {
        lock (_gate)
            return _measured.TryGetValue(place.Fingerprint, out var measured) is false
                || clock.UtcNow - measured.At >= Freshness;
    }
}
```

- [ ] **Step 4 : implémenter `RelayPing`**

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Core.Sync;

/// <summary>Un aller-retour UDP vers un service, par la réflexion qu'il sert déjà.</summary>
/// <remarks>
/// Aucun service n'a besoin d'être mis à jour pour être mesuré. Une socket
/// éphémère, et non celle du perçage : on veut un temps, pas une adresse.
/// </remarks>
public static class RelayPing
{
    public static async Task<TimeSpan?> PingAsync(
        RelayPlace place, Func<IPAddress, bool> acceptOpen, TimeSpan timeout, CancellationToken ct)
    {
        var address = await ResolveAsync(place, acceptOpen, ct).ConfigureAwait(false);

        if (address is null)
            return null;

        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address.AddressFamily is AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

        var watch = Stopwatch.StartNew();
        var reflected = await RendezvousClient.ReflectAsync(socket, new IPEndPoint(address, place.At.Port), timeout, ct).ConfigureAwait(false);

        return reflected is null ? null : watch.Elapsed;
    }

    private static async Task<IPAddress?> ResolveAsync(RelayPlace place, Func<IPAddress, bool> acceptOpen, CancellationToken ct)
    {
        // Un service du cercle ouvert n'a pas été choisi par l'utilisateur : on
        // ne vise qu'une adresse publique, comme pour l'annonce.
        if (place.Open)
            return await PeerConnector.PublicHostAsync(place.At.Host, Dns.GetHostAddressesAsync, acceptOpen, ct).ConfigureAwait(false) is { } host
                ? IPAddress.Parse(host)
                : null;

        if (IPAddress.TryParse(place.At.Host, out var literal))
            return literal;

        return (await Dns.GetHostAddressesAsync(place.At.Host, ct).ConfigureAwait(false)).FirstOrDefault();
    }
}
```

- [ ] **Step 5 : lancer les tests**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~RelayLatenciesTests"`
Attendu : PASS.

- [ ] **Step 6 : commit**

```bash
git add Linkpearl/Core/Sync/RelayLatencies.cs Linkpearl/Core/Sync/RelayPing.cs Linkpearl.Core.Tests/Sync/RelayLatenciesTests.cs
git commit -m "feat(relais): latences des services éligibles, mesurées en tâche de fond"
```

---

### Task 10 : `PeerConnector` choisit son relais et se replie (plugin)

**Files:**
- Modify: `Linkpearl/Core/Sync/PeerConnector.cs` (constructeur, `ConnectAsync`, `OpenRelayAsync`, `SealCandidates`)
- Test: `Linkpearl.Core.Tests/Sync/PeerConnectorTests.cs`

**Interfaces:**
- Consumes : `RelayPlacement.Eligible`, `IOpenCircle.RelayEntriesFor` (Tâche 6), `RelayMeasurements`, `CandidateSet.TryDecode(..., out consumed, ...)` (Tâche 7), `RelayChoice.Decide` (Tâche 8), `RelayLatencies` (Tâche 9).
- Produces :
  - `public sealed record RelayOpening(IPeerLink? Link, bool Refused);`
  - `public interface IRelayOpener { Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct); }`
  - `PeerConnector.RelayBudget` (20 s, devenu public), `PeerConnector.ChosenRelayBudget` (8 s), `PeerConnector.FallbackRelayBudget` (25 s)
  - `public static Task<(IPeerLink? Link, RendezvousAddress Via)> RelayWithFallbackAsync(IRelayOpener opener, RelayPlace chosen, RelayPlace matched, byte[] ticket, ILogSink log, Action<RendezvousAddress>? refused, CancellationToken ct)`
  - journal : `"Relais ouvert par {ServiceConsensus.Canonical(via)}."` à chaque relais réussi (le harnais le lit, Tâche 12)
  - constructeur : nouveau dernier paramètre `RelayLatencies? latencies = null`
  - `SealCandidates(ReadOnlySpan<byte> pairSecret, IReadOnlyList<IPEndPoint> candidates, IReadOnlyList<RelayMeasurement>? measurements = null)`

- [ ] **Step 1 : écrire les tests qui échouent**

Ajouter dans `PeerConnectorTests.cs` (en tête : `using System.Security.Cryptography;` et `using Linkpearl.Core.Transport;` si absents) :

```csharp
/// <summary>Un ouvreur de relais scripté : chaque service accepte, refuse ou se tait.</summary>
internal sealed class ScriptedRelays(Dictionary<string, string> behaviour) : IRelayOpener
{
    public List<(string Host, TimeSpan Budget)> Opened { get; } = [];

    public Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct)
    {
        Opened.Add((place.At.Host, budget));

        return Task.FromResult(behaviour.GetValueOrDefault(place.At.Host) switch
        {
            "accepte" => new RelayOpening(new FakeRelayLink(), false),
            "refuse" => new RelayOpening(null, true),
            _ => new RelayOpening(null, false),
        });
    }
}
```

Pour `FakeRelayLink`, réutiliser le faux `IPeerLink` déjà présent dans `Linkpearl.Core.Tests` (chercher `: IPeerLink` dans le projet de tests). S'il n'y en a pas, en écrire un dans ce fichier qui implémente chaque membre de `IPeerLink` (`Linkpearl/Core/Transport/IPeerLink.cs`) sans effet, avec `IsOpen => true`.

Puis les tests :

```csharp
    private static RelayPlace Relay(string host) => new(new RendezvousAddress(host, 47900), null, true);

    [Fact]
    public async Task Le_relais_choisi_sert_quand_il_accepte()
    {
        var relays = new ScriptedRelays(new() { ["proche.us"] = "accepte" });

        var (link, via) = await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), null, CancellationToken.None);

        Assert.NotNull(link);
        Assert.Equal("proche.us", via.Host);
        Assert.Equal([("proche.us", PeerConnector.ChosenRelayBudget)], relays.Opened);
    }

    [Fact]
    public async Task Un_refus_se_replie_sur_le_service_d_appariement_et_se_retient()
    {
        var relays = new ScriptedRelays(new() { ["proche.us"] = "refuse", ["suisse.ch"] = "accepte" });
        var refused = new List<RendezvousAddress>();

        var (link, via) = await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), refused.Add, CancellationToken.None);

        Assert.NotNull(link);
        Assert.Equal("suisse.ch", via.Host);
        Assert.Equal([("proche.us", PeerConnector.ChosenRelayBudget), ("suisse.ch", PeerConnector.FallbackRelayBudget)], relays.Opened);
        Assert.Equal("proche.us", Assert.Single(refused).Host);
    }

    [Fact]
    public async Task Un_silence_se_replie_sans_retenir_de_refus()
    {
        var relays = new ScriptedRelays(new() { ["suisse.ch"] = "accepte" });
        var refused = new List<RendezvousAddress>();

        var (link, _) = await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("proche.us"), Relay("suisse.ch"), new byte[16], new SilentLog(), refused.Add, CancellationToken.None);

        Assert.NotNull(link);
        Assert.Empty(refused);
    }

    [Fact]
    public async Task Quand_le_choix_est_le_service_d_appariement_un_seul_essai_suffit()
    {
        var relays = new ScriptedRelays(new() { ["suisse.ch"] = "accepte" });

        await PeerConnector.RelayWithFallbackAsync(
            relays, Relay("suisse.ch"), Relay("suisse.ch"), new byte[16], new SilentLog(), null, CancellationToken.None);

        Assert.Equal([("suisse.ch", PeerConnector.RelayBudget)], relays.Opened);
    }

    [Fact]
    public void Les_budgets_tiennent_dans_l_attente_du_service()
    {
        // Un côté qui échoue aussitôt attend au service d'appariement ; l'autre
        // l'y rejoint au bout du budget du relais choisi. Il faut que ce soit
        // avant que le service ne lâche la première demande.
        Assert.True(PeerConnector.ChosenRelayBudget < PeerConnector.FallbackRelayBudget);
        Assert.True(PeerConnector.FallbackRelayBudget < TimeSpan.FromSeconds(30));
        Assert.True(PeerConnector.ChosenRelayBudget + TimeSpan.FromSeconds(1) < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Les_mesures_scellees_se_relisent()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        RelayMeasurement[] measures = [new(7, 12)];

        var sealedBlock = PeerConnector.SealCandidates(secret, [], measures);

        Assert.True(PeerConnector.TryOpenCandidates(secret, sealedBlock, out var plain));
        Assert.True(CandidateSet.TryDecode(plain, out _, out var consumed, out _));
        Assert.Equal(measures, RelayMeasurements.TryRead(plain.AsSpan(consumed)));
    }
```

(`SilentLog` est déclaré `internal` dans `SyncEngineTests.cs`, même projet.)

- [ ] **Step 2 : lancer, ça doit échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~PeerConnectorTests"`
Attendu : échec de compilation.

- [ ] **Step 3 : implémenter**

En tête de `PeerConnector.cs`, après `ConnectionAttempt` :

```csharp
/// <summary>Ce qu'a donné une demande de relais : un lien, ou un refus explicite.</summary>
/// <remarks>Un silence n'est pas un refus : le pair a pu se replier ailleurs avant nous.</remarks>
public sealed record RelayOpening(IPeerLink? Link, bool Refused);

/// <summary>Ouvre un relais sur un service, dans un budget donné.</summary>
public interface IRelayOpener
{
    Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct);
}
```

Constructeur primaire :

```csharp
public sealed class PeerConnector(
    PeerLinkFactory links, RendezvousEndpoint rendezvous, IClock clock, ILogSink log,
    TimeSpan? announceBudget = null, IOpenCircle? circle = null, Func<IPAddress, bool>? acceptOpenAddress = null,
    RelayLatencies? latencies = null) : IPeerDialer, IRelayOpener
```

Rendre `RelayBudget` public (`public static readonly TimeSpan RelayBudget`) et ajouter à côté :

```csharp
    /// <summary>Attente au relais choisi, quand ce n'est pas le service d'appariement.</summary>
    /// <remarks>
    /// Courte : le service garde une demande trente secondes, et celui des deux
    /// pairs qui échoue vite attend déjà l'autre au service d'appariement.
    /// </remarks>
    public static readonly TimeSpan ChosenRelayBudget = TimeSpan.FromSeconds(8);

    /// <summary>Attente au service d'appariement après l'échec du relais choisi.</summary>
    public static readonly TimeSpan FallbackRelayBudget = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Relaie par le service choisi, et à défaut par celui qui a apparié.
    /// </summary>
    /// <remarks>
    /// Les deux côtés ont décidé le même service ; s'il échoue pour l'un, il
    /// échoue d'ordinaire pour l'autre, et les deux se retrouvent au service
    /// d'appariement, que tous deux ont déjà atteint.
    /// </remarks>
    public static async Task<(IPeerLink? Link, RendezvousAddress Via)> RelayWithFallbackAsync(
        IRelayOpener opener, RelayPlace chosen, RelayPlace matched, byte[] ticket, ILogSink log,
        Action<RendezvousAddress>? refused, CancellationToken ct)
    {
        if (chosen.Fingerprint == matched.Fingerprint)
            return Opened((await opener.OpenAsync(matched, ticket, RelayBudget, ct).ConfigureAwait(false)).Link, matched.At, log);

        var first = await opener.OpenAsync(chosen, ticket, ChosenRelayBudget, ct).ConfigureAwait(false);

        if (first.Link is not null)
            return Opened(first.Link, chosen.At, log);

        if (first.Refused)
            refused?.Invoke(chosen.At);

        log.Info($"Relais choisi {ServiceConsensus.Canonical(chosen.At)} {(first.Refused ? "refusé" : "sans réponse")}, "
                 + $"repli sur {ServiceConsensus.Canonical(matched.At)}.");

        return Opened((await opener.OpenAsync(matched, ticket, FallbackRelayBudget, ct).ConfigureAwait(false)).Link, matched.At, log);
    }

    private static (IPeerLink? Link, RendezvousAddress Via) Opened(IPeerLink? link, RendezvousAddress via, ILogSink log)
    {
        if (link is not null)
            log.Info($"Relais ouvert par {ServiceConsensus.Canonical(via)}.");

        return (link, via);
    }
```

Remplacer `OpenRelayAsync` par l'implémentation de `IRelayOpener` (garder son commentaire) :

```csharp
    public async Task<RelayOpening> OpenAsync(RelayPlace place, byte[] ticket, TimeSpan budget, CancellationToken ct)
    {
        var client = new RendezvousClient();

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(budget);

            // Un service du cercle ouvert ne se joint qu'à une adresse publique,
            // comme pour l'annonce ; l'ancrage garde la confiance qu'il a toujours eue.
            var host = place.Open
                ? await PublicHostAsync(place.At.Host, Dns.GetHostAddressesAsync, acceptOpenAddress ?? ServiceConsensus.IsPublicAddress, deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException($"{place.At.Host} ne mène à aucune adresse publique")
                : place.At.Host;

            await client.ConnectAsync(host, place.At.Port, deadline.Token).ConfigureAwait(false);

            // Les deux côtés arrivent ici après le même budget de perçage. Un
            // service d'avant le 24 septembre 2026 garait alors les deux
            // demandes sans les apparier, une fois sur deux au banc : quelques
            // centaines de millisecondes d'écart suffisent à l'éviter.
            await Task.Delay(Random.Shared.Next(0, 400), deadline.Token).ConfigureAwait(false);

            if (await client.OpenRelayAsync(ticket, deadline.Token).ConfigureAwait(false))
                return new RelayOpening(new RelayPeerLink(new RendezvousRelayPipe(client), new DnsEndPoint(place.At.Host, place.At.Port)), false);

            log.Info($"Relais refusé par {place.At.Host}.");
            await client.DisposeAsync().ConfigureAwait(false);
            return new RelayOpening(null, true);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            log.Info($"Relais par {place.At.Host} sans réponse : {e.Message}");
        }

        await client.DisposeAsync().ConfigureAwait(false);
        return new RelayOpening(null, false);
    }
```

`SealCandidates` :

```csharp
    public static byte[] SealCandidates(
        ReadOnlySpan<byte> pairSecret, IReadOnlyList<IPEndPoint> candidates, IReadOnlyList<RelayMeasurement>? measurements = null)
    {
        byte[] plain = measurements is null
            ? CandidateSet.Encode(candidates)
            : [.. CandidateSet.Encode(candidates), .. RelayMeasurements.Encode(measurements)];

        var nonce = RandomNumberGenerator.GetBytes(CryptoPrimitives.NonceLength);
        var sealedBody = CryptoPrimitives.Seal(CandidateKey(pairSecret), nonce, plain, CandidateKeyInfo);

        return [.. nonce, .. sealedBody];
    }
```

Dans `ConnectAsync` :

1. Remplacer `var sealedCandidates = SealCandidates(pair.PairSecret, candidates);` par :

```csharp
        // Les services où l'on pourrait relayer, et ce qu'on en mesure. Un pair
        // en relais seul ne livre que sa région : ses RTT vers plusieurs
        // continents laisseraient trianguler l'adresse que ce mode protège.
        var eligible = RelayPlacement.Eligible(pair.PairSecret, circle?.RelayEntriesFor(pair) ?? [], pair.Rendezvous);
        var measured = latencies?.MeasurementsFor(eligible);

        if (measured is not null && relayOnly)
            measured = RelayMeasurements.Synthetic(eligible, measured);

        var sealedCandidates = SealCandidates(pair.PairSecret, candidates, measured);
```

2. Remplacer le décodage des candidats par :

```csharp
        if (CandidateSet.TryDecode(plain, out var theirCandidates, out var consumed, out var why) is false)
            return new ConnectionAttempt(null, false, $"candidats refusés : {why}");

        var theirMeasures = RelayMeasurements.TryRead(plain.AsSpan(consumed));
```

3. Remplacer la fin (depuis `var ticket = RelayTicketFor(...)`) par :

```csharp
        var ticket = RelayTicketFor(pair.PairSecret, sealedCandidates, match.Value.Theirs);

        // Le relais ne sort jamais de nos éligibles : une empreinte que nous ne
        // connaissons pas ne peut venir que d'un pair qui ment, et l'on reste
        // alors au service d'appariement.
        var matchedPlace = eligible.FirstOrDefault(place => place.Fingerprint == RelayPlace.FingerprintOf(match.Value.At))
            ?? new RelayPlace(match.Value.At, null, viaOpen);
        var decision = RelayChoice.Decide(measured, theirMeasures, matchedPlace.Fingerprint);
        var chosenPlace = eligible.FirstOrDefault(place => place.Fingerprint == decision.Service) ?? matchedPlace;

        if (chosenPlace.Fingerprint != matchedPlace.Fingerprint)
            log.Info($"{pair.DisplayName} : relais par {ServiceConsensus.Canonical(chosenPlace.At)} ({decision.WorstMs} ms"
                     + (decision.MatchedWorstMs is { } before ? $" contre {before} ms au service d'appariement)." : ")."));

        var (relayed, via) = await RelayWithFallbackAsync(
            this, chosenPlace, matchedPlace, ticket, log, latencies is null ? null : latencies.MarkRefused, ct).ConfigureAwait(false);

        return relayed is null
            ? new ConnectionAttempt(null, false, "ni perçage ni relais : le service refuse peut-être de relayer")
            : new ConnectionAttempt(relayed, false, null, Via: via);
```

- [ ] **Step 4 : lancer toute la suite**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj`
Attendu : PASS.

- [ ] **Step 5 : commit**

```bash
git add Linkpearl/Core/Sync/PeerConnector.cs Linkpearl.Core.Tests/Sync/PeerConnectorTests.cs
git commit -m "feat(relais): relayer par le service le plus proche des deux pairs, repli sur l'appariement"
```

---

### Task 11 : câblage dans le plugin, liste v2 (plugin)

**Files:**
- Modify: `Linkpearl/Integration/ConsensusFetcher.cs:71-104`
- Create: `Linkpearl/Integration/RelayLatencyLoop.cs`
- Modify: `Linkpearl/Plugin.cs` (champs vers la ligne 116, construction vers 240, `Start` vers 428, `PeerConnector` vers 680, tick vers 920, `Dispose` vers 1728)

**Interfaces:**
- Consumes : `RendezvousClient.QueryConsensusV2Async` (Tâche 2), `RelayLatencies`, `RelayPing` (Tâche 9), `RelayPlacement.Eligible`, `OpenCircle.RelayEntriesFor` (Tâche 6), constructeur de `PeerConnector` (Tâche 10).
- Produces : `public sealed class RelayLatencyLoop(RelayLatencies latencies, IPluginLog log) : IDisposable` avec `Start()`, `Offer(IReadOnlyList<RelayPlace> targets)`, `static readonly TimeSpan TargetsEvery`.

- [ ] **Step 1 : `ConsensusFetcher` demande la v2, puis la v1**

Dans `FetchAsync`, remplacer le bloc qui crée le `RendezvousClient`, se connecte et appelle `QueryConsensusAsync`, jusqu'au `return;` du document absent inclus, par :

```csharp
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Patience);

            // La v2 d'abord, qui porte les régions. Une autorité d'avant répond
            // « trame inattendue » et ferme : on redemande alors la v1, sur une
            // connexion neuve.
            var (document, failure) = await QueryAsync(v2: true, deadline.Token).ConfigureAwait(false);

            if (document is null)
            {
                log.Information($"Liste signée v2 indisponible ({failure}), repli sur la v1.");
                (document, failure) = await QueryAsync(v2: false, deadline.Token).ConfigureAwait(false);
            }

            if (document is null)
            {
                log.Information($"Liste signée indisponible : {failure}");
                return;
            }
```

Le reste (`circle.Offer` puis l'écriture du fichier) ne change pas : `TryVerify` reconnaît seul la v2. Ajouter :

```csharp
    private static async Task<(byte[]? Document, string? Failure)> QueryAsync(bool v2, CancellationToken ct)
    {
        await using var client = new RendezvousClient();
        await client.ConnectAsync(RendezvousList.Authority.Host, RendezvousList.Authority.Port, ct).ConfigureAwait(false);

        return v2
            ? await client.QueryConsensusV2Async(ct).ConfigureAwait(false)
            : await client.QueryConsensusAsync(ct).ConfigureAwait(false);
    }
```

- [ ] **Step 2 : `RelayLatencyLoop`**

```csharp
using Dalamud.Plugin.Services;
using Linkpearl.Core.Sync;

namespace Linkpearl.Integration;

/// <summary>
/// Fait tourner les mesures de latence hors du thread du jeu.
/// </summary>
/// <remarks>
/// Le carnet n'est lu que sur le thread du jeu : c'est lui qui calcule les
/// services à mesurer et les confie ici, ce qui réveille la boucle.
/// </remarks>
public sealed class RelayLatencyLoop(RelayLatencies latencies, IPluginLog log) : IDisposable
{
    /// <summary>Tous les combien le thread du jeu recalcule les services à mesurer.</summary>
    public static readonly TimeSpan TargetsEvery = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private IReadOnlyList<RelayPlace> _targets = [];

    public void Start() => _ = Task.Run(() => LoopAsync(_life.Token));

    public void Offer(IReadOnlyList<RelayPlace> targets)
    {
        Volatile.Write(ref _targets, targets);

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Déjà réveillée : elle lira la liste la plus récente.
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            try
            {
                await latencies.RefreshAsync(Volatile.Read(ref _targets), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (ct.IsCancellationRequested is false)
            {
                log.Information($"Mesure des relais interrompue : {e.Message}");
            }

            try
            {
                await _wake.WaitAsync(TargetsEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
        _wake.Dispose();
    }
}
```

- [ ] **Step 3 : câbler dans `Plugin.cs`**

Champs, à côté de `_consensusFetcher` :

```csharp
    private readonly RelayLatencies _relayLatencies;
    private readonly RelayLatencyLoop _relayLoop;
    private DateTimeOffset _lastRelayTargets = DateTimeOffset.MinValue;
```

Construction, après `_consensusFetcher = ...` :

```csharp
        _relayLatencies = new RelayLatencies(
            clock, (place, timeout, ct) => RelayPing.PingAsync(place, ServiceConsensus.IsPublicAddress, timeout, ct));
        _relayLoop = new RelayLatencyLoop(_relayLatencies, Log);
```

Démarrage, après `_consensusFetcher.Start();` : `_relayLoop.Start();`

`PeerConnector`, dans `StartEngineIfReady` : ajouter `latencies: _relayLatencies` après `circle: _openCircle`.

Tick, après le bloc qui se termine par `_lastOpenCircle = openCircle;` :

```csharp
                        // Les services où chaque paire pourrait relayer, recalculés
                        // ici parce que le carnet ne se lit que sur ce thread.
                        if (_clock.UtcNow - _lastRelayTargets >= RelayLatencyLoop.TargetsEvery)
                        {
                            _lastRelayTargets = _clock.UtcNow;
                            _relayLoop.Offer([.. _pairing.Book.Listed
                                .SelectMany(pair => RelayPlacement.Eligible(pair.PairSecret, _openCircle.RelayEntriesFor(pair), pair.Rendezvous))
                                .DistinctBy(place => place.Fingerprint)]);
                        }
```

Libération, après `_consensusFetcher.Dispose();` : `_relayLoop.Dispose();`

(Le champ d'horloge s'appelle `_clock` dans `Plugin.cs` (ligne 684) ; dans le constructeur, employer la variable locale `clock` passée à `OpenCircle` ligne 240.)

- [ ] **Step 4 : build et tests**

Run : `dotnet build Linkpearl/Linkpearl.csproj -c Release && dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj`
Attendu : build sans warning, PASS.

- [ ] **Step 5 : commit**

```bash
git add Linkpearl/Integration/ConsensusFetcher.cs Linkpearl/Integration/RelayLatencyLoop.cs Linkpearl/Plugin.cs
git commit -m "feat(relais): liste signée v2 et mesures de latence branchées dans le plugin"
```

---

### Task 12 : scénario de bout en bout et documentation (plugin)

**Files:**
- Modify: `Linkpearl.Harness/OpenCircleRun.cs`
- Modify: `docs/protocol.md` (après le passage sur le relais seul, vers la ligne 743)

**Interfaces:**
- Consumes : tout ce qui précède, en particulier le journal `"Relais ouvert par {hôte:port}."` (Tâche 10).

- [ ] **Step 1 : compter les relais par service dans `CountingLog`**

Dans `CountingLog`, ajouter :

```csharp
    private readonly List<string> _relays = [];

    /// <summary>Les services par lesquels un relais s'est ouvert, en « hôte:port ».</summary>
    public IReadOnlyList<string> Relays
    {
        get
        {
            lock (_relays)
                return [.. _relays];
        }
    }
```

et dans `Info`, avant `_inner.Info(message);` :

```csharp
        const string opened = "Relais ouvert par ";

        if (message.StartsWith(opened, StringComparison.Ordinal))
            lock (_relays)
                _relays.Add(message[opened.Length..].TrimEnd('.'));
```

- [ ] **Step 2 : ajouter le scénario « relais le plus proche »**

Dans `OpenCircleRun.ExecuteAsync`, après le scénario « repli sur l'ancrage » :

```csharp
        ok &= await ScenarioAsync(
            "relais le plus proche", SignWithRegions(key, settings.Open), key, settings, expectOpen: true, ct,
            nearestRelay: settings.Open[0]).ConfigureAwait(false);
```

Ajouter la liste v2 du harnais :

```csharp
    /// <summary>
    /// Une liste v2 : les deux premiers services en Europe, le troisième en
    /// Amérique. L'Europe compte deux familles, donc un tirage régional.
    /// </summary>
    private static byte[] SignWithRegions(ECDsa key, IReadOnlyList<RendezvousAddress> services)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[] regions = ["EU", "EU", "NA"];
        var entries = services
            .Select((at, i) => new ConsensusEntry(
                ServiceConsensus.Canonical(at), $"ouvert {i + 1}", [.. Enumerable.Repeat((byte)(i + 1), 8)], regions[i % regions.Length]))
            .ToList();

        return ServiceConsensus.SignV2(
            new ServiceConsensus(1, issued, issued + (long)ServiceConsensus.Lifetime.TotalSeconds, entries), key);
    }
```

Modifier `ScenarioAsync` :

1. Nouveau dernier paramètre : `RendezvousAddress? nearestRelay = null`.

2. À la construction des carnets, **seul celui d'Alice** met Bob en relais seul quand `nearestRelay` est donné. Alice n'envoie alors aucune adresse, les deux côtés passent au relais, et Alice n'envoie que des RTT synthétiques : `0` pour l'Europe, où le service le plus proche est `nearestRelay`. Bob envoie de vrais RTT. Mettre les deux en relais seul donnerait `0` à deux services européens et laisserait l'empreinte trancher au hasard.

```csharp
        var bobForAlice = FederationRun.Pair(bobId, bobPublic, pairSecret, "Bob", anchor);
        aliceBook.Load([nearestRelay is null ? bobForAlice : bobForAlice with { Policy = ConnectionPolicy.RelayOnly }]);
```

(à la place de l'actuel `aliceBook.Load([FederationRun.Pair(bobId, bobPublic, pairSecret, "Bob", anchor)]);`).

3. Après la création des deux `OpenCircle`, préparer des latences dont le ping ajoute 200 ms à tout service autre que `nearestRelay`, et les mesurer avant de lancer les moteurs :

```csharp
        RelayLatencies? Latencies() => nearestRelay is not { } near ? null : new RelayLatencies(clock, async (place, timeout, token) =>
        {
            var rtt = await RelayPing.PingAsync(place, _ => true, timeout, token).ConfigureAwait(false);
            return rtt is null ? null : place.At == near ? rtt : rtt + TimeSpan.FromMilliseconds(200);
        });

        var aliceLatencies = Latencies();
        var bobLatencies = Latencies();
        var places = RelayPlacement.Eligible(pairSecret, aliceCircle.RelayEntriesFor(aliceBook.Listed[0]), anchor);

        if (aliceLatencies is not null)
            await aliceLatencies.RefreshAsync(places, ct, TimeSpan.Zero).ConfigureAwait(false);

        if (bobLatencies is not null)
            await bobLatencies.RefreshAsync(places, ct, TimeSpan.Zero).ConfigureAwait(false);
```

4. Passer `latencies: aliceLatencies` et `latencies: bobLatencies` aux deux `PeerConnector`.

5. Remplacer le calcul de `good` et la ligne d'affichage par :

```csharp
        var nearestUsed = nearestRelay is not { } wanted
            || aliceLog.Relays.Concat(bobLog.Relays).Any(relay => relay == ServiceConsensus.Canonical(wanted));
        var good = applied && viaOpen == expectOpen && nearestUsed;

        Console.WriteLine($"   {(applied ? "apparence posée" : "aucun appariement")}, "
                        + $"{(viaOpen ? "par le cercle ouvert" : "par l'ancrage")}"
                        + (nearestRelay is null ? "" : nearestUsed ? ", relais par le service le plus proche" : ", relais ailleurs")
                        + $", attendu : posée {(expectOpen ? "par le cercle ouvert" : "par l'ancrage")} → {(good ? "conforme" : "ÉCHEC")}");
```

(ajouter les `using Linkpearl.Core.Identity;` et `using Linkpearl.Core.Sync;` s'ils manquent ; `ConnectionPolicy` vit dans `Linkpearl.Core.Identity`.)

- [ ] **Step 3 : lancer le harnais**

Lancer trois services ouverts (47911 à 47913), un ancrage (47914) et une autorité (47915) en local, comme pour le scénario existant (aide de `Linkpearl.Harness/Program.cs`, commande `open-circle`), puis :

```bash
dotnet run --project Linkpearl.Harness -c Release -- open-circle --cle-autorite <clé affichée par l'autorité>
```

Attendu : les trois scénarios passent, le troisième affiche « relais par le service le plus proche », et la dernière ligne vaut `TOUT EST PASSÉ`.

- [ ] **Step 4 : documenter le protocole**

Dans `docs/protocol.md`, après le paragraphe sur le relais seul, ajouter :

```markdown
### Choix du relais

Le relais ne passe plus forcément par le service qui a apparié. Chaque pair
glisse, à la fin de son bloc de candidats scellé, ses RTT vers les services
où la paire a le droit de relayer : les deux du placement, le meilleur score
de chaque région qui compte au moins deux familles, et l'ancrage.

    extension = etiquette(1) || longueur(2) || contenu
    0x01      : nombre(1) || (service(8) || rtt_ms(2))*

`service` vaut les huit premiers octets de SHA-256 de l'adresse canonique ;
`0xFFFF` veut dire injoignable. Un pair en relais seul envoie `0` pour sa
région la plus proche et `1000` ailleurs, jamais de vrai RTT.

Les deux côtés retiennent, parmi les services mesurés des deux, celui qui
minimise `max(rttA, rttB)`, puis `rttA + rttB`, puis l'empreinte. Le service
d'appariement n'est quitté que pour un gain d'au moins 20 ms et 20 %. Le
relais choisi reçoit 8 secondes ; à défaut, le service d'appariement 25.
Un pair sans mesures (client d'avant) garde le service d'appariement.

La région d'un service vient de l'autorité, par GeoIP, dans la liste signée
v2 (`ConsensusV2Query`, 0x1B), servie à côté de la v1.
```

- [ ] **Step 5 : commit**

```bash
git add Linkpearl.Harness/OpenCircleRun.cs docs/protocol.md
git commit -m "test(relais): scénario du relais le plus proche, et protocole documenté"
```

---

### Task 13 : vérification finale et publication (les deux dépôts)

Les étapes 2 et 3 ne se lancent qu'avec l'accord explicite de l'utilisateur : elles publient ou déploient.

- [ ] **Step 1 : tout vérifier**

```bash
cd ~/Projects/linkpearl-sync/plugin
dotnet build Linkpearl/Linkpearl.csproj -c Release && dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj
cd ~/Projects/linkpearl-sync/rendezvous
dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release && dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj
cd ~/Projects/linkpearl-sync
for f in RendezvousWire ServiceConsensus RendezvousAddress RendezvousTicket; do diff plugin/Linkpearl/Core/Transport/Rendezvous/$f.cs rendezvous/Protocol/Core/Transport/Rendezvous/$f.cs; done
diff plugin/Linkpearl.Core.Tests/Fixtures/rendezvous-vectors.json rendezvous/Protocol/rendezvous-vectors.json
diff plugin/Linkpearl.Core.Tests/Rendezvous/RendezvousVectorTests.cs rendezvous/Linkpearl.Rendezvous.Tests/RendezvousVectorTests.cs
grep -rnP '\x{2014}' plugin/Linkpearl plugin/Linkpearl.Harness plugin/docs/protocol.md rendezvous/Linkpearl.Rendezvous rendezvous/README.md rendezvous/CLAUDE.md | grep -v '/obj/\|/bin/'
git -C plugin log --format=%B -15 | grep -i 'co-authored\|claude-session'
git -C rendezvous log --format=%B -5 | grep -i 'co-authored\|claude-session'
```

Attendu : builds sans warning, tests PASS, `diff` et `grep` muets.

- [ ] **Step 2 : release du service (après accord)**

Tag annoté `vX.Y.Z` sur `main` du service, poussé ; puis `deploy.sh` vers la production (commande du `CLAUDE.md` du service). Vérifier ensuite dans la console (`ssh -L 47901:127.0.0.1:47901`) que chaque service listé affiche sa région, et dans `journalctl -u lprdv` la ligne « Base GeoIP renouvelée ».

- [ ] **Step 3 : release du plugin (après accord)**

Selon la procédure habituelle du dépôt du plugin, une fois l'autorité de production à jour.
