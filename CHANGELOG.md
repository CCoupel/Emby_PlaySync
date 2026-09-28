# Changelog — Emby Shared Playlist (PlaySync)

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

### Security

## [1.1.0] — 2026-09-28

**Status**: VALIDATED WITH RESERVATIONS (QA: 62/62 tests pass, visual reserve documented)

### Added

- **Page utilisateur PlaySync** : nouvelle interface dans le menu utilisateur d'Emby (Avatar → PlaySync) permettant au propriétaire d'une playlist de gérer simplement les membres (ajout, changement de niveau Lecture/Écriture, retrait) et les deux options de partage (retrait automatique du média lu, propagation de l'état de lecture) via des interrupteurs, sans éditer manuellement les étiquettes. Page bilingue FR/EN selon la langue du client Emby. Masquée pour les comptes sans permission de partage.
- **Endpoints non-admin** pour le plugin (`/SharedPlaylist/User/*`) : gestion des playlists possédées, sélection des membres, modification des options. Autorisation basée sur la session HTTP, propriété vérifiée côté serveur.
- **Journal de diagnostic** : nouveaux kinds `ShareChanged` (ajout/retrait de membres) et `MarkerSet` (bascule des options) pour un suivi détaillé des opérations via la page. Nouveau kind `OwnerLost` pour signaler incidents d'intégrité rares lors du retrait du dernier membre.
- **Bascule atomique des options** (D19) : remplace toutes les étiquettes d'une famille en une seule opération verrouilée, résout les conflits OUI+NON en un clic.

### Changed

- **Mention de la page PlaySync dans la configuration du plugin** : l'encart d'aide indique désormais que le propriétaire peut aussi utiliser la page PlaySync pour gérer les partages, plus simplement qu'en passant par « Modifier les métadonnées ».

### Fixed

- **Sérialisation de `Options` dans les réponses de l'API** : JSON objet conforme au contrat (ex. `{"remove-si-lu": "Oui", "propager-lu": "Non"}`).
- **Gestion des `UserId` malformés** : HTTP 400 + code d'erreur stable `invalid-user` (pas de message serveur brut).
- **Intégrité de `ManageDelete` du propriétaire** : confirmée stable lors du retrait du dernier membre (vérification complète en conditions réelles).

### Security

- **Audit sécurité complet** (issue #39, `_work/reports/code-review-20260928-161829.md`) : endpoints d'écriture de droits (`SaveUserItemShares`/`DeleteUserItemShares`) audités pour l'élévation, IDOR, énumération d'utilisateurs, XSS côté page, fuite d'erreurs. Les seules données exposées au propriétaire sont les identifiants et noms de ses propres playlists, ses comptes et leurs niveaux de partage.

### Notes

- **Réserve QA unique** : contrôle visuel `MANUAL.md §5quinquies` (menu, dialogue natif, toasts, FR/EN) non réalisé en raison d'une contrainte de profil navigateur partagé ; fortement corroboré par preuve API et code (`25-page-utilisateur.sh` : P1-P23 all pass, y compris sécurité P23). À couvrir avant GATE PROD (utilisateur ou `qa` depuis profil isolé).
- Couverture de tests : 97.3% (inchangée depuis v1.0.0) — tests unitaires non mesurables dans l'environnement QA (`dotnet` absent), intégration 62/62 OK, NR gated 22/22 OK.

## [1.0.0] — 2026-09-27

### Added

- **Retrait automatique du média lu** : quand un membre passe un média à l'état « lu », celui-ci est retiré de la playlist pour tous les autres (étiquette `remove-si-lu=OUI`).
- **Propagation du flag lu** : l'état de lecture d'un média est copié chez les autres membres (étiquette `propager-lu=OUI`).
- **Propagation de l'avancement de lecture** : la position de lecture à l'arrêt ou la pause est propagée aux autres membres, permettant de poursuivre sur un autre compte.
- **Permission de partage automatique** : le plugin peut poser automatiquement la permission Emby « Permettre le partage de contenus personnels » pour tous les utilisateurs (paramètre `AutoEnableSharing`, **désactivé par défaut depuis v1.0.0** pour des raisons de sécurité sur les serveurs exposés publiquement).
- **Diagnostics et journalisation** : accès aux événements du plugin et à l'état en mémoire via l'interface d'administration Emby (si `EnableDiagnostics=true`).
- **Icon de plugin** : icône Material dans le menu de configuration.

### Changed

- **`AutoEnableSharing` : défaut `false`** (à partir de v1.0.0) — décision de sécurité GATE PROD (#29). De v0.4.0 à v0.5.0, le défaut était `true` (acceptable sur QUALIF non exposée, rejeté en production).

### Fixed

- **Retrait du dernier membre** : comportement cohérent avec le reste du moteur (playlist redevient non gérée, étiquettes inertes jusqu'au prochain partage).
- **Ré-entrance et concurrence** : garanties d'exécution immédiate sous verrou par playlist, sans file, sans blocage observé (issue #52, essai U11 validé).

### Security

- **Audit sécurité complet** (issue #29) : examiné le plugin v0.4.0 pour vulnérabilités (injection, XSS, élévation de privilèges, secrets exposés) — résultat : green avec un point porté à GATE PROD (défaut `AutoEnableSharing` changé en `false`).

## [0.5.0] — 2026-09-27

Robustesse et passage à l'échelle : comportements confirmés en conditions réelles (script `24-robustesse.sh` sur QUALIF).

### Added

### Changed

- **Exécution du moteur** : confirmé stable sous verrou par playlist, sans file, aucun `database is locked` ni boucle détectés en conditions de charge réelle.

### Fixed

- **Croissance non bornée de `PlaylistLocks`** : documentée comme décision consciente (D18) — croissance jugée négligeable sur un serveur personnel ; une éviction active aurait un risque supérieur au bénéfice.

## [0.4.0] — 2026-09-27

### Added

- **Permission de partage automatique** : interrupteur `AutoEnableSharing` posant `Policy.AllowSharingPersonalItems=true` pour tous les comptes (existants et nouveaux) au démarrage, à la réconciliation et à la création d'un compte. Défaut : `true` (à changer en `false` pour v1.0.0).
- **Encart d'aide dans la page de configuration** : décrit le partage natif, les deux étiquettes et le comportement du plugin.
- **Guide utilisateur complet** (docs/chronogrammes.md §8).

### Changed

### Fixed

## [0.3.1] — 2026-09-27

### Added

- **Propagation de l'avancement de lecture** : la position de lecture à l'arrêt ou à la pause est écrite chez les autres membres si `propager-lu=OUI` (via `ISessionManager.PlaybackProgress`/`PlaybackStopped`, indépendant du flux `UserDataSaved`). Dernier écrit gagne, seuil minimal 30 secondes.

### Changed

### Fixed

## [0.3.0] — 2026-09-26

### Added

- **Propagation du flag lu** : quand un membre passe un média à « lu », le flag est posé chez les autres membres si `propager-lu=OUI`.
- **Anti-écho du flag lu** : les écritures du plugin ne déclenchent pas leur propre moteur (mécanisme `PluginWriteTracker`), garantissant l'absence de transitivité entre listes.
- **Message d'aide mis à jour** : le message de v0.2.0 (qui mentionnait la propagation comme « fonction à venir ») est remplacé par la version v0.3.0 à chaque première détection ou passe de réconciliation, si la description n'a pas été modifiée.

### Changed

### Fixed

## [0.2.0] — 2026-09-26

### Added

- **Retrait automatique du média lu** : quand un membre passe un média de non lu à « lu », celui-ci est retiré de la playlist pour tous. Déclenché uniquement sur la transition (relire un média déjà lu ne le retire pas).
- **Étiquettes `remove-si-lu` et `propager-lu`** : deux familles indépendantes (`=NON` par défaut, `=OUI` pour activer) posées automatiquement dès le premier partage d'une playlist.
- **Message d'aide** : écrit dans la description si elle est vide, ne remplace jamais un texte existant.
- **Exécution immédiate sous verrou par playlist**, sans file.
- **Diagnostics et journal** : endpoints `/Diagnostics/Journal` et `/State` exposant les décisions du moteur en temps réel (admin seulement).
- **Tâche planifiée de réconciliation** : toutes les 5 min, détecte les nouvelles playlists partagées et repose les étiquettes manquantes après une grâce de 2 passes.

### Changed

### Fixed

## [0.1.0] — 2026-09-26

### Added

- **Partage natif Emby** : confirmation expérimentale que le plugin peut lire et écrire les partages natifs d'Emby via `ILibraryManager.SaveUserItemShares` et `IItemRepository.GetUserItemShares`.
- **Endpoints Spike** : endpoints temporaires de diagnostic pour valider la faisabilité technique (partage natif, étiquettes, permission de partage).
- **Squelette du plugin** : structure initiale, listeners d'événements, composition des services.

### Changed

### Fixed

