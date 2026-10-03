# Le pairage, et pourquoi il ne ressemble pas à ce que la spec prévoyait

Écrit le 22 septembre 2026, après trois itérations en jeu.

## Ce que la spec prévoyait, et pourquoi c'était faux

Un code d'invitation portant la clé publique complète, échangé hors du jeu. Le
raisonnement était juste sur le plan cryptographique et faux sur le plan humain :

1. **Cent vingt et un caractères, puis soixante-sept.** Toujours trop pour
   quelque chose qu'on envoie à quelqu'un.
2. **La vérification par six mots comparés de vive voix** supposait que les gens
   se parlent sur Discord. Ils ne le font pas. Au mieux ils s'échangent quelque
   chose par message privé en jeu.
3. Plus profondément : **je concevais le pairage comme un échange de secrets
   entre inconnus, alors que ce sont deux personnes debout l'une à côté de
   l'autre dans le même jeu.**

## Ce qui remplace

**On ne montre jamais une clé à un utilisateur.** Il voit des noms de
personnage, qui sont l'identité qui l'intéresse : il veut voir les mods de
Jhalen Tavari, pas ceux de la clé 4QF0WG.

### La boîte aux lettres

Chaque plugin dépose au rendez-vous une boîte dont l'adresse se calcule depuis
le nom et le monde du personnage :

```
adresse = SHA-256("linkpearl:mbox:v1" || nom@monde || fenêtre)[0..6]
```

Quiconque voit le personnage peut donc calculer l'adresse. C'est délibéré, et
cela donne les deux fonctions d'un coup :

- **La détection** : la boîte existe, donc la personne utilise Linkpearl. Le
  plugin peut la signaler parmi les joueurs alentour.
- **La demande** : on cible quelqu'un, on dépose une demande dans sa boîte, il
  voit une fenêtre « untel veut se synchroniser avec vous » et accepte ou refuse.

### Le prix, énoncé sans détour

Pour qu'un inconnu puisse reconnaître un joueur, l'adresse doit dériver de son
nom. L'espace des noms de FFXIV est énumérable. **Donc l'opérateur du
rendez-vous peut savoir qui est en ligne et quand.**

C'est exactement ce que faisait Mare, et exactement ce que ce projet prétendait
éviter. Les jetons tournants n'y changent rien : ils empêchent de relier deux
fenêtres dans le temps, pas de deviner un nom dans une fenêtre donnée.

Ce n'est pas une faute de conception, c'est une conséquence logique : **être
découvrable, c'est être découvrable.** On ne peut pas à la fois permettre à un
inconnu de vous reconnaître et empêcher le serveur de le faire.

Décision prise : **la détection est active par défaut**, parce qu'une fonction
désactivée par défaut n'existe pas, avec un réglage pour s'en retirer et une
phrase claire à la première utilisation. Ce qui n'est toujours pas révélé au
serveur : les manifestes et les fichiers. Le contenu des demandes, lui, lui est
visible : nom, monde, clé publique, aléa et clés éphémères voyagent en clair.
Le secret de paire, lui, vient de l'accord des éphémères, qu'un service qui se
contente de regarder ne peut pas calculer. Voir `protocol.md`.

### Ce qui reste protégé

| Le rendez-vous voit | Il ne voit pas |
|---|---|
| Qu'un nom de personnage est en ligne | Les manifestes |
| Les adresses IP | Les fichiers |
| Le contenu des demandes (nom, monde, clés) | Le contenu des apparences |
| | Les échanges une fois la session établie |

Sans TLS vers le service, tout ce que voit la colonne de gauche se lit aussi
sur le réseau, entre le joueur et lui.

### Ce qui remplace la vérification par six mots

Le nom du personnage. Celui qui reçoit une demande voit **de qui elle vient**,
et peut vérifier d'un coup d'œil que cette personne est bien devant lui. C'est
une vérification que les gens font naturellement, contrairement à la
comparaison de six mots sur un canal qu'ils n'utilisent pas.

Un opérateur de rendez-vous malveillant peut toujours s'intercaler. C'est
assumé : pour un cercle qui héberge son propre service, l'opérateur est l'un
d'eux. Et comme la liaison avec le service n'a pas de TLS, un attaquant actif
sur le chemin réseau d'un joueur peut en faire autant.

**Jusqu'en octobre 2026, n'importe quel client le pouvait aussi**, sans être
le service. L'adresse d'une boîte dérive d'un nom que tout le monde voit, et
rien n'empêchait de l'ouvrir en même temps que son titulaire : on y lisait ses
demandes, on y répondait avant lui, et le demandeur épinglait le nom du
titulaire sur la clé de l'intrus. Trois garde-fous depuis :

- **La boîte personnelle est réclamée**, pour une seule connexion, sur un
  service à jour. Qui la réclame avant son titulaire la garde, mais le
  titulaire est averti, dans le chat et dans l'interface : « Votre boîte aux
  lettres sur … est tenue par une autre connexion ». Un service ancien, lui,
  ne garantit aucune exclusivité, et le plugin s'y replie sur l'ouverture
  partagée d'avant.
- **Une réponse ne conclut que si celui qu'elle annonce est visible**, nom et
  monde, chez le demandeur. C'est la règle du face à face, appliquée par le
  plugin et plus seulement par le joueur.
- **Deux réponses différentes à la même demande annulent le pairage**, pendant
  dix minutes après la première : rien ne dit laquelle était la bonne, et il
  faut redemander en face à face.

Aucun ne retire au service son pouvoir : il voit passer les clés, et peut les
remplacer.

## Ce qui garde les codes

Les groupes. Un groupe n'a pas de nom de personnage, donc il lui faut bien un
identifiant à échanger : douze caractères suivis du service
(`ABCD-EFGH-JKLM@rdv.exemple`), que le propriétaire et les modérateurs copient
depuis la page Groupes et envoient par /tell.

Ce code est **une porte, pas une clé**. Il mène à la boîte d'admission du
groupe, où un membre en ligne demande le mot de passe, ou bien un modérateur
valide l'entrée ; il ne donne jamais le secret du groupe à lui seul. Il se
change sans rien redistribuer : l'ancien ne mène plus nulle part, et les
membres restent. La même confiance au premier contact que le pairage s'y
applique, pour les mêmes raisons. Voir
`superpowers/specs/2026-09-24-groupes-design.md` et la section « Groupes
privés » de `protocol.md`.
