# Avis de pause : plan d'implémentation

> **Pour les agents :** sous-skill requis, superpowers:subagent-driven-development ou superpowers:executing-plans, pour exécuter ce plan tâche par tâche. Les étapes se cochent (`- [ ]`).

**Objectif :** quand on met un pair en pause pendant une session, son plugin le sait et l'affiche, au lieu de nous croire absent.

**Architecture :** un message `Pause` (0x11) sans charge utile, envoyé par le moteur juste avant de fermer la session d'un pair qu'on vient de mettre en pause. Le moteur qui le reçoit marque le pair `PausedByPeer` dans son carnet, le fait enregistrer, et l'efface à la session suivante. `PeerPhases.Of` en tire une phase `PausedByPeer`, que la page Pairs affiche.

**Technologies :** C# (.NET 10), xunit 2.9 pour le noyau, Dalamud.NET.Sdk 15 pour le plugin.

**Spec :** `docs/superpowers/specs/2026-09-26-avis-de-pause-design.md`

## Contraintes globales

- `Linkpearl/Core/` ne référence jamais Dalamud (`ArchitectureTests`).
- `dotnet build Linkpearl/Linkpearl.csproj -c Release` passe **sans warning** à chaque commit.
- Commentaires en français, qui disent le *pourquoi*. Jamais de tiret cadratin (U+2014), ni dans le code, ni dans les commentaires, ni dans les commits.
- Commits en Conventional Commits, sujet en français. **Jamais** de ligne `Co-Authored-By:` ni `Claude-Session:`, ni d'URL de session, ni de mention « Generated with ».
- Un octet de protocole est une constante de `MessageKind`, jamais une valeur d'énumération.
- L'avis ne part **que** sur une session ouverte au moment de la pause ; bloquer n'envoie rien.
- Chaque tâche qui touche au plugin se termine par `./scripts/deploy-plugin-dev.sh`, lancé sans demander.

## Points de vigilance

Ce que la spec implique sans qu'aucun test de tâche ne l'exerce, du plus probable au moins probable :

1. **La trame perdue à la fermeture** : sur LiteNetLib, une déconnexion juste après l'envoi peut jeter une trame fiable encore en file. Attendu : l'avis arrive dans le cas courant. La tâche 2 attend que la file du canal de contrôle se vide, une seconde au plus, avant de fermer ; vérifié en jeu en tâche 4.
2. **Un carnet d'avant ce champ** : il doit se relire, avec `PausedByPeer` faux. Paramètre positionnel à valeur par défaut dans `PairBookStore` (tâche 4), vérifié en jeu en rechargeant le plugin.
3. **Un membre de groupe** : on ne met pas en pause un membre de groupe, et un `Pause` reçu sur une session de groupe est ignoré (tâche 2, garde `runtime.Pair.Group is null`).
4. **Pause puis reprise avant le tic suivant** : aucun avis ne part si le pair est redevenu actif avant que le moteur ne referme ; c'est le comportement voulu, rien ne change chez l'autre.
5. **Les deux se mettent en pause l'un l'autre** : chacun garde la marque de l'autre ; elle s'efface à la prochaine session, qui n'arrive qu'une fois les deux repris.

---

### Tâche 1 : la phase `PausedByPeer`

