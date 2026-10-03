# Groupes et groupe Public

Conçu le 24 septembre 2026. Point 15 de la feuille de route (« Groups »), auquel
l'utilisateur a ajouté un groupe « Public » global, et qui absorbe le point 10
(appliquer les listes de bannissement du rendez-vous), sans lequel le Public
n'aurait aucune modération.

## But

Se synchroniser avec tout un groupe d'un coup, une compagnie libre ou un cercle
de jeu de rôle, sans pairer chaque membre un à un. Un groupe n'a pas de nom de
personnage par lequel le trouver : c'est lui qui porte le code annoncé dans
`pairage.md` (« Ce qui garde les codes ») et dans la contrainte 5 de `reprise.md`.

Et, à côté, un groupe « Public » sans code : quiconque l'active voit
l'apparence de tout joueur visible qui l'a activé aussi.

## Décisions prises avec l'utilisateur

- **Une syncshell à la Mare.** Être membre suffit : je vois tous les membres et
  ils me voient, sans pairage individuel. Quitter le groupe coupe tout.
- **Propriétaire et modérateurs.** Le créateur nomme des modérateurs ; eux et lui
  peuvent exclure et bannir.
- **Jusqu'à une centaine de membres**, la taille d'une compagnie libre. On ne se
  connecte donc qu'aux membres visibles, jamais à tous en permanence.
- **Admission au choix du propriétaire, jamais par le code seul** : un mot de
  passe (par défaut), ou la validation par un modérateur.
- **Pas de rotation du secret en v1.** Le prix est énoncé plus bas.
- **Public** : un seul groupe global, désactivé par défaut, sans propriétaire,
  modéré par le blocage local et par les listes de bannissement des services ;
  animations, VFX et sons bloqués par défaut pour ses membres.

## Ce qui a été écarté, et pourquoi

**Un serveur qui tient les groupes, comme celui de Mare.** C'est le plus simple
côté client, et c'est interdit : le rendez-vous n'apprend aucune identité stable
et ne garde rien sur disque qui ne vienne de son opérateur (règle de son dépôt).
Une liste de membres chez lui dirait à qui appartient qui, en permanence.

**Un code qui vaut le secret du groupe.** Douze caractères, soixante bits, se
devinent hors ligne par qui voit passer l'adresse de boîte qui en dérive. Et
changer le code obligerait à redistribuer le secret. Le code est donc une porte,
pas une clé.

**Un jeton d'annonce commun à tout le groupe.** Le rendez-vous apparie
strictement deux par deux (`HandleAnnounceAsync`, un seul occupant par jeton) :
un jeton partagé par cent membres n'apparierait que le premier couple. Chaque
couple de membres a donc ses propres jetons, dérivés du secret du groupe.

**La rotation du secret à l'exclusion.** Un membre hors ligne au moment de
l'exclusion devrait récupérer le nouveau secret sans pouvoir joindre personne,
puisqu'il n'a que l'ancien. Faisable, avec une période de grâce où l'ancien sert
encore à la seule remise du nouveau, mais c'est une machine à états entière pour
un gain limité (voir « Prix assumés »).

## Constat de départ

Relevé dans le code à `df40af5` :

- Toute session naît d'une composition sortante vers un `PairRecord` connu
  (`SyncEngine.StartDueDials`, `PeerConnector.ConnectAsync`). Le secret de paire
  alimente tout : jetons d'annonce (`RendezvousTicket`), candidats scellés, jeton
  LiteNetLib, jeton de relais.
- `PeerSession.Authorizes` exige `PeerId.Of(clé) == pair.Id`. Un membre de
  groupe inconnu n'a pas encore d'`Id` quand on le compose.
- `PairBook.IsAuthorized` n'a aucun appelant ; `PairTrust.Pending` n'est jamais
  produit ; `PairPermissions.SendAppearance` n'est lu nulle part. Ce sont des
  restes, pas des points d'appui.
