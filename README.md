# Emby Shared Playlist

Plugin Emby Media Server pour partager une playlist « À voir » entre plusieurs utilisateurs.

> **État : en développement (v0.2.0, QUALIF seulement).** La spécification est en cours de validation par l'utilisateur ; le spike v0.1.0 a confirmé la faisabilité (simulation API et essais réels sur Emby Web). Le moteur de retrait est en cours de livraison ; la propagation arrive en 0.3.0.

## Principe

Une playlist appartient à un utilisateur (le propriétaire), qui la partage avec d'autres utilisateurs grâce au partage natif de playlists d'Emby (menu « … » → **Gérer la collaboration**). Le plugin ajoute la gestion de l'état « vu », réglée par **deux étiquettes indépendantes** posées sur la playlist :

| Étiquette | Effet quand elle vaut `OUI` | Version |
|---|---|---|
| `remove-si-lu` | Quand un membre passe un média de non lu à **lu**, il est **retiré de la liste pour tous**. | 0.2.0 |
| `propager-lu` | L'**état de lecture** est copié chez les autres membres : le « lu » (0.3.0), puis la position de lecture, pour commencer avec un compte et poursuivre avec l'autre (0.3.1). | 0.3.0 / 0.3.1 |

- Dès qu'une playlist est partagée, le plugin pose `remove-si-lu=NON` et `propager-lu=NON` (et, si la description est vide, un message d'aide). Tant que ce sont des `NON`, **rien ne change** : Emby se comporte comme d'habitude (legacy).
- Pour activer une option, le propriétaire **remplace `NON` par `OUI`** : ajouter `...=OUI` et retirer `...=NON` dans la même édition (Modifier les métadonnées > Mot-clé). Si `OUI` et `NON` sont présents ensemble, `NON` l'emporte. Le plugin ne supprime jamais une étiquette. Casse et espaces autour du `=` sont sans importance.
- Seul le **propriétaire** gère les membres et les étiquettes ; les membres en écriture peuvent ajouter et retirer des médias.
- Seule la **transition** non lu → lu retire le média : relire un média déjà lu ne le retire pas. Sortie manuelle d'un média lu resté dans la liste : décocher puis recocher « lu », ou le retirer directement.
- Un média lu n'est retiré que des listes dont son lecteur est membre (pas de transitivité entre listes).
- Le plugin ne mémorise rien : tout est en mémoire, il replace ce qui manque (étiquette absente, description vide) après quelques minutes.

Ce n'est pas une wishlist de demandes de médias (comme Ombi ou Seerr) : la liste ne contient que des médias déjà présents dans la bibliothèque.

### Permission de partage

Emby masque le partage tant que l'utilisateur n'a pas la permission « Permettre le partage de contenus personnels tels que des listes de lecture avec d'autres utilisateurs sur ce serveur » (Tableau de bord → Utilisateurs → l'utilisateur → onglet Profil). Elle n'est requise que pour le propriétaire. Une pose automatique par le plugin est prévue en 0.4.0.

### Limites

La version 0.2.0 n'est déployée qu'en QUALIF. À son démarrage, elle pose les deux étiquettes `NON` et le message d'aide sur toutes les playlists déjà partagées. Les applis TV et mobile n'ont pas été vérifiées (visibilité, retrait, édition des étiquettes). Le guide utilisateur complet est au §8 de la spécification.

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