**Fichiers :**
- Modifier : `Linkpearl/Core/Sync/PeerPhase.cs`
- Modifier : `Linkpearl.Core.Tests/Sync/PeerPhaseTests.cs`
- Modifier : `Linkpearl/Core/Sync/SyncEngine.cs` (l'appel de `PeerPhases.Of` dans `Statuses`, argument `false` provisoire)

**Interfaces :**
- Produit : `PeerPhase.PausedByPeer`, et `PeerPhases.Of(bool dialing, bool hasSession, DateTimeOffset now, DateTimeOffset nextAttempt, string? lastFailure, bool wasAbsent, bool pausedByPeer, PeerView view, bool applied)`.

- [ ] **Étape 1 : les tests qui échouent**

Dans `PeerPhaseTests.cs`, les deux aides deviennent :

```csharp
    private static PeerPhase Offline(bool dialing = false, TimeSpan? wait = null, string? failure = null,
                                     bool wasAbsent = false, bool pausedByPeer = false)
        => PeerPhases.Of(dialing, hasSession: false, Now, Now + (wait ?? TimeSpan.Zero), failure, wasAbsent,
                         pausedByPeer, Empty, applied: false);

    private static PeerPhase Online(PeerView view, bool applied = false, bool pausedByPeer = false)
        => PeerPhases.Of(dialing: false, hasSession: true, Now, Now, null, wasAbsent: false, pausedByPeer, view,
                         applied);
```

Et, à la fin de la classe :

```csharp
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, "relais injoignable")]
    public void Un_pair_qui_nous_a_mis_en_pause_le_reste_sans_session(bool dialing, bool wasAbsent, string? failure)
    {
        // Pendant les essais qui suivent la pause, il sera tour à tour
        // recherché, absent ou en échec : la ligne doit dire la pause.
        Assert.Equal(PeerPhase.PausedByPeer,
                     Offline(dialing, TimeSpan.FromSeconds(20), failure, wasAbsent, pausedByPeer: true));
    }

    [Fact]
    public void Une_session_rouverte_l_emporte_sur_la_pause()
    {
        Assert.Equal(PeerPhase.AwaitingAppearance, Online(Empty, pausedByPeer: true));
    }
```

- [ ] **Étape 2 : les voir échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter PeerPhaseTests`
Attendu : échec de compilation, `PausedByPeer` inconnu et `Of` sans le paramètre `pausedByPeer`.

- [ ] **Étape 3 : la phase**

Dans `PeerPhase.cs`, ajouter après `Failing` :

```csharp

    /// <summary>Il nous a mis en pause : il reviendra de lui-même en nous reprenant.</summary>
    PausedByPeer,
```

Dans `PeerPhases.Of`, ajouter la documentation du paramètre après celle de `wasAbsent` :

```csharp
    /// <param name="pausedByPeer">Il nous a envoyé un avis de pause, qu'aucune session n'a encore effacé.</param>
```

La signature devient :

```csharp
    public static PeerPhase Of(bool dialing, bool hasSession, DateTimeOffset now, DateTimeOffset nextAttempt,
                               string? lastFailure, bool wasAbsent, bool pausedByPeer, PeerView view, bool applied)
```

Et le début du bloc sans session devient :

```csharp
        if (hasSession is false)
        {
            // La pause avant tout : pendant les essais qui la suivent, il est
            // tour à tour recherché, absent ou en échec, et c'est elle seule
            // qui dit pourquoi.
            if (pausedByPeer)
                return PeerPhase.PausedByPeer;

            // Absent jusqu'à preuve du contraire, même pendant l'essai suivant :
```

(la suite du bloc ne change pas).

Dans `SyncEngine.Statuses`, l'appel devient provisoirement :

```csharp
            PeerPhases.Of(
                entry.Value.Dial is not null, entry.Value.Session is not null, _clock.UtcNow,
                entry.Value.NextAttempt, entry.Value.LastFailure, entry.Value.WasAbsent, false,
                entry.Value.Exchange?.View ?? EmptyView, entry.Value.AppliedOn is not null)))
```

Le `false` est remplacé par la marque du carnet en tâche 2.

- [ ] **Étape 4 : les voir passer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter PeerPhaseTests`
Attendu : PASS, 14 tests.

- [ ] **Étape 5 : commit**

```bash
git add Linkpearl/Core/Sync/PeerPhase.cs Linkpearl/Core/Sync/SyncEngine.cs Linkpearl.Core.Tests/Sync/PeerPhaseTests.cs
git commit -m "feat(sync): une phase pour un pair qui nous a mis en pause"
```

---

### Tâche 2 : l'avis dans le carnet et dans le moteur

**Fichiers :**
- Modifier : `Linkpearl/Core/Identity/PairBook.cs` (`PairRecord.PausedByPeer`, `PairBook.SetPausedByPeer`)
- Modifier : `Linkpearl/Core/Protocol/MessageKind.cs` (`Pause`)
- Modifier : `Linkpearl/Core/Sync/SyncEngine.cs` (envoi, réception, effacement, événement)
- Créer : `Linkpearl.Core.Tests/Identity/PairBookPauseTests.cs`
- Modifier : `Linkpearl.Core.Tests/Sync/SyncEngineTests.cs`