- Une boîte aux lettres porte 512 octets par dépôt, ne garde rien, et une session
  en ouvre 16 au plus (`RendezvousLimits.MaxMailboxesPerSession`). Une requête
  interroge 64 adresses au plus.
- Un type de message inconnu est ignoré par un client plus ancien
  (`MessageKind`) : ajouter des messages ne casse personne.
- La liste de bannissement n'est servie que par la console du service
  (`GET /api/bans`), qui n'écoute qu'en local. Le plugin ne peut pas l'atteindre.
- La sauvegarde en un fichier (`IdentityBackup`, version 1) porte la clé et le
  carnet de chaque personnage, rien d'autre.
- `InvitationTicket` et `InvitationTicketText` (12 caractères Base32 Crockford
  avec contrôle, suivis de `@service`) existent et n'ont plus d'appelant depuis
  l'abandon des codes d'invitation.

## Vue d'ensemble

Un groupe est un secret de 32 octets partagé par ses membres, plus une politique
signée par son propriétaire ou un modérateur. Le rendez-vous ne voit que des
adresses de boîte et des jetons opaques, comme pour une paire.

```
créer        propriétaire : clé de signature du groupe, secret, politique v1, code
rejoindre    code → boîte d'admission → un membre en ligne répond → secret scellé
se voir      je vois un joueur → j'interroge sa boîte de présence de groupe
             → elle existe → secret de paire de groupe → composition ordinaire
gouverner    politique signée, propagée de session en session, la plus haute gagne
```

Tout ce qui suit la composition (handshake SIGMA-I, manifestes, transfert,
application) est celui des paires, sans changement.

## Données

### `GroupRecord`

Dans `Core/Groups`, un `sealed record` par groupe dont on est membre :

| Champ | Contenu |
|---|---|
| `Id` | `GroupId`, 16 octets : SHA-256 de la clé publique de signature du groupe, tronqué, comme `PeerId` |
| `Secret` | 32 octets aléatoires, tirés à la création |
| `OwnerKey` | La clé publique de signature du groupe (P-256, compressée) |
| `SigningKey` | La clé privée correspondante, chez le seul propriétaire ; sinon nulle |
| `Role` | Octet validé : membre, modérateur, propriétaire. Dérivé de la politique, gardé pour l'interface |
| `Policy` | La `GroupPolicy` signée la plus récente, avec sa signature |
| `Rendezvous` | Les services du groupe, repris de la politique ; celui du code seul avant la première politique |
| `Members` | Par `PeerId` : nom affiché, empreinte épinglée, dernière vue, `Paused`, `Receive` |
| `Pins` | Empreinte → `PeerId` : la première clé vue pour un personnage dans ce groupe |
| `JoinedAt` | Date d'entrée |

`Members` ne contient que les membres **rencontrés** : on n'a pas de liste
complète, puisque personne ne la tient. C'est la liste que l'interface montre et
où un modérateur choisit qui exclure.

**Au plus 10 groupes par personnage**, et **au plus 256 membres rencontrés par
groupe** (2 048 dans le Public). Révisé le 3 octobre 2026 : au-delà, le nouveau
venu est refusé ; oublier un membre épinglé rouvrait son premier contact à qui
savait remplir le carnet.

### Persistance

`characters/<empreinte du ContentId>/groups.json`, JSON chiffré par DPAPI
(entropie `"linkpearl:groups:v1"`), écrit de façon atomique comme `pairs.json`
par un `GroupStore` calqué sur `PairBookStore`. Ajouté à
`CharacterStorage.Belongings`.

La sauvegarde en un fichier passe en **version 2** : chaque entrée porte en plus
les groupes (`taille (4) | groupes`), clé de signature comprise pour le
propriétaire. La lecture accepte les versions 1 et 2 ; l'écriture produit la 2.
Sans cela, un propriétaire qui réinstalle perd son groupe pour de bon.

### `GroupPolicy`

Ce que le groupe dit de lui-même, signé :

