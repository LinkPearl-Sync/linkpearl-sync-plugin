<p align="center">
  <img src="Linkpearl/Assets/Images/banner.png" alt="Linkpearl" width="640">
</p>

<p align="center">
  <b>Voyez les apparences moddées de vos amis dans Final Fantasy XIV,<br>
  directement de joueur à joueur, sans serveur au milieu.</b>
</p>

<p align="center">
  <a href="https://linkpearl-sync.github.io/">linkpearl-sync.github.io</a> · <a href="https://github.com/orgs/LinkPearl-Sync/projects/3">Feuille de route</a>
</p>

<p align="center">
  <a href="README.md">English</a> · <b>Français</b>
</p>

---

## Installer

Dans le jeu, tapez `/xlsettings`, ouvrez l'onglet **Experimental**, et collez cette adresse dans **Custom Plugin Repositories** :

```
https://linkpearl-sync.github.io/repo.json
```

Cliquez sur **+**, puis sur **Enregistrer**. Ouvrez ensuite `/xlplugins`, cherchez **Linkpearl Sync** et installez-le.

> [!IMPORTANT]
> Les groupes et le Public demandent la version 0.4.0 ou plus pour chaque membre. Les versions antérieures à 0.3.0 ne se connectent plus du tout : gardez le plugin à jour.