**Interfaces :**
- Consomme : `PeerPhases.Of(..., bool pausedByPeer, ...)` (tâche 1).
- Produit : `PairRecord.PausedByPeer` (`bool`, `init`), `PairBook.SetPausedByPeer(PeerId id, bool paused)`, `MessageKind.Pause = 0x11`, `event Action? SyncEngine.BookChanged`.

- [ ] **Étape 1 : le test du carnet qui échoue**

`Linkpearl.Core.Tests/Identity/PairBookPauseTests.cs` :

```csharp
using Linkpearl.Core.Identity;
using Linkpearl.Core.Tests.Sync;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Identity;

/// <summary>La marque d'un pair qui nous a mis en pause.</summary>
public class PairBookPauseTests
{
    private static readonly PeerId Them = PeerId.FromBytes(Enumerable.Repeat((byte)7, 16).ToArray());

    private static PairBook Book()
    {
        var book = new PairBook(new MovableClock());

        book.Load([new PairRecord
        {
            Id = Them,
            PairSecret = new byte[32],
            DisplayName = "Pair",
            Rendezvous = [new RendezvousAddress("rdv.exemple.ch", 47900)],
            Trust = PairTrust.Accepted,
            PairedAt = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero),
        }]);

        return book;
    }

    [Fact]
    public void La_marque_se_pose_et_s_efface()
    {
        var book = Book();

        book.SetPausedByPeer(Them, true);
        Assert.True(book.Find(Them)!.PausedByPeer);

        book.SetPausedByPeer(Them, false);
        Assert.False(book.Find(Them)!.PausedByPeer);
    }

    [Fact]
    public void Un_pair_qui_nous_a_mis_en_pause_reste_actif()
    {
        // Il faut continuer à le chercher : c'est ainsi que sa reprise se voit.
        var book = Book();

        book.SetPausedByPeer(Them, true);

        Assert.Contains(book.Active, pair => pair.Id == Them);
    }
}
```

- [ ] **Étape 2 : les tests du moteur qui échouent**

Dans `SyncEngineTests.cs`, juste avant `[Fact] public async Task Une_session_qui_tombe_se_rejoint_sans_attendre()` :

```csharp
    [Fact]
    public async Task Une_pause_se_dit_et_la_reprise_l_efface()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        var saved = 0;
        world.Bob.BookChanged += () => saved++;

        world.AliceBook.SetPaused(world.BobId, true);

        Assert.True(
            await world.SettleAsync(() => world.BobStatus().Phase is PeerPhase.PausedByPeer, [], sees),
            "la pause n'a jamais été dite : " + world.Describe());

        Assert.True(world.BobBook.Find(world.AliceId)!.PausedByPeer);
        Assert.True(saved > 0, "le carnet marqué n'a pas été donné à enregistrer");

        world.AliceBook.SetPaused(world.BobId, false);

        Assert.True(
            await world.SettleAsync(() => world.BobBook.Find(world.AliceId)!.PausedByPeer is false, [], sees),
            "la reprise n'a pas effacé la marque : " + world.Describe());

        Assert.NotEqual(PeerPhase.PausedByPeer, world.BobStatus().Phase);
    }

    [Fact]
    public async Task Sans_session_ouverte_la_pause_ne_dit_rien()
    {
        // Alice met Bob en pause avant qu'ils ne se soient jamais joints : rien
        // ne doit l'obliger à le chercher pour le lui dire.
        await using var world = await TwoEnginesAsync();

        world.AliceBook.SetPaused(world.BobId, true);

        for (var i = 0; i < 40; i++)
            await world.TickAsync([], []);

        Assert.False(world.BobBook.Find(world.AliceId)!.PausedByPeer);
    }

    [Fact]
    public async Task Un_blocage_ne_se_dit_pas()
    {
        await using var world = await TwoEnginesAsync();

        IReadOnlyList<VisiblePlayer> sees = [new VisiblePlayer(new GameObjectRef(4, 100), AlicePrint)];

        Assert.True(
            await world.SettleAsync(() => world.BobApplicator.Applied.Count > 0, [], sees),
            "l'apparence n'a jamais été posée : " + world.Describe());

        world.AliceBook.Block(world.BobId);

        for (var i = 0; i < 40; i++)
            await world.TickAsync([], sees);

        Assert.False(world.BobBook.Find(world.AliceId)!.PausedByPeer);
    }
```

