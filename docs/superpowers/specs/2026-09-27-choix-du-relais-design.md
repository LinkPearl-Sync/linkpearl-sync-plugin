# Choisir le relais le plus proche

Écrit le 27 septembre 2026. Conception d'un sous-système, à lire après
`2026-09-26-cercle-ouvert-design.md`, dont elle reprend le placement par
hachage sans le modifier.

## Contexte

Quand le perçage échoue, deux pairs relaient par **le service qui les a
appariés** (`PeerConnector.cs`, `OpenRelayAsync(match.Value.At, ...)`). Ce
service est le premier à avoir répondu à l'annonce, pas le mieux placé pour
porter du trafic pendant des minutes.

Tant que tous les services sont en Europe, la différence est nulle. Elle cesse
de l'être dès qu'arrivent des joueurs lointains : deux joueurs de New York
appariés sur `rdv` en Suisse font un aller-retour transatlantique à chaque
paquet, alors qu'un service nord-américain leur éviterait l'océan.

Deux faits du code rendent la chose possible sans rien casser :

- le **jeton de relais** (`RelayTicketFor`) ne dépend que des deux blocs de
  candidats scellés, donc il vaut sur n'importe quel service, pas seulement sur
  celui qui a apparié ;
- `CandidateSet.TryDecode` **ignore les octets qui suivent les adresses**, donc
  un bloc enrichi reste lisible par les clients déjà publiés.

## Ce que la conception doit servir

Un seul motif : **donner un relais proche aux joueurs éloignés de l'Europe**.
Le débit du relais, la répartition de charge et la migration d'un relais en
cours de session sont hors sujet.

## Décisions

1. **L'appariement ne change pas.** On sépare « où l'on se trouve » et « par
   où l'on relaie ». `ServicePlacement.Choose` reste identique, octet pour
   octet, pour que les clients d'avant apparient toujours avec ceux d'après.
2. **Éligibles : un service par région.** Le hasard du secret de paire décide
   qui a le droit de relayer, la latence ne fait que départager. Un choix libre
   parmi toute la liste laisserait un opérateur qui pose des serveurs partout
   aspirer les relais de régions entières.
3. **Région attribuée par l'autorité, par GeoIP.** L'opérateur ne la déclare
   pas : il pourrait se dire seul dans une région creuse pour en capter tous les
   relais.
4. **Régions = continents** (`EU`, `NA`, `SA`, `AS`, `OC`, `AF`), sans table à
   tenir. Une région n'est retenue qu'avec **au moins deux services de familles
   distinctes**, sinon ses services restent dans le seul tirage global.
5. **En `RelayOnly`, on ne partage qu'une région**, jamais de ping : ce mode
   existe pour cacher son adresse au pair, qui ne doit pas pouvoir trianguler.
6. **Mesurer en tâche de fond, décider à l'appariement.** Aucune connexion
   n'attend un ping.

## Partie 1 : la région dans la liste signée (autorité)

### Source GeoIP

La base **DB-IP « IP to Country Lite »** : gratuite, sans compte, sous licence
CC BY 4.0, publiée chaque mois au format MMDB, avec le code de continent. Lue
par le paquet NuGet `MaxMind.Db` (Apache 2.0).

- Seule l'autorité (`--directory-authority`) la télécharge, une fois par mois,
  dans `/var/lib/lprdv/geoip.mmdb`, écrite par `AtomicFile`. Un service
  ordinaire n'en a jamais besoin et ne la télécharge pas.
- L'attribution exigée par la licence figure dans le README et dans la console.
- La recherche passe par une interface `IRegionLookup`, pour que les tests de
  l'autorité la simulent.

### Attribution

L'autorité sonde déjà chaque service, et `ServiceProbe` rend l'adresse
effectivement jointe, celle dont elle tire la famille. On cherche **cette même
adresse** dans la base, ce qui donne deux lettres ou rien.

Pas de région quand la base est absente, quand elle date de plus de 90 jours,
ou quand l'adresse y est inconnue. Le service reste alors dans le tirage global,
comme aujourd'hui. La console affiche la région de chaque service.

### Liste v2

Le format v1 est strict, aucune entrée ne peut porter un champ de plus. D'où un
second format :