**Prérequis :** [Penumbra](https://github.com/xivdev/Penumbra) et [Glamourer](https://github.com/Ottermandias/Glamourer), installés et activés.

---

## Ce que fait Linkpearl

Vous vous pairez avec un ami, en jeu, en deux clics. Dès lors, chacun voit l'autre tel qu'il s'est habillé : mods Penumbra, état Glamourer, et ce que montrent les plugins voisins.

<p align="center">
  <img src="docs/images/comment-ca-marche.svg" alt="Deux joueurs reliés directement par un lien chiffré, le service de rendez-vous à l'écart" width="608">
</p>

- **De joueur à joueur.** Vos fichiers passent directement de votre jeu à celui de votre ami, chiffrés de bout en bout. Aucun serveur ne les stocke ni ne les redistribue. Quand la connexion directe est impossible, ils passent par le relais du service de rendez-vous, qui les transporte sans pouvoir les lire.
- **Seulement avec qui vous avez choisi.** Rien ne s'échange sans l'accord des deux.
- **Au-delà des tenues.** Animations, effets visuels et sons moddés, et ce que montrent Customize+, SimpleHeels, Honorific, Moodles et PetNicknames.

---

## Premiers pas

### Au premier lancement

Une courte présentation s'ouvre et vous fait choisir **où ranger le cache** (le stockage local des apparences reçues, qui évite de les télécharger à nouveau) et sa **Taille maximale du cache**. Vous pourrez la revoir à tout moment depuis les réglages.

La fenêtre du plugin s'ouvre avec `/lpearl`, ou en cliquant sur l'entrée Linkpearl de la barre de statut du jeu.
En bas de la fenêtre, **Service disponible** ou **Service indisponible** indique si le service de rendez-vous répond. Une session déjà établie peut continuer même si le service devient indisponible.

### Se pairer

<p align="center">
  <img src="docs/images/se-pairer.svg" alt="Une icône orange à côté du nom, le clic droit « Linkpearl : demander le pairage », puis la demande acceptée par l'autre" width="608">
</p>

Un **pair** est un joueur ajouté à vos contacts Linkpearl.

1. Approchez-vous de votre ami. Une **icône orange** à côté de son nom signale qu'il utilise Linkpearl.
2. **Clic droit** sur son personnage, puis **Linkpearl : demander le pairage**. Vous pouvez aussi passer par la page **À proximité** de la fenêtre.
3. Votre ami reçoit une notification et **accepte** d'un clic.

C'est tout : vos apparences s'échangent dès que vous êtes à portée l'un de l'autre.

Le pairage se fait face à face : **Accepter** reste grisé tant que le demandeur est hors de vue, et une réponse d'un joueur que vous ne voyez pas est ignorée. Si deux réponses différentes arrivent au même nom, Linkpearl annule le pairage par prudence : redemandez-le, l'un à côté de l'autre. Si la fenêtre signale que votre boîte aux lettres sur un service est tenue par une autre connexion, et que vous n'avez pas d'autre jeu ouvert, quelqu'un tente peut-être d'intercepter vos demandes de pairage.

### Les couleurs des icônes

| Couleur | Signification |
|---|---|
| Vert | pairé et connecté |
| Orange | utilise Linkpearl, pas encore pairé |
| Bleu | demande de pairage reçue |
| Gris | pairé, hors ligne ou en pause |
| Violet | membre d'un de vos groupes |
| Rouge | pairé, mais quelque chose a échoué : voir la page **Pairs** |

---

## Au quotidien

<p align="center">
  <img src="docs/images/garder-la-main.svg" alt="Une ligne de pair avec les bascules animations, effets, sons et pause" width="608">
</p>

- **Réappliquer** une apparence qui s'est mal posée : clic droit sur le personnage, **Linkpearl : réappliquer**.
- **Direct ou relayé** : sur la page **Pairs**, une icône à côté du nom de chaque pair connecté indique si la session est directe ou passe par le relais, avec la latence en infobulle.
- **Mettre un pair en pause** : page **Pairs**, bouton pause. La connexion se ferme et son apparence est retirée.
- **Bloquer animations, effets ou sons** : pour tout le monde depuis la barre de titre de la fenêtre, ou pour un seul pair depuis la page **Pairs**. Rien n'est téléchargé de ce que vous bloquez.
- **Le cache** : dossier et taille dans **Réglages > Cache**. Au-delà de la taille choisie, les apparences les plus anciennes partent, jamais celles que vous avez sous les yeux.
- **Sauvegarder vos personnages et leurs pairages** : **Réglages > Sauvegarde**, en tête de page. Un seul fichier, protégé par un mot de passe si vous le souhaitez, permet de reprendre la même identité Linkpearl après une réinstallation ou sur un autre PC.

---

## Groupes

Un groupe synchronise tous ses membres entre eux, sans les pairer un à un : une compagnie libre, un cercle de jeu de rôle. Tout se passe sur la page **Groupes** de la fenêtre.

- **Créer** : **Créer un groupe**, donnez-lui un nom (lettres, chiffres et tirets, sans espace), un mot de passe si vous le souhaitez, puis **Créer**. Avec un mot de passe, n'importe quel membre en ligne fait entrer qui le connaît. Sans mot de passe, vous ou un modérateur validez chaque entrée.
- **Partager le code** : sous **Inviter**, le code (`ABCD-EFGH-JKLM@service`) et son bouton **Copier le code**. Envoyez-le par /tell. La page ne le montre qu'au propriétaire et aux modérateurs.
- **Rejoindre** : **Rejoindre un groupe**, collez le code, le mot de passe si le groupe en a un, puis **Rejoindre**. Un membre doit être en ligne pour vous répondre, ou un modérateur si le groupe valide chaque entrée. Le réglage **Être visible par les autres joueurs** doit être activé : c'est par là que le groupe vous répond.
- **Valider** : les demandes d'entrée arrivent sur la page **Demandes** du propriétaire et des modérateurs, avec **Accepter** et **Refuser**.
- **Modérer** : sur la ligne d'un membre, **Exclure** se fait en deux clics, et le propriétaire peut **Nommer modérateur**. Sous **Gérer le groupe** : un panneau **Admission** avec **Mot de passe** ou **Validation par un modérateur**, un panneau **Mot de passe** avec **Définir** ou **Changer** une fois qu'il y en a un, la liste **Exclus** avec **Lever** ou **Aucun joueur exclu.** quand elle est vide, et une **Zone sensible** avec **Nouveau code** et **Dissoudre le groupe**.
- **Quitter ou dissoudre** : **Quitter le groupe**. Le propriétaire ne quitte pas : il dissout le groupe avec **Dissoudre le groupe**, sous **Gérer le groupe**. Les membres l'apprennent en le croisant, gardez donc le groupe dans la liste jusqu'à ce qu'ils l'aient vu, puis **Retirer de la liste**.

Le code est une porte, pas une clé : seul, il ne fait entrer personne. Après avoir exclu quelqu'un qui connaissait le mot de passe, changez le code et le mot de passe.

### Mode public

En tête de la page **Groupes**, le **Mode public** est désactivé par défaut. La première activation se confirme avec **Activer le mode public**. Vous voyez alors l'apparence moddée de tout joueur visible qui l'a activé, et il voit la vôtre, sans code ni pairage, même si vous ne vous connaissez pas. Les animations, effets visuels (VFX) et sons de ces inconnus sont coupés par défaut. Les boutons **Animations**, **VFX** et **Sons** les rétablissent pour tout le Mode public ; sous **Joueurs rencontrés**, le bouton d'effets d'un joueur le règle à part, et **Suivre le mode public** le ramène au réglage commun. **Bloquer**, en deux clics, arrête tout partage d'apparence avec ce joueur ; la liste **Bloqués** permet de **Débloquer**. Désactiver le Mode public garde vos blocages pour la prochaine fois.

Linkpearl ne peut pas prouver qu'un inconnu du Mode public joue vraiment le personnage qu'il affiche, même pour un joueur qui n'a pas le plugin : bloquez toute apparence qui vous semble usurpée. Débloquer libère d'abord le personnage ; l'identité Linkpearl bloquée reste dans la liste (**identité Linkpearl bloquée**) jusqu'à ce que vous la débloquiez aussi.

### Listes de bannissement des services

Chaque service de rendez-vous peut publier une liste de personnages bannis. Un personnage listé par l'un de vos services n'est ni pairé, ni admis dans vos groupes, ni vu par le Mode public, et son apparence n'est pas appliquée chez vous, même s'il est dans vos pairs. Sa ligne porte la puce **bloqué par un service**, avec le motif au survol. Une telle liste relève de la réputation, pas de la preuve : retirer un service de vos réglages lève ses bannissements.

---

## Vie privée

- Vos fichiers ne quittent votre jeu que vers les pairs que vous avez acceptés, chiffrés de bout en bout.
- Pour que deux joueurs se trouvent, Linkpearl passe par un **service de rendez-vous**. Il ne voit jamais vos fichiers ni vos apparences. Il voit en revanche passer les demandes de pairage, avec les noms des personnages et leurs clés publiques, ainsi que les adresses IP de ceux qui l'utilisent.
- Si vous activez **Être visible par les autres joueurs** (réglage par défaut), le service peut savoir que votre personnage est en ligne, comme quiconque l'interroge sur le nom de votre personnage : c'est ce qui permet aux autres de vous reconnaître. Vous pouvez le désactiver dans **Réglages > Visibilité**.
- La connexion au service de rendez-vous n'est pas chiffrée. Quelqu'un qui contrôle votre réseau (un wifi public, par exemple) voit ce que voit le service, et pourrait s'intercaler lors d'un premier pairage, comme le pourrait un service malhonnête. Les sessions entre joueurs restent chiffrées de bout en bout, et un pair déjà accepté ne peut pas être usurpé.
- Rejoindre un groupe passe aussi par le service de rendez-vous, qui voit le code, ainsi que le nom et le monde du personnage. Comme pour le pairage, la confiance s'établit au premier contact : pour un groupe à mot de passe, c'est un mot de passe long qui le protège vraiment.
- Vous pouvez héberger votre propre service de rendez-vous en une commande : voir [Héberger un rendez-vous](https://linkpearl-sync.github.io/heberger.html#fr) (Linux avec systemd ; le serveur se met ensuite à jour seul), ou le [guide pas à pas](https://linkpearl-sync.github.io/expert.html#self-host). Vous et vos amis devez simplement en partager au moins un. Par défaut, un nouveau service se porte aussi candidat au réseau ouvert, et y entre après 72 heures de réponses fiables (`--no-announce` pour s'en tenir à l'écart).

---

## Une question, un souci ?

Ouvrez une [issue](https://github.com/LinkPearl-Sync/linkpearl-sync-plugin/issues) en décrivant ce que vous avez fait et ce que vous avez vu. Le journal de Dalamud (`/xllog`) aide beaucoup. Les issues en français ou en anglais sont également les bienvenues.

Pour les curieux, [la page technique](https://linkpearl-sync.github.io/expert.html) explique la connexion, le chiffrement et la fédération, et [la page du réseau](https://linkpearl-sync.github.io/reseau.html#fr) montre le réseau ouvert en direct. Pour les contributeurs, la conception et le protocole sont décrits dans [`docs/`](docs/).
