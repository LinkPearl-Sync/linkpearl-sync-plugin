# Clarté des textes du plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rendre les textes de Linkpearl compréhensibles et actionnables pour un joueur qui découvre le plugin.

**Architecture:** Les changements restent dans la couche d'interface et dans les messages remis au joueur. Des tests lisent les sources d'interface déjà embarquées dans l'assembly de tests afin de protéger les formulations critiques sans charger Dalamud.

**Tech Stack:** C# 13, .NET 10, xUnit, ImGui/Dalamud.

## Global Constraints

- Conserver « pair » et « pairage », avec une définition dès l'onboarding.
- Ne modifier ni le protocole, ni le réseau, ni le stockage, ni les règles de groupe.
- Ne jamais employer de tiret cadratin.
- Préserver toutes les modifications locales déjà présentes dans les fichiers touchés.
- Maintenir `README.md` et `README.fr.md` cohérents lorsque les libellés cités changent.
- Ne pas committer les changements d'implémentation sans accord explicite, car les mêmes fichiers portent déjà des modifications locales antérieures.

---

### Task 1: Garde-fous éditoriaux

**Files:**
- Create: `Linkpearl.Core.Tests/UiCopyTests.cs`
- Read: `Linkpearl.Core.Tests/UiConventionTests.cs`
- Test: `Linkpearl.Core.Tests/UiCopyTests.cs`

**Interfaces:**
- Consumes: les ressources `.UiSources.` embarquées par `Linkpearl.Core.Tests.csproj`.
- Produces: des tests de présence et d'absence sur les textes visibles critiques.

- [ ] **Step 1: Écrire les tests en échec**

Créer un catalogue local de sources et cinq faits xUnit :

```csharp
using System.Reflection;
using Xunit;

namespace Linkpearl.Core.Tests;

public class UiCopyTests
{
    private static string Source(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.Contains(".UiSources.", StringComparison.Ordinal)
                             && resource.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void La_presentation_definit_un_pair_et_explique_le_relais()
    {
        var source = Source("Onboarding.OnboardingWindow.cs");
        Assert.Contains("Un pair est un joueur ajouté à vos contacts Linkpearl", source);
        Assert.Contains("un service Linkpearl les relaie sans pouvoir les lire", source);
        Assert.DoesNotContain("sert uniquement à trouver", source);
    }

    [Fact]
    public void Les_demandes_expliquent_quand_accepter()
    {
        var source = Source("Pages.RequestsPage.cs");
        Assert.Contains("N'accepter que si vous reconnaissez ce personnage", source);
        Assert.Contains("ne peut pas confirmer", source);
    }

    [Fact]
    public void Les_etats_de_pair_decrivent_un_resultat_visible()
    {
        var source = Source("Pages.PairsPage.cs");
        Assert.Contains("apparence appliquée", source);
        Assert.Contains("prêt quand le joueur sera visible", source);
        Assert.Contains("mis en pause par ce joueur", source);
        Assert.DoesNotContain("apparence posée", source);
        Assert.DoesNotContain("pause du pair", source);
    }

    [Fact]
    public void Le_mode_public_annonce_les_joueurs_inconnus()
    {
        var source = Source("Pages.GroupsPage.cs");
        Assert.Contains("Mode public", source);
        Assert.Contains("même si vous ne les connaissez pas", source);
        Assert.DoesNotContain("Activer Public", source);
    }

    [Fact]
    public void Le_cache_nomme_sa_taille_maximale()
        => Assert.Contains("Taille maximale du cache", Source("CacheChooser.cs"));
}
```

- [ ] **Step 2: Vérifier que les tests échouent pour les formulations actuelles**

Run: `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter FullyQualifiedName~UiCopyTests`

Expected: FAIL sur les cinq faits, avec les textes attendus absents.

---

### Task 2: Onboarding, demandes et Mode public

**Files:**
- Modify: `Linkpearl/Ui/Onboarding/OnboardingWindow.cs:63-88`
- Modify: `Linkpearl/Ui/Onboarding/OnboardingArt.cs:70-104`
- Modify: `Linkpearl/Ui/Pages/RequestsPage.cs:38-113`
- Modify: `Linkpearl/Ui/RequestToasts.cs:130-181`
- Modify: `Linkpearl/Ui/Pages/GroupsPage.cs:95-279`
- Test: `Linkpearl.Core.Tests/UiCopyTests.cs`

