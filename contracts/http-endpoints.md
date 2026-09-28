# Contrats HTTP — Emby_shared_playlist

> Endpoints exposés par le plugin via `IService` (préfixe `/emby` selon l'installation). Auth : header `X-Emby-Token` (clé API admin ou token de session admin).
> Les endpoints `Spike/*` (diagnostic temporaire de v0.1.0/v0.2.0) et la sonde de réentrance U11 ont été **retirés en v0.2.0 (#15)** : remplacés par `Diagnostics/*` ci-dessous. Leur historique (routes, format, garde-fous) reste dans `CHANGELOG.md` et dans git.

## Diagnostics (permanent — admin uniquement, v0.2.0)

> **Auth** : Admin (`[Authenticated(Roles = "Admin")]`). **404** si `PluginConfiguration.EnableDiagnostics == false` (défaut : `true`). Lecture seule sauf `clear`. **Ids seulement** : aucun nom d'utilisateur, de playlist ou de média, aucun secret. Casse : réponses en **PascalCase**. Erreurs : 401, 403, 404, 500 (`error: <Type>: <message>`).
> Déclenchement d'une passe de réconciliation pour les tests : pas d'endpoint plugin, utiliser la tâche planifiée Emby (`POST /ScheduledTasks/Running/{id}`, admin ; l'id de la tâche « Emby Shared Playlist — réconciliation » est lisible par `GET /ScheduledTasks`).
> **Deux familles d'étiquettes** (mêmes règles pour chacune : casse ignorée, espaces tolérés autour de `=`, OUI+NON ensemble => NON l'emporte, le plugin ne supprime jamais d'étiquette, aucune étiquette de la famille => considérée NON et reposée) : `remove-si-lu` (retrait du média quand il devient lu ; **actif en v0.2.0**) et `propager-lu` (propagation de l'état de lecture — flag lu depuis v0.3.0, avancement de lecture depuis v0.3.1 ; posée et lue en NON dès v0.2.0). Dans les contrats, `Family` = `remove-si-lu` | `propager-lu`.

### GET /SharedPlaylist/Diagnostics/Journal?clear={bool}&kind={liste}

**Description** : journal mémoire borné (500 entrées, plus récent en dernier) des décisions du plugin. `clear=true` vide après lecture (tout le journal). `kind` (liste séparée par des virgules, insensible à la casse) filtre : **sans `kind`, seules les décisions du moteur sont renvoyées** (`ScanPass`, `MarkerPosed`, `DescriptionWritten`, `MarkerSeen`, `Removal`, `Propagation`, `PositionPropagation`, `PermissionPass`, `PermissionPosed`, `Skipped`, `Error`) ; avec `kind`, exactement les kinds demandés.

**Response 200** :
```json
[ { "Ts": "ISO-8601", "Kind": "ScanPass|MarkerPosed|DescriptionWritten|MarkerSeen|Removal|Propagation|PositionPropagation|PermissionPass|PermissionPosed|Skipped|Error",
    "UserId": "string|null", "ItemId": "string|null", "PlaylistId": "string|null", "Detail": "string|null" } ]
```
`Kind` et `Detail` (ids et compteurs uniquement) :
- `ScanPass` : `playlists=<n> shared=<n> posed=<n> pending=<n> durationMs=<n>` (une entrée par passe).
- `MarkerPosed` : `<Family>=NON` posé (`Detail` = `family=<Family> cause=first-detection|grace-elapsed`).
- `DescriptionWritten` : message d'aide écrit ou remplacé (`Detail` = `cause=first-detection|grace-elapsed|v1-to-v2`). `v1-to-v2` (#51) : la description était encore identique caractère pour caractère à `HelpText.V1` (v0.2.0) ; remplacée par `HelpText.V2` (propagation effective), à chaque première détection ou passe, sans grâce, sans état mémorisé.
- `MarkerSeen` : état d'une famille lu à l'événement de retrait (`Detail` = `family=remove-si-lu state=Oui|Non|Both|None`).
- `Removal` : `PlaylistId`, `ItemId`, `UserId` (déclencheur), `Detail` = `entries=<n> durationMs=<n>` (durée du traitement dans le gestionnaire, verrou compris).
- `Propagation` (v0.3.0, #20 : flag lu) : `PlaylistId`, `ItemId`, `UserId` (declencheur de la transition), `Detail` = `members=<n> propagated=<n> alreadyPlayed=<n> noAccess=<n>` (une seule entree par playlist, meme si `propagated=0`). Journalisee uniquement si `propager-lu=OUI` seule (independant de `remove-si-lu`).
- `PositionPropagation` (v0.3.1, #45 : avancement de lecture) : `PlaylistId`, `ItemId`, `UserId` (declencheur), `Detail` = `members=<n> propagated=<n> samePosition=<n> noAccess=<n> durationMs=<n>`. Declenchement : **`ISessionManager.PlaybackProgress`** (transition `IsPaused` false->true, position au-dela d'un seuil minimal) et **`ISessionManager.PlaybackStopped`** (position finale, meme seuil, sauf si le media est/devient lu pour l'utilisateur declencheur) — **pas** `UserDataSaved` (mecanisme distinct de `Propagation`/`Removal`, sans rapport avec `PlayedTransitionTracker`). Jamais si le media est lu (regle du lu, prioritaire) ; jamais sur un `PlaybackProgress` ordinaire en cours de lecture (pas de mise a jour periodique).
- `Skipped` : pas d'action (`Detail` = `inactive|not-shared|already-seen|already-removed|marker-present|unknown-owner|reentrant|lock-busy|budget-exceeded|no-effect|no-access|already-played|echo-consumed|same-position|no-position|too-short|trigger-already-played`). `no-access`/`already-played`/`same-position` sont journalises avec `UserId`=membre concerne (propagation du lu ou de l'avancement, #20/#45). `echo-consumed` est au niveau de l'evenement (pas de la playlist : `PlaylistId=null`), pose par `PlaybackListener` quand `PluginWriteTracker` reconnait l'ecriture (#21). `no-position`/`too-short`/`trigger-already-played` (v0.3.1, #45) sont AUSSI au niveau de l'evenement (`PlaylistId=null`, `UserId`=declencheur), poses par `PlaybackSessionListener` avant meme d'atteindre `PlaybackPositionEngine` (position absente, arret/pause en deca du seuil ~30s, ou media deja lu pour le declencheur a cet instant — garde D-c) : distinct de `already-played` (R7, #20), qui vise un MEMBRE deja lu par playlist, pas le declencheur au niveau de l'evenement.
- `PermissionPass` (v0.4.0, #26 : permission de partage) : une entrée par passe (démarrage, réconciliation), `Detail` = `users=<n> enabled=<n> alreadyEnabled=<n> durationMs=<n>`. Absente si `AutoEnableSharing=false` (aucune tentative, donc rien à journaliser).
- `PermissionPosed` (v0.4.0, #26) : `UserId` (compte concerné), `Detail` vide — `Policy.AllowSharingPersonalItems` posé à `true` (première fois vu à `false`) ; ne modifie que ce champ, jamais de révocation. Déclenché à la passe de réconciliation (tous les utilisateurs), au démarrage (première passe) et à la création d'un utilisateur (`IUserManager.UserCreated`).
- `Error` : exception isolée (`Detail` = type d'exception, sans message brut si celui-ci peut contenir des noms).

### GET /SharedPlaylist/Diagnostics/State

**Description** : état **en mémoire** du plugin (rien n'est persisté ; remis à zéro à chaque redémarrage), pour les tests d'intégration et le support.

**Response 200** :
```json
{ "SeenPlaylistIds": ["string"],
  "GraceCounters": { "<playlistId>": { "remove-si-lu": 0, "propager-lu": 0, "description": 0 } },
  "LastPass": { "Ts": "ISO-8601|null", "DurationMs": 0, "PlaylistsSeen": 0, "SharedManaged": 0 },
  "Handler": { "Count": 0, "LastMs": 0, "MaxMs": 0 },
  "GracePasses": 2,
  "SkippedCounts": { "already-seen": 0, "reentrant": 0, "lock-busy": 0, "echo-consumed": 0, "no-access": 0, "already-played": 0, "same-position": 0 } }
```
`SkippedCounts` = **tous** les `Skipped` depuis le démarrage, par raison. Les raisons bruyantes (`already-seen`, `reentrant`, `not-shared`, `unknown-owner`, `echo-consumed` : une par événement) ne sont PAS inscrites dans le journal (500) : elles n'existent que dans ces compteurs (et en Debug dans le fichier de log) ; les autres (`lock-busy`, `inactive`, `already-removed`, `marker-present`, `budget-exceeded`, `no-effect`, `no-access`, `already-played`, `same-position`, `no-position`, `too-short`, `trigger-already-played`) sont dans le journal ET comptées. `SeenPlaylistIds` = playlists ayant subi la première détection depuis le démarrage. `GraceCounters` = passes consécutives sans étiquette de la famille (ou avec description vide). `Handler` = traitements de transition effectués dans le gestionnaire d'événement, retrait/propagation du lu/propagation de l'avancement confondus (nombre, dernière et plus longue durée en ms) : sert à vérifier le budget de latence.

> **Config plugin** : `EnableDiagnostics` (bool, défaut `true`), `GracePasses` (int, défaut `2`, min 1), `LogToConsole` (bool, défaut `true`), `LogLevel` (`Off|Info|Debug`, défaut `Info`), **`AutoEnableSharing`** (bool, **défaut `false` depuis v1.0.0** — GATE PROD, `security-20260927-221434.md` M1 ; défaut `true` de v0.4.0 à v0.5.0, #26) — pose automatiquement `Policy.AllowSharingPersonalItems=true` pour tous les utilisateurs (existants et nouveaux) ; désactivé = aucune écriture, mais **ne révoque jamais** un accès déjà accordé. **Aucun état n'est stocké** (ni dans `PluginConfiguration`, ni dans un fichier). Le changement de défaut ne s'applique qu'à une configuration vierge (sérialisation XML standard d'Emby : une valeur déjà sauvegardée, explicite ou via tout enregistrement de la page de config, n'est jamais réécrite). La période de réconciliation est celle de la tâche planifiée Emby (déclencheurs par défaut : démarrage + 5 min ; modifiable dans le tableau de bord).