| Champ | Borne |
|---|---|
| `GroupId` | 16 octets, doit égaler celui du groupe |
| `Version` | `ulong`, strictement croissante |
| `Name` | 1 à 32 caractères, mêmes règles qu'un nom affiché |
| `Code` | Le ticket courant (6 octets) |
| `Rendezvous` | 1 à 4 adresses de service, celle du code d'abord |
| `Admission` | Octet validé : `0x01` mot de passe, `0x02` validation |
| `Password` | 0 à 64 octets UTF-8 ; vide seulement en mode validation |
| `Moderators` | Au plus 16 clés publiques |
| `Bans` | Au plus 256 entrées : `PeerId`, empreinte de personnage, ou les deux |
| `Dissolved` | Booléen |
| `Signer` | La clé publique du signataire |
| `Signature` | ECDSA P-256 sur l'encodage canonique de tout ce qui précède |

Une politique est valide si, en plus des bornes :
- le signataire est la clé du groupe (`OwnerKey`), ou un modérateur nommé par
  l'**attestation** qu'elle embarque. L'attestation (propriétaire, modérateurs,
  mode d'admission) est signée par la clé du groupe : un membre qui vient
  d'entrer n'a aucune version précédente sur laquelle s'appuyer, et un
  modérateur ne peut ainsi changer ni les modérateurs ni le mode d'admission ;
- elle ne bannit par clé ni le propriétaire ni un modérateur attesté, quel que
  soit le signataire ;
- seule la clé du groupe dissout.

Le détail (encodage, règles, ordre) est dans `docs/protocol.md`, section
« Groupes privés ».

Le mot de passe voyage dans la politique : tous les membres le connaissent,
c'est ce qui permet à n'importe lequel d'admettre. Elle ne circule que dans une
session chiffrée entre membres.

Le décodage passe par `Core/Safety` : une seule règle violée et la politique
entière est rejetée.

**Deux modérateurs qui publient la même version** : la politique retenue est
celle dont le contenu signé a le plus petit SHA-256 (pas la signature, qu'ECDSA
tire au hasard). L'ordre compare d'abord la dissolution, puis la version
d'attestation, puis la version. Une des deux modifications est
perdue ; le modérateur qui la voit disparaître la refait. Rare à cette échelle,
et moins coûteux qu'un journal d'opérations.

## Rejoindre

### Le code

Le format `InvitationTicketText` existant : `ABCD-EFGH-JKLM@rdv.exemple`, douze
caractères Base32 Crockford avec contrôle, puis le service du groupe. Sans
suffixe, le premier service configuré. Le propriétaire et les modérateurs le
voient avec un bouton de copie, pour l'envoyer par /tell.

Chaque membre en ligne qui peut admettre (tous en mode mot de passe, le
propriétaire et les modérateurs en mode validation) ouvre la **boîte
d'admission** :

```
adresse = SHA-256("linkpearl:group-join:v1" || code (6) || fenêtre (8 BE))[0..6]
```

sur les fenêtres de 30 minutes de `MailboxAddress`. Changer le code dans la
politique ferme l'ancienne boîte chez tous les membres qui reçoivent la nouvelle
version.

### Les dépôts

Nouveaux types, à la suite de `PairRequestMessage` (`0x03`, `0x04`), chacun
sous les 512 octets :