```
liste_v2  = prefixe || version(4) || emise(8) || expire(8) || nombre(2) || entree_v2*
entree_v2 = longueur(1) || adresse || longueur(1) || libelle || famille(8) || region(2)
prefixe   = "linkpearl:consensus:v2"
document  = liste_v2 || nombre_signatures(1) || signature*
```

- `region` vaut deux octets ASCII (`NA`), ou `00 00` si elle est absente.
- Le préfixe fait partie des octets signés : aucune signature v1 ne vaut pour un
  document v2, ni l'inverse.
- Une nouvelle requête `QueryConsensusV2` sert ce document. L'ancienne requête
  continue de servir la v1, signée par la même clé : les plugins déjà publiés ne
  voient rien changer.

### Ce qui reste au client

L'autorité publie des faits (la région de chaque service), pas de règle. Le
seuil des deux familles s'applique dans le plugin, à l'identique des deux côtés
puisqu'ils lisent la même liste.

### Règle de non-autorité

L'autorité apprend la position de **serveurs** dont l'adresse est déjà publique
dans la liste. Aucun client ne lui est révélé. Le rendez-vous reste aveugle aux
mesures, qui voyagent scellées.

## Partie 2 : les mesures (plugin)

### Qui est éligible

`RelayPlacement.Eligible(pairSecret, liste v2, ancrage)`, fonction pure, rend
l'union dédoublonnée de :

- les deux services de `ServicePlacement.Choose`, inchangé ;
- pour chaque région retenue (au moins deux familles distinctes), le service de
  meilleur `ServicePlacement.Score` parmi ceux de la région ;
- les services d'ancrage de la paire.

Sans liste v2 (autorité ancienne, réseau ouvert coupé, paire que
`OpenCircle.MayUse` écarte), il ne reste que l'ancrage et, le cas échéant, le
placement.

### Mesurer

Un `RelayLatencies` de fond pingue l'union des éligibles de toutes les paires :

- par `RendezvousClient.ReflectAsync` sur une socket éphémère, qui existe déjà
  côté service : aucun service n'a besoin d'être mis à jour pour être mesuré ;
- trois essais, on garde le minimum, une seconde d'attente par essai ;
- chaque RTT est gardé trente minutes ;
- au plus 64 services par cycle, étalés dans le temps.

Un service jamais mesuré est absent des mesures envoyées. On n'attend jamais un
ping pour se connecter.

### Transport

Les mesures s'ajoutent **après** les adresses, dans le bloc scellé :

```
bloc      = candidats (inchangé) || extension*
extension = etiquette(1) || longueur(2) || contenu
  0x01 mesures : nombre(1) || (service(8) || rtt_ms(2))*
```

- `service` = `SHA-256(adresse canonique)[..8]`, l'adresse canonique étant
  celle de `ServiceConsensus.Canonical`.
- `rtt_ms` est borné à `0xFFFE` ; `0xFFFF` veut dire « injoignable ».
- Au plus 16 mesures. Une étiquette inconnue est sautée grâce à sa longueur ; un
  bloc tronqué est refusé.
- Le bloc entier reste sous `RendezvousWire.MaxSealedCandidatesLength`.

### RelayOnly

Le client pingue quand même les serveurs, ce que le pair ne voit pas. Il envoie
l'extension `0x01` avec des **RTT synthétiques** :

- `0` pour les services de sa région la plus proche, c'est-à-dire la région du
  service éligible de plus faible RTT mesuré parmi ceux qui en ont une ; si
  aucun n'en a, aucun service ne reçoit `0` ;
- `1000` pour les autres services joignables ;
- `0xFFFF` pour les injoignables.

Le pair apprend un continent, rien de plus, et la décision n'a qu'un seul
chemin.

## Partie 3 : la décision et le repli

### La règle

`RelayChoice.Decide(mesures A, mesures B, service d'appariement)`, fonction
pure, **symétrique** : elle rend le même service quel que soit le côté qui
l'appelle.

1. Si l'un des deux blocs n'a pas de mesures (client d'avant, extension
   illisible), on garde le service d'appariement.
2. Candidats : les services présents dans les deux mesures, sans ceux qu'un côté
   déclare injoignables. Cette intersection absorbe sans cas particulier deux
   versions de liste décalées ou deux ancrages différents. Vide : on garde le
   service d'appariement.
