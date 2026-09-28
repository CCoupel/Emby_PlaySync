# Emby Shared Playlist

Plugin Emby Media Server pour partager une playlist « À voir » entre plusieurs utilisateurs.

> **État : v1.1.0 (dev 1.1.0.3 sur QUALIF), non encore en production.** Livraison v1.1.0 : page utilisateur PlaySync dans le menu Emby (gestion simplifiée des membres et des options), endpoints non-admin, audit sécurité complet. Versions précédentes (v0.1.0–v1.0.0) : partage natif, retrait automatique du média lu, propagation du flag lu et de l'avancement, permission automatique de partage, encart d'aide. La bascule en production se fait par `/deploy prod`, sur décision explicite.

## Principe

Une playlist appartient à un utilisateur (le propriétaire), qui la partage avec d'autres utilisateurs grâce au partage natif de playlists d'Emby. Le plugin ajoute la gestion de l'état « vu », réglée par **deux étiquettes indépendantes** posées sur la playlist :

| Étiquette | Effet quand elle vaut `OUI` | Version |
|---|---|---|
| `remove-si-lu` | Quand un membre passe un média de non lu à **lu**, il est **retiré de la liste pour tous**. | 0.2.0 |
| `propager-lu` | L'**état de lecture** est copié chez les autres membres : le « lu » (0.3.0), puis l'**avancement de lecture** — position à la pause et à l'arrêt, ≥ 30 s de lecture — pour commencer avec un compte et poursuivre avec l'autre, la dernière lecture gagne (0.3.1). | 0.3.0 / 0.3.1 |

Par défaut, les deux étiquettes sont à `NON` : le plugin ne change rien au comportement natif d'Emby (« legacy »).

## Guide utilisateur

### 1. Permission de partage