| Type | Sens | Contenu |
|---|---|---|
| `0x05` demande | candidat → boîte d'admission | code (6), clé publique (33), éphémère (33), aléa (12), monde (2), nom (≤ 64) |
| `0x06` défi | membre → boîte du candidat | aléa de la demande (12), éphémère du membre (33) |
| `0x07` preuve | candidat → boîte d'admission | code (6), aléa (12), éphémère du membre (33), étiquette HMAC (32) |
| `0x08` bienvenue | membre → boîte du candidat | aléa (12), éphémère du membre (33), AES-GCM(GroupId, secret, clé du groupe, nom) |
| `0x09` refus | membre → boîte du candidat | aléa (12), motif (octet : mot de passe faux, trop d'essais, refusé par un modérateur) |

La clé est `HKDF(ECDH(éphémères) || aléa)`, comme
`PairRequestMessage.AgreeOnPairing`, dérivée par usage
(`"linkpearl:group-admit:v1:proof"`, `"linkpearl:group-admit:v1:welcome"`). La
preuve n'est pas le mot de passe scellé mais une étiquette HMAC liée au mot de
passe : un faux défieur n'en tire qu'une empreinte attaquable par dictionnaire,
jamais le mot de passe en clair. La boîte du candidat est sa boîte
personnelle, déjà ouverte. La bienvenue ne porte aucun service : quatre
adresses de 255 caractères ne tiendraient pas dans 512 octets. Le candidat
utilise celui du code jusqu'à ce que la politique lui donne les autres.

### Mode mot de passe

1. Le candidat dépose une demande.
2. Tout membre en ligne qui la reçoit répond par un défi. Les défis vont dans
   la boîte personnelle du candidat, que les autres membres ne voient pas : un
   délai pour s'abstenir ne servirait à rien. Le candidat retient le premier
   défi et ignore les autres.
3. Le candidat renvoie la preuve, liée à l'éphémère de ce membre-là.
4. Le membre compare l'étiquette en temps constant, puis répond bienvenue ou
   refus.

Au-delà de **5 échecs par fenêtre** pour une même clé de candidat, le membre
répond « trop d'essais » sans vérifier. Le compte est par membre, en mémoire.
Il freine l'erreur honnête répétée, pas la devinette : une clé de candidat
neuve par essai le contourne. Contre la devinette, la seule protection est
l'entropie du mot de passe.

Sans bienvenue ni refus une minute après le défi, le candidat revient en
attente avec la même demande et accepte un autre défi. Un membre qui a gardé
le défi le renvoie à l'identique au redépôt, ce qui rattrape une preuve perdue.

### Mode validation

1. Le candidat dépose une demande, et la redépose **chaque minute pendant
   10 minutes** : les boîtes ne gardent rien, et un modérateur peut se
   connecter entre-temps. Son écran dit qu'il attend un membre en ligne, ou un
   modérateur si le groupe valide chaque entrée.
2. Seuls le propriétaire et les modérateurs l'affichent, dans la page Demandes :
   « Untel veut rejoindre Groupe », avec la puce « visible autour de vous »
   existante.
3. Accepter envoie directement la bienvenue, avec l'éphémère du modérateur.
   Refuser envoie un refus. Le premier modérateur qui répond l'emporte ; chez les
   autres, la demande s'efface quand elle cesse d'être redéposée.

### Dans les deux modes

- Un candidat dont la clé ou l'empreinte est bannie ne reçoit **aucune** réponse.
- Un groupe dissous ne répond plus.
- Le candidat qui reçoit la bienvenue crée son `GroupRecord` sans politique
  (« en attente de sa politique ») ; elle arrive à sa première session avec un
  membre.
  Jusque-là, il ne connaît ni les bannis ni les modérateurs, ce qui ne lui
  permet rien de plus que d'être vu.

## Présence et connexion

### Se reconnaître

Chaque membre ouvre, pour chacun de ses groupes, une **boîte de présence** :

```
adresse = SHA-256("linkpearl:group-presence:v1" || secret (32) || empreinte (16) || fenêtre (8 BE))[0..6]
```

`PresenceService.RefreshDetectionAsync` interroge déjà par lots de 64 les boîtes
personnelles des joueurs visibles. Il y ajoute, pour chaque joueur **détecté**
(sa boîte personnelle existe) et chaque groupe, l'adresse de présence de groupe.
Seuls les joueurs détectés sont interrogés : on ne fait pas payer au service les
passants sans Linkpearl.

Budget de boîtes ouvertes par session : 2 personnelles, plus 2 de présence et 2
d'admission par groupe au changement de fenêtre, soit 42 pour 10 groupes. **Le
service passe `MaxMailboxesPerSession` de 16 à 64**, seule modification du
serveur hors bannissements.

### Se composer

Une réponse positive donne un `PairRecord` **éphémère** :

```
secret de paire = HKDF(secret du groupe, sel = empreintes triées, "linkpearl:group-pair:v1", 32)
```

Il porte une origine « groupe » (`GroupId`), l'empreinte attendue, les services
du groupe, `Trust = Accepted`, les réglages du membre s'il est déjà rencontré,
et **jamais** d'entrée dans `pairs.json`. Le moteur le compose comme une paire.

`Authorizes` gagne une seconde règle, choisie par l'origine du `PairRecord` :

- paire directe : inchangée ;
- groupe : la clé n'est pas bannie par la politique, et si l'empreinte est
  épinglée dans `Pins`, c'est bien cette clé. Si l'empreinte n'est pas épinglée,
  la clé l'est à la fin du handshake.

L'annonce `Hello` doit ensuite porter l'empreinte attendue, faute de quoi le pair
passe en « Contesté » par le chemin existant de `ReconcileVisibility`.

**Une paire directe l'emporte** : si l'empreinte est déjà épinglée dans le
carnet, on ne compose pas par le groupe. Deux groupes communs : un seul
`PairRecord`, celui du groupe au plus petit `GroupId`, pour ne pas ouvrir deux
sessions vers la même personne. Les deux côtés font le même choix, puisqu'ils
voient les mêmes groupes communs.

### Vivre et finir

- Le manifeste part à toute session ouverte, comme aujourd'hui.
- Une session de groupe se **ferme après 5 minutes hors de vue** (`Bye`), et le
  `PairRecord` éphémère disparaît. Revenir à portée recompose.
- Un membre banni par une politique reçue en cours de session : la session est
  fermée et son apparence retirée.
- Au plus **48 sessions de groupe** ouvertes à la fois ; au-delà, on ne compose
  plus que les membres les plus proches. Un lieu de RP bondé ne doit pas tenir
  cent liens.

### Propager la politique

Nouveau `MessageKind.GroupPolicy = 0x10` : `GroupId (16) || politique signée`.
Envoyé à l'ouverture de chaque session de groupe, puis à chaque nouvelle version.
Le receveur garde la plus haute version valide, et renvoie la sienne si elle est
plus haute que celle reçue. Un client plus ancien l'ignore.

Pas de `MessageKind.GroupCard` (`0x11`), écarté en écrivant le plan de
l'incrément 2 : le nom d'un membre vient déjà du jeu, et son rôle se déduit de
la politique.

## Gouvernance

| Geste | Qui | Effet |
|---|---|---|
| Créer | tous | Clé de signature, secret, code, politique v1 : en mode mot de passe si un mot de passe est donné, en validation sinon |
| Nommer ou retirer un modérateur | propriétaire | Nouvelle politique |
| Changer le code, le mot de passe | propriétaire, modérateur | Nouvelle politique ; l'ancien code ne mène plus nulle part |
| Changer le mode d'admission | propriétaire | Nouvelle politique |
| Exclure | propriétaire, modérateur | Bannit la clé et l'empreinte. L'exclu qui reçoit la politique garde le groupe, exclu, jusqu'à ce qu'une politique plus récente le réintègre ou qu'il le quitte (révisé le 3 octobre 2026 : sinon un modérateur hostile dissolvait de fait) ; s'il ne coopère pas, les autres refusent sa clé et son personnage |
| Lever un bannissement | propriétaire, modérateur | Nouvelle politique |
| Dissoudre | propriétaire | `Dissolved`. Chaque membre qui la reçoit quitte le groupe, sans la relayer : seul le propriétaire la transmet, et il garde le groupe dans sa liste jusqu'à ce que les membres l'aient vu |
| Quitter | tous | Local : le `GroupRecord` disparaît. Le propriétaire qui quitte dissout |
| Mettre en pause, bloquer les effets d'un membre | tous | Local, dans `Members` |

Hors v1 : transférer la propriété, faire tourner le secret.

## Public

Un groupe comme les autres, sauf :

- **Le secret est une constante** : `SHA-256("linkpearl:public:v1")`. Pas de
  code, pas d'admission, pas de politique, pas de propriétaire.
- **Désactivé par défaut.** L'activer affiche une fois : « Tout joueur visible
  qui a aussi activé Public verra votre apparence moddée, et vous la sienne. »
- **Animations, VFX et sons bloqués par défaut** pour les membres du Public,
  réactivables d'un clic par membre et d'un réglage pour tout le Public.
- **Modération** : le blocage local d'un membre (clé et empreinte, gardés dans
  `groups.json`) et les listes de bannissement des services (section suivante).
