# PlaySync v1.0.0 est disponible !

Il y a quelques mois, l'idée était simple : sur un serveur Emby partagé en famille ou entre amis,
pourquoi la liste « à voir » de chacun reste-t-elle un fichier séparé, sans lien avec ce que les
autres ont déjà regardé ? PlaySync est né de cette question, et sa v1.0.0 — la première version
complète du projet, testée et robuste — est désormais en production.

## Les grandes nouveautés

### Partager une liste comme n'importe quelle autre
PlaySync s'appuie entièrement sur le partage natif de playlists d'Emby : pas de nouvel écran, pas
de nouveau compte à créer. Le propriétaire d'une liste choisit ses membres depuis le menu habituel
« Gérer la collaboration », en Lecture ou en Écriture. La seule friction qui existait — la
permission de partage à activer manuellement pour chaque utilisateur — est désormais posée
automatiquement par le plugin.

### La liste se nettoie toute seule
Sur une playlist où l'option `remove-si-lu` est activée, un média disparaît de la liste pour tous
les membres dès que l'un d'eux le termine. Un geste volontaire (décocher puis recocher « lu »)
permet toujours de le retirer à la main si besoin.

### La lecture continue, peu importe le compte
Avec `propager-lu`, le fait d'avoir vu un média et la position exacte où la lecture s'est arrêtée
sont partagés entre les membres. Commencer un film sur le compte d'un enfant et reprendre en
soirée sur le compte adulte : plus besoin d'y penser, c'est la dernière lecture qui l'emporte.

### Une vue sur ce qui se passe
Pour les administrateurs, un journal de décisions et un état en mémoire, consultables à tout
moment, permettent de comprendre pourquoi un média a été retiré ou une position propagée.

## Robustesse

Avant cette v1.0.0, le comportement du plugin a été éprouvé à plus grande échelle qu'un simple
test unitaire : une dizaine de playlists partagées, cinq membres actifs en même temps, un
redémarrage du serveur en cours de route — avec des temps de réaction sous la seconde à chaque
fois.

## Migration

Aucune migration n'est nécessaire depuis une version de développement antérieure : il suffit de
remplacer le fichier `EmbySharedPlaylist.dll` et de redémarrer Emby. Les étiquettes déjà posées
sur les playlists existantes sont conservées telles quelles.

## Merci

Cette v1.0.0 est le résultat d'un travail continu de recette, de revue de code et d'audit de
sécurité avant chaque étape. Merci à toutes celles et ceux qui suivent le projet.

[Voir le dépôt](https://github.com/CCoupel/Emby_shared_playlist) ·
[Documentation](https://github.com/CCoupel/Emby_shared_playlist#readme) ·
[GitHub Release v1.0.0](https://github.com/CCoupel/Emby_shared_playlist/releases/tag/v1.0.0)

---

*PlaySync v1.0.0 est en production.*