Le partage d'une playlist nécessite, côté **propriétaire**, la permission Emby « Permettre le partage de contenus personnels tels que des listes de lecture avec d'autres utilisateurs sur ce serveur » (Tableau de bord → Utilisateurs → l'utilisateur → onglet Profil).

Depuis la v0.4.0, le plugin peut la poser **automatiquement pour tous les comptes** (interrupteur `AutoEnableSharing` dans la configuration du plugin). **Désactivé par défaut depuis v1.0.0** (décision GATE PROD : un élargissement de droits pour tous les comptes n'est pas activé sans revue explicite de l'administrateur, en particulier sur un serveur exposé — voir `security-20260927-221434.md`, finding M1 ; défaut actif de v0.4.0 à v0.5.0, QUALIF non exposé). Si vous l'activez :
- si un administrateur la **décoche manuellement** pour un compte, elle est **réactivée à la passe de réconciliation suivante** (5 min au plus) tant que l'interrupteur global reste actif ;
- seul le **décochage de l'interrupteur global** `AutoEnableSharing` empêche de futures activations ;
- désactiver l'interrupteur **ne révoque jamais** un accès déjà accordé.

Sans activer l'interrupteur, chaque propriétaire doit accorder cette permission lui-même (Tableau de bord → Utilisateurs → son compte → onglet Profil) avant de pouvoir partager une playlist. Les destinataires n'ont rien à activer : leurs droits viennent uniquement du niveau de partage (Écriture/Lecture).

### 2. Partager une liste

**Méthode native (interface Emby)** :
1. Le propriétaire crée sa playlist « À voir ».
2. Menu « … » de la playlist → **Gérer la collaboration**.
3. Choisir pour chaque utilisateur le niveau **Écriture** (peut ajouter et retirer des médias) ou **Lecture** (consultation seule).

**Méthode PlaySync (depuis v1.1.0)** :
1. Le propriétaire ouvre le menu utilisateur (Avatar, en haut à droite) → **PlaySync**.
2. Sélectionner la playlist, ajouter un membre, choisir son niveau, valider.
3. Gérer également les deux options (retrait automatique du média lu, propagation de l'état de lecture) via des interrupteurs simples, sans éditer les étiquettes manuellement.

Seul le propriétaire gère les membres et les étiquettes. Un membre en écriture ne peut ni repartager la liste ni modifier son nom, sa description ou ses étiquettes. Voir [docs/chronogrammes.md §9](docs/chronogrammes.md#9-page-utilisateur-playsync-v110-39) pour le guide complet de la page PlaySync.

Dès qu'une playlist est partagée, le plugin lui ajoute les deux étiquettes **`remove-si-lu=NON`** et **`propager-lu=NON`**, et, si la description est vide, un message d'aide.

### 3. Activer une option : remplacer NON par OUI

Le format des étiquettes est `<option>=NON` ou `<option>=OUI` (casse et espaces autour du `=` sans importance). Pour activer une option :

1. Menu « … » de la playlist → **Modifier les métadonnées**.
2. Section **Mot-clé** (Étiquette) → **Ajouter** `remove-si-lu=OUI` (ou `propager-lu=OUI`), et **retirer** `remove-si-lu=NON` (ou `propager-lu=NON`), **dans la même édition**.
3. Enregistrer. Sans `OUI`, rien ne change.

Règles communes aux deux étiquettes :
- Si `OUI` et `NON` sont présents ensemble, **`NON` l'emporte** : rien ne se passe.
- Le plugin **ne supprime jamais** une étiquette qu'il trouve.
- Retirer `NON` seul ne suffit pas : il faut ajouter `OUI`. Si toutes les étiquettes d'une option sont supprimées, le plugin repose `NON` après quelques minutes (~10 min) ; de même, le message d'aide est réécrit si la description est vidée.
- Les playlists publiques non partagées explicitement sont ignorées.

### 4. Ce que fait chaque option

- **`remove-si-lu=OUI`** : quand un membre (propriétaire, Écriture ou Lecture) fait passer un média à « lu », il est **retiré de la liste pour tous**. Seule la **transition** non lu → lu déclenche le retrait : relire jusqu'au bout un média déjà lu ne le retire pas, et mettre en favori, importer ou masquer un film déjà vu non plus. Pour sortir à la main un média lu resté dans la liste : décocher puis recocher « lu » (un geste volontaire, toujours pris en compte), ou le retirer directement.
- **`propager-lu=OUI`** : l'état de lecture est copié chez les autres membres —
  - le **flag lu** : quand un membre finit un média, il est marqué lu chez les autres (sans jamais modifier un flag déjà posé) ;
  - l'**avancement de lecture** : à la pause ou à l'arrêt d'une lecture d'au moins 30 s, la position est copiée chez les autres membres — commencez avec un compte, poursuivez avec l'autre ; la **dernière lecture gagne**, dans les deux sens.

Les deux options sont indépendantes (l'une sans l'autre est un usage valide) et un média lu n'est retiré que des listes dont son lecteur est membre (pas de transitivité entre listes).

### 5. Limites connues

- **Clients TV et mobile** : la visibilité de la playlist partagée et le retrait fonctionnent (partage natif Emby), mais **poser ou modifier une étiquette depuis un client TV ou mobile n'est pas garanti**. Vérification manuelle au cas par cas (issue #28) : reste ouverte, **non bloquante** pour la livraison.
- Le message écrit dans la description d'une playlist gérée décrit le retrait, la propagation du flag lu et celle de l'avancement de lecture.
- La page de configuration du plugin comporte un encart d'aide (partage natif, les deux étiquettes, permission automatique) et la case `AutoEnableSharing`.

Ce n'est pas une wishlist de demandes de médias (comme Ombi ou Seerr) : la liste ne contient que des médias déjà présents dans la bibliothèque.

## Spécification

Le comportement complet (règles, chronogrammes des cas d'usage, algorithme, décisions) est dans [docs/chronogrammes.md](docs/chronogrammes.md). C'est la référence pour le développement.

## Build

```bash
dotnet build src/EmbySharedPlaylist/EmbySharedPlaylist.csproj --configuration Release --output dist/
```

Produit `dist/EmbySharedPlaylist.dll`. Le SDK Emby (`libs/MediaBrowser.*.dll`) est fourni dans le dépôt.

## Installation

Copier `EmbySharedPlaylist.dll` dans le dossier `plugins/` du serveur Emby (à la racine, pas dans un sous-dossier), puis redémarrer Emby. Les versions sont publiées dans les [Releases](https://github.com/CCoupel/Emby_PlaySync/releases).

## Développement

Voir [CLAUDE.md](CLAUDE.md) pour les instructions de build, de déploiement et l'organisation de l'équipe d'agents.