- Il compte dans le budget des sessions de groupe comme les autres.
- Ses services sont les services actifs de la configuration.

Le service peut tout apparier dans le Public, puisqu'il connaît le secret : il
sait quels joueurs visibles l'un de l'autre l'ont activé. Il ne lit toujours ni
manifeste ni fichier : le contenu passe dans la session SIGMA-I, dont les clés
viennent des éphémères. Les candidats scellés, eux, lui sont lisibles : il voit
les adresses IP, ce qu'il voit déjà par la connexion.

## Listes de bannissement (point 10)

Le service les tient déjà (`BanStore`, empreintes PBKDF2 de `nom@monde`). Il
manque le chemin jusqu'au plugin :

- Nouvelles trames `BanListQuery = 0x15` et `BanListData = 0x16` dans
  `RendezvousWire`, qui portent le JSON de `BanList` tel que `/api/bans` le
  sert, borné à la trame de 64 Kio. Fichier copié : recopie dans le dépôt du
  service et `diff` vide, vecteurs mis à jour.
- Le plugin la demande à la connexion puis toutes les heures, par service actif,
  et la garde en mémoire.
- Un joueur listé par **un** service actif : pas de pairage avec lui (demande
  ignorée, bouton désactivé), pas d'admission dans un groupe, pas de session de
  groupe ni Public, et son apparence n'est pas posée, paire directe comprise.
  Le motif s'affiche sur sa ligne.
