# Emby Shared Playlist

Plugin Emby Media Server pour partager une playlist « À voir » entre plusieurs utilisateurs.

## Principe

Une playlist appartient à un utilisateur (le propriétaire), qui la partage avec d'autres utilisateurs grâce au partage natif de playlists d'Emby. Le plugin ajoute la gestion de l'état « vu », réglée par **trois étiquettes indépendantes** posées sur la playlist :

| Étiquette | Effet quand elle vaut `OUI` | Version |
|---|---|---|
| `remove-si-lu` | Quand un membre passe un média de non lu à **lu**, il est **retiré de la liste pour tous** (nécessite `propager-lu=OUI` depuis v1.2.0). | 0.2.0 |
| `propager-lu` | Le **flag lu** est copié chez les autres membres quand l'un d'eux termine un média. Depuis v1.2.0, ne couvre plus l'avancement (c'est le rôle de `propager-avancement`). | 0.3.0 / 1.2.0 |
| `propager-avancement` | L'**avancement de lecture** — position en continu (environ toutes les 10 s pendant la lecture, toujours à la pause et à l'arrêt), ≥ 30 s de lecture — est copié chez les autres membres pour commencer avec un compte et poursuivre avec l'autre, la dernière lecture gagne. Nouveau en v1.2.0, amélioré en v1.2.1. | 1.2.0 |

Par défaut, les trois étiquettes sont à `NON` : le plugin ne change rien au comportement natif d'Emby (« legacy »). **Depuis v1.2.0** : `remove-si-lu` n'a d'effet que si `propager-lu` est aussi actif.

## Guide utilisateur

### 1. Permission de partage

