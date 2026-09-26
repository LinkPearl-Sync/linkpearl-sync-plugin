<p align="center">
  <img src="Linkpearl/Assets/Images/banner.png" alt="Linkpearl" width="640">
</p>

<p align="center">
  <b>See your friends' modded looks in Final Fantasy XIV,<br>
  straight from player to player, with no server in between.</b>
</p>

<p align="center">
  <a href="https://linkpearl-sync.github.io/">linkpearl-sync.github.io</a> · <a href="https://github.com/orgs/LinkPearl-Sync/projects/3">Roadmap</a>
</p>

<p align="center">
  <b>English</b> · <a href="README.fr.md">Français</a>
</p>

> [!NOTE]
> The plugin's interface is in French for now. Below, each in-game label is quoted exactly as it appears, followed by its meaning.

---

## Install

In game, type `/xlsettings`, open the **Experimental** tab, and paste this address into **Custom Plugin Repositories**:

```
https://linkpearl-sync.github.io/repo.json
```

Click **+**, then **Save**. Then open `/xlplugins`, search for **Linkpearl Sync** and install it.

> [!IMPORTANT]
> Groups and Public need version 0.4.0 or later for every member. Versions before 0.3.0 no longer connect at all: keep the plugin up to date.

**Requirements:** [Penumbra](https://github.com/xivdev/Penumbra) and [Glamourer](https://github.com/Ottermandias/Glamourer), installed and enabled.

---

## What Linkpearl does

You pair with a friend, in game, in two clicks. From then on, each of you sees the other as they dressed up: Penumbra mods, Glamourer state, and what neighbouring plugins show.

<p align="center">
  <img src="docs/images/comment-ca-marche.svg" alt="Two players linked directly by an encrypted connection, with the rendezvous service off to the side" width="608">
</p>

- **Player to player.** Your files go straight from your game to your friend's, end-to-end encrypted. No server stores or redistributes them. When a direct connection is impossible, they pass through the rendezvous service's relay, which carries them without being able to read them.
- **Only with the people you chose.** Nothing is shared unless you both agree.
- **More than outfits.** Modded animations, visual effects and sounds, plus what Customize+, SimpleHeels, Honorific, Moodles and PetNicknames show.

---

## Getting started

### First launch

A short introduction opens and asks you to choose **where to keep the cache** (the looks you receive, kept on your disk so they don't have to be downloaded again) and **its maximum size**. You can change both at any time in the settings.

The plugin window opens with `/lpearl`, or by clicking the Linkpearl entry in the game's server info bar.

### Pairing

<p align="center">
  <img src="docs/images/se-pairer.svg" alt="An orange glyph next to the name, the right-click entry « Linkpearl : demander le pairage », then the other player accepting the request" width="608">
</p>

1. Get close to your friend. An **orange glyph** next to their name means they use Linkpearl.
2. **Right-click** their character, then **Linkpearl : demander le pairage** (request pairing). You can also use the **Autour** (nearby) page of the window.
3. Your friend gets a notification and **accepts** with one click.

That's it: your looks are exchanged whenever you are within range of each other.

### Glyph colours

| Colour | Meaning |
|---|---|
| Green | paired and connected |
| Orange | uses Linkpearl, not paired yet |
| Blue | sent you a pairing request |
| Grey | paired, offline or paused |
| Purple | member of one of your groups |
| Red | paired, but something failed: see the **Pairs** page |

---

## Day to day

<p align="center">
  <img src="docs/images/garder-la-main.svg" alt="A peer row with the animations, effects, sounds and pause toggles" width="608">
</p>

- **Reapply** a look that didn't apply properly: right-click the character, **Linkpearl : réappliquer** (reapply).
- **Direct or relayed**: on the **Pairs** page, an icon next to each connected peer's name shows whether the session is direct or goes through the relay, with the latency in its tooltip.
- **Pause a peer**: **Pairs** page, pause button. The connection closes and their look is removed.
- **Block animations, effects or sounds**: for everyone from the window's title bar, or for a single peer from the **Pairs** page. Nothing you block is downloaded.
- **The cache**: folder and size in **Réglages > Cache** (Settings > Cache). Past the size you chose, the oldest looks go first, never the ones currently in front of you.
- **Rendezvous services**: **Réglages > Réseau** (Settings > Network). The list comes with the project's service; **Ajouter** (add) takes another address, and the refresh button next to a service shows the services it knows, added only once you tick them. **Brider l'envoi** (throttle uploads), off by default, keeps your ping low in dungeons at the cost of much slower outfits.
- **Back up your identity**: **Réglages > Sauvegarde** (Settings > Backup), at the top of the page. A single file, password-protected if you like. After a reinstall or on another PC, restoring it saves you from pairing with everyone again.

---

## Groups

A group syncs all its members with each other, without pairing them one by one: a free company, a roleplay circle. Everything happens on the **Groupes** (Groups) page of the window.

- **Create**: **Créer un groupe** (create a group), give it a name (letters, digits and dashes, no spaces), optionally a password, then **Créer** (create). With a password, any member online lets in whoever knows it. Without one, you or a moderator approve each newcomer.
- **Share the code**: under **Inviter** (invite), the code (`ABCD-EFGH-JKLM@service`) and its **Copier le code** (copy the code) button. Send it by /tell. The page shows it to the owner and the moderators only.
- **Join**: **Rejoindre un groupe** (join a group), paste the code, the password if the group has one, then **Rejoindre** (join). A member must be online to answer, or a moderator if the group approves each newcomer. Joining needs **Me signaler aux autres joueurs** (let other players see me) turned on: that is how the group answers you.
- **Approve**: requests to join show up on the **Demandes** (Requests) page for the owner and the moderators, with **Accepter** (accept) and **Refuser** (decline).
- **Moderate**: on a member's row, **Exclure** (exclude) takes two clicks, and the owner can **Nommer modérateur** (make moderator). Under **Gérer le groupe** (manage the group): an **Admission** panel with **Mot de passe** (password) or **Validation par un modérateur** (approval by a moderator), a **Mot de passe** (password) panel with **Définir** (set) or **Changer** (change) once one is set, the **Exclus** (excluded) list with **Lever** (lift) or **Personne n'est exclu** (no one is excluded) when it's empty, and a **Zone sensible** (danger zone) with **Nouveau code** (new code) and **Dissoudre le groupe** (dissolve the group).
- **Leave or dissolve**: **Quitter le groupe** (leave the group). The owner doesn't leave: they dissolve the group with **Dissoudre le groupe** (dissolve the group), under **Gérer le groupe**. Members find out when they next meet the owner, so keep the group in your list until they have, then **Retirer de la liste** (remove from the list).

The code is a door, not a key: on its own, it lets no one in. After excluding someone who knew the password, change the code and the password.

### Public

At the top of the **Groupes** (Groups) page, the **Public** card, off by default. Click **Activer** (enable), then the first time confirm with **Activer Public** (enable Public): you see the modded appearance of every visible player who enabled it too, and they see yours, with no code and no pairing. They are strangers: their animations, VFX and sounds are off by default. Under **Effets reçus** (effects received), the **Animations**, **VFX** and **Sons** (sounds) buttons turn them back on for all of Public; under **Joueurs rencontrés** (players met), a player's effects button sets them for that player alone, and **Suivre le Public** (follow Public) brings them back to the shared setting. The block button, in two clicks, means they no longer see you and you no longer see them; the **Bloqués** (blocked) list lets you **Débloquer** (unblock). **Désactiver** (disable) keeps your blocks for next time.

### Services' ban lists

Each rendezvous service in your list can publish a list of banned characters. A character listed by one of your services is not paired, not admitted into your groups, not seen through Public, and their appearance is not applied on your side, even if they are one of your pairs. Their row carries the **banni par un service** (banned by a service) chip, with the reason on hover. Such a list is a matter of reputation, not proof: turning a service off or removing it from your settings lifts its bans. Open-circle services cannot ban anyone.

---

## The open circle

Once paired, you and your pair also meet through two services run by volunteers, picked from a list signed by the project's authority (rdv.linkpearl.eorzea.events). A service enters it on its own, after 72 hours of answering at least 95% of checks, and never sees a key or a character name: pairing itself, Public and group members you have not met yet stay on the services in your list. If neither of your two services answers within 10 seconds, or no valid list is held, everything goes back to your list. The list is fetched again every six hours. The **Cercle ouvert** (open circle) switch, under **Réglages > Réseau** (Settings > Network), is on by default. [Who is in it](https://linkpearl-sync.github.io/reseau.html).

---

## Privacy

- Your files leave your game only for your pairs, the members of your groups and, if you enabled Public, the visible players who enabled it too. Always end-to-end encrypted.
- For two players to find each other, Linkpearl goes through **rendezvous services**. They never see your files or your looks. The services in your list do see pairing requests go by, with the characters' names and public keys, and the IP addresses of those who use them.
- Open-circle services see your IP address, when you and a pair announce yourselves, and the volume of a relayed session, but no name, key or file.
- If you keep **Me signaler aux autres joueurs** (let other players see me, on by default), the service can know that your character is online: that is what lets others recognise you. You can turn it off in **Réglages > Visibilité** (Settings > Visibility).
- Joining a group also goes through the rendezvous service, which sees the code and the character's name and world. As with pairing, trust is established on first contact: for a group with a password, a long password is what really protects it.
- You can host your own rendezvous service in one command: see [Host a rendezvous](https://linkpearl-sync.github.io/heberger.html) (Linux with systemd; the server then updates itself), or the [step-by-step guide](https://linkpearl-sync.github.io/expert.html#self-host). You and your friends just need to share at least one. By default a new service also applies to the open circle, and joins it after 72 hours of reliable answers (`--no-announce` to stay out).

---

## Questions or problems?

Open an [issue](https://github.com/LinkPearl-Sync/linkpearl-sync-plugin/issues) describing what you did and what you saw. The Dalamud log (`/xllog`) helps a lot. Issues in English or French are both welcome.

For the curious, [the technical page](https://linkpearl-sync.github.io/expert.html) explains connections, encryption and federation, and [the network page](https://linkpearl-sync.github.io/reseau.html) shows the open circle live. For contributors, the design and the protocol are described in [`docs/`](docs/) (in French).