3. Coût : `max(rttA, rttB)`, puis `rttA + rttB` pour départager, puis
   l'empreinte `service`, octet par octet.
4. **Hystérésis** : si le service d'appariement fait partie des candidats, on ne
   le quitte que pour un gain d'au moins 20 ms **et** d'au moins 20 % sur le
   coût. Sans ce seuil, deux relais équivalents se disputeraient au gré du bruit.

Exemple : deux joueurs à New York, appariés sur un service suisse (95 et 98 ms),
avec un service NA à 12 et 30 ms. Le coût tombe de 98 à 30 ms, on relaie par le
service NA.

### Le repli

Le service garde une demande de relais en attente trente secondes
(`RendezvousLimits.RelayWaitTimeout`).

1. Relais choisi, s'il diffère du service d'appariement : **8 secondes** de
   budget.
2. En cas d'échec (refus, silence), service d'appariement : **25 secondes** de
   budget.

Un côté qui échoue vite attend au service d'appariement ; l'autre l'y rejoint au
bout de ses 8 secondes, bien avant les trente. Pire cas : environ 33 secondes
contre 20 aujourd'hui, seulement quand le relais choisi est en panne. Quand le
choix est le service d'appariement, rien ne change : un seul essai, avec le
budget actuel.

### Apprendre des refus

La liste ne dit pas si un service relaie (`RelayEnabled` peut valoir `false`).
Un service qui refuse est noté « ne relaie pas » vingt-quatre heures dans
`RelayLatencies`, et déclaré `0xFFFF` dans nos mesures d'ici là : les deux côtés
s'en écartent dès la connexion suivante.

### Journal

`ConnectionAttempt.Via` désigne le relais réellement utilisé. Le journal donne
la raison : « relais par rdv-na (30 ms contre 98 ms au service
d'appariement) », ou « relais choisi injoignable, repli sur rdv ».

## Tests

**Protocole** (copie littérale, dans les deux dépôts) : liste v2 écrite, signée,
relue ; signature v1 refusée sur un document v2 et l'inverse ; région absente.
Nouveaux cas dans `rendezvous-vectors.json`, vérifiés des deux côtés par
`RendezvousVectorTests.cs`.

**Plugin, fonctions pures** :

- `RelayPlacement.Eligible` : seuil des deux familles, dédoublonnage avec le
  placement, déterminisme, absence de liste v2 ;
- extension du bloc : un décodeur d'aujourd'hui l'ignore, étiquette inconnue
  sautée, bloc tronqué refusé, plafond de 16 mesures ;
- `RelayChoice.Decide` : symétrie sur des entrées tirées au hasard, hystérésis,
  intersection vide, pair sans mesures, RTT synthétiques du `RelayOnly`.

**Plugin, enchaînement** : `PeerConnector` avec un faux dialer ; relais choisi
refusé puis repli dans les budgets ; refus mémorisé vingt-quatre heures.

**Service** : l'autorité avec un `IRegionLookup` simulé ; une petite base MMDB
de démonstration en fixture ; base absente ou périmée, service sans région.

**Bout en bout** : `Linkpearl.Harness rdv` avec deux services locaux en ancrage,
l'un rendu lent par un délai injecté dans sa réflexion côté harness. `alice` et
`bob` relaient par le rapide, puis se replient sur l'autre quand on le coupe.

## Déploiement

Chaque étape est sans danger prise seule.

1. **Release du service**, déployée sur l'autorité de production : elle sert la
   v2 avec les régions, et toujours la v1. Aucun autre service n'a besoin d'être
   mis à jour.
2. **Release du plugin** : il demande la v2 et retombe sur la v1 face à une
   autorité ancienne ; il envoie ses mesures et décide. Face à un pair d'avant,
   il garde le service d'appariement, soit le comportement actuel.

## Effet attendu

Nos deux services sont en Europe : la région EU remplit le seuil, mais le choix
se fera entre deux services proches, et l'hystérésis gardera presque toujours le
service d'appariement. Le gain apparaîtra avec le premier service NA, AS ou OC
admis dans le réseau ouvert. C'est voulu : le mécanisme sera en place ce jour-là.

## Hors sujet

- Choisir le relais selon son débit ou sa charge.
- Migrer un relais en cours de session.
- Rendre l'appariement lui-même sensible à la région.
- Une région déclarée par l'opérateur ou corrigée à la main.
