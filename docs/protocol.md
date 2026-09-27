# Protocole Linkpearl

Ce document décrit le protocole sur le fil, tel que le code l'implémente. Il
existe pour qu'une personne extérieure puisse le relire sans lire le code.
Chaque section renvoie au fichier qui fait foi.

> **La construction n'a pas encore été validée.** Elle est maison, et les
> vecteurs figés du dépôt ne valident que sa cohérence avec elle-même,
> puisqu'ils ont été produits par cette même implémentation. Une première
> relecture, le 24 septembre 2026, a surtout montré que la version précédente de
> ce document décrivait un modèle de confiance qui n'existait plus : voir
> [Limites connues](#limites-connues). Une relecture extérieure reste une
> condition de diffusion.

## Conventions

- Tous les entiers **du fil** sont en **big-endian** : horodatages, compteurs,
  ports, numéros de monde, longueurs. Seule exception, hors fil : le nombre
  d'itérations du fichier de sauvegarde, en little-endian (voir
  [Secrets locaux](#secrets-locaux)).
- Tout aléa et toute clé viennent de `RandomNumberGenerator` et de
  `ECDiffieHellman.Create` / `ECDsa.Create`, c'est-à-dire du générateur du
  système (CNG sous Windows). Un échec de génération lève une exception, qui
  fait échouer l'opération : il n'y a pas de repli sur une source plus faible.
- Les horodatages sont des secondes Unix sur 64 bits signés.
- Un point P-256 voyage **non compressé** (`0x04 || X(32) || Y(32)`, 65 octets),
  sauf dans les demandes de pairage, les dépôts d'admission et les politiques de
  groupe, où il est **compressé** (`0x02|0x03 || X(32)`, 33 octets).
- `||` est la concaténation. `[a..b]` désigne les octets `a` inclus à `b` exclu.

## Primitives

| Rôle | Primitive |
|---|---|
| Identité, signature | ECDSA P-256, SHA-256, signature concaténée r&#124;&#124;s de 64 octets |
| Accord de clé | ECDH P-256, éphémère à chaque session |
| Dérivation | HKDF-SHA256, HMAC-SHA256 |
| Chiffrement authentifié | AES-256-GCM, nonce de 12 octets, étiquette de 16 octets |
| Empreintes, adressage de contenu | SHA-256 |

Toutes viennent de `System.Security.Cryptography`, sans dépendance. Voir
`Core/Crypto/CryptoPrimitives.cs` pour le choix de P-256 plutôt que des courbes
25519, et d'AES-GCM plutôt que ChaCha20-Poly1305.

**Validation des points.** Tout point public reçu du réseau, compressé ou non,
passe par `CryptoPrimitives.Decode` ou `Decompress` avant usage :

- longueur et préfixe exacts, sinon refus ;
- `x < p` et `y < p` ;
- `y² ≡ x³ - 3x + b (mod p)`, ou pour un point compressé, existence d'une racine
  vérifiée par élévation au carré.

Le point à l'infini n'a pas de représentation dans ces deux encodages, il ne
peut donc pas être reçu. Le cofacteur de P-256 vaut 1 : un point de la courbe
appartient au sous-groupe d'ordre premier, il n'y a pas de sous-groupe de petit
ordre à écarter. **Le runtime ne fait pas cette vérification à l'import**, d'où
le contrôle explicite.

## Identités

Chaque personnage a sa propre clé ECDSA P-256, générée localement ; seule la
clé publique sort. `PeerId = SHA-256(point non compressé)`. L'autorisation vient
**du carnet local et de lui seul** : une session n'est acceptée que si la clé
présentée a pour empreinte le `PeerId` attendu pour ce lien précis
(`PeerSession.Authorizes`), et, si le carnet la connaît déjà, si elle est
identique octet pour octet.

**La clé privée d'identité a un second usage, hors protocole** : son scalaire
`D` est l'entrée d'une dérivation `HKDF-SHA256(ikm = D, info =
"linkpearl:moodles-guid:v1")`, qui donne la clé HMAC masquant les GUID Moodles
transmis dans les manifestes (`Core/Manifest/MoodlesSanitizer.cs`). HMAC étant
une fonction pseudo-aléatoire, la sortie ne révèle rien de `D` ; mais la clé
n'est donc pas réservée à ECDSA, et une séparation propre la remplacerait par
un secret dédié.

## Modèle de confiance

C'est la section qui compte le plus, et la seule où le protocole ne tient pas
ce que la conception d'origine promettait.

**La clé publique d'un pair est apprise par le rendez-vous, au pairage.** Il
n'y a plus de code d'invitation échangé hors du jeu (voir `pairage.md`) : la
demande de pairage et sa réponse transitent par la boîte aux lettres du
service, en clair. Il en découle :

| Adversaire | Peut | Ne peut pas |
|---|---|---|
| Observateur du réseau | voir les adresses IP, les tailles, les horaires | lire une session, usurper une identité |
| Rendez-vous honnête mais curieux, ayant vu passer le pairage | savoir quels personnages se sont pairés | calculer le secret de paire, qui vient d'un accord éphémère ; lire une session |
| Rendez-vous malveillant, **au moment du pairage** | substituer sa propre clé des deux côtés et s'intercaler dans toutes les sessions suivantes de cette paire | agir sur une paire formée ailleurs |
| Rendez-vous malveillant, **après le pairage** | refuser le service, mentir sur une adresse, relayer ou non | faire accepter une autre identité : la clé est épinglée dans le carnet |
| Service malveillant du **réseau ouvert** | refuser, mentir sur une annonce, observer qui relaie avec qui | voir une clé, s'intercaler dans un pairage : il ne porte que des pairs épinglés |

C'est donc une **confiance au premier contact** (TOFU), dont le premier contact
passe par le serveur. Pour un membre de groupe, ce premier contact n'est pas le
pairage mais le premier handshake avec lui, et il reste gagnable par qui arrive
avant le vrai : voir [Groupes (noyau)](#groupes-noyau). Pour un cercle qui héberge son propre service,
l'opérateur est l'un d'eux, et c'est assumé dans `pairage.md`. Pour le service
public, c'est la limite principale du protocole.

**L'admission dans un groupe privé est, elle aussi, une confiance au premier
contact.** Le candidat n'a que le code : rien ne lui permet de reconnaître la
clé du vrai groupe, ni à un membre de reconnaître le candidat. Les dépôts
passent par le service, en clair sauf l'étiquette de preuve et l'octroi scellé
(voir [Groupes privés](#groupes-privés)). Aucune vérification hors bande n'est
proposée, par décision : les joueurs ne se parlent pas hors du jeu.

| Adversaire | Peut | Ne peut pas |
|---|---|---|
| Service honnête mais curieux | lire le code, le nom, le monde et la clé de chaque demande qu'il voit passer, donc déposer lui-même des demandes | lire le mot de passe, l'octroi, le secret |
| Service qui s'intercale, ou porteur du code qui devance les membres, **en mode mot de passe** | se faire passer pour un membre et obtenir une étiquette liée au mot de passe, attaquable par dictionnaire hors ligne ; un mot de passe faible tombe, et avec lui l'entrée dans le vrai groupe, donc le secret | lire le mot de passe en clair dans la preuve, ni sa longueur |
| Le même, **en mode validation** | substituer sa propre clé et son propre éphémère dans la demande, ou déposer sous le nom d'un autre : le modérateur approuve un nom affiché, et la bienvenue, donc le secret, va à qui a déposé | faire entrer quelqu'un sans qu'un modérateur approuve |
| Le même, face au candidat | le faire entrer dans un **faux groupe** en répondant avant les membres : le candidat n'a aucune ancre vers la clé du vrai groupe ; le nom affiché à l'entrée le trahit s'il diffère de celui qu'on lui a annoncé | falsifier un groupe déjà rejoint : sa clé est fixée à l'entrée, et toute politique doit s'y vérifier |
| Qui connaît `nom@monde` du candidat | lire dans sa boîte personnelle les défis et la bienvenue scellée, y déposer des refus | ouvrir la bienvenue, produire la preuve |

Tout ce qu'apprend ainsi un intrus au premier contact, secret du groupe
compris, lui ouvre ensuite les boîtes de présence et les secrets de paire de
tous les membres, et le premier contact avec chacun d'eux : voir
[Groupes (noyau)](#groupes-noyau).

**Le mot de passe est connu de tous les membres.** Il voyage dans la politique,
qui ne circule que dans des sessions chiffrées entre membres, et c'est ce qui
permet à n'importe lequel d'admettre. Il protège l'entrée contre les inconnus,
pas contre un membre qui le donnerait. Sa seule protection contre la devinette
est son entropie.

**Un exclu garde le secret.** Il n'y a pas de rotation : les membres refusent
sa clé et son personnage, mais il peut encore interroger les boîtes de
présence et savoir lesquels des membres dont il connaît le nom sont en ligne.

**Dans le Public, le service sait tout de l'appartenance.** Il connaît le
secret, puisque c'est une constante : il sait quels joueurs visibles l'un de
l'autre l'ont activé, et peut les apparier. Il ne lit pas davantage le contenu,
qui passe dans la session SIGMA-I. Un membre du Public est un inconnu : le
premier contact avec lui est gagnable par qui arrive avant le vrai, comme dans
tout groupe.

**Une liste de bannissement relève de la réputation, pas de la preuve.** Un
service ne voit aucun contenu, donc il ne peut vérifier aucune accusation.
Chaque service actif peut imposer sa liste : un seul qui liste un personnage
suffit à l'écarter partout. Retirer ce service de ses réglages lève ses
bannissements.

## Pairage

Fichiers : `Core/Sync/PairRequestMessage.cs`, `Integration/PresenceService.cs`,
`Core/Identity/PairSecret.cs`, `Core/Identity/MailboxAddress.cs`.

Chaque personnage ouvre une boîte aux lettres dont l'adresse se calcule depuis
son nom et son monde, sur une fenêtre de trente minutes :

```
empreinte = empreinte de nom@monde                             voir PlayerFingerprint
adresse   = SHA-256("linkpearl:mbox:v1" || empreinte || fenetre(8))[tronquée]
```

Le demandeur dépose dans la boîte de la cible, **sur tous les services de sa
propre liste** puisqu'il ignore lequel la cible emploie :

```
demande  = 0x03 || cleA_compressee(33) || alea_pairage(12) || ephA_compresse(33) || monde(2) || nom_utf8(≤64)
reponse  = 0x04 || cleB_compressee(33) || alea_pairage(12) || ephB_compresse(33) || monde(2) || nom_utf8(≤64)
```

Le tout en clair. La réponse reprend l'aléa de la demande, que le demandeur
retrouve dans ses demandes en attente, avec la moitié privée de `ephA`, gardée
en mémoire seulement : un plugin rechargé entre la demande et la réponse ne peut
plus conclure, et il faut redemander. `ephB` est tiré à l'acceptation. Les types
`0x01` et `0x02`, ceux de la version 1 sans éphémère, sont refusés avec un motif
lisible.

Le secret de paire :

```
materiau = ECDH(ephA, ephB) || alea_pairage
sel      = min(PeerIdA, PeerIdB) || max(PeerIdA, PeerIdB)
prk      = HKDF-Extract(sel, ikm = materiau)
secret   = HKDF-Expand(prk, "linkpearl:pair:v2", 32)
```

Il ne chiffre jamais une session. Il sert aux jetons de rendez-vous, au
scellement des candidats et au jeton de relais, c'est-à-dire à cacher au
serveur qui parle à qui. Un service qui se contente de regarder passer le
pairage ne voit que `ephA` et `ephB`, et ne peut pas calculer l'accord. Un
service qui **substitue** ses propres éphémères le peut, mais il substitue
alors aussi les identités : c'est la limite du modèle de confiance, pas une
fuite supplémentaire.

En version 1, le matériau était l'aléa seul, en clair : tout service qui voyait
passer le pairage connaissait le secret. Les paires formées avant le
24 septembre 2026 gardent ce secret-là jusqu'à ce qu'elles se pairent à nouveau.

## Groupes (noyau)

Fichiers : `Core/Groups/GroupId.cs`, `Core/Groups/GroupDerivation.cs`,
`Core/Groups/GroupBook.cs`, `Integration/GroupStore.cs`.

Un groupe est un secret de 32 octets, partagé entre ses membres. Chaque membre
ouvre une boîte de présence, dérivée du secret et de son empreinte, renouvelée
toutes les trente minutes. Le détecteur existant interroge ces boîtes pour les
joueurs visibles ; un planificateur en transforme les réponses en `PairRecord`
éphémères, que le moteur compose comme des paires ordinaires. Le handshake
autorise la session par épinglage de la première clé vue (TOFU), et choisit
l'initiateur en comparant ordinalement les empreintes en hexadécimal.

**Identifiant de groupe** :

```
GroupId = SHA-256(matériau)[0..16]
```

où le matériau est le secret du groupe (32 octets) pour un groupe fabriqué à partir d'un secret seul, ou la clé
publique **compressée** du groupe (33 octets) pour un groupe privé (voir [Groupes privés](#groupes-privés)).

Exemple : secret = octets 0x00 à 0x1f, `GroupId` = `630dcd2966c4336691125448bbb25b4f`.

**Empreintes des exemples**, calculées par `PlayerFingerprint.Of`
(`SHA-256("linkpearl:ident:v1" || nom normalisé en UTF-8 || monde (2, gros-boutiste))[0..16]`) :

| Personnage | Empreinte |
|---|---|
| `alice`@21 | `e01e458cfd411c4eb2e0c5d509f02995` |
| `bob`@21 | `b65c349dc0a65fb594b4117d93729e62` |

**Boîte de présence**, fenêtres de trente minutes :

```
fenetre      = floor(temps / 1800) en secondes Unix
adresse      = SHA-256("linkpearl:group-presence:v1" || secret (32) || empreinte (16) || fenetre(8))[0..6]
```

Exemple : secret = octets 0x00 à 0x1f, alice@21 le 2026-09-22 12:00:00 UTC
(fenêtre 994488), présence = `31e46f1f1b91`.

**Secret de paire de groupe**, déduit du secret et des empreintes des deux
membres :

```
sel      = min(empreinte A, empreinte B) || max(empreinte A, empreinte B)
prk      = HKDF-Extract(sel, ikm = secret du groupe)
secret   = HKDF-Expand(prk, "linkpearl:group-pair:v1", 32)
```

Exemple : alice et bob, secret partagé : `fd0ebba6aa5cd0fa751986531ed267f714904c16f7e47bce559b34d896041672`.

**Identifiant de runtime**, local, jamais transmis :

```
runtime_id = SHA-256("linkpearl:group-runtime:v1" || secret de paire)[0..16]
```

Exemple : `31474004ac6d8e2d031c1e3553bd693b`.

**Choix de l'initiateur** : celui des deux dont l'empreinte est ordinalement la
plus faible en hexadécimal. Les deux côtés voient le même résultat puisqu'ils
ont les mêmes empreintes.

**Modèle de confiance pour les groupes** : chaque membre connaît le secret,
donc peut calculer les jetons de tous les autres et de n'importe quel couple.
La parade : la première clé vue pour un personnage est épinglée dans le carnet
de groupe, et une autre clé pour ce personnage est « Contestée ». Le premier
contact reste gagnable par qui arrive avant le vrai membre : un membre qui se
présente sous l'empreinte d'un autre avant lui, ou quiconque a appris le
secret (un service qui s'est intercalé à l'admission, par exemple), fait
épingler sa propre clé. C'est un prix assumé, énoncé dans la spec des groupes,
et non un risque exclu. Ce qui est exclu, c'est d'épingler sans preuve : la
clé n'est présentée au carnet de groupe qu'une fois la signature du transcript
et la liaison vérifiées (voir [Liaison des identités](#liaison-des-identités)),
donc seulement par qui détient la partie privée. Une fois la clé épinglée, un
rendez-vous peut faire échouer une connexion, pas la remplacer. Le service
apprend qu'un joueur tient une boîte supplémentaire, jamais laquelle ni quel
groupe elle porte.

## Groupes privés

Fichiers : `Core/Groups/GroupPolicy.cs`, `Core/Groups/GroupPolicyCodec.cs`,
`Core/Groups/GroupPolicyRules.cs`, `Core/Groups/GroupGovernance.cs`,
`Core/Groups/AdmissionMessages.cs`, `Core/Groups/AdmissionHost.cs`,
`Core/Groups/AdmissionCandidate.cs`, `Core/Protocol/MessageKind.cs`,
`Integration/PresenceService.cs`.

Un groupe privé ajoute au noyau une **clé du groupe** (ECDSA P-256), tirée à la
création chez le propriétaire et qui ne sert jamais d'identité, une
**politique** signée qui dit qui gouverne et qui est exclu, et une **admission**
par code. Le secret de 32 octets reste ce qui fait le membre : la politique ne
le porte pas, seul l'octroi le remet.

```
GroupId = SHA-256(clé publique du groupe, compressée (33))[0..16]
```

### Le code

`InvitationTicketText` : douze caractères Base32 Crockford avec contrôle, qui
portent 6 octets, suivis de `@service`. Montré par groupes de quatre
(`ABCD-EFGH-JKLM@rdv.exemple`), accepté avec ou sans tirets ; sans suffixe, le
premier service actif. Le code est dans la politique : le changer déplace la
boîte d'admission, et l'ancien ne mène plus nulle part.

### La boîte d'admission

```
adresse = SHA-256("linkpearl:group-join:v1" || code (6) || fenêtre (8))[0..6]
```

Mêmes fenêtres de trente minutes que la boîte personnelle, la courante et la
suivante. Elle dérive du code et non du secret : le candidat ne connaît que le
code.

Exemple : code = `01 02 03 04 05 06`, le 2026-09-22 12:00:00 UTC (fenêtre
994488), adresse = `e5153db1e51e`.

Un membre ne l'ouvre que s'il peut admettre : le groupe a une clé et une
politique non dissoute, et soit le mode est « mot de passe », soit il en est
le propriétaire ou un modérateur. En mode validation, un simple membre ne fait
pas payer au service des dépôts qu'il jetterait. Les boîtes ne s'ouvrent
qu'avec la détection active (réglage « Me signaler aux autres joueurs ») :
c'est elle qui tient la connexion au service.

### Les dépôts

Cinq types, à la suite de ceux du pairage (`0x03`, `0x04`), chacun sous les
512 octets d'une boîte :

```
0x05 demande   : code (6) | clé (33) | éphémère (33) | aléa (12) | monde (2) | nom (1 à 64)
0x06 défi      : aléa (12) | éphémère du membre (33)
0x07 preuve    : code (6) | aléa (12) | éphémère du membre (33) | étiquette (32)
0x08 bienvenue : aléa (12) | éphémère du membre (33) | octroi scellé (99 à 226)
0x09 refus     : aléa (12) | motif (1)
```

Clés et éphémères compressés. Le nom est de l'UTF-8 strict (un octet invalide
fait refuser la demande, jamais remplacer), sans caractère de contrôle ni de
catégorie Format (les marques bidi comme U+202E inverseraient l'affichage), et
pas seulement des blancs. La preuve a une taille fixe. Motifs de refus :
`0x01` mot de passe faux, `0x02` trop d'essais, `0x03` refusé par un
modérateur ; tout autre octet fait refuser le dépôt.

La demande et la preuve vont dans la boîte d'admission, sur le service du code.
Le défi, la bienvenue et le refus vont dans la **boîte personnelle** du
candidat, dont l'adresse se calcule depuis le nom et le monde de la demande,
sur les services de la politique.

### Le scellement

```
materiau  = ECDH(éphémère du candidat, éphémère du membre) || aléa (12)
cle(usage)= HKDF-SHA256(ikm = materiau, sel vide, info = "linkpearl:group-admit:v1:" || usage, 32)
associé(t)= t (1) || aléa (12) || éphémère du membre compressé (33)
```

Les usages sont `proof` et `welcome` : une preuve ne s'ouvre jamais comme une
bienvenue.

Exemple : partagé = octets 0x00 à 0x1f, aléa = octets 0x64 à 0x6f :

| Usage | Clé |
|---|---|
| `proof` | `8341723f2280566dcb34344d5eded1c5f25eb0287a702e68c529c3067fa1b482` |
| `welcome` | `657e3195b920ee89df8d42bf96d75de2a3ab0f79a807a4e4305d1db51250c0ac` |

**La preuve n'est pas le mot de passe scellé**, mais une étiquette :

```
étiquette = HMAC-SHA256(cle(proof), associé(0x07) || SHA-256(UTF-8(NFC(mot de passe))))
```

Les deux côtés la calculent, l'accord ECDH étant symétrique, et le membre
compare en temps constant. La normalisation NFC fait qu'un « café » saisi en
NFD donne la même étiquette. Un faux défieur n'obtient ainsi qu'une empreinte,
attaquable par dictionnaire hors ligne (voir
[Modèle de confiance](#modèle-de-confiance)), jamais le mot de passe en clair
ni sa longueur.

**L'octroi**, scellé dans la bienvenue :

```
octroi   = GroupId (16) | secret (32) | clé du groupe compressée (33) | n (1) | nom (n)
scellé   = AES-256-GCM(cle(welcome), nonce = 0, octroi, donnée associée = associé(0x08))
```

Le nonce nul est sûr parce que chaque clé ne scelle qu'une fois : l'éphémère du
membre n'a servi qu'à ce défi-là, et une validation par un modérateur tire un
éphémère neuf. Le candidat vérifie que la clé du groupe est un point valide,
que `SHA-256(clé)[0..16]` égale le `GroupId`, et que le nom suit les règles
d'un nom de groupe. Ce contrôle prouve la cohérence de l'octroi, pas son lien
avec le code : voir le faux groupe au [Modèle de confiance](#modèle-de-confiance).

L'octroi ne porte aucun service : le candidat garde celui du code jusqu'à ce
que la politique lui donne les autres. Il ne porte pas non plus de politique :
le groupe reste « en attente de sa politique » jusqu'à la première session
avec un membre.

### Mode mot de passe

1. Le candidat tire un éphémère et un aléa neufs, et dépose sa demande.
2. **Chaque membre en ligne** qui la reçoit répond par un défi, avec un éphémère
   neuf. Les défis vont dans la boîte personnelle du candidat, que les membres
   ne voient pas : aucun ne peut savoir qu'un autre a déjà défié.
3. Le candidat retient le premier défi et dépose la preuve. Les autres membres
   reconnaissent à l'éphémère que la preuve ne leur est pas destinée.
4. Le membre revérifie tout contre la politique courante et le code **de la
   demande mémorisée**, jamais celui de la preuve, qu'aucune donnée associée ne
   couvre : groupe non dissous, toujours en mode mot de passe, code inchangé,
   candidat non banni. Puis il compare l'étiquette, et répond bienvenue ou refus.

Un redépôt identique de la même demande fait renvoyer le même défi, au plus une
fois par 30 secondes et par aléa ; une demande différente sous le même aléa est
un rejeu et n'obtient rien. Un défi vit dix minutes. Côté candidat, sans
bienvenue ni refus soixante secondes après le défi, la candidature revient en
attente avec la même demande et accepte un autre défi : un membre qui s'est
déconnecté ne la gèle pas.

Au-delà de **5 échecs** dans la fenêtre de trente minutes pour une même clé de
candidat, le membre répond « trop d'essais » sans vérifier. Le compte est tenu
par chaque membre, en mémoire. Il freine l'erreur honnête répétée, il ne
protège pas de la devinette : voir [Modèle de confiance](#modèle-de-confiance).

### Mode validation

Le candidat redépose sa demande chaque minute pendant dix minutes : les boîtes
ne gardent rien, et un modérateur peut se connecter entre-temps. Seuls le
propriétaire et les modérateurs la voient, page « Demandes ». Un redépôt ne la
rafraîchit que s'il est identique à la demande gardée (clé, éphémère, nom,
monde, code) ; sinon il est ignoré, pour qu'un intrus ne fasse pas sceller la
bienvenue pour son propre éphémère. Elle disparaît après trois minutes sans
redépôt. Accepter envoie la bienvenue avec un éphémère neuf, après avoir
revérifié les bannis ; refuser envoie le refus `0x03`. Le premier modérateur
qui répond l'emporte.

### Dans les deux modes

- Une demande dont la clé ou le personnage est banni, ou qui vient de nous, ne
  reçoit **aucune** réponse. Un groupe dissous ne répond plus.
- Un membre se souvient quinze minutes d'avoir répondu à un aléa, et n'y
  répond plus.
- Au plus **64 défis vivants** et **64 demandes en attente de validation** par
  membre ; au-delà, silence. Réponses et comptes d'échecs gardés : 256 au plus,
  les plus anciens sortent d'abord.
- Au plus **20 réponses d'admission par minute** et par client, le surplus
  jeté : le service compte 60 trames par minute et par adresse IP, et des
  demandes forgées en nombre feraient sinon déconnecter le membre.
- La candidature expire après dix minutes sans réponse. Une seule à la fois,
  oubliée au changement de personnage, comme les défis et attentes de l'hôte.

### La politique

Deux encodages canoniques, binaires, entiers en gros-boutiste, textes précédés
d'un octet de longueur :

```
attestation : format (1) | groupe (16) | version (8) | admission (1) | propriétaire (33)
              | n (1) | n × modérateur (33) | signature (64)
politique   : format (1) | groupe (16) | version (8) | nom (1+) | code (6)
              | n (1) | n × service (1+) | mot de passe (1+) | n (2) | n × banni
              | dissous (1) | taille (2) | attestation | signataire (33) | signature (64)
banni       : drapeaux (1, 1 = clé, 2 = personnage) | PeerId (16)? | empreinte (16)?

signature de l'attestation = ECDSA(clé du groupe, "linkpearl:group-attest:v1" || attestation sans sa signature)
signature de la politique  = ECDSA(signataire,    "linkpearl:group-policy:v1" || politique sans sa signature)
```

`format` vaut `0x01`. `admission` vaut `0x01` (mot de passe) ou `0x02`
(validation). `propriétaire` est la clé d'**identité** du propriétaire en tant
que membre, distincte de la clé du groupe : elle sert à le protéger d'un
bannissement et à afficher son rôle. `modérateur` : au plus 16 clés d'identité.

**L'attestation** est ce que seul le propriétaire décide : propriétaire,
modérateurs, mode d'admission. Signée par la clé du groupe, elle est embarquée
dans chaque politique, pour qu'un membre qui vient d'entrer vérifie un
modérateur sans rien connaître de l'historique.

Bornes au décodage : nom de 1 à 32 caractères (128 octets UTF-8 au plus), sans
caractère de contrôle ni seulement des blancs ; 1 à 4 services de 255 octets au
plus ; mot de passe de 64 octets UTF-8 au plus, sans caractère de contrôle ;
256 bannis au plus, drapeaux 1 à 3 ; `dissous` 0 ou 1 ; 16 Kio en tout ; aucun
octet en trop. **Une seule forme d'octets par politique** : le décodage
réencode ce qu'il a lu et exige les mêmes octets, sans quoi plusieurs trames
distinctes décoderaient vers la même politique.

**Règles d'acceptation** (`GroupPolicyRules.TryAccept`), une seule violée et
la politique entière est rejetée :

1. la clé du groupe donne le `GroupId` attendu, et la politique comme
   l'attestation portent ce `GroupId` ;
2. en mode mot de passe, le mot de passe n'est pas vide ;
3. l'attestation est signée par la clé du groupe ;
4. le signataire est la clé du groupe ou un modérateur **de l'attestation
   embarquée**, et la signature de la politique se vérifie sous sa clé ;
5. toutes les clés de l'attestation sont des points valides ;
6. aucun bannissement par clé ne vise le propriétaire-membre ni un modérateur
   attesté, quel que soit le signataire : pour exclure un modérateur, le
   propriétaire le retire d'abord des modérateurs, dans la même politique ;
7. seule la clé du groupe dissout.

Un modérateur ne peut donc changer ni les modérateurs ni le mode d'admission :
ils sont dans l'attestation, qu'il ne sait pas signer. Un bannissement par
empreinte seule peut viser le personnage d'un pair protégé ; il ne s'applique
pas à sa clé (`GroupPolicy.IsBanned`).

**Ordre entre deux politiques valides** (`GroupPolicyRules.IsNewer`) :

1. la dissolution l'emporte, et reste acquise : un modérateur retiré, qui garde
   une attestation où il figure, ne ressuscite pas un groupe dissous ;
2. puis la plus haute version d'attestation : un modérateur retiré ne reprend
   pas la main sous l'ancienne ;
3. puis la plus haute version ;
4. puis le plus petit SHA-256 **du contenu signé**, et non de la signature :
   ECDSA est aléatoire et malléable, deux signatures du même contenu ne
   départagent pas. À contenu identique, aucune ne l'emporte.

Toute modification du propriétaire re-signe l'attestation en haussant sa
version, même à contenu inchangé : elle l'emporte toujours, y compris sur un
modérateur hostile qui aurait poussé la version du corps au maximum. Une
modification de modérateur hausse la version de un, et échoue lisiblement à
l'épuisement.

### Propagation

`MessageKind.GroupPolicy = 0x10`, sur une session de groupe seulement :

```
charge = GroupId (16) || politique encodée
```

Chaque côté envoie la sienne à l'ouverture de la session, puis à chaque
nouvelle version. Le receveur ignore un `GroupId` qui n'est pas celui de la
session, garde au plus quatre charges en attente par session, et les traite au
tic suivant : acceptée et plus récente, elle est adoptée (le nom et les
services du groupe la suivent) ; plus ancienne, il renvoie la sienne ; même
contenu, rien. Deux membres qui se croisent repartent avec la même. Un client
qui ne connaît pas `0x10` l'ignore.

Un membre qui adopte une politique où il est banni, ou une politique dissoute,
quitte le groupe et le dit au joueur. Il ne la relaie donc pas : **la
dissolution n'atteint que les membres que le propriétaire croise lui-même**,
et c'est pourquoi un groupe dissous reste dans la liste du propriétaire
jusqu'à ce qu'il le retire.

### Gouvernance concurrente

Pas de journal d'opérations : la politique la plus récente remplace l'autre en
entier. Il en découle, et c'est assumé :

- deux modérateurs qui publient la même version : une des deux modifications
  est perdue, le modérateur qui la voit disparaître la refait ;
- une modification du propriétaire faite depuis un état périmé écrase une
  modification de modérateur qu'il n'a pas encore reçue, puisque l'attestation
  domine l'ordre : un bannissement prononcé par un modérateur peut ainsi
  sauter si le propriétaire agit avant de l'avoir vu ;
- un modérateur hostile qui pousse la version du corps au maximum gèle les
  autres modérateurs jusqu'à la prochaine action du propriétaire ;
- un membre qui vient d'entrer accepte la première politique valide qu'on lui
  présente, même sous une attestation ancienne, jusqu'à croiser un membre qui
  en a une plus récente.

### Ce que l'admission ne garantit pas

Dans la ligne du [Modèle de confiance](#modèle-de-confiance), et sans
vérification hors bande :

- **Le nom et la clé d'une demande ne sont pas authentifiés.** Un modérateur
  approuve un nom affiché ; la puce « visible autour de vous » dit qu'un
  personnage de ce nom est là, pas que la demande vient de lui.
- **Le code circule en clair** dans la demande et la preuve : tout service qui
  voit passer une demande connaît le code, et peut déposer à son tour.
- **Les bannissements se contournent à l'admission** : ils ne visent que la clé
  et le personnage déclarés. Un exclu qui connaît encore le code et le mot de
  passe revient sous une clé neuve et un autre personnage. Changer le code et
  le mot de passe après une exclusion ferme cette porte-là.
- **Le plafond de 5 échecs** se contourne par une clé neuve à chaque essai. Un
  porteur du code peut aussi s'en servir contre une victime : épuiser les
  essais sous sa clé s'il la connaît, ou, en lisant sa boîte personnelle, déposer une fausse
  preuve qui consomme le vrai défi et lui vaut un refus.
- **Les refus ne sont pas authentifiés** : qui connaît `nom@monde` du candidat
  et l'aléa peut en déposer un.

Le verrouillage d'une victime, la fausse preuve et le faux refus sont du déni
de service par un porteur du code ou un tiers : ils coûtent au candidat un
nouvel essai, jamais un accès à qui n'en a pas.

## Groupe Public

Un groupe comme les autres, sans code, sans admission, sans politique et sans
propriétaire. Quiconque l'active voit l'apparence de tout joueur visible qui
l'a activé aussi, et en est vu.

- **Secret constant** : `SHA-256("linkpearl:public:v1")` =
  `41b3c4bdfd876a04e0c524eca45a469d6baf181d0657e464eeef4f59bfaa5a7a`.
  `GroupId = SHA-256(secret)[0..16]` = `c92263f11cad477c5082acba30895596`.
  Tout le reste en dérive comme pour un groupe privé : boîte de présence,
  secret de paire de membres, identifiant de runtime (voir
  [Groupes (noyau)](#groupes-noyau)).
- **Aucune clé de groupe**, donc aucune politique : un `GroupPolicy` reçu pour
  le Public est ignoré.
- **Services** : ceux de la configuration du joueur, pas ceux d'une politique.
- **Désactivé par défaut.** Désactivé, il reste enregistré, « dormant » : il
  n'ouvre aucune boîte et ne compose personne, mais garde ses blocages.
- **Modération locale** : un joueur bloqué (clé épinglée et empreinte de
  personnage) n'est ni admis au handshake ni composé. La liste n'est jamais
  transmise. S'y ajoutent les listes de bannissement des services (section
  suivante) ; tant que la vérification d'un joueur visible n'est pas faite, un
  membre du Public n'est pas composé.
- **Animations, VFX et sons refusés par défaut** pour ses membres, réglables
  pour tout le Public et membre par membre. Ce réglage ne touche que ce qu'on
  reçoit : il ne change rien au fil.
- Il compte dans les 48 sessions de groupe, et pas dans les 10 groupes d'un
  personnage.

## Listes de bannissement

Chaque service publie une liste d'empreintes lentes de personnages
(`BanList`, PBKDF2-SHA256 de `nom@monde` normalisé, sous un sel propre à la
liste). Elle descend au plugin par deux trames du rendez-vous :

```
BanListQuery  0x15 | page (2, BE)
BanListData   0x16 | page (2, BE) | pages (2, BE) | JSON UTF-8 d'une BanList
```

- **Pages de 64 entrées**, 64 pages au plus : une entrée pèse jusqu'à
  860 octets en JSON indenté, et une liste compte au plus 4 096 entrées. Chaque
  page est une liste complète, sous le même sel et le même coût, portant une
  tranche des entrées. Le client les fusionne, refuse des pages sous deux sels
  ou deux coûts, et ne compte qu'une fois une entrée vue sur deux pages (la
  liste peut changer entre deux pages).
- **Une page hors de la liste** reçoit une erreur, et la connexion reste.
  Au-delà de 64 pages servies sur une connexion, le service répond par une
  erreur et ferme : trois octets de demande pour 64 Kio de réponse feraient
  sinon de lui un amplificateur.
- **Le client** demande la liste de chaque service actif à la connexion, puis
  toutes les heures, sur une connexion à part, et la garde en mémoire. Un échec
  garde la liste précédente. **Un service qui ne connaît pas ces trames**
  répond « trame inattendue » et ferme : il n'a pas de liste, et ne bannit
  personne.
- **Application** : un personnage listé par un seul service actif n'est pas
  pairé (sa demande est ignorée, le bouton de demande n'est pas proposé), pas
  admis dans un groupe, pas composé par un groupe ni par le Public, et son
  apparence n'est pas posée, paire directe comprise. Le motif s'affiche au
  survol de sa ligne.
- **Coût** : une dérivation coûte de 0,1 à 0,3 s de processeur. Elle se fait
  une fois par personnage et par sel, sur le pool de threads, pour les seuls
  joueurs visibles qui ont le plugin ou sont au carnet. Une liste vide ne coûte
  rien. Les dérivations à la demande (demande de pairage ou d'admission venue
  d'un inconnu) sont plafonnées à 20 par minute ; au-delà, la demande est
  ignorée.

## Réseau ouvert

Fichiers : `Core/Transport/Rendezvous/ServiceConsensus.cs`, `Core/Sync/ServicePlacement.cs`,
`Core/Sync/OpenCircle.cs`, `Core/Sync/PeerConnector.cs`. Conception :
`docs/superpowers/specs/2026-09-26-réseau-ouvert-design.md`.

Deux réseaux de services. Le **réseau d'ancrage** est la liste des réglages,
composée à la main : il porte tout ce qui dérive de `nom@monde`, donc les
pairages, et c'est le seul qui voit passer une clé. Le **réseau ouvert** est
une liste signée par une autorité, qui y admet des services après 72 heures de
sondes à 95 %. Il ne porte que ce qui dérive d'un secret entre pairs dont la
clé est épinglée : l'annonce et le relais d'une paire du carnet, ou d'un membre
épinglé d'un groupe privé. Un membre non épinglé et le groupe Public restent
dans l'ancrage.

**Liste signée**, en ECDSA P-256 / SHA-256, signature r‖s de 64 octets :

```
liste     = version(4) || emise(8) || expire(8) || nombre(2) || entree*
entree    = longueur(1) || adresse || longueur(1) || libelle || famille(8)
signature = identifiant_cle(8) || r||s(64)
document  = liste || nombre_signatures(1) || signature*

famille         = SHA-256("linkpearl:family:v1" || /24 IPv4 ou /48 IPv6)[0..8]
identifiant_cle = SHA-256(clé publique compressée)[0..8]
```

Elle vaut sept jours. Le client n'accepte que les clés inscrites dans le plugin
(`ConsensusKeys`), refuse une version antérieure à celle qu'il détient, et la
redemande à l'autorité toutes les six heures, par pages :

```
ConsensusQuery  0x17 | page (2, BE)
ConsensusPage   0x18 | page (2, BE) | pages (2, BE) | tranche du document (≤ 32 Kio)
```

L'autorité publie aussi l'état public du réseau, en JSON, pour la page du réseau
du site. Le plugin ne le demande jamais : il ne sert qu'à l'affichage, et rien n'y
est signé. Mêmes tranches, mêmes bornes, même plafond de pages par connexion ; un
service ordinaire répond par une erreur.

```
NetworkStatusQuery  0x19 | page (2, BE)
NetworkStatusPage   0x1A | page (2, BE) | pages (2, BE) | tranche du JSON (≤ 32 Kio)
```

**Placement** d'une paire, par hachage de rendez-vous :

```
score(s) = SHA-256("linkpearl:place:v1" || secret_de_paire || hote_en_minuscules:port)
```

Les deux meilleurs scores de familles différentes sont retenus. Aucune rotation
dans le temps : la place dépend du secret, qu'un attaquant ignore.

**Annonce.** Le client s'annonce sur ses deux services ouverts tout de suite, et
sur l'ancrage dix secondes plus tard, ou dès que les deux services ouverts ont
échoué. Le premier appariement gagne et désigne le relais. Sans liste valable,
tout passe par l'ancrage comme avant.

## Rendez-vous et connexion

Fichiers : `Core/Transport/Rendezvous/RendezvousTicket.cs`, `Core/Sync/PeerConnector.cs`,
`Core/Sync/CandidateSet.cs`, `Core/Transport/Rendezvous/RendezvousWire.cs`.

**Jetons d'annonce**, fenêtres de dix minutes, la courante et la suivante :

```
jeton(i) = HMAC-SHA256(secret, "linkpearl:rv:v1" || i(8))[0..16]
```

Le service apparie deux annonces qui partagent un jeton et renvoie à chacune le
bloc de candidats de l'autre, qu'il ne peut pas lire sans le secret.

**Bloc de candidats, format 2** (le format 1 est abandonné, voir plus bas) :

```
cle   = HKDF-Expand(secret, "linkpearl:candidates:v2", 32)
clair = n(1) || n × ( longueur(1) || adresse(4 ou 16) || port(2) )     n ≤ 8
bloc  = alea(12) || AES-GCM(cle, alea, clair, donnée associée = "linkpearl:candidates:v2")
```

Un aléa neuf à chaque annonce. Le format 1 scellait sous un nonce nul, avec une
clé fixe pour toute la vie de la paire, et les deux pairs scellaient chacun leur
bloc : chaque annonce réemployait le même couple clé-nonce sous les yeux du
service, ce qui en AES-GCM livre le XOR des clairs et de quoi forger des
étiquettes. Changer l'étiquette de dérivation retire aussi du jeu la clé dont
le format 1 a pu laisser fuir de quoi forger.

Un pair en mode **relais seul** envoie un bloc à zéro candidat.

**Perçage.** Si les deux blocs portent au moins une adresse, chaque côté tente
une connexion UDP (LiteNetLib) vers toutes les adresses de l'autre, pendant dix
secondes. La décision ne dépend que des deux blocs, que les deux côtés voient à
l'identique : ils tentent le perçage ensemble, ou passent au relais ensemble.

**Relais.** Sinon, ou si le perçage échoue, chaque côté ouvre le relais sur le
service qui a apparié, sous un jeton que les deux calculent sans échange :

```
jeton_relais = HMAC-SHA256(secret, "linkpearl:relay:v1" || min(blocA, blocB) || max(blocA, blocB))[0..16]
```

Le service met les deux connexions TCP bout à bout et recopie les trames sans
les lire. Le jeton change à chaque tentative, puisque les blocs portent un aléa.

## Handshake, SIGMA-I

Fichiers : `Core/Crypto/Handshake*.cs`, `Core/Sync/PeerSession.cs`.

Trois trames sur le canal 0. A est l'initiateur, B le répondeur. Le rôle se
décide par comparaison des `PeerId` (le plus petit initie), pas par qui a appelé :
les deux côtés se joignent en même temps.

```
prologue = "linkpearl-handshake-v1"

msg1  A -> B   clair    version(2) || horodatage(8) || ephA(65) || aleaA(16)          91 octets
msg2  B -> A   clair    version(2) || ephB(65) || aleaB(16)                           83 octets
               scellé   { idB(65) || sigB(64) || liaisonB(32) } + étiquette(16)       177 octets
msg3  A -> B   scellé   { idA(65) || sigA(64) || liaisonA(32) } + étiquette(16)       177 octets
```

`version` vaut `majeur(1) || mineur(1)`, actuellement `0x01 0x00`.

Dérivation :

```
dh      = ECDH(ephA, ephB)
th1     = SHA256(prologue || msg1 || msg2_clair)
prk     = HKDF-Extract(sel = th1, ikm = dh)
k_b     = HKDF-Expand(prk, "b->a", 32)          scelle msg2
k_a     = HKDF-Expand(prk, "a->b", 32)          scelle msg3
k_liai  = HKDF-Expand(prk, "bind", 32)
sid     = HKDF-Expand(prk, "session id", 16)
k_a2b   = HKDF-Expand(prk, "data a->b", 32)     canal de données
k_b2a   = HKDF-Expand(prk, "data b->a", 32)

sigB        = ECDSA(idB, "linkpearl-sig-responder-v1" || th1)
liaisonB    = HMAC-SHA256(k_liai, idB_pub || th1)
msg2_scellé = AES-GCM(k_b, nonce = 0, idB_pub || sigB || liaisonB, donnée associée = th1)

th2         = SHA256(th1 || msg2_scellé)
sigA        = ECDSA(idA, "linkpearl-sig-initiator-v1" || th2)
liaisonA    = HMAC-SHA256(k_liai, idA_pub || th2)
msg3        = AES-GCM(k_a, nonce = 0, idA_pub || sigA || liaisonA, donnée associée = th2)
```

Les deux trames scellées emploient un **nonce constant**, sûr ici et seulement
ici : chaque clé vient d'un accord éphémère neuf, ne chiffre qu'un seul message,
et les deux sens ont des clés distinctes.

Vérifications à la réception d'une trame scellée, dans cet ordre
(`HandshakeTranscript.TryOpenAuthentication`) : déchiffrement authentifié, taille,
validité du point, signature sur le transcript, liaison en temps constant, puis
seulement **autorisation par le carnet**. Le premier échec clôt la session.
L'autorisation vient en dernier parce que, pour un membre de groupe, elle
épingle la clé présentée : la consulter avant la preuve laisserait épingler une
clé dont le correspondant ne détient pas la partie privée.

### Liaison des identités

C'est la construction SIGMA-I de Krawczyk. Les signatures ne portent pas les
identités en clair ; ce qui lie chaque identité à la session, ce sont :

- son chiffrement sous une clé dérivée de `dh`, avec le transcript comme donnée
  associée ;
- la **liaison**, HMAC de sa propre clé publique et du transcript sous `k_liai`,
  qui est le « MAC de l'identité » de SIGMA et ce qui empêche la mauvaise
  liaison d'identité (*unknown key-share*) ;
- pour l'initiateur, sa signature porte sur `th2`, qui contient `msg2_scellé`,
  donc l'identité du répondeur ;
- côté applicatif, l'exigence que la clé reçue soit celle du pair attendu pour
  ce lien, et non n'importe quelle clé du carnet.

### Anti-rejeu

Il repose sur **les éphémères et les aléas des deux côtés**, pas sur
l'horodatage. Un `msg1` rejoué obtient un `msg2` construit sur un éphémère neuf
de B, et l'attaquant ne peut pas produire le `msg3` qui signe ce nouveau
transcript. Un `msg2` rejoué ne correspond pas au `msg1` en cours de A et
échoue au déchiffrement. Aucun état des sessions passées n'est donc nécessaire.

L'horodatage de `msg1` est vérifié par le répondeur seul, avec une tolérance de
soixante secondes d'avance ou de retard : c'est un filtre contre les vieilles
trames, pas le mécanisme anti-rejeu.

### Établissement

- **A** considère la session établie dès `msg2` vérifié et `msg3` envoyé. Il
  n'a pas de confirmation explicite que B a accepté `msg3` : la première trame
  du canal de données de B en tient lieu, et si B refuse, il ferme le lien.
- **B** la considère établie dès `msg3` vérifié.
- Chaque trame du handshake est attendue quinze secondes au plus. Tout échec,
  refus ou délai ferme le lien ; la connexion est retentée plus tard, avec un
  délai croissant.

## Canal de données

Fichier : `Core/Crypto/SecureChannel.cs`.

```
trame           = type(1) || canal(1) || compteur(8) || chiffré || étiquette(16)
nonce           = sid[0..4] || compteur(8)
donnée associée = type || canal || compteur
```

- `canal` est inférieur à 64. **L'octet de poids fort du compteur porte l'index
  de canal**, et le récepteur vérifie qu'il concorde avec l'octet `canal` de
  l'en-tête : deux canaux ne peuvent pas produire le même nonce. Les cinquante-six
  bits restants comptent les messages du canal à partir de 1.
- Les valeurs de `type` sont celles de `Core/Protocol/MessageKind.cs`. Le canal
  de données les authentifie ; la couche applicative ignore celles qu'elle ne
  connaît pas, ce qui laisse ajouter un type sans casser les anciens clients.
- La longueur du chiffré est celle de la trame moins l'en-tête et l'étiquette :
  le transport délimite les messages.
- Le récepteur tient un compteur par canal et refuse tout compteur inférieur ou
  égal au dernier accepté. **Le compteur n'avance qu'après authentification.**
- Le canal **exige un transport fiable et ordonné par canal** : une trame perdue
  ou déclassée ferait refuser la suivante. C'est ce que fournissent le mode
  `ReliableOrdered` de LiteNetLib et le relais TCP ; la perte de paquets est
  réparée sous cette couche.
- **Pas de renouvellement de clé en cours de session.** À l'épuisement d'un
  compteur (2⁵⁶ messages), l'émission lève et la session doit être renégociée.
  Une reconnexion refait un handshake complet, avec des clés neuves ; les
  sessions précédentes n'ont plus de clé en mémoire.

## Lien relayé

Fichier : `Core/Transport/RelayPeerLink.cs`.

Le relais ne porte qu'un flux ordonné. Chaque trame `RelayData` du service
(64 Kio au plus) transporte :

```
fragment = sorte(1) || canal(1) || données
sorte    : 0x00 fragment suivi d'autres, 0x01 dernier fragment,
           0x02 sonde (horodatage local(8)), 0x03 écho de sonde
```

Un message du canal de données est découpé en fragments de 32 Kio au plus, tous
émis sans entrelacement avec un autre message ; le récepteur les recolle par
canal, et ferme le lien sur tout message de plus de 16 Mio ou toute sorte
inconnue. Le contenu est une trame du canal de données, déjà scellée : le
service transporte sans pouvoir lire.

## Secrets locaux

Fichiers : `Integration/DpapiIdentityStore.cs`, `Integration/PairBookStore.cs`,
`Core/Identity/IdentityBackup.cs`.

| Secret | Où | Protection |
|---|---|---|
| Clé privée d'identité | `characters/<empreinte>/` du dossier de configuration | DPAPI, portée utilisateur Windows, entropie `linkpearl:identity:v1` |
| Carnet, secrets de paire compris | même dossier | DPAPI, même portée |
| Éphémère d'une demande de pairage en attente | mémoire seule | perdue au rechargement du plugin |
| Clés de session | mémoire seule | jamais écrites |

DPAPI protège contre un autre compte de la machine et contre la copie du
fichier, pas contre un programme qui tourne déjà sous le compte du joueur.

**Sauvegarde.** Un fichier exporté à la demande, qui survit à une
réinstallation du système. Avec mot de passe : PBKDF2-SHA256 (600 000
itérations par défaut, bornées entre 100 000 et 10 000 000 à la lecture), sel
de 16 octets, AES-256-GCM avec l'en-tête en donnée associée. **Sans mot de
passe, choix laissé à l'utilisateur, le fichier contient la clé privée en
clair** et n'est protégé que par un contrôle SHA-256 contre la corruption :
qui le récupère se fait passer pour le personnage.

**Effacement.** Le code est en C# managé : les clés dérivées et les secrets
sont des tableaux que le ramasse-miettes peut copier, et leur effacement n'est
pas garanti. Seul le secret brut de l'accord de pairage est explicitement mis à
zéro. C'est une limite, pas une garantie.

**Retrait d'un pair.** Le pair passe à l'état `Revoked` : on le joint encore,
mais la session ne porte plus que l'avis de retrait, jamais une apparence, et
on referme après trente secondes s'il ne raccroche pas. Son secret de paire
reste au carnet jusqu'à l'oubli de l'avis. Il n'existe **aucune rotation
d'identité** : une clé perdue ou compromise impose de recréer le personnage
côté plugin et de se pairer à nouveau avec chacun, sans moyen de prévenir les
pairs que l'ancienne clé ne doit plus être crue.

## Bornes et déni de service

Ce qu'un tiers ou un rendez-vous peut faire consommer, et ce qui l'arrête.

| Ressource | Borne | Où |
|---|---|---|
| Trame du service | 64 Kio | `RendezvousWire.MaxFrameLength` |
| Dépôt dans une boîte | 512 octets | `RendezvousWire.MaxDepositLength` |
| Bloc de candidats | 4 Kio, 8 adresses | `RendezvousWire`, `CandidateSet` |
| Trames par adresse et par minute (service public) | 60 | `RendezvousLimits` |
| Connexions simultanées par adresse (un /64 en IPv6) | 32 | `RendezvousLimits` |
| Attente d'un partenaire au relais | 30 s côté service, 20 s côté client | `RendezvousLimits`, `PeerConnector` |
| Trame du handshake attendue | 15 s | `PeerSession` |
| Message reconstitué sur le relais | 16 Mio | `RelayPeerLink` |
| Nouvelle tentative après échec | 5 s, doublée jusqu'à 5 min | `SyncEngineSettings` |
| Politique de groupe | 16 Kio, 4 services, 16 modérateurs, 256 bannis | `GroupPolicyCodec` |
| Politiques en attente de traitement, par session | 4 | `SyncEngine` |
| Défis vivants, demandes en attente de validation | 64 chacun, par membre | `AdmissionHost` |
| Renvoi d'un même défi | une fois par 30 s | `AdmissionHost` |
| Réponses d'admission déposées | 20 par minute | `PresenceService` |
| Échecs de mot de passe | 5 par clé de candidat et par fenêtre de 30 min, par membre | `AdmissionHost` |
| Liste signée | 1 024 entrées, 16 pages de 32 Kio, 16 pages servies par connexion | `ServiceConsensus`, `RendezvousWire` |
| Admissions au réseau ouvert | 5 par jour, 2 par famille | `AuthorityLedger` (service) |

Un rendez-vous malveillant peut toujours refuser tout service : c'est la
raison d'être de la liste de services. Un pair malveillant, déjà au carnet,
peut faire échouer ses propres sessions ; les manifestes et fichiers qu'il
envoie passent par `Core/Safety` avant tout usage, et un manifeste qui viole une
seule règle est rejeté en entier.

## Métadonnées visibles

Le chiffrement protège le contenu, pas les métadonnées.

| Qui | Connexion directe | Connexion relayée |
|---|---|---|
| Le pair | votre adresse IP, publique et locales | vos adresses aussi si le relais suit un perçage raté ; aucune en mode relais seul |
| Le rendez-vous | votre adresse IP, vos horaires de présence, que vous vous annoncez, la taille du bloc de candidats | tout cela, plus la durée, le volume et le rythme de la session relayée |
| Le réseau | les deux adresses, les volumes, les horaires | votre adresse et celle du service |

S'y ajoute, au pairage, tout le contenu des demandes (voir
[Pairage](#pairage)), à l'admission dans un groupe le code, le nom, le monde et
la clé du candidat (voir [Groupes privés](#groupes-privés)), et en permanence l'existence de votre boîte, dont
l'adresse se calcule depuis votre nom : le service sait qui est en ligne et
quand.

## Versionnage

- **Handshake** : `version` porte un majeur et un mineur. Le majeur doit être
  identique, sinon refus. Le mineur est transmis mais **n'est pas lu** : aucune
  négociation n'existe encore.
- **Pairage** : types `0x03` et `0x04` depuis le 24 septembre 2026, avec
  éphémère. Les types `0x01` et `0x02` sont refusés.
- **Bloc de candidats** : format 2 depuis le 24 septembre 2026, incompatible
  avec le format 1. Deux clients de formats différents échouent à ouvrir le
  bloc l'un de l'autre et ne se connectent pas ; il faut que les deux soient à
  jour.
- **Groupes privés** : dépôts `0x05` à `0x09`, politique et attestation au
  format `0x01`, message `0x10`, depuis le 24 septembre 2026. Un client plus
  ancien ignore `0x10` et ne reconnaît pas les dépôts d'admission.
- Les chaînes de dérivation portent leur version (`…:v1`, `…:v2`) : un
  changement de format change l'étiquette.
- Une incompatibilité se signale par un refus, jamais par une lecture
  dégradée : majeur différent, type de demande de la version 1, bloc de
  candidats qui ne s'ouvre pas. Le motif est journalisé ; seul l'échec de
  connexion apparaît au joueur, en infobulle sur le pair. Une demande de
  pairage d'un ancien client est écartée sans rien afficher. Aucun chemin ne
  retombe sur un format plus ancien.

## Limites connues

Par ordre d'importance.

1. **Le rendez-vous qui voit un pairage peut s'y intercaler, et rien ne permet
   de le détecter.** La clé publique arrive par lui, en clair. Voir
   [Modèle de confiance](#modèle-de-confiance). Aucune vérification hors bande
   n'est proposée : c'est une limite d'authentification, pas seulement de
   confidentialité.
2. **L'admission dans un groupe privé hérite de cette limite** : le
   candidat ne peut pas reconnaître le vrai groupe, un modérateur approuve un
   nom affiché, et un faux défieur obtient de quoi attaquer le mot de passe
   hors ligne. Un exclu garde le secret, faute de rotation. Voir
   [Modèle de confiance](#modèle-de-confiance) et
   [Ce que l'admission ne garantit pas](#ce-que-ladmission-ne-garantit-pas).
3. **Les paires formées avant le 24 septembre 2026** ont un secret dérivé de
   l'aléa seul, que tout service ayant vu leur pairage connaît. Ces services
   peuvent relier leurs présences dans le temps et lire leurs adresses
   candidates. Se pairer à nouveau suffit ; rien ne l'impose aujourd'hui.
4. **Le contenu des demandes est visible du service** : nom, monde et clé
   publique, déposés sur tous les services de la liste du demandeur. C'est le
   nom en clair qui permet au destinataire de reconnaître le demandeur.
5. **Aucune rotation d'identité** : une clé compromise ne peut pas être
   révoquée auprès des pairs. Voir [Secrets locaux](#secrets-locaux).
6. **Une sauvegarde sans mot de passe vaut l'identité**, en clair.
7. La clé privée d'identité sert aussi, par HKDF, à masquer les GUID Moodles.
8. L'effacement des secrets en mémoire n'est pas garanti (C# managé).
9. Pas de renouvellement de clé en cours de session, pas de négociation du
   mineur.
10. `Core/Crypto/ShortAuthString.cs` (six mots tirés d'une liste de 64, soit
   36 bits dérivés de `sid`) existe mais **n'est affiché nulle part**. Il
   n'apporte donc aucune protection aujourd'hui, et rien dans ce document ne
   doit être lu comme s'il en apportait une.

## Vecteurs figés

`Linkpearl.Core.Tests/Fixtures/protocol-vectors.json` couvre la dérivation de
clés et le format de trame du canal, qui sont déterministes. Le handshake tire
des éphémères et des aléas, ses trames ne sont donc pas reproductibles.

Les vecteurs des groupes (boîtes de présence et d'admission, secret de paire de
groupe, clés de scellement de l'admission) sont figés dans les tests de
`Linkpearl.Core.Tests/Groups/` (ceux de l'admission recalculés en Python) ; leurs
valeurs sont reprises dans [Groupes (noyau)](#groupes-noyau) et
[Groupes privés](#groupes-privés). Le secret et l'identifiant du Public sont
figés par `PublicGroupTests.Le_secret_et_l_identifiant_sont_figes`, et les
trames `bannissement-demande` et `bannissement-page` par
`rendezvous-vectors.json`, commun au plugin et au service.

Régénérer : `dotnet run --project Linkpearl.Harness -- vectors`. **Une
modification de ce fichier est un changement de protocole sur le fil**, et doit
s'accompagner d'une montée de version.