- [ ] **Étape 3 : les voir échouer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~PairBookPauseTests|FullyQualifiedName~SyncEngineTests"`
Attendu : échec de compilation, `PausedByPeer`, `SetPausedByPeer` et `BookChanged` inconnus.

- [ ] **Étape 4 : le carnet**

Dans `PairRecord`, après `public bool Paused { get; init; }` :

```csharp

    /// <summary>
    /// Ce pair nous a mis en pause, et aucune session ne s'est rouverte depuis.
    /// </summary>
    /// <remarks>
    /// Sans elle, sa pause se lirait comme un départ du jeu. Enregistrée pour
    /// survivre à un rechargement ; la reprise se voit à la session suivante,
    /// qui l'efface.
    /// </remarks>
    public bool PausedByPeer { get; init; }
```

Dans `PairBook`, après `SetPaused` :

```csharp

    public void SetPausedByPeer(PeerId id, bool paused)
        => Update(id, record => record with { PausedByPeer = paused });
```

- [ ] **Étape 5 : le type de message**

Dans `MessageKind`, après `GroupPolicy` :

```csharp

    /// <summary>
    /// L'expéditeur vient de nous mettre en pause, et ferme la session.
    /// </summary>
    /// <remarks>
    /// Sans charge utile, comme <see cref="Unpair"/>. Envoyé seulement sur une
    /// session déjà ouverte : une pause ne fait jamais chercher le pair pour la
    /// lui dire. Un client qui ne le connaît pas l'ignore.
    /// </remarks>
    public const byte Pause = 0x11;
```

- [ ] **Étape 6 : l'événement et la marque d'exécution**

Dans `SyncEngine`, après `public event Action<PairRecord>? RevocationDelivered;` :

```csharp

    /// <summary>Le moteur a changé le carnet de lui-même : l'hôte l'enregistre.</summary>
    /// <remarks>Levé depuis le tic, sur le fil du moteur, comme <see cref="PairEnded"/>.</remarks>
    public event Action? BookChanged;
```

Dans la classe `Runtime`, après `public volatile bool EndedByPeer;` :

```csharp

        /// <summary>Un avis de pause est arrivé, que le tic suivant reporte au carnet.</summary>
        public volatile bool PausedByPeerNotice;
```

- [ ] **Étape 7 : la réception**

Dans `PumpAsync`, juste après le bloc `if (message.Kind == MessageKind.Unpair) { ... }` :

```csharp

                // Ramassé au tic suivant, comme l'avis de retrait. Ignoré d'un
                // membre de groupe : on ne met pas en pause un membre, on le
                // bloque, et un blocage ne se dit pas.
                if (message.Kind == MessageKind.Pause)
                {
                    if (runtime.Pair.Group is null)
                        runtime.PausedByPeerNotice = true;

                    continue;
                }
```

Ajouter, juste avant le commentaire `/// <summary>` de `private async Task FollowEndingsAsync(CancellationToken ct)` :

```csharp
    /// <summary>Reporte au carnet les avis de pause reçus depuis le tic précédent.</summary>
    private void FollowPauses()
    {
        foreach (var (id, runtime) in _runtimes)
        {
            if (runtime.PausedByPeerNotice is false)
                continue;

            runtime.PausedByPeerNotice = false;
            _book.SetPausedByPeer(id, true);
            runtime.Pair = _book.Find(id) ?? runtime.Pair;

            _log.Info($"{runtime.Pair.DisplayName} : nous a mis en pause.");
            BookChanged?.Invoke();
        }
    }

```

Dans `TickAsync`, juste avant la ligne `await FollowEndingsAsync(ct).ConfigureAwait(false);`, ajouter :

```csharp
            FollowPauses();
```

- [ ] **Étape 8 : l'effacement à la reprise**

Dans `AdoptSessionAsync`, juste après `_book.Seen(id);` :

```csharp

        // Une session rouverte, c'est la reprise vue d'ici : la pause qu'il
        // nous avait dite est finie.
        if (runtime.Pair.PausedByPeer)
        {
            _book.SetPausedByPeer(id, false);
            runtime.Pair = _book.Find(id) ?? runtime.Pair;
            BookChanged?.Invoke();
        }
```

- [ ] **Étape 9 : l'envoi**

Dans `ReconcileBookAsync`, dans le bloc commenté « Mis en pause, bloqué, supprimé ou retiré », remplacer :