**Interfaces:**
- Consumes: `RequestsPage.IsVisible`, déjà partagé avec les notifications.
- Produces: le vocabulaire de référence repris par les autres écrans.

- [ ] **Step 1: Corriger le modèle mental dans l'onboarding**

Introduire exactement ces idées dans les six étapes, en gardant leur structure :

```text
Un pair est un joueur ajouté à vos contacts Linkpearl.
Les données passent directement entre les joueurs quand c'est possible. Sinon, un service Linkpearl les relaie sans pouvoir les lire. Aucun fichier n'est conservé par le service.
Une icône à côté du nom indique que le joueur utilise Linkpearl.
Bloquer les animations, les effets visuels (VFX) ou les sons.
Le cache est le stockage local des apparences reçues. Il évite de les télécharger à nouveau.
```

Remplacer également « glyphe » par « icône » dans l'illustration de pairage.

- [ ] **Step 2: Ajouter la consigne de sécurité aux demandes**

Sur la page et les notifications, afficher avant les boutons :

```text
N'accepter que si vous reconnaissez ce personnage.
```

Quand le personnage est hors de vue, ajouter :

```text
Linkpearl ne peut pas confirmer qu'il se trouve actuellement à proximité.
```

Conserver les boutons Accepter et Refuser ainsi que les calculs de visibilité actuels.

- [ ] **Step 3: Renommer uniquement l'affichage du Mode public**

Employer « Mode public », « Activer le mode public », « Désactiver le mode public » et « Suivre le mode public ». Remplacer l'avertissement par :

```text
Le Mode public partage votre apparence avec tous les joueurs visibles qui l'ont activé, même si vous ne les connaissez pas.
```

Ne renommer ni `PublicGroup`, ni les identifiants ImGui, ni les méthodes internes.

- [ ] **Step 4: Exécuter les tests ciblés**

Run: `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter FullyQualifiedName~UiCopyTests`

Expected: les tests onboarding, demandes et Mode public passent ; les tests d'états et de cache restent rouges.

---

### Task 3: États, barre de service, cache et sauvegarde

**Files:**
- Modify: `Linkpearl/Ui/Pages/PairsPage.cs:36-400`
- Modify: `Linkpearl/Ui/Pages/GroupsPage.cs:1090-1138`
- Modify: `Linkpearl/Core/Sync/TransferBadge.cs:23-45`
- Modify: `Linkpearl/Ui/Shell/StatusBar.cs:43-66`
- Modify: `Linkpearl/Ui/CacheChooser.cs:45-113`
- Modify: `Linkpearl/Ui/Pages/BackupCard.cs:55-123`
- Modify: `Linkpearl/Ui/NameplateLegend.cs:10-24`
- Test: `Linkpearl.Core.Tests/UiCopyTests.cs`

**Interfaces:**
- Consumes: `PeerPhase`, `ShellStatus`, `TransferBadge` et `CacheKeeper` sans changer leurs signatures.
- Produces: des libellés compréhensibles pour les mêmes états internes.

- [ ] **Step 1: Remplacer les états techniques**

Appliquer les correspondances suivantes dans les listes et badges :

```text
apparence posée                  -> apparence appliquée
prêt, hors de vue               -> prêt quand le joueur sera visible
attend son apparence            -> attend les données d'apparence
pause du pair                   -> mis en pause par ce joueur
absent du service               -> joueur hors ligne ou en pause
application                     -> application de l'apparence
```

Garder les couleurs, les icônes, les phases et les délais actuels.

- [ ] **Step 2: Rendre la barre d'état exacte**

Remplacer « En ligne » et « Hors ligne » par « Service disponible » et « Service indisponible ». Conserver le nom du personnage et le détail d'échec comme informations secondaires.

- [ ] **Step 3: Nommer le réglage de taille du cache**

Ajouter `Text.Label("Taille maximale du cache");` juste avant le curseur. Remplacer « quota » dans les messages visibles par « taille maximale ».

- [ ] **Step 4: Décrire le contenu de la sauvegarde avant l'identité**

Employer « Sauvegarder les personnages et leurs pairages » dans l'aide et les dialogues. Expliquer ensuite qu'une sauvegarde permet de reprendre la même identité Linkpearl, sans retirer l'avertissement sur le mot de passe.

- [ ] **Step 5: Vérifier les tests ciblés**

Run: `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj --filter FullyQualifiedName~UiCopyTests`

