# Avis de pause

Écrit le 26 septembre 2026. Issue [#23](https://github.com/LinkPearl-Sync/linkpearl-sync-plugin/issues/23).

## Pourquoi

Mettre un pair en pause ferme la session et cesse de le chercher. De son côté,
rien ne distingue alors la pause d'un départ du jeu : sa page Pairs nous dit
« absent », indéfiniment. Il ne sait pas qu'il doit attendre qu'on le reprenne,
et peut croire à une panne.

## Ce que l'utilisateur a choisi

- **L'avis ne part que sur une session ouverte au moment de la pause.** Un pair
  hors ligne à ce moment continue de nous voir absent. Une pause ne doit jamais
  obliger à le chercher, ce qui lui apprendrait quand on se connecte.
- **Celui qui reçoit l'avis le retient dans son carnet**, au-delà d'un
  rechargement du plugin, jusqu'à ce qu'une session se rouvre.

## Le protocole

Un type de message neuf :

```
Pause = 0x11, sans charge utile, sur le canal de contrôle
```

Comme `Unpair` (0x0F) : la session chiffrée dit déjà de qui il vient, et seul
ce pair peut l'envoyer. Un client qui ne le connaît pas l'ignore, ce que la
couche applicative fait déjà de tout type inconnu. Rien à négocier, aucune
incompatibilité.

Écartés :

- **Réutiliser `Bye` (0x0E)**, déclaré mais jamais envoyé ni lu : lui donner un
  sens après coup rendrait ambigu tout usage futur.
- **Une pause douce**, session gardée ouverte sans apparence : elle révélerait
  qu'on est en ligne, et changerait ce que « pause » veut dire aujourd'hui (la
  session se ferme, l'apparence est retirée).

`protocol.md` le documente avec les autres types et dans « Versionnage ».

## Chez celui qui met en pause

Quand le carnet passe un pair en pause et qu'une session est ouverte avec lui,
le moteur envoie `Pause` sur cette session, puis la ferme comme aujourd'hui.
Sans session ouverte, rien ne part.

L'envoi est au mieux : si la trame n'arrive pas avant la fermeture, l'autre
nous voit absent, ce qui est le comportement d'avant. Pas de nouvel essai.

Bloquer n'envoie rien : un blocage ne doit rien révéler. Retirer un pair garde
son avis propre (`Unpair`).

## Chez celui qui reçoit l'avis

- Le moteur marque le pair `PausedByPeer` dans le carnet et lève un événement,
  pour que l'hôte enregistre le carnet, comme il le fait pour `PairEnded`.
- La session tombe ensuite ; le moteur continue de chercher le pair au rythme
  d'un absent (une annonce de 25 s toutes les 30 s), sans quoi la reprise ne
  serait jamais remarquée. Rien ne change à ce rythme.
- **La première session rouverte efface la marque**, et la fait enregistrer.
  C'est la reprise, vue d'ici.
- Un pair `PausedByPeer` que nous mettons nous-mêmes en pause garde la marque :
  elle sera effacée à la prochaine session, quel que soit celui qui reprend.

### Le carnet

`PairRecord` gagne `bool PausedByPeer`, faux par défaut. `PairBookStore` l'écrit
dans le JSON du carnet ; un carnet écrit avant ce champ se relit avec `false`.
La sauvegarde en un fichier transporte le carnet tel quel, donc la marque avec.

### La phase

`PeerPhase` gagne `PausedByPeer`. `PeerPhases.Of` reçoit la marque, et la fait
passer avant « absent », « recherche » et « échec » tant qu'aucune session
n'est ouverte : pendant les essais qui suivent, la ligne ne clignote pas. Une
session ouverte l'emporte, puisqu'elle efface la marque.

### L'interface

Page Pairs, pour cette phase :

- la puce « vous a mis en pause », jaune comme la pause, icône de pause ;
- l'infobulle « Il vous reprendra quand il le voudra ; la synchronisation
  reviendra alors d'elle-même. » ;
- le pair reste dans « En attente de lien », et Réappliquer reste grisé.

Pas de message dans le chat : la page Pairs suffit, et un avis de pause
annoncé à voix haute aurait l'air d'une accusation.

## Tests

Sous Linux, dans les tests du noyau :

- `PeerPhases.Of` : `PausedByPeer` passe avant absent, recherche et échec sans
  session ; une session l'emporte.
- Deux moteurs : Alice met Bob en pause pendant une session ; chez Bob, le pair
  Alice est marqué et sa phase devient `PausedByPeer`. Alice reprend : la
  session se rouvre et la marque disparaît.
- Sans session ouverte au moment de la pause, rien n'est envoyé.
- Un blocage n'envoie rien.
- `PairBook` : la marque se pose, s'efface, et ne change pas le fait d'être actif.

`PairBookStore` vit dans l'intégration et ne compile pas sous Linux : la
relecture d'un ancien carnet sans le champ se vérifie par construction (paramètre
avec valeur par défaut), puis en jeu en rechargeant le plugin.

## Hors périmètre

- Les membres de groupe : on les bloque, on ne les met pas en pause.
- Dire depuis quand : la date de la pause n'est pas transmise.
