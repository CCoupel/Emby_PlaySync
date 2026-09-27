# Contrats HTTP — Emby_shared_playlist

> Endpoints exposés par le plugin via `IService` (préfixe `/emby` selon l'installation). Auth : header `X-Emby-Token` (clé API admin ou token de session admin).
> Les endpoints `Spike/*` (diagnostic temporaire de v0.1.0/v0.2.0) et la sonde de réentrance U11 ont été **retirés en v0.2.0 (#15)** : remplacés par `Diagnostics/*` ci-dessous. Leur historique (routes, format, garde-fous) reste dans `CHANGELOG.md` et dans git.

## Diagnostics (permanent — admin uniquement, v0.2.0)

> **Auth** : Admin (`[Authenticated(Roles = "Admin")]`). **404** si `PluginConfiguration.EnableDiagnostics == false` (défaut : `true`). Lecture seule sauf `clear`. **Ids seulement** : aucun nom d'utilisateur, de playlist ou de média, aucun secret. Casse : réponses en **PascalCase**. Erreurs : 401, 403, 404, 500 (`error: <Type>: <message>`).
> Déclenchement d'une passe de réconciliation pour les tests : pas d'endpoint plugin, utiliser la tâche planifiée Emby (`POST /ScheduledTasks/Running/{id}`, admin ; l'id de la tâche « Emby Shared Playlist — réconciliation » est lisible par `GET /ScheduledTasks`).
> **Deux familles d'étiquettes** (mêmes règles pour chacune : casse ignorée, espaces tolérés autour de `=`, OUI+NON ensemble => NON l'emporte, le plugin ne supprime jamais d'étiquette, aucune étiquette de la famille => considérée NON et reposée) : `remove-si-lu` (retrait du média quand il devient lu ; **actif en v0.2.0**) et `propager-lu` (propagation de l'état de lecture ; posée et lue en NON dès v0.2.0, **sans effet avant v0.3.0**). Dans les contrats, `Family` = `remove-si-lu` | `propager-lu`.

### GET /SharedPlaylist/Diagnostics/Journal?clear={bool}&kind={liste}

**Description** : journal mémoire borné (500 entrées, plus récent en dernier) des décisions du plugin. `clear=true` vide après lecture (tout le journal). `kind` (liste séparée par des virgules, insensible à la casse) filtre : **sans `kind`, seules les décisions du moteur sont renvoyées** (`ScanPass`, `MarkerPosed`, `DescriptionWritten`, `MarkerSeen`, `Removal`, `Skipped`, `Error`) ; avec `kind`, exactement les kinds demandés.

**Response 200** :
```json
[ { "Ts": "ISO-8601", "Kind": "ScanPass|MarkerPosed|DescriptionWritten|MarkerSeen|Removal|Skipped|Error",
    "UserId": "string|null", "ItemId": "string|null", "PlaylistId": "string|null", "Detail": "string|null" } ]
```
`Kind` et `Detail` (ids et compteurs uniquement) :
- `ScanPass` : `playlists=<n> shared=<n> posed=<n> pending=<n> durationMs=<n>` (une entrée par passe).
- `MarkerPosed` : `<Family>=NON` posé (`Detail` = `family=<Family> cause=first-detection|grace-elapsed`).
- `DescriptionWritten` : message d'aide écrit, description vide (`Detail` = `cause=first-detection|grace-elapsed`).
- `MarkerSeen` : état d'une famille lu à l'événement de retrait (`Detail` = `family=remove-si-lu state=Oui|Non|Both|None`).
- `Removal` : `PlaylistId`, `ItemId`, `UserId` (déclencheur), `Detail` = `entries=<n> durationMs=<n>` (durée du traitement dans le gestionnaire, verrou compris).
- `Skipped` : pas d'action (`Detail` = `inactive|not-shared|already-seen|already-removed|marker-present|unknown-owner|reentrant|lock-busy|budget-exceeded|no-effect`).
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
  "SkippedCounts": { "already-seen": 0, "reentrant": 0, "lock-busy": 0 } }
```
`SkippedCounts` = **tous** les `Skipped` depuis le démarrage, par raison. Les raisons bruyantes (`already-seen`, `reentrant`, `not-shared`, `unknown-owner` : une par événement de playlist) ne sont PAS inscrites dans le journal (500) : elles n'existent que dans ces compteurs (et en Debug dans le fichier de log) ; les autres (`lock-busy`, `inactive`, `already-removed`, `marker-present`, `budget-exceeded`, `no-effect`) sont dans le journal ET comptées. `SeenPlaylistIds` = playlists ayant subi la première détection depuis le démarrage. `GraceCounters` = passes consécutives sans étiquette de la famille (ou avec description vide). `Handler` = traitements de transition effectués dans le gestionnaire d'événement (nombre, dernière et plus longue durée en ms) : sert à vérifier le budget de latence.

> **Config plugin (v0.2.0)** : `EnableDiagnostics` (bool, défaut `true`), `GracePasses` (int, défaut `2`, min 1), `LogToConsole` (bool, défaut `true`), `LogLevel` (`Off|Info|Debug`, défaut `Info`). **Aucun état n'est stocké** (ni dans `PluginConfiguration`, ni dans un fichier). La période de réconciliation est celle de la tâche planifiée Emby (déclencheurs par défaut : démarrage + 5 min ; modifiable dans le tableau de bord).
