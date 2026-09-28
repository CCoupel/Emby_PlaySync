# Release v1.0.0 — PlaySync, la première version stable

**Date** : 2026-09-28

PlaySync (le plugin Emby de playlists « À voir » partagées) atteint sa v1.0.0 : la première
version complète, testée à l'échelle et désormais disponible. Elle rassemble tout le travail
livré depuis les premières versions de développement.

## Nouveautés

### Partage natif, sans réglage manuel
Une playlist « À voir » se partage désormais comme n'importe quelle autre liste Emby, depuis le
menu standard « Gérer la collaboration ». PlaySync accorde automatiquement, à tous les comptes,
la permission nécessaire pour partager — plus besoin d'aller la chercher dans les réglages de
chaque utilisateur.

### Un média vu disparaît tout seul
Sur une playlist où l'option est activée, dès qu'un membre termine un média, il est retiré de la
liste pour tout le monde. Fini les allers-retours pour nettoyer une liste partagée à la main.

### Continuez où vous vous êtes arrêté, même sur un autre compte
PlaySync peut aussi propager l'état « vu » et la position de lecture entre les membres d'une
même playlist : commencez un épisode sur un compte, reprenez-le là où vous en étiez sur un
autre. C'est la dernière lecture qui fait foi.

### Diagnostics pour les administrateurs
Un journal des décisions du plugin et un état en mémoire sont consultables par un administrateur,
pour comprendre à tout moment ce que PlaySync a fait et pourquoi.

### Une icône bien à lui
Le plugin a désormais sa propre icône dans la liste des plugins Emby, plus facile à repérer.

## Robustesse

Le comportement a été vérifié en conditions réelles à plus grande échelle : une dizaine de
playlists partagées, cinq membres actifs simultanément, avec des temps de réaction sous la
seconde et un comportement stable après redémarrage du serveur.

## Corrections

Cette version regroupe surtout des nouveautés ; les ajustements de cette clôture sont d'ordre
interne (nettoyage du code de mise au point, couverture de tests renforcée) et n'ont pas d'impact
visible pour les utilisateurs.

## Comment mettre à jour

1. Téléchargez `EmbySharedPlaylist.dll` depuis la page [Releases](https://github.com/CCoupel/Emby_shared_playlist/releases).
2. Copiez-le dans le dossier `plugins/` d'Emby, à la racine (pas de sous-dossier).
3. Redémarrez Emby.

Aucune action supplémentaire n'est nécessaire : les playlists déjà partagées conservent leur
configuration, et les nouvelles étiquettes restent désactivées (`NON`) par défaut tant qu'elles
ne sont pas activées à la main.

> Cette version est **en production** : après une recette complète en environnement de
> qualification, elle est publiée sous le tag `v1.0.0` et déployée.

## Liens

- [Guide d'utilisation et documentation technique](https://github.com/CCoupel/Emby_shared_playlist#readme)
- [GitHub Release v1.0.0](https://github.com/CCoupel/Emby_shared_playlist/releases/tag/v1.0.0)
- [Dépôt du projet](https://github.com/CCoupel/Emby_shared_playlist)