```csharp
            _log.Info($"{runtime.Pair.DisplayName} : pair retiré des actifs, session fermée.");
            await TearDownAsync(id, runtime, ct).ConfigureAwait(false);
```

par :

```csharp
            // Mis en pause pendant une session : le dire avant de raccrocher,
            // sans quoi l'autre nous croit parti du jeu. Pas un blocage, qui ne
            // doit rien révéler, et jamais en cherchant le pair exprès.
            if (runtime.Session is { } open && _book.Find(id) is { Paused: true, Trust: PairTrust.Accepted })
                await SendPauseAsync(runtime, open, ct).ConfigureAwait(false);

            _log.Info($"{runtime.Pair.DisplayName} : pair retiré des actifs, session fermée.");
            await TearDownAsync(id, runtime, ct).ConfigureAwait(false);
```

Et ajouter, juste après la méthode `NotifyRevokedAsync` :

```csharp

    /// <summary>
    /// Dit au pair qu'on le met en pause, puis laisse partir la trame.
    /// </summary>
    /// <remarks>
    /// La fermeture suit aussitôt, et LiteNetLib peut jeter une trame fiable
    /// encore en file quand la connexion tombe : on attend que le canal de
    /// contrôle se vide, une seconde au plus. Au mieux : perdu, l'avis laisse
    /// l'autre nous voir absent, comme avant qu'il existe.
    /// </remarks>
    private async Task SendPauseAsync(Runtime runtime, PeerSession session, CancellationToken ct)
    {
        try
        {
            await session.SendAsync(ChannelPlan.ControlChannel, MessageKind.Pause, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);

            for (var waited = 0; waited < 20 && session.Link.PendingOn(ChannelPlan.ControlChannel) > 0; waited++)
                await Task.Delay(50, ct).ConfigureAwait(false);

            _log.Info($"{runtime.Pair.DisplayName} : avis de pause envoyé.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning($"{runtime.Pair.DisplayName} : avis de pause non envoyé.", e);
        }
    }
```

- [ ] **Étape 10 : la phase lit la marque**

Dans `Statuses`, remplacer l'argument provisoire `false` posé en tâche 1 par `entry.Value.Pair.PausedByPeer`.

- [ ] **Étape 11 : les voir passer**

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter "FullyQualifiedName~PairBookPauseTests|FullyQualifiedName~SyncEngineTests"`
Attendu : PASS.

Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj`
Attendu : PASS, toute la suite. `Une_session_qui_tombe_se_rejoint_sans_attendre` est connu pour être intermittent : le relancer seul avant de conclure à une régression.

- [ ] **Étape 12 : commit**

```bash
git add Linkpearl/Core Linkpearl.Core.Tests
git commit -m "feat(sync): dire la pause au pair, et l'effacer à la reprise"
```

---

### Tâche 3 : le protocole écrit

**Fichiers :**
- Modifier : `docs/protocol.md`

- [ ] **Étape 1 : la pause à côté du retrait**

