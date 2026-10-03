# Où on en est, et par où reprendre

Écrit le 22 septembre 2026, en fin de première session. Mis à jour le 23, après
la première journée d'essais en jeu, et le 24 pour les groupes. À lire en
premier.

## Ce que le projet est devenu

Un successeur de Mare Synchronos en pair à pair. Les fichiers passent
directement d'un joueur à l'autre, chiffrés de bout en bout. Le seul service est
un rendez-vous minimal et auto-hébergeable, qui aide deux pairs à se trouver et
relaie quand le direct échoue.

**La conception a beaucoup bougé pendant la première session**, et toujours dans
le même sens : de l'échange de secrets entre inconnus vers deux personnes debout
l'une à côté de l'autre dans le même jeu. Voir `pairage.md`, qui raconte les
trois itérations et pourquoi les deux premières étaient fausses.

**Le 23, deux personnages se sont vus pour la première fois**, sur une même
machine : pairage par l'interface, apparence moddée posée chez l'autre, suivi
des changements de tenue.

## Les contraintes qui viennent de l'utilisateur, et qu'aucun code ne dit

Ce sont elles qui ont invalidé le plus de travail. À ne pas réinventer.

1. **Une apparence moyenne pèse environ 800 Mo**, pas les 300 mesurés sur le
   premier personnage. Tout le dimensionnement en découle.
2. **Les joueurs ne se parlent pas sur Discord.** Au mieux ils s'échangent
   quelque chose par message privé en jeu. Toute vérification hors du jeu ne se
   fera jamais.
3. **Tout doit passer par l'interface**, comme dans les clients comparables : on
   demande un pairage, l'autre reçoit une invite, il accepte ou refuse.
4. **Un utilisateur ne doit jamais voir une clé.** Il voit des noms de
   personnage. C'est l'identité qui l'intéresse.
5. **Les codes sont réservés aux groupes.** Un groupe n'a pas de nom de
   personnage, donc il lui faut un identifiant, qu'on s'envoie par /tell.
6. **Désactiver le plugin ne doit rien changer à l'apparence** du joueur. Les
   clients comparables ne le font pas, et un jour où je l'ai fait c'était un bug.
7. **Les joueurs visés font du jeu de rôle, pas du donjon.** Quelques
   millisecondes de ping leur coûtent moins qu'une tenue qui met des minutes à
   arriver : le limiteur d'envoi est désactivé par défaut, et « à l'époque de la
   fibre », un débit de quelques Mo/s n'est pas acceptable.
8. **Les références d'ergonomie sont Snowcloak et UmbraSync** : listes plutôt
   que cartes, « Réappliquer » au clic droit, progression visible sur le
   personnage. Ce qu'un joueur y connaît déjà, il doit le retrouver ici.
9. **Une identité doit survivre à une réinstallation** sans fouiller dans
   AppData : c'est la sauvegarde en un fichier, mot de passe facultatif.

## Ce qui vient ensuite, dans l'ordre voulu par l'utilisateur

1. **Les autres intégrations, comme le font les clients comparables** :
   Customize+, SimpleHeels, Honorific, Moodles et PetNicknames. Fait le 23
   septembre, voir `superpowers/specs/2026-09-23-integrations-design.md` ;
   à éprouver en jeu avec `essais-integrations.md`.
2. **Les animations, les VFX et les sons**, avec un moyen d'en bloquer la
   synchronisation : un réglage global rapide, et un réglage par pair. Fait le
   23 septembre, voir `superpowers/specs/2026-09-23-transitoires-design.md` ;
   à éprouver en jeu avec la seconde partie de `essais-integrations.md`.
3. **Les groupes**, en cours, voir `superpowers/specs/2026-09-24-groupes-design.md`.
   Incrément 1 livré (noyau). Incrément 2 livré le 24 septembre (groupes
   privés) : création, code, admission par mot de passe ou par validation d'un
   modérateur, politique signée et propagée, gouvernance, page Groupes,
   sauvegarde v2 ; protocole dans la section « Groupes privés » de
   `protocol.md`. Incrément 3 livré le 24 septembre (groupe Public, listes de
   bannissement des services servies par pages et appliquées partout) ;
   sections « Groupe Public » et « Listes de bannissement » de `protocol.md`.
   Restent les essais en jeu : créer, rejoindre dans les deux modes, exclure,
   dissoudre, puis Public et un bannissement, à deux personnages.