- La vérification se fait une fois par personnage visible et par liste, sur le
  pool de threads : PBKDF2 à 600 000 itérations coûte des dizaines de
  millisecondes.

## Interface

- **Page « Groupes »** dans la barre latérale :
  - en tête, l'interrupteur Public et le réglage de ses effets ;
  - « Créer un groupe » (nom, mot de passe) et « Rejoindre » (code, mot de passe
    si demandé, état de l'attente) ;
  - une section repliable par groupe : membres rencontrés, triés en ligne à
    proximité puis les autres, avec pause et effets par membre comme dans le
    carnet ;
  - pour le propriétaire et les modérateurs : code avec copie, changer le code
    ou le mot de passe, exclure depuis la ligne d'un membre, bannis avec levée ;
  - pour le propriétaire : mode d'admission, modérateurs, dissoudre.
- **Page Demandes** : les demandes d'admission en mode validation, à côté des
  demandes de pairage.
- **Plaque de nom** : un symbole distinct pour un membre de groupe, à côté de
  ceux du carnet.
- **Menu clic droit du jeu** : « Linkpearl : réappliquer » vaut aussi pour un
  membre de groupe.

Libellés en français seulement, comme le reste de l'interface ; le README anglais
les cite avec leur traduction.

## Modèle de confiance et prix assumés

- **Admission en confiance au premier contact.** L'éphémère du candidat et celui
  du membre passent en clair par le service, qui peut s'intercaler. Il
  n'apprend pas le mot de passe directement : en se faisant passer pour un
  membre, il obtient une étiquette liée au mot de passe, attaquable par
  dictionnaire hors ligne ; un mot de passe faible tombe, et avec lui l'entrée
  dans le vrai groupe, donc le secret. En mode validation, le modérateur
  approuve un nom affiché, et la bienvenue va à qui a déposé la demande. Un
  porteur du code qui devance les membres peut aussi faire entrer le candidat
  dans un faux groupe, que le nom affiché à l'entrée trahit. C'est le TOFU du
  pairage, déjà assumé : ne pas ajouter de vérification hors bande.
- **Un banni garde l'ancien secret.** Les membres refusent sa clé et son
  personnage, mais il peut encore interroger les boîtes de présence et savoir
  quels membres, dont il connaît les noms, sont en ligne. C'est le prix de
  l'absence de rotation.
- **Un membre peut tenter de se faire passer pour un autre.** Tous les membres
  connaissent le secret, donc les jetons de n'importe quel couple. La parade :
  la première clé vue pour un personnage est épinglée dans le groupe, et une
  autre clé pour ce personnage est « Contestée ». Le premier contact reste
  gagnable par qui arrive avant le vrai.
- **Le service apprend qu'un joueur appartient à un groupe** au sens où il voit
  une boîte de présence de plus, sans savoir lequel ni le relier à un autre
  membre d'une fenêtre à l'autre. Dans le Public, il sait tout de l'appartenance.
- **Le mot de passe est connu de tous les membres.** Il protège l'entrée contre
  les inconnus, pas contre un membre qui le donnerait.

Ces points vont dans `protocol.md`, section « Modèle de confiance ».

## Protocole et service

- `docs/protocol.md` : dérivations (boîtes d'admission et de présence, secret de
  paire de groupe, scellement de l'admission), dépôts `0x05` à `0x09`, messages
  `0x10` et `0x11`, trames `0x15` et `0x16`, encodage canonique et règles de la
  politique, bornes, vecteurs figés.
- Dépôt du service : `MaxMailboxesPerSession` à 64, trames de bannissement,
  copie de `RendezvousWire.cs`, vecteurs identiques dans les deux dépôts.

## Tests

Sous Linux, dans `Linkpearl.Core.Tests` :

- codecs des dépôts d'admission et des messages de groupe : aller-retour, bornes,
  refus de tout champ hors règles ;
- politique : signature, signataire autorisé, modérateur qui touche aux
  modérateurs, bannissement du propriétaire, versions concurrentes, rejet en
  bloc ;
- vecteurs de dérivation figés ;
- admission : deux modes, mot de passe faux, trop d'essais, banni sans réponse,
  plusieurs membres qui répondent au même défi ;
- moteur : deux membres qui se voient se composent et échangent ; paire directe
  prioritaire ; fermeture hors de vue ; banni en cours de session ; clé
  contestée ;
- sauvegarde : lecture des versions 1 et 2 ;
- liste de bannissement : trame, application au pairage et à l'apparence.

Au faux pair (`Linkpearl.Harness`), trois clients d'un même groupe sur un relais
local. En jeu, à deux personnages sur une machine : créer, rejoindre dans les
deux modes, exclure, puis Public.

## Livraison en trois incréments

1. **Le noyau** : `GroupRecord` et son stockage, boîtes de présence, secret de
   paire de groupe, `PairRecord` éphémère, autorisation, fermeture hors de vue,
   budget de sessions. Testable avec un groupe fabriqué à la main.
2. **Les groupes privés** : création, code, admission dans les deux modes,
   politique et sa propagation, gouvernance, page Groupes, sauvegarde v2, limite
   de boîtes du service.
3. **Public et bannissements** : l'interrupteur, ses défauts, les trames de
   bannissement et leur application partout.

## Documentation à reporter

- `pairage.md`, « Ce qui garde les codes » : ce que les codes sont devenus.
- `reprise.md` : feuille de route et constat.
- `protocol.md` : voir plus haut.
- `README.md` et `README.fr.md` : une section Groupes et une sur Public.
- Feuille de route : carte 15 en In progress, issue « Public group » créée, et
  le point 10 fermé avec l'incrément 3.