Dans `docs/protocol.md`, juste après le paragraphe qui commence par `**Retrait d'un pair.**` (il se termine par « que l'ancienne clé ne doit plus être crue. »), ajouter :

```markdown

**Pause d'un pair.** Mettre un pair en pause ferme la session. Si une session
est ouverte à ce moment, le moteur y envoie d'abord `MessageKind.Pause = 0x11`,
sans charge utile, puis attend que le canal de contrôle se vide, une seconde au
plus. Sans session ouverte, rien ne part : une pause ne fait jamais chercher le
pair pour la lui dire, ce qui lui apprendrait quand on se connecte. Un blocage
n'envoie rien. Le receveur marque le pair dans son carnet et continue de le
chercher au rythme d'un absent ; la première session rouverte efface la marque.
Un avis reçu sur une session de groupe est ignoré.
```

- [ ] **Étape 2 : le versionnage**

Dans la section « Versionnage », après la puce **Groupes privés**, ajouter :

```markdown
- **Avis de pause** : message `0x11` depuis le 26 septembre 2026. Un client plus
  ancien l'ignore et continue de voir absent un pair qui l'a mis en pause.
```

- [ ] **Étape 3 : commit**

```bash
git add docs/protocol.md
git commit -m "docs(protocol): l'avis de pause"
```

---

### Tâche 4 : l'enregistrement et l'interface

**Fichiers :**
- Modifier : `Linkpearl/Integration/PairBookStore.cs` (`Dto`, `Save`, `Rehydrate`)
- Modifier : `Linkpearl/Plugin.cs` (abonnement à `BookChanged`)
- Modifier : `Linkpearl/Ui/Pages/PairsPage.cs` (`Tint`, `DrawState`)
- Modifier : `docs/reprise.md`

**Interfaces :**
- Consomme : `PairRecord.PausedByPeer`, `SyncEngine.BookChanged`, `PeerPhase.PausedByPeer` (tâches 1 et 2).

- [ ] **Étape 1 : le carnet enregistré**

Dans `PairBookStore`, le commentaire du `Dto` gagne, après la phrase sur `RevokedAt` :

```csharp
    /// <c>PausedByPeer</c> aussi : un carnet ancien ne sait d'aucun pair qu'il
    /// nous a mis en pause.
```

Le `Dto` se termine par :

```csharp
        string[]? Rendezvous = null, int? Receive = null, long? RevokedAt = null, bool PausedByPeer = false);
```

Dans `Save`, la ligne `record.RevokedAt?.ToUnixTimeSeconds()));` devient :

```csharp
            record.RevokedAt?.ToUnixTimeSeconds(),
            record.PausedByPeer));
```

Dans `Rehydrate`, après `Paused = dto.Paused,` :

```csharp
                PausedByPeer = dto.PausedByPeer,
```

- [ ] **Étape 2 : l'hôte enregistre**

Dans `Plugin.cs`, juste après `_engine.RevocationDelivered += _ => _pairing.Save();` :

```csharp
        _engine.BookChanged += () => _pairing.Save();
```

- [ ] **Étape 3 : la page Pairs**

Dans `PairsPage.Tint`, le `switch` gagne une branche, avant `PeerPhase.Absent` :

```csharp
               PeerPhase.PausedByPeer                   => Theme.Idle,
```

Dans `PairsPage.DrawState`, ajouter au `switch (status.Phase)`, avant `case PeerPhase.Absent:` :

```csharp
            case PeerPhase.PausedByPeer:
                Chip.Draw("vous a mis en pause", Theme.Idle, Icons.Paused);
                Feedback.TooltipOnHover(
                    "Il vous reprendra quand il le voudra ; la synchronisation reviendra alors d'elle-même.");
                return;
```

`Linked(status)` ne change pas : sans session, le pair reste dans « En attente de lien » et Réappliquer reste grisé.

- [ ] **Étape 4 : compiler et tester**

Run : `dotnet build Linkpearl/Linkpearl.csproj -c Release`
Attendu : `0 Warning(s)`, `0 Error(s)`.
Run : `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj`
Attendu : PASS.

- [ ] **Étape 5 : la reprise**

Dans `docs/reprise.md`, section « Ce qui reste de mémoire », supprimer la puce qui commence par `- **Une pause ne se voit pas de l'autre côté.**` (trois lignes). Dans « Ce qui a changé le 26 », ajouter à la fin de la liste :

```markdown
- **Une pause se dit** (#23) : mis en pause pendant une session, un pair
  l'apprend par un message `Pause` (0x11), le retient dans son carnet et
  l'affiche, jusqu'à la session suivante. Hors session, rien ne part.
```

Et dans « Ce qui attend l'utilisateur en jeu », ajouter :

```markdown
- **L'avis de pause**, à deux personnages : mettre l'autre en pause pendant
  une session, lire « vous a mis en pause » chez lui, recharger son plugin (la
  marque reste), reprendre (elle s'efface).
```

- [ ] **Étape 6 : déployer et regarder**

Run : `./scripts/deploy-plugin-dev.sh`
En jeu, à deux personnages : A met B en pause ; chez B, « vous a mis en pause » en jaune dans « En attente de lien » ; recharger le plugin de B, la puce reste ; A reprend, la session revient et la puce disparaît. Puis A met B en pause alors que B est déconnecté : à sa connexion, B voit A « absent ».

- [ ] **Étape 7 : commit**

```bash
git add Linkpearl/Integration/PairBookStore.cs Linkpearl/Plugin.cs Linkpearl/Ui/Pages/PairsPage.cs docs/reprise.md
git commit -m "feat(ui): « vous a mis en pause » sur la page Pairs"
```
