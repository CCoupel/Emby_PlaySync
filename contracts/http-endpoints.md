# Contrats HTTP — Emby_shared_playlist

> Endpoints exposés par le plugin via `IService` (préfixe `/emby` selon l'installation). Auth : header `X-Emby-Token` (clé API admin ou token de session admin).
> Les endpoints `Spike/*` étaient **temporaires** (v0.1.0) : retirés en v0.2.0 (#15), remplacés par `Diagnostics/*`.

## Diagnostics (permanent — admin uniquement, v0.2.0)

> Remplace `Spike/Events`. **Auth** : Admin (`[Authenticated(Roles = "Admin")]`). **404** si `PluginConfiguration.EnableDiagnostics == false` (défaut : `true`). Lecture seule sauf `clear`. **Ids seulement** : aucun nom d'utilisateur, de playlist ou de média, aucun secret. Casse : réponses en **PascalCase** (comme `Spike/*`). Erreurs : 401, 403, 404, 500 (`error: <Type>: <message>`).
> Déclenchement d'une passe de réconciliation pour les tests : pas d'endpoint plugin, utiliser la tâche planifiée Emby (`POST /ScheduledTasks/Running/{id}`, admin ; l'id de la tâche « Emby Shared Playlist — réconciliation » est lisible par `GET /ScheduledTasks`).
> **Deux familles d'étiquettes** (mêmes règles pour chacune : casse ignorée, espaces tolérés autour de `=`, OUI+NON ensemble => NON l'emporte, le plugin ne supprime jamais d'étiquette, aucune étiquette de la famille => considérée NON et reposée) : `remove-si-lu` (retrait du média quand il devient lu ; **actif en v0.2.0**) et `propager-lu` (propagation de l'état de lecture ; posée et lue en NON dès v0.2.0, **sans effet avant v0.3.0**). Dans les contrats, `Family` = `remove-si-lu` | `propager-lu`.

### GET /SharedPlaylist/Diagnostics/Journal?clear={bool}&kind={liste}

**Description** : journal mémoire borné (500 entrées, plus récent en dernier) des décisions du plugin. `clear=true` vide après lecture. `kind` (liste séparée par des virgules) filtre.

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
- `Skipped` : pas d'action (`Detail` = `inactive|not-member|not-shared|already-seen|marker-present|description-not-empty|no-transition|unknown-owner|reentrant|lock-busy`).
- `Error` : exception isolée (`Detail` = type d'exception, sans message brut si celui-ci peut contenir des noms).

### GET /SharedPlaylist/Diagnostics/State

**Description** : état **en mémoire** du plugin (rien n'est persisté ; remis à zéro à chaque redémarrage), pour les tests d'intégration et le support.

**Response 200** :
```json
{ "SeenPlaylistIds": ["string"],
  "GraceCounters": { "<playlistId>": { "remove-si-lu": 0, "propager-lu": 0, "description": 0 } },
  "LastPass": { "Ts": "ISO-8601|null", "DurationMs": 0, "PlaylistsSeen": 0, "SharedManaged": 0 },
  "Handler": { "Count": 0, "LastMs": 0, "MaxMs": 0 },
  "GracePasses": 2 }
```
`SeenPlaylistIds` = playlists ayant subi la première détection depuis le démarrage. `GraceCounters` = passes consécutives sans étiquette de la famille (ou avec description vide). `Handler` = traitements de transition effectués dans le gestionnaire d'événement (nombre, dernière et plus longue durée en ms) : sert à vérifier le budget de latence.

> **Config plugin (v0.2.0)** : `EnableDiagnostics` (bool, défaut `true`), `GracePasses` (int, défaut `2`, min 1), `LogToConsole` (bool, défaut `true`), `LogLevel` (`Off|Info|Debug`, défaut `Info`). `EnableSpikeEndpoints` supprimé avec `Spike/*`. **Aucun état n'est stocké** (ni dans `PluginConfiguration`, ni dans un fichier). La période de réconciliation est celle de la tâche planifiée Emby (déclencheurs par défaut : démarrage + 5 min ; modifiable dans le tableau de bord).

> **Sonde temporaire U11 (B52)** : option `EnableReentrancyProbe` (défaut `false` ; **sans garde de comptes** — décision utilisateur, instance QUALIF : n'importe quel compte, admin compris, peut la déclencher ; seule une playlist nommée `SPIKE-P1`…`SPIKE-P6` (préfixe exact) active un scénario) ; les résultats sont journalisés dans `Spike/Events` avec `Kind=Probe` et `Detail` = `scenario=P1..P6 durationMs=<n> echoes=<n> outcome=OK|KO`. Aucun nouvel endpoint. Supprimée avec `Spike/*` (#15).
> **Pilotage de la sonde (implémenté)** : le scénario est choisi par le **nom de la playlist** (`SPIKE-P1` … `SPIKE-P6`, suivi de rien ou d'un séparateur : `SPIKE-P1-x` oui, `SPIKE-P10` non) et déclenché par des actions Emby standard, jamais par un endpoint :
> - P1 `RemoveFromPlaylist` depuis `UserDataSaved` : un membre de la playlist (n'importe quel compte) passe un média de la playlist à lu (`POST /Users/{id}/PlayedItems/{item}`, `TogglePlayed`) → la sonde retire l'entrée.
> - P2 mise à jour de métadonnées (`UpdateToRepository`, l'`…Async` n'existe pas dans ce SDK) depuis `UserDataSaved` : même déclencheur ; l'`Overview` est modifiée à chaque itération.
> - P3 `SaveUserData` d'un **autre** compte `test_*` ayant une ligne de partage (garde `PluginWriteTracker` : l'écho est reconnu `pluginWrite=true` et ne rebondit pas) : même déclencheur ; le compte cible passe à lu.
> - P4 mise à jour de métadonnées depuis `PlaylistItemsAdded` ou `ItemUpdated` (première détection simulée : la playlist est marquée « vue » avant l'écriture ; les événements suivants donnent `skipped=already-seen` ou `skipped=reentrant`) : ajouter une entrée ou éditer la playlist une première fois.
> - P5 rafale : comme P1, avec plusieurs transitions simultanées (fils différents) sur la même playlist ; le verrou par playlist sérialise ; un même média demandé plusieurs fois ne retire qu'une entrée (`removed=0 note=absent` pour les suivants).
> - P6 le gestionnaire attend 2 s (`note=sleep2000`) pendant que le propriétaire édite l'étiquette par REST, puis relit et écrit (sa durée n'entre pas dans le calcul de latence).
> Chaque exécution ajoute une entrée `Spike/Events` `Kind=Probe`, `Detail` = `scenario=Pn durationMs=<gestionnaire complet, verrou compris> lockWaitMs=<n> echoes=<événements reçus dans les 1,5 s> outcome=OK|KO` + champs propres (`removed=`, `remaining=`, `target=`, `playedAfter=`, `tagsAfter=`, `echoKinds=Kind:n,…`, `reason=lock-busy|no-target-user|loop-suspected`, `error=timeout-deadlock-suspected|<Type>`). `outcome=KO` si exception, appel SDK > 5 s (interblocage suspecté), verrou non obtenu en 5 s, ou plus de 5 échos (boucle suspectée). `GET Spike/Events` gagne le paramètre `kind` (liste, ex. `kind=Probe`) et le champ `Detail`.

> **RETIRÉ EN v0.2.0 (#15)** : la section `Spike` ci-dessous documente l'existant de v0.1.0 et sera supprimée de ce fichier par dev-plugin/doc-updater à la fin de #15 (l'historique reste dans git et dans `CHANGELOG.md`).

## Spike (diagnostic — admin uniquement)

Tous : **Auth** : Admin (`[Authenticated(Roles = "Admin")]`) ; **404** si `PluginConfiguration.EnableSpikeEndpoints == false`. Erreurs communes : 400 (paramètre invalide), 401, 403 (non admin), 404 (utilisateur/item/playlist introuvable), 500 (exception SDK — le corps est le message `error: <Type>: <message>`, jamais de secret).

**Casse JSON (constatée en réel, emby2)** : les réponses sont en **PascalCase** (`PlaylistId`, `Shares[{UserId, ShareLevel}]`…) : le sérialiseur d'Emby ignore les `[JsonPropertyName]`, retirés du code. Les exemples ci-dessous donnent les noms **finaux**. Requêtes : les paramètres de requête GET (`userId=`, `playlistId=`, `clear=`, `saveReason=`) sont acceptés en camelCase (confirmé : `Playlists?userId=` fonctionne). Les corps POST sont documentés en PascalCase (noms des propriétés des DTO) ; leur acceptation en camelCase (ce qu'envoient les scripts `tests/spike/`) dépend du binder d'Emby, réputé insensible à la casse, mais **non testable en unitaire** (le sérialiseur n'est pas dans `libs/`) : à confirmer à l'exécution, sinon les scripts passent en PascalCase.

**Identifiants** : les ids d'items (playlist, média, entrée `playlistItemId`) sont les identifiants internes Emby (entiers, en chaîne) ; les ids d'utilisateurs sont des GUID hexadécimaux sans tirets (`N`).

**Garde-fous (implémentés)** : les écritures (`Setup`, `MarkPlayed`, `SetPosition`, `Policy` POST) sont **refusées (400)** pour tout compte dont le nom ne commence pas par `test_` **ou qui est administrateur** (même nommé `test_x`) (jamais `admin`, `cyril`, `user2`) ; `RemoveItem` et `Tags` POST sont refusés (400) pour une playlist dont le nom ne commence pas par `SPIKE`. `Setup` préfixe le nom demandé par `SPIKE-` s'il ne commence pas déjà par `SPIKE` (défaut : « SPIKE À voir »).

### POST /SharedPlaylist/Spike/Setup

**Description** : crée une playlist « SPIKE À voir » pour `ownerUserId` avec `itemIds`, puis la partage en `Write` avec `memberUserIds` (`SaveUserItemShares`). Le propriétaire n'a pas de partage explicite créé par le plugin : ses droits viennent de `CreatePlaylist`. Le propriétaire présent dans `memberUserIds` est refusé (400). `shares` de la réponse = partages relus dans le dépôt après écriture. Points 1 (côté serveur) et 3.

**Request body** :
```json
{ "OwnerUserId": "string", "MemberUserIds": ["string"], "ItemIds": ["string"], "Name": "string (optionnel)" }
```

**Response 200** :
```json
{ "PlaylistId": "string", "Shares": [ { "UserId": "string", "ShareLevel": "Read|Write|Manage|ManageDelete|None" } ], "Entries": [ { "PlaylistItemId": "string", "ItemId": "string" } ] }
```

### GET /SharedPlaylist/Spike/Playlists?userId={id}

**Description** : playlists vues par cet utilisateur : celles où il a une ligne de partage (propriétaire = `ManageDelete`, membres = leur niveau) **ou** qui sont publiques (`IsPublic`) ; la requête serveur avec contexte utilisateur renvoyait aussi des playlists sans lien (constaté en réel), elles sont filtrées. `ShareLevel`, `CanManageAccess` et `CanLeaveSharedContent` sont calculés pour cet utilisateur à partir de sa ligne de partage (`GetShareLevel()` renvoyait `None` même pour le propriétaire). Points 1, 2.

**Response 200** :
```json
[ { "PlaylistId": "string", "Name": "string", "OwnerUserId": "string|null", "ShareLevel": "string", "CanManageAccess": true, "CanLeaveSharedContent": false, "IsPublic": false, "EntryCount": 0, "Entries": [ { "PlaylistItemId": "string", "ItemId": "string" } ] } ]
```

### POST /SharedPlaylist/Spike/RemoveItem

**Description** : le plugin retire une entrée de la playlist via `IPlaylistManager.RemoveFromPlaylist` (contexte admin/plugin, pas celui du propriétaire). Point 3. Les événements `PlaylistItemsRemoved` résultants sont journalisés.

**Request body** : `{ "PlaylistId": "string", "PlaylistItemIds": ["string"] }`
**Response 200** : `{ "Removed": true, "EntriesAfter": [ { "PlaylistItemId": "string", "ItemId": "string" } ] }`

### POST /SharedPlaylist/Spike/MarkPlayed

**Description** : écrit le flag lu via `IUserDataManager.SaveUserData`. Si `asPlugin` = true, l'écriture est enregistrée dans l'ensemble « écritures plugin » avant l'appel (anti-écho). Point 4.

**Request body** : `{ "UserId": "string", "ItemId": "string", "Played": true, "AsPlugin": true }`
**Response 200** : `{ "Saved": true, "PlayedAfter": true }`

### GET /SharedPlaylist/Spike/Events?clear={bool}&saveReason={liste}

**Description** : journal mémoire (borné à 500 entrées, plus récent en dernier) des événements captés : `UserDataSaved`, `PlaylistItemsAdded/Removed/Moved` et `ItemUpdated` (playlists seulement). `clear=true` vide après lecture. Points 2, 3, 4. Rien n'est journalisé si `EnableSpikeEndpoints` est faux. Tous les `UserDataSaved` sont journalisés, y compris `PlaybackProgress` (périodique, il sature vite le journal borné : filtrer et vider régulièrement). `saveReason` (optionnel) = liste de `SaveReason` séparés par des virgules (insensible à la casse) pour ne renvoyer que ceux-là (ex. `PlaybackFinished,PlaybackProgress`) ; `clear` vide tout le journal, filtré ou non. Issue #44. Pour `ItemUpdated`, `saveReason` porte le `ItemUpdateType` (ex. `MetadataEdit`) : c'est ce qui permet de détecter une boucle après une écriture d'étiquette. Pour `PlaylistItems*`, `entryId` porte le `PlaylistItemId` (`itemId` n'est renseigné que pour `Added` ; `Moved` ne journalise pas le nouvel index).

**Response 200** :
```json
[ { "Ts": "ISO-8601", "Kind": "UserDataSaved|PlaylistItemsAdded|PlaylistItemsRemoved|PlaylistItemsMoved|ItemUpdated", "UserId": "string|null", "ItemId": "string|null", "PlaylistId": "string|null", "EntryId": "string|null", "Played": true, "PositionTicks": 0, "LastPlayedDate": "ISO-8601|null", "SaveReason": "string|null", "PluginWrite": false } ]
```
`pluginWrite` = vrai si (userId, itemId) était dans l'ensemble « écritures plugin » (l'entrée est alors consommée).

### POST /SharedPlaylist/Spike/SetPosition

**Description** (issue #44, temporaire) : écrit `PlaybackPositionTicks` et `LastPlayedDate` d'un utilisateur `test_*` via `IUserDataManager.SaveUserData` (`SaveReason=PlaybackProgress`), **sans** modifier `Played` ni `PlayCount`. L'écriture est enregistrée dans l'ensemble « écritures plugin » avant l'appel (`pluginWrite=true` dans `Events`). Sert à trancher : le plugin peut-il écrire la position d'un autre utilisateur, et le média apparaît-il dans « reprendre » (`GET /Users/{id}/Items/Resume`, à lire par le script) ?

**Request body** : `{ "UserId": "string", "ItemId": "string", "PositionTicks": 0 }` (400 si `positionTicks` < 0)
**Response 200** : `{ "Saved": true, "PositionTicks": 0, "Played": false, "PlayCount": 0, "LastPlayedDate": "ISO-8601|null" }` (valeurs relues après écriture)

### GET /SharedPlaylist/Spike/Shares?playlistId={id}

**Description** : liste des partages d'une playlist lus **cote plugin** (`IItemRepository.GetUserItemShares`), sans contexte utilisateur. Verifie que la decouverte des playlists partagees (donc « gerees ») est possible sans REST utilisateur.

**Response 200** : `{ "PlaylistId": "string", "OwnerUserId": "string|null", "Shares": [ { "UserId": "string", "ShareLevel": "Read|Write|Manage|ManageDelete" } ] }`

### GET|POST /SharedPlaylist/Spike/Tags

**Description** : etiquettes et description d'une playlist, lues/ecrites **par le plugin**. GET `?playlistId={id}` ; POST `{ "PlaylistId": "string", "AddTags": ["string"], "RemoveTags": ["string"], "Overview": "string|null" }` (`overview` null = inchange). Ne remplace jamais l'ensemble des etiquettes : ajoute/retire seulement celles nommees (`removeTags` est reserve au diagnostic : le moteur, v0.2.0, ne supprime JAMAIS d'etiquette — decision utilisateur) (comparaison insensible a la casse ; `removeTags` l'emporte si un nom figure dans les deux listes). Plusieurs etiquettes (`propager-lu=NON` et `propager-lu=OUI`) peuvent coexister ; l'ordre « retirer NON puis ajouter OUI » (ou l'inverse) se teste en deux POST successifs, l'evenement `ItemUpdated` de chacun est journalise. Verifie : etiquette `propager-lu=NON` (caractere `=`, casse), preservation des etiquettes du proprietaire, absence de boucle sur l'evenement de mise a jour de metadonnees (journalise dans `Events`, kind `ItemUpdated`).

**Response 200** : `{ "PlaylistId": "string", "Tags": ["string"], "Overview": "string|null" }`

### GET|POST /SharedPlaylist/Spike/Policy

**Description** : point 5 bis. GET `?userId={id}` = valeur actuelle de `Policy.AllowSharingPersonalItems` ; POST `{ "UserId": "string", "AllowSharingPersonalItems": true }` la modifie via `IUserManager` (mise a jour de la politique). Ne modifie que ce champ : la politique complete est relue (`IUserManager.GetUserPolicy`) puis reecrite (`UpdateUserPolicy`). POST refuse tout compte hors `test_*` ou administrateur. Verifie que le plugin peut poser/lire cette permission (option future : « activer le partage pour les proprietaires de listes »).

**Response 200** : `{ "UserId": "string", "AllowSharingPersonalItems": false }`
