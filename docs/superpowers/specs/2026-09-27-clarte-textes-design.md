# Clarté des textes du plugin

## But

Rendre l'interface compréhensible par un joueur qui découvre Linkpearl, sans
changer le protocole, les comportements ni le vocabulaire propre au produit.
Le joueur doit comprendre ce que fait une action, pourquoi un état apparaît et
quoi faire après un échec.

## Vocabulaire

« Pair » et « pairage » restent les termes de Linkpearl. La présentation
définit un pair dès la première occurrence comme un joueur ajouté aux contacts
Linkpearl. Les écrans suivants peuvent ensuite employer le terme sans répéter
sa définition.

Les termes techniques visibles sont reformulés ou introduits :

- « glyphe » devient « icône à côté du nom » ;
- « VFX » devient « effets visuels (VFX) » à sa première occurrence ;
- « Public » devient « Mode public » dans les textes visibles ;
- « identité » est précédé du bénéfice concret, sauvegarder les personnages et
  leurs pairages ;
- « cache » est décrit comme le stockage local qui évite les nouveaux
  téléchargements.

## Parcours et confiance

La présentation explique le fonctionnement réel : les données passent
directement entre les joueurs quand c'est possible ; sinon, un service
Linkpearl les relaie sans pouvoir les lire ; aucun fichier n'est conservé par
le service.

Une demande de pairage ou d'entrée dans un groupe rappelle de n'accepter qu'un
personnage reconnu. « Hors de vue » précise que Linkpearl ne peut pas confirmer
que le personnage se trouve actuellement à proximité.

Le Mode public dit explicitement qu'il partage aussi avec des joueurs inconnus
qui l'ont activé. Sa confirmation conserve les deux choix actuels et ne change
pas son fonctionnement.

## États et réglages

Les états décrivent un résultat visible plutôt qu'un état interne :

- « apparence posée » devient « apparence appliquée » ;
- « prêt, hors de vue » devient « prêt quand le joueur sera visible » ;
- « attend son apparence » devient « attend les données d'apparence » ;
- « pause du pair » devient « mis en pause par ce joueur » ;
- l'absence d'un service devient « joueur hors ligne ou en pause ».

La barre inférieure décrit la disponibilité du service Linkpearl, et non l'état
global du plugin. Le curseur du cache porte le libellé visible « Taille maximale
du cache ».

## Erreurs

Les exceptions, noms d'états internes et messages du système ne sont plus
affichés directement au joueur. Le détail reste dans le journal. Le message
visible indique l'opération qui a échoué et une action raisonnable : vérifier
l'adresse, l'emplacement ou la connexion, réessayer, puis consulter le journal
si le problème persiste.

Les refus métier déjà écrits pour un joueur, comme un mot de passe incorrect ou
une demande expirée, restent précis et visibles.

## Cohérence éditoriale

Les textes emploient des phrases complètes, des pluriels naturels et une même
voix. Les formes comme `service(s)` disparaissent de l'interface. Les boutons
gardent des verbes courts à l'infinitif. Les explications privilégient les
conséquences pour le joueur et évitent les détails d'implémentation.

## Vérification

Des tests sur les sources embarquées de l'interface protègent les formulations
critiques : définition de « pair », description du relais, avertissement des
demandes, nom « Mode public », libellé du cache et absence des formulations
techniques remplacées. Les tests existants du noyau et la compilation du plugin
doivent rester sans erreur ni avertissement.

## Hors périmètre

Cette passe ne renomme aucun type, fichier, commande ou concept interne. Elle ne
change ni le réseau, ni le pairage, ni le stockage, ni les règles de groupe.
Elle ne refond pas la mise en page au-delà de l'ajout de courts libellés ou
avertissements nécessaires à la compréhension.
