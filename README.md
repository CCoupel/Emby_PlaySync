# Emby Shared Playlist

Plugin Emby Media Server pour partager une playlist « À voir » entre plusieurs utilisateurs.

> **État : en cours de conception.** La spécification est validée, le code du plugin n'est pas encore écrit.

## Principe

Une playlist appartient à un utilisateur (le propriétaire), qui la partage avec d'autres utilisateurs. Le plugin s'appuie sur le partage natif de playlists d'Emby et ajoute la gestion de l'état « vu » :

- Quand un membre termine un média, il est **retiré de la liste**.
- L'état **lu** est **propagé aux autres membres** du groupe (option par liste, activée par défaut).
- Un propriétaire peut avoir **plusieurs listes**, chacune avec ses propres membres. Un média lu n'est retiré que des listes dont son lecteur est membre.
- Seul le propriétaire gère les membres ; les autres peuvent ajouter et retirer des médias.

Ce n'est pas une wishlist de demandes de médias (comme Ombi ou Seerr) : la liste ne contient que des médias déjà présents dans la bibliothèque.

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