Expected: PASS, 5 tests réussis.

---

### Task 4: Messages d'erreur destinés au joueur

**Files:**
- Modify: `Linkpearl/Plugin.cs:975-990,1120-1230,1380-1632`
- Modify: `Linkpearl/Ui/CacheChooser.cs:115-137`
- Modify: `Linkpearl/Ui/Pages/SettingsPage.cs:143-160`
- Modify: `Linkpearl/Integration/PresenceService.cs:196-210,767-835,895-940`

**Interfaces:**
- Consumes: les journaux existants `Log`, `_log` et les canaux `Report`/`Tell`.
- Produces: des messages stables et actionnables ; aucune nouvelle API publique.

- [ ] **Step 1: Remplacer les exceptions brutes par des messages d'action**

Utiliser les formulations suivantes selon l'opération :

```text
Impossible de consulter ce service. Vérifier son adresse et réessayer.
Sauvegarde impossible. Vérifier l'emplacement et les droits d'écriture.
Lecture impossible. Vérifier que le fichier existe et reste accessible.
Demande impossible. Vérifier la connexion aux services Linkpearl, puis réessayer.
Action impossible. Réessayer. Si le problème persiste, consulter le journal.
Suppression impossible. Vérifier que le dossier n'est pas utilisé, puis réessayer.
```

Journaliser l'exception complète là où elle ne l'est pas encore. Ne jamais inclure le code d'un groupe ou un chemin local complet dans le journal.

- [ ] **Step 2: Retirer les noms d'états internes des refus**

Remplacer `Report($"Refus : le groupe a changé entre-temps ({outcome}).")` par :

```text
Le groupe a changé entre-temps. Rouvrir sa gestion et réessayer.
```

Conserver les refus métier explicites provenant de `InvitationTicketText`, `IdentityBackup` et `AdmissionCandidate`.

- [ ] **Step 3: Vérifier qu'aucune fuite évidente ne subsiste**

Run: `rg -n 'Report\(\$?"[^\n]*e\.Message|Tell\(\$?"[^\n]*e\.Message|_error = \$?"[^\n]*e\.Message|\{outcome\}' Linkpearl/Plugin.cs Linkpearl/Ui Linkpearl/Integration/PresenceService.cs`

Expected: aucune occurrence destinée directement au joueur.

---

### Task 5: Cohérence, documentation et vérification complète

**Files:**
- Modify: `Linkpearl/Ui/Pages/PairsPage.cs:43-50`
- Modify: `Linkpearl/Ui/Pages/SettingsPage.cs:220-235`
- Modify: `README.md`
- Modify: `README.fr.md`

**Interfaces:**
- Consumes: tous les libellés définitifs des tâches précédentes.
- Produces: une interface et deux README alignés.

- [ ] **Step 1: Corriger les dernières formes éditoriales**

Corriger « ou restaurez » en « ou restaurer ». Remplacer `service(s)` par une branche singulier/pluriel :

```csharp
var serviceCount = list.Entries.Count;
var services = serviceCount == 1 ? "1 service" : $"{serviceCount} services";
```

Employer une majuscule au début des messages autonomes et conserver l'infinitif sur les boutons.

- [ ] **Step 2: Aligner les README**

Mettre à jour chaque libellé français cité dans `README.md` et `README.fr.md`, notamment « À proximité », « Mode public », la barre de service et « Taille maximale du cache ». Garder le contenu anglais et français équivalent.

- [ ] **Step 3: Exécuter tous les tests du noyau**

Run: `dotnet test Linkpearl.Core.Tests/Linkpearl.Core.Tests.csproj`

Expected: PASS, aucune erreur et aucun test ignoré nouvellement.

- [ ] **Step 4: Compiler le plugin**

Run: `dotnet build Linkpearl/Linkpearl.csproj -c Release`

Expected: `Build succeeded.`, 0 warning, 0 error.

- [ ] **Step 5: Contrôler le diff final**

Run: `git diff --check`

Expected: aucune erreur d'espace ou de fin de ligne.

Run: `rg --pcre2 -n '\x{2014}|service\(s\)|apparence posée|prêt, hors de vue|pause du pair|Activer Public|sert uniquement à trouver' Linkpearl README.md README.fr.md`

Expected: aucune occurrence visible non justifiée ; les éventuelles occurrences historiques dans des commentaires sont reformulées dans la même passe.