Le partage d'une playlist nécessite, côté **propriétaire**, la permission Emby « Permettre le partage de contenus personnels tels que des listes de lecture avec d'autres utilisateurs sur ce serveur » (Tableau de bord → Utilisateurs → l'utilisateur → onglet Profil).

Depuis la v0.4.0, le plugin peut la poser **automatiquement pour tous les comptes** (interrupteur `AutoEnableSharing` dans la configuration du plugin). **Désactivé par défaut depuis v1.0.0** (décision GATE PROD : un élargissement de droits pour tous les comptes n'est pas activé sans revue explicite de l'administrateur, en particulier sur un serveur exposé — voir `security-20260927-221434.md`, finding M1 ; défaut actif de v0.4.0 à v0.5.0, QUALIF non exposé). Si vous l'activez :
- si un administrateur la **décoche manuellement** pour un compte, elle est **réactivée à la passe de réconciliation suivante** (5 min au plus) tant que l'interrupteur global reste actif ;
- seul le **décochage de l'interrupteur global** `AutoEnableSharing` empêche de futures activations ;
- désactiver l'interrupteur **ne révoque jamais** un accès déjà accordé.

Sans activer l'interrupteur, chaque propriétaire doit accorder cette permission lui-même (Tableau de bord → Utilisateurs → son compte → onglet Profil) avant de pouvoir partager une playlist. Les destinataires n'ont rien à activer : leurs droits viennent uniquement du niveau de partage (Écriture/Lecture).

### 2. Créer une liste (depuis v1.2.0)

Le propriétaire peut créer directement une playlist vide depuis la page PlaySync :
1. Menu utilisateur (Avatar) → **PlaySync** → bouton **Nouvelle playlist**.
2. Entrer un nom (1–100 caractères, unique par propriétaire et insensible à la casse).
3. Valider. La playlist est créée vide, non partagée, et prête à recevoir des médias et des membres.

Quota : **10 playlists possédées par utilisateur** (comptées sur toutes les playlists Emby, natifs ou créées via PlaySync).

### 3. Partager une liste

**Méthode native (interface Emby)** :
1. Le propriétaire crée sa playlist « À voir ».
2. Menu « … » de la playlist → **Gérer la collaboration**.
3. Choisir pour chaque utilisateur le niveau **Écriture** (peut ajouter et retirer des médias) ou **Lecture** (consultation seule).

**Méthode PlaySync (depuis v1.1.0)** :
1. Le propriétaire ouvre le menu utilisateur (Avatar) → **PlaySync**.
2. Sélectionner une playlist non partagée, ajouter un membre, choisir son niveau, valider.
3. Gérer aussi les trois options (retrait automatique, propagation du lu, propagation de l'avancement) via des interrupteurs simples, sans éditer manuellement les étiquettes.

Seul le propriétaire gère les membres et les étiquettes. Un membre en écriture ne peut ni repartager la liste ni modifier son nom, sa description ou ses étiquettes. Voir [docs/chronogrammes.md §10-§11](docs/chronogrammes.md#s10--page-utilisateur--partager-et-activer-une-option-v110-d19d20) pour le guide complet de la page PlaySync.

Dès qu'une playlist est partagée, le plugin lui ajoute les trois étiquettes **`remove-si-lu=NON`**, **`propager-lu=NON`** et **`propager-avancement=NON`**, et, si la description est vide, un message d'aide mis à jour.

### 4. Activer une option : interrupteurs ou étiquettes manuelles

**Méthode PlaySync (recommandée depuis v1.2.0)** — via les trois interrupteurs :
1. Menu utilisateur → **PlaySync** → sélectionner la playlist.
2. Activer ou désactiver les trois options avec les interrupteurs :
   - « **Retirer si lu** » — retiré **au démarrage automatique** de « Propager le lu » ;
   - « **Propager le lu** » — le flag lu ; décocher garde `remove-si-lu=OUI` inerte ;
   - « **Propager l'avancement** » — la position de lecture (indépendant des autres).
3. Les changements sont appliqués atomiquement (sans état intermédiaire).

**Méthode manuelle (avancée)** — édition directe des étiquettes :
1. Menu « … » de la playlist → **Modifier les métadonnées**.
2. Section **Mot-clé** (Étiquette) → pour chaque option, **ajouter** `<option>=OUI` et **retirer** `<option>=NON` **dans la même édition**.
3. Enregistrer. Sans `OUI`, l'option reste à NON (inerte).

Règles communes aux trois étiquettes :
- Si `OUI` et `NON` sont présents, **`NON` l'emporte** : l'option reste inerte.
- Le plugin **ne supprime jamais** une étiquette (sauf via les interrupteurs PlaySync). Retirer `NON` seul ne suffit pas : il faut ajouter `OUI`. Si toutes les étiquettes sont supprimées manuellement, le plugin repose `NON` après ~10 min.
- Le message d'aide est réécrit si la description est vidée.
- Les playlists publiques non partagées explicitement sont ignorées.

### 5. Ce que fait chaque option

- **`remove-si-lu=OUI` (dépend de `propager-lu=OUI`)** : quand un membre (propriétaire, Écriture ou Lecture) fait passer un média à « lu », il est **retiré de la liste pour tous**. Seule la **transition** non lu → lu déclenche le retrait : relire jusqu'au bout un média déjà lu ne le retire pas, et mettre en favori, importer ou masquer un film déjà vu non plus. **Changement v1.2.0** : cette option est désormais inerte sans `propager-lu=OUI`. Pour sortir à la main un média lu resté : décocher puis recocher « lu », ou le retirer directement.
- **`propager-lu=OUI`** : le **flag lu** est copié chez les autres membres quand l'un d'eux termine un média (0.3.0). Seul le passage à lu est propagé, jamais le retour à non lu. Sans jamais modifier un flag déjà posé, et sans propager la position (c'est le rôle de `propager-avancement`).
- **`propager-avancement=OUI`** (nouveau en v1.2.0, amélioré en v1.2.1) : l'**avancement de lecture** — position en continu (environ toutes les 10 s pendant la lecture, toujours à la pause ou à l'arrêt), ≥ 30 s — est copié chez les autres membres **quel que soit l'état lu** (la dernière lecture gagne, dans les deux sens). Permet de commencer avec un compte et poursuivre avec l'autre. Indépendant de `propager-lu` : on peut propager l'avancement sans le flag, ou réciproquement.

Les trois options sont indépendantes dans leur activation (chacune fonctionne ou non), mais `remove-si-lu` a besoin de `propager-lu` pour avoir un effet. Un média lu n'est retiré que des listes dont son lecteur est membre (pas de transitivité).

### 6. Limites et changements de v1.2.0

**Changements de comportement depuis v1.1.0** (lisez-les si vous migrez) :
- Une playlist avec `propager-lu=OUI` **cesse de propager l'avancement** → pour retrouver cette fonction, activez « Propager l'avancement » via PlaySync.
- Une playlist avec `remove-si-lu=OUI` mais `propager-lu=NON` **ne retire plus aucun média** → activez « Propager le lu » pour restaurer le retrait.
- Une position ~99 % écrite par le plugin **ne marque plus automatiquement le média lu** → activez « Propager le lu » si vous voulez le flag aussi.

**Autres limites** :
- **Clients TV et mobile** : la visibilité de la playlist partagée et le retrait fonctionnent (partage natif Emby), mais **poser ou modifier une étiquette depuis un client TV ou mobile n'est pas garanti**. Vérification manuelle au cas par cas (issue #28) : reste ouverte, **non bloquante** pour la livraison. Les interrupteurs PlaySync ne sont accessibles que depuis le web.
- Le message écrit dans la description d'une playlist gérée décrit les trois options et leur dépendance.
- La page de configuration du plugin comporte un encart d'aide (partage natif, les trois étiquettes, permission automatique) et la case `AutoEnableSharing`.

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
