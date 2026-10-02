# Changelog — Emby Shared Playlist (PlaySync)

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.1] — 2026-10-02

**Status**: VALIDATED WITH RESERVATIONS (QUALIF 1.2.1.0; réserves #59 et #60 hors périmètre)

### Changed

- **Propagation continue de l'avancement** (#58) : l'avancement de lecture est maintenant propagé **pendant la lecture** à chaque `PlaybackProgress`, au plus une fois toutes les 10 s par couple (déclencheur, média) ; pause et arrêt restent immédiats. Corrige : un membre ne voyait pas l'avancement d'une lecture continue sans pause jusqu'à la fin.
- **Fin de lecture** : si `propager-lu=OUI` est aussi active sur la playlist, la position écrite chez les membres est **0** (aucun point de reprise, miroir du déclencheur) ; sinon position d'arrêt brute (S9f v1.2.0). Les playlists visées sont celles mémorisées pendant la lecture, même si `remove-si-lu` a retiré le média avant l'arrêt. Corrige : « lu » posé chez le membre mais point de reprise périmé.
- **Journal diagnostics** : `PositionPropagation.Detail` gagne `trigger=<pause|stop|completion>`; les propagations issues d'un `PlaybackProgress` périodique ne sont **pas** journalisées (bruit). Nouvelle clé `Diagnostics/State.PositionProgress` : compteurs `{ Propagated, Throttled, LockBusy }`. `Diagnostics/State.Handler` ne mesure plus les Periodic (biais sur les moyennes/max).

### Fixed

- **Position non mise à jour en fin de lecture** (#58) : quand une lecture continuit jusqu'au bout sans pause, la position n'était écrite chez le membre que si `propager-avancement=OUI` était actif. Maintenant elle est écrite dès le premier `PlaybackProgress` ≤ 10 s après le dernier, et remise à 0 si `propager-lu=OUI` aussi (décorrélation du flag lu).

## [1.2.0] — 2026-09-29

**Status**: VALIDATED WITH RESERVATIONS (QA: 897/897 tests pass; coverage 63.6% global; manual UI and platform verification pending)

### Added

- **Création de playlist depuis PlaySync** (#55) : nouveau bouton « Nouvelle playlist » dans la page utilisateur permettant de créer une playlist vide, non partagée, avec un nom unique par propriétaire (insensible à la casse). Noms de 1 à 100 caractères, quota de 10 playlists possédées par utilisateur. Messages d'erreur stables : `invalid-name` (400), `name-exists` (409), `limit-reached` (409).
- **Trois interrupteurs de partage** dans la page PlaySync : « Retirer si lu », « Propager le lu », « Propager l'avancement ». Le premier reste grisé et inactif tant que le second n'est pas activé (dépendance D21).
- **Étiquette `propager-avancement`** : nouvelle famille d'étiquettes (`=NON` par défaut) activant la propagation de la position de lecture (pause, arrêt) indépendamment du flag lu. Posée à la première détection de toute playlist partagée, sans héritage de v1.1.0.
- **Relecture avec propagation** (#57) : la position est maintenant propagée à chaque pause/arrêt (≥ 30 s) d'un média **déjà lu**, permettant à l'autre membre de reprendre où on a laissé sans marquer de nouveau lu.
- **Message d'aide V3** : mise à jour du message dans la description vide des playlists, mentionnant les trois options et leur dépendance (« Retirer si lu » nécessite « Propager le lu »). Messages V1 et V2 détectés et remplacés ; les textes personnalisés ne sont jamais écrasés.
- **Journal et diagnostics** : nouvelles raisons `Skipped` (`lock-busy` par membre, accès au verrou utilisateur/média partagé). Kind `PlaylistCreated` pour tracer les créations. Compteurs mis à jour pour les trois familles.

### Changed

- **Dépendance entre étiquettes** (D21) : `remove-si-lu` n'a plus d'effet que si `propager-lu` est aussi active. Une playlist avec `remove-si-lu=OUI` + `propager-lu=NON` ne retire plus aucun média. **Changement de comportement : retrait perdu sur les playlists existantes** jusqu'à activation explicite de « Propager le lu ». Aucune étiquette n'est modifiée automatiquement par le plugin.
- **Propagation du lu** : ne propage plus la position de lecture (ce rôle passe à `propager-avancement`). **Changement de comportement : avancement perdu sur les playlists existantes** jusqu'à activation explicite de « Propager l'avancement ».
- **Avancement** : position écrite brute sans appliquer les règles de fin de lecture (pas d'`UpdatePlayState`). Un arrêt proche de la fin (~99 %) ne marque pas automatiquement le média lu chez le membre (c'est le rôle de `propager-lu` si activé). Flux d'événements indépendant du flag lu (pas de verrou common, mais verrou partagé par couple utilisateur/média).
- **Bloc BREAKING — récapitulatif** : [Unreleased] d'un projet avec des playlists actives (v0.3.1–v1.1.0) verra :
  1. Une playlist en `propager-lu=OUI` cesse de propager l'avancement → pour retrouver cette fonction, activer « Propager l'avancement ».
  2. Une playlist en `remove-si-lu=OUI` + `propager-lu=NON` ne retire plus rien → activer « Propager le lu » pour restaurer.
  3. Une position ~100 % écrite par le plugin ne marque pas le média lu chez les autres → activer « Propager le lu » pour ça.
  4. Aucune migration : toutes les étiquettes restent en place, inertes ou réduites à leur nouveau périmètre.

### Fixed

- **Perte d'écritures concurrentes** : verrou par couple (utilisateur, média) étendu à la propagation du lu (R4b) et de l'avancement (R10), garantissant qu'aucune mise à jour n'écrase l'autre lors d'écritures parallèles.
- **Relecture supprimée du journal diagnostics** : la garde « média déjà lu pour le déclencheur » (`trigger-already-played`) supprimée — pas de fausse alerte sur la propagation d'avancement lors de relecture.

### Security

- **Audit sécurité complet** (issue #55, `_work/reports/security-20260929-145841.md`) : création de playlist audité pour anti-IDOR (propriétaire = session), validation du nom, verrou d'unicité, quota de création, absence de journalisation du nom. Score 88/100 — deux points MOYENNE (quota implémenté, test des noms hostiles passé) sans blocage critique.

### Notes

- **Réserves QA** (§5 du rapport QA) : (R1) couverture globale 63,6 % < seuil 70 % (adaptateurs Emby non testables en unitaire) ; (R2) test 25 défaillant en enchaînement (quota du script, non du produit) ; (R3) tests manuels/UI (pause réelle, relecture sur client réel, trois interrupteurs, créationde playlist sur client) non exécutés ; (R4) flaky isolé I23.S6a non reproduit.
- **À vérifier manuellement par l'utilisateur** (MANUAL.md §5ter–§5septies) : pause réelle, relecture S9d/S9f, affichage des trois interrupteurs (grisage de « Retirer si lu »), bouton et champ « Nouvelle playlist », messages d'erreur FR/EN, clients TV/mobile (non bloquant).
- Couverture de tests : 63,6 % lignes global ; Core 98,4 %, Engine 97,6 %, Marker 96,1 %, Reconciliation 95,7 %, UserPage 98,9 % — logique pure bien couverte, lacune structurelle dans les adaptateurs Emby.

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