## État des jalons

| Jalon | État |
|---|---|
| 0, décider et mesurer | Fait pour l'utilisateur. Son cercle reste à interroger. |
| 1, boucle jeu locale | **Clos**, voir `jalon-1-resultats.md`. |
| 2, débit | **Mesuré**, voir `jalon-2-debit.md`, puis dépassé le 23 (voir plus bas). Le ping en jeu n'a pas été mesuré (#9, retirée le 24). |
| 2 bis, traversée de NAT | NAT classé favorable. Pas de test entre deux réseaux réels : retiré de la feuille de route le 24 (#9). |
| 3, crypto et protocole | **Clos**. Pas de relecture formelle (#8, fermée le 24), voir plus bas. |
| 4, cache et transfert | **Clos**. Transfert par tronçons depuis le 23. |
| 5, rendez-vous et relais | **Clos**, déployé et éprouvé depuis l'internet. Le serveur vit dans son propre dépôt. |
| 6, moteur de synchronisation | **Éprouvé en jeu**, à deux personnages sur une même machine. |
| 7, interface | Bien avancé : listes, badges de transfert, barre de statut, sauvegarde. |

## Ce qui a changé le 23

- **Transfert par tronçons de 4 Mio**, répartis sur 32 canaux
  (`Core/Transfer/BlobSegments.cs`). Un blob n'a plus à tenir sur un seul
  canal : c'était la traîne de tout transfert.
- **Accusés LiteNetLib à la milliseconde et découverte du MTU**
  (`PeerLinkFactory`). Au défaut de 15 ms, la fenêtre fiable se libérait trop
  lentement, même en local.
- **Limiteur** : une marge absolue de 20 ms avant de reculer, sans laquelle un
  lien local s'effondrait au plancher ; et débrayable, débrayé par défaut.
- **Reconnexion** : une session qui tombe après avoir tenu se réannonce
  aussitôt, et l'attente d'un pair absent se compte depuis le début de
  l'annonce, ce qui tient bien 25 s d'annonce sur 30.
- **Apparence locale** : on écoute aussi `StateChanged` de Glamourer, on attend
  que le personnage soit entièrement chargé avant de lire ses ressources
  (`DrawReadiness`), et une demande de reconstruction n'est plus jamais perdue.
- **Sauvegarde de l'identité** en un fichier, et une identité illisible est
  désormais écartée au lieu d'être écrasée en silence.
- Interface : « Autour de vous » en listes, badges de transfert sous les pairs,
  symbole HQ dans la barre de statut et le titre.
- **Animations, VFX et sons.** L'émetteur les capte à leur chargement
  (`TransientCapture`, événement Penumbra), les retient par job
  (`TransientMemory`, `transients.json` du personnage), les re-résout à chaque
  construction et écarte les `.pap` qui animent des os que son squelette n'a
  pas (`PapSkeletonCheck`). Le receveur contrôle la forme des fichiers
  (`TransientFileCheck`, règles mesurées sur 5 109 `.pap`) et filtre avant tout
  téléchargement selon le réglage global (barre du haut) et celui du pair
  (bouton du carnet).
- **Course du handshake corrigée** : un premier message scellé arrivé avant la
  fin du handshake chez le répondeur était perdu, ce qui rendait les tests du
  moteur intermittents.

### Ce que le faux pair mesure maintenant

Une apparence réelle de 405 Mo, 70 blobs dont plusieurs de 85 Mo, le vrai
moteur des deux côtés, sans limiteur, un relais qui retarde les paquets :

| | 0 ms | 20 ms | 60 ms |
|---|---|---|---|
| avant le 23, 8 canaux | 12,5 Mo/s | 5,5 Mo/s | 2,0 Mo/s |
| maintenant, 32 canaux | 53,8 Mo/s | 33,0 Mo/s | 17,5 Mo/s |

Au-delà de 32 canaux, c'est instable : 48 canaux se sont effondrés à 3,6 Mo/s à
60 ms. Les mesures d'avant le 23, qui plafonnaient vers 16 canaux, étaient
faussées par un `/tmp` en mémoire saturé par le banc lui-même ; il nettoie
désormais derrière lui.

Commande : `dotnet run -c Release --project Linkpearl.Harness -- fakepeer
--channels 32 --no-limit --latency 20`. Le mode `endtoend` sert un blob à la
fois et ne mesure pas le moteur.

## Ce qui a changé le 24

Deux régressions vues en jeu sur la v0.3.1, à deux comptes sur une machine.

- **Les teintures avancées des armes d'un pair disparaissaient.** L'état
  Glamourer était appliqué avec `ApplyFlag.Once`, qui efface les teintures
  avancées dès que le matériau se recharge avec une autre table de couleurs :
  c'est ce que fait notre redessin sur une arme moddée. Le drapeau est retiré
  (`GlamourerIpc`).
- **Les idles empruntées à une autre race ne partaient pas.** Les échanges de
  chemins de jeu de Penumbra étaient classés (`FileSwap`) puis jetés. Ils
  voyagent maintenant sous une clé facultative du manifeste, que les clients
  plus anciens ignorent, passent la même politique que les chemins de fichier
  avec une extension identique des deux côtés (`FileSwapPolicy`), et suivent le
  blocage des animations. Confirmé en jeu.
- Au passage, **un échange que Penumbra écrit avec des antislashs** (la cible
  d'une animation empruntée) était pris pour un fichier local et signalé absent
  du disque (`ResourcePathClassifier`).

## Ce qui a changé le 25

- **L'interface a pris l'apparence du site et du logo** : nuit marine, halo
  bleu, orange pompon réservé à l'action principale, perle, Fredoka pour les
  titres et Nunito pour le texte. Voir
  `superpowers/specs/2026-09-25-design-nuit-design.md`.
- Inter reste embarqué, fusionné derrière les deux polices, pour les glyphes
  qu'elles n'ont pas. `scripts/generer-polices.sh` régénère les fichiers et
  échoue si un caractère de l'interface n'a plus de police.
- `UiConventionTests` refuse, sous Linux, ce qui contournerait les composants :
  case ImGui brute, en-tête repliable natif, `SameLine()` sans espacement,
  bouton hors de `Btn`, couleur en dur, `BtnTone.Primary`.

## Ce qui a changé le 26

Pendant les essais en jeu de la refonte, hors du plan :

- **La page Pairs dit où en est chaque pair.** Le moteur calcule une phase
  (`PeerPhase`) : recherche au rendez-vous, absent, échec avec l'heure du
  prochain essai, attente de l'apparence, réception, prête hors de vue, posée.
  Jusque-là, l'interface ne regardait que l'existence d'une session, et un
  pair repris s'affichait « hors ligne » pendant toute sa recherche. Un pair
  absent le reste pendant les essais suivants, sans quoi sa ligne clignotait
  entre « recherche » et « absent » toutes les trente secondes.
- **Le nom d'un nouveau groupe** n'a ni espace ni caractère spécial, refusés
  dès la frappe (`GroupPolicyCodec.IsCreatableName`). La règle de réception
  reste plus large : la resserrer ferait rejeter la politique des groupes nés
  avant elle.
- **La sauvegarde** passe en tête des réglages, sous ce nom, et la page Pairs
  la rappelle tant qu'aucune n'a été faite ou restaurée sur ce PC.
- La barre d'état dit « En ligne » ou « Hors ligne » en toutes lettres, et le
  nom du monde plutôt que son numéro.
- **Une pause se dit** (#23) : mis en pause pendant une session, un pair
  l'apprend par un message `Pause` (0x11), le retient dans son carnet et
  l'affiche, jusqu'à la session suivante. Hors session, rien ne part.

## Ce qui reste de mémoire

- **Un client d'avant les tronçons ne peut plus échanger** avec un client
  d'après. Le receveur le dit dans le journal, mais la version du protocole n'a
  pas été relevée, pour ne pas invalider ses vecteurs figés. De même,
  un client d'avant les intégrations refuse les manifestes v2 (« version de
  manifeste inconnue ») : décidé ainsi puisque les transferts étaient déjà
  incompatibles.
- **SimpleHeels garde un décalage reçu** jusqu'à ce qu'on le désenregistre, ce
  qui exige un personnage visible : un pair retiré hors de vue garde son
  décalage chez nous jusqu'au rechargement de SimpleHeels.
- **Le contrôle de squelette n'a aucun test automatique** : il passe par le
  chargeur Havok du jeu. À éprouver en jeu avec une animation d'un squelette
  étendu.
- **Le rythme d'une milliseconde de LiteNetLib** reste à surveiller en jeu :
  rien n'a encore mesuré ce qu'il coûte au processeur.
- **Un service tiers d'avant les trames de bannissement ne bannit personne** :
  il répond « trame inattendue », et le plugin n'a alors aucune liste de lui.
- **Vérifier un joueur visible contre une liste coûte de 0,1 à 0,3 s de
  processeur** par sel de liste, une fois. Seuls les joueurs qui ont le plugin
  ou sont au carnet sont vérifiés, et une liste vide ne coûte rien.
- **Les groupes privés n'ont pas encore été éprouvés en jeu.** Tout ce qui
  décide est testé sous Linux ; l'interface et les boîtes d'admission sur un
  vrai service, non.
- **La dissolution n'est relayée que par le propriétaire** : un membre qu'il
  ne croise plus garde le groupe. L'interface lui dit de garder le groupe
  listé jusqu'à ce que les membres l'aient vu.
- **Un exclu garde le groupe**, exclu, jusqu'à ce qu'une politique plus
  récente le réintègre ou qu'il le quitte (3 octobre) : sinon un modérateur
  hostile dissolvait de fait. Le propriétaire qui le refuse ne lui apporte
  pas la dissolution.
- **Un carnet de groupe plein refuse les nouveaux venus** au lieu d'oublier
  un membre épinglé : 256 membres par groupe privé, 2 048 dans le Public.
  Rien ne permet encore d'oublier un membre à la main.
- **Dans le Public, un inconnu peut usurper le personnage d'un passant**, même
  sans plugin : voir « Modèle de confiance » dans `protocol.md`. Débloquer
  rend le personnage et garde la clé bloquée.
- **Un joueur qui a coupé la détection n'est pas trouvé par ses groupes.** La
  détection interroge les boîtes de présence ; si elle ne tourne pas, un membre
  en groupe ne sera pas composé, même si sa boîte répond.
- **Un service tiers resté à 16 boîtes par session refuse l'ouverture dès huit
  groupes**, et plus tôt au fil des fenêtres : c'est la limite de
  `MaxMailboxesPerSession` du rendez-vous. À relever avant la généralisation.

- **`Une_session_qui_tombe_se_rejoint_sans_attendre` est intermittent** : une
  fois sur une vingtaine, sous la charge de toute la suite. Il laisse environ
  400 ms de temps réel à une poignée de main qui tourne sur le pool de threads ;
  il faudrait l'attendre à l'horloge du test plutôt qu'à des pauses réelles.

## Défauts connus, non corrigés

- **Le handshake SIGMA-I n'a pas eu de relecture formelle, et n'en aura pas.**
  Décidé le 24 (#8) : deux passes de relecture de `protocol.md` ont mené à de
  vraies corrections, livrées en v0.3.0 (#17), et le document dit franchement
  son modèle de confiance et ses limites. L'enjeu est des fichiers de mods : ces
  limites sont un compromis assumé, plus une condition de diffusion.
- **Les trames de contrôle de LiteNetLib restent en clair**, donc falsifiables
  par qui connaît l'adresse. Déni de service, pas atteinte à la confidentialité.
- **Pas de TLS vers le rendez-vous.** Un attaquant actif sur le réseau du
  joueur a les pouvoirs d'un service malveillant, donc peut s'intercaler au
  premier contact. Dit dans le README et le tableau de `protocol.md`.
- **Une boîte personnelle peut être réclamée avant son titulaire**, y compris
  pour une fenêtre à venir. Le titulaire est alerté et ne reçoit plus ses
  demandes sur ce service, mais rien ne l'empêche. Un service d'avant les
  boîtes exclusives n'offre aucune exclusivité.
- **Dans le Public, rien ne prouve qu'un inconnu joue le personnage qu'il
  revendique**, même un joueur sans le plugin. Une clé ne tient plus qu'un
  personnage et un membre épinglé n'est plus évincé, mais une clé neuve ne
  coûte rien : le blocage reste le seul recours.
- **Audit de sécurité du 3 octobre** : corrigé dans les deux dépôts, éprouvé
  sous Linux seulement. À essayer en jeu : la boîte occupée, le bouton
  Accepter grisé hors de vue, l'avertissement de remplacement d'un pairage,
  l'état exclu d'un groupe, le déblocage en deux temps.

## Ce qui attend l'utilisateur en jeu

- **L'avis de pause**, à deux personnages : mettre l'autre en pause pendant
  une session, lire « vous a mis en pause » chez lui, recharger son plugin (la
  marque reste), reprendre (elle s'efface).
- Les **groupes privés**, à deux personnages : créer un groupe, le rejoindre
  par mot de passe puis par validation, exclure un membre, dissoudre.
- **Public**, à deux personnages : activé d'un côté puis des deux, effets
  coupés puis réactivés pour tous puis pour un seul, bloquer, désactiver puis
  réactiver (le blocage reste).
- **Un bannissement** ajouté par la console d'un service local : la puce
  « banni par un service », l'apparence retirée, la demande de pairage ignorée.

## Pièges appris à la dure

- Penumbra applique un fichier nommé par son seul hash : l'extension n'est jamais
  une donnée fournie par le pair.
- Penumbra rend les ressources *chargées* : lues pendant un redessin, elles sont
  incomplètes. Vu en jeu : un fichier sur soixante-quatre, et le pair a reçu les
  objets de base sans aucun mod.
- Passer une clé non nulle à `ApplyState` de Glamourer **verrouille** l'état,
  même sans le drapeau `Lock`. La documentation dit « to unlock or lock ».
- `StateFinalized` de Glamourer ne se lève qu'à la fin d'un changement groupé :
  une retouche manuelle ne lève que `StateChanged`.
- Moodles transporte « Nom@Monde » et des GUID identiques d'un personnage à
  l'autre ; PetNicknames transporte nom, monde et ContentId. Les deux sont
  nettoyés à l'envoi et vérifiés à la réception.
- LiteNetLib ne garantit l'ordre qu'à l'intérieur d'un canal : un tronçon entier
  doit tenir sur un seul canal.
- Sa file d'envoi n'est pas bornée : sans contre-pression, un transfert alloue
  tout d'avance dans le processus du jeu.
- Sa fenêtre fiable est une constante de 64 paquets, libérée au rythme de
  `UpdateTime` : le multi-canal et un rythme rapide sont la condition
  d'existence du transfert, pas une optimisation.
- Un seuil de ping relatif seul ne tient pas sur un lien local : 2 ms qui
  passent à 4, c'est cent pour cent de plus.
- DPAPI lie l'identité au compte Windows : copier le dossier de configuration
  ne survit pas à une réinstallation du système.
- Le runtime .NET ne vérifie pas qu'un point public importé est sur la courbe.
- La réflexion d'adresse doit partir de la socket qui portera les liens.
- Sous WSL, `/tmp` vit en mémoire : un banc qui n'y nettoie pas fausse les
  mesures suivantes avant de tout bloquer.
- `ApplyFlag.Once` de Glamourer n'est pas anodin : l'état posé ne survit pas
  au rechargement d'un matériau, et les teintures avancées partent avec lui.
- Penumbra rend la cible d'un échange de chemin avec des antislashs, comme un
  chemin Windows : ce n'est pas pour autant un fichier local.
- Les bindings ImGui de Dalamud n'ont ni dégradé radial ni ellipse : le halo
  est un éventail de triangles colorés par sommet (`Surface.Halo`), que la carte
  graphique interpole sans marches.
- La liste de dessin d'une fenêtre est rognée en deçà des marges : un fond peint
  depuis `Draw` doit pousser son propre rectangle de rognage, sans intersection.
- `InputTextWithHint` n'accepte pas de filtre de caractères dans les bindings
  de Dalamud : un champ filtré passe par `InputText` et dessine son indication
  à la main (`GroupEntryWindow.DrawHint`).
