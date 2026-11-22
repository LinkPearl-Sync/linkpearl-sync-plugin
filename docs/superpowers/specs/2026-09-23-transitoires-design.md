# Animations, VFX et sons : synchronisation et blocage

Conçu le 23 septembre 2026. Sous-projet B de la feuille de route (voir
`docs/reprise.md`). Demandé parce que l'idle et la façon de s'asseoir d'un
personnage moddé ne passaient pas, alors que tout le reste passait.

## But

Qu'un pair voie les animations, VFX et sons moddés d'un personnage comme avec
les clients comparables, sans qu'une animation cassée venue du réseau puisse
faire planter son jeu, et qu'il puisse couper chaque catégorie d'un clic,
pour tout le monde ou pour un pair.

## Décisions prises avec l'utilisateur

- **Activés par défaut** : animations, VFX et sons passent pour tout nouveau
  pair.
- **Blocage global rapide** par trois bascules toujours visibles dans la barre
  du haut de la fenêtre ; blocage par pair sur sa ligne du carnet.
- **Approche A** : l'émetteur vérifie ses animations contre son squelette ; le
  receveur refuse tout fichier transitoire mal formé, en code managé borné, sans
  le donner au moteur du jeu. Le risque résiduel (animation bien formée mais
  faite pour un autre squelette) est couvert par les interrupteurs et par le
  pairage explicite.
  *Novembre 2026, après audit :* le pairage explicite ne couvre pas les membres
  d'un groupe privé, et un échange ne porte aucun fichier que l'émetteur
  vérifierait. `FileSwapPolicy` refuse donc tout échange d'animation, de
  squelette, de physique ou de points d'attache entre deux familles de
  squelette (`chara/human/` vers `chara/monster/`, par exemple). Et le
  receveur juge désormais lui aussi chaque `.pap` reçu contre le squelette de
  la cible, avant de le poser (`ReceivedAnimationScreen`, `RemoteApplicator`),
  sauf si l'apparence porte son propre squelette. **À vérifier en jeu avant
  publication** : qu'une animation ordinaire n'est pas écartée, qu'une
  animation faite pour un squelette personnalisé passe, et ce que devient la
  première application quand Glamourer change la race du pair (le squelette
  lu avant le redessin n'est pas encore le bon).
- **Catégories** : Animations (`.pap`, `.tmb`), VFX (`.avfx`, `.atex`), Sons
  (`.scd`). `.shpk` reste refusé.
- **Périmètre** : le personnage du joueur. Monture, familier et compagnon
  viendront plus tard.

## Constat de départ

Relevé dans Penumbra fa825ca, Snowcloak fe98096 et UmbraSync 541100a :

- L'arbre de ressources de Penumbra (`GetGameObjectResourcePaths`) ne contient
  ni animation de corps, ni `.tmb`, ni `.avfx`, ni `.scd`. Ces fichiers n'existent
  pour l'émetteur qu'à travers l'événement `Penumbra.GameObjectResourcePathResolved`
  (`nint gameObject, string gamePath, string localPath`), levé à chaque
  chargement, vanilla compris, depuis le fil qui charge la ressource.
- Penumbra préfixe les `.tmb` et `.avfx` d'une collection non par défaut par
  `|…|` : il faut le retirer avant de comparer au chemin de jeu.
- Aucun des deux clients ne protège le receveur : leur seul contrôle anti-crash
  (os de l'animation contre squelette) tourne chez l'émetteur, sur ses propres
  fichiers, et se contourne.

## Capture chez l'émetteur

- `TransientCapture` (adaptateur) s'abonne à l'événement. Il garde un chemin si
  l'objet est le joueur local (adresse comparée à celle relevée à chaque image),
  si le chemin résolu, préfixe retiré et normalisé, diffère du chemin de jeu, et
  si l'extension est transitoire. L'événement pouvant venir d'un autre fil, il
  ne fait que déposer le chemin dans une file concurrente et signaler
  l'anti-rebond.
- `TransientMemory` (noyau, testable) retient les chemins par job : un chemin vu
  sous un job y est rangé ; revu sous un autre job, il passe en commun. Il est
  oublié s'il redevient vanilla ou s'il n'a pas été revu depuis 30 jours.
  Sérialisé en JSON dans `characters/<empreinte>/transients.json` : des chemins
  de jeu seulement, ni nom ni monde.
- À chaque reconstruction, les chemins retenus pour le job courant et le commun
  sont re-résolus par `Penumbra.ResolvePlayerPaths` ; ceux qui redeviennent
  vanilla sont oubliés, les autres rejoignent les ressources statiques avant le
  classement. C'est ce qui fait partir un idle qui n'a pas rejoué depuis le
  lancement. Une animation jamais vue sur ce personnage ne part pas.
- **Contrôle de squelette** : avant d'annoncer un `.pap`, l'émetteur charge sa
  section Havok par le moteur du jeu, sur le thread du framework, et compare le
  plus grand indice d'os animé au plus grand indice d'os de son propre
  squelette. Au-delà, le fichier est écarté et signalé dans le journal. Sans
  risque : son jeu charge déjà ce fichier. Code propre au projet (Snowcloak est
  sous AGPL), sur les API publiques de FFXIVClientStructs.

## Réception

- `ExtensionAllowList` accepte les extensions transitoires ; `.shpk` reste
  refusé.
- `TransientFileCheck` (noyau) vérifie la structure d'un blob transitoire avant
  toute pose, en ne lisant que des octets bornés :
  - `.pap` : marque `pap `, version `0x00020001`, section d'informations à 26,
    de 0 à 1 024 animations, section Havok à au moins `26 + 40 × n`, fin de la
    section Havok après son début + 8 et dans le fichier, section Havok
    commençant par la marque d'un fichier Havok (tagfile `1E0DB0CA CEFA11D0` ou
    packfile `57E0E057 10C0C010`). Mesuré sur 5 109 `.pap` de mods réels : 5 108
    passent, le refusé est un fichier Havok brut nommé `.pap`. Le type (humain,
    monstre, arme) n'est pas contraint : 40 fichiers légitimes ne sont pas de
    type humain.
  - `.tmb` : marque `TMLB` (205 sur 205 mesurés) ; `.avfx` : `XFVA` (1 133 sur
    1 133) ; `.scd` : `SEDBSSCF` (576 sur 576). `.atex` : taille seulement,
    comme les textures.
  - Un blob mal formé est retiré du plan d'application, les autres restent ; la
    raison va au journal.
- **Blocage** : `TransientCategories(Animations, Vfx, Sounds)` dit ce qui est
  accepté. L'effectif d'un pair est le « et » du réglage global et de son
  réglage propre. Le manifeste reçu est filtré par l'effectif **avant** le plan
  de téléchargement : rien n'est reçu de ce qui est bloqué. Changer un réglage
  redemande le manifeste des pairs concernés.
- Les réglages : trois booléens globaux dans `Configuration` (tous à vrai), et
  sur `PairRecord` un `TransientCategories Receive` (tout à vrai), persisté
  dans le carnet avec des champs optionnels pour relire un ancien carnet.

## Application

Les fichiers transitoires rejoignent le mod temporaire du pair, dans la même
collection. Un changement de fichiers déclenche l'application complète avec
redessin, comme aujourd'hui ; l'anti-rebond de l'émetteur regroupe les
animations captées en rafale en début de session.

## Interface

- Barre du haut : trois icônes bascules (animations, VFX, sons), barrées quand
  bloquées, avec info-bulle.
- Carnet : un bouton par ligne ouvre un petit menu à trois cases pour ce pair.

## Erreurs

- Événement Penumbra absent ou qui lève : aucune capture, le reste fonctionne.
- Contrôle de squelette qui échoue sur une exception : le fichier est écarté
  (prudence), avec une ligne de journal.
- Mémoire illisible : repartir d'une mémoire vide, sans l'écraser tant qu'elle
  n'a pas été reconstruite.

## Tests (noyau, sous Linux)

- `ExtensionAllowList` : transitoires acceptés, `.shpk` refusé.
- `TransientFileCheck` : un en-tête `.pap` réel reconstruit (mesures ci-dessus)
  accepté ; chaque règle violée une à une refusée ; données tronquées refusées
  sans exception ; signatures des trois autres.
- `TransientPolicy` : filtrage par catégorie, entrée retirée quand tous ses
  chemins sont bloqués, rien de filtré quand tout est accepté.
- `AppearancePlanner` : un blob transitoire mal formé retiré, le reste posé.
- `TransientMemory` : rangement par job, promotion en commun, oubli, purge à 30
  jours, JSON aller-retour, JSON illisible.
- Moteur : un pair dont les animations sont bloquées ne demande ni ne pose ses
  `.pap`.

## Hors périmètre

Objets possédés (monture, familier, compagnon), mode d'enregistrement,
application sans redessin des seuls transitoires.
