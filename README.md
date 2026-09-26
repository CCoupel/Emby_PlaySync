# Emby Shared Playlist

Plugin Emby Media Server pour partager une playlist « À voir » entre plusieurs utilisateurs.

> **État : en cours de conception.** La spécification est validée, le code du plugin n'est pas encore écrit.

## Principe

Une playlist appartient à un utilisateur (le propriétaire), qui la partage avec d'autres utilisateurs grâce au partage natif de playlists d'Emby (menu « … » → **Gérer la collaboration**). Le plugin ajoute la gestion de l'état « vu », **uniquement sur les playlists dont le marqueur est actif** :

- Dès qu'une playlist est partagée, le plugin lui pose l'étiquette **`propager-lu=NON`** (et, si la description est vide, un message d'aide). Tant que l'étiquette est `NON`, **rien ne change** : Emby se comporte comme d'habitude.
- Pour activer le plugin sur une liste, le propriétaire **remplace `propager-lu=NON` par `propager-lu=OUI`** (ajouter `OUI` et retirer `NON`, dans la même édition ; si les deux sont présents, `NON` l'emporte). Sans `OUI`, rien ne change.
- Avec `OUI` : quand un membre termine un média, il est **retiré de la liste** et l'état **lu** est **propagé aux autres membres**. Un média lu n'est retiré que des listes dont son lecteur est membre (pas de transitivité entre listes).
- Avec `OUI`, la **position de lecture** est aussi propagée à l'arrêt ou à la pause : on commence avec un compte, on poursuit avec l'autre (la dernière lecture gagne). *Prévu en v0.3.1.*
- Seul le propriétaire gère les membres et les étiquettes ; les membres en écriture peuvent ajouter et retirer des médias.

Ce n'est pas une wishlist de demandes de médias (comme Ombi ou Seerr) : la liste ne contient que des médias déjà présents dans la bibliothèque.

### Permission de partage

Emby masque le partage tant que l'utilisateur n'a pas la permission « Permettre le partage de contenus personnels tels que des listes de lecture avec d'autres utilisateurs sur ce serveur » (Tableau de bord → Utilisateurs → l'utilisateur → onglet Profil). Elle n'est requise que pour le propriétaire. Le plugin la pose **automatiquement pour tous les utilisateurs** (interrupteur dans la configuration, actif par défaut ; le désactiver ne retire rien).

### Limites

Les applis TV et mobile n'ont pas été vérifiées (visibilité, retrait, édition des étiquettes, reprise de lecture). Le guide utilisateur complet est au §8 de la spécification.

## Spécification

Le comportement complet (règles, chronogrammes des cas d'usage, algorithme, décisions) est dans [docs/chronogrammes.md](docs/chronogrammes.md). C'est la référence pour le développement.

## Build

```bash
dotnet build src/EmbySharedPlaylist/EmbySharedPlaylist.csproj --configuration Release --output dist/
```

Produit `dist/EmbySharedPlaylist.dll`. Le SDK Emby (`libs/MediaBrowser.*.dll`) est fourni dans le dépôt.

## Installation

Copier `EmbySharedPlaylist.dll` dans le dossier `plugins/` du serveur Emby (à la racine, pas dans un sous-dossier), puis redémarrer Emby. Les versions sont publiées dans les [Releases](https://github.com/CCoupel/Emby_shared_playlist/releases).

## Développement

Voir [CLAUDE.md](CLAUDE.md) pour les instructions de build, de déploiement et l'organisation de l'équipe d'agents.
