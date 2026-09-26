# Contrats HTTP — Emby_shared_playlist

> Endpoints exposés par le plugin via `IService` (préfixe `/emby` selon l'installation). Auth : header `X-Emby-Token` (clé API admin ou token de session admin).
> Les endpoints `Spike/*` sont **temporaires** (v0.1.0) : diagnostic du partage natif, à retirer ou masquer derrière `EnableSpikeEndpoints` une fois le design tranché.

## Spike (diagnostic — admin uniquement)

Tous : **Auth** : Admin (`[Authenticated(Roles = "Admin")]`) ; **404** si `PluginConfiguration.EnableSpikeEndpoints == false`. Erreurs communes : 400 (paramètre invalide), 401, 403 (non admin), 404 (utilisateur/item/playlist introuvable), 500 (exception SDK — le corps contient `error` = type + message, jamais de secret).

### POST /SharedPlaylist/Spike/Setup

**Description** : crée une playlist « SPIKE À voir » pour `ownerUserId` avec `itemIds`, puis la partage en `Write` avec `memberUserIds` (`SaveUserItemShares`) et en `Manage` avec le propriétaire si le SDK l'exige. Points 1 (côté serveur) et 3.

**Request body** :
```json
{ "ownerUserId": "string", "memberUserIds": ["string"], "itemIds": ["string"], "name": "string (optionnel)" }
```

**Response 200** :
```json
{ "playlistId": "string", "shares": [ { "userId": "string", "shareLevel": "Read|Write|Manage|ManageDelete|None" } ], "entries": [ { "playlistItemId": "string", "itemId": "string" } ] }
```

### GET /SharedPlaylist/Spike/Playlists?userId={id}

**Description** : playlists vues par cet utilisateur (requête serveur avec son contexte utilisateur) : id, nom, `canManageAccess`, `canLeaveSharedContent`, niveau de partage effectif, nombre d'entrées. Points 1, 2.

**Response 200** :
```json
[ { "playlistId": "string", "name": "string", "ownerUserId": "string|null", "shareLevel": "string", "canManageAccess": true, "entryCount": 0, "entries": [ { "playlistItemId": "string", "itemId": "string" } ] } ]
```

### POST /SharedPlaylist/Spike/RemoveItem

**Description** : le plugin retire une entrée de la playlist via `IPlaylistManager.RemoveFromPlaylist` (contexte admin/plugin, pas celui du propriétaire). Point 3. Les événements `PlaylistItemsRemoved` résultants sont journalisés.

**Request body** : `{ "playlistId": "string", "playlistItemIds": ["string"] }`
**Response 200** : `{ "removed": true, "entriesAfter": [ { "playlistItemId": "string", "itemId": "string" } ] }`

### POST /SharedPlaylist/Spike/MarkPlayed

**Description** : écrit le flag lu via `IUserDataManager.SaveUserData`. Si `asPlugin` = true, l'écriture est enregistrée dans l'ensemble « écritures plugin » avant l'appel (anti-écho). Point 4.

**Request body** : `{ "userId": "string", "itemId": "string", "played": true, "asPlugin": true }`
**Response 200** : `{ "saved": true, "playedAfter": true }`

### GET /SharedPlaylist/Spike/Events?clear={bool}

**Description** : journal mémoire (borné à 500 entrées, plus récent en dernier) des événements captés : `UserDataSaved`, `PlaylistItemsAdded/Removed/Moved` et `ItemUpdated` (playlists seulement). `clear=true` vide après lecture. Points 2, 3, 4.

**Response 200** :
```json
[ { "ts": "ISO-8601", "kind": "UserDataSaved|PlaylistItemsAdded|PlaylistItemsRemoved|PlaylistItemsMoved|ItemUpdated", "userId": "string|null", "itemId": "string|null", "playlistId": "string|null", "played": true, "saveReason": "string|null", "pluginWrite": false } ]
```
`pluginWrite` = vrai si (userId, itemId) était dans l'ensemble « écritures plugin » (l'entrée est alors consommée).

### GET /SharedPlaylist/Spike/Shares?playlistId={id}

**Description** : liste des partages d'une playlist lus **cote plugin** (`IItemRepository.GetUserItemShares`), sans contexte utilisateur. Verifie que la decouverte des playlists partagees (donc « gerees ») est possible sans REST utilisateur.

**Response 200** : `{ "playlistId": "string", "ownerUserId": "string|null", "shares": [ { "userId": "string", "shareLevel": "Read|Write|Manage|ManageDelete" } ] }`

### GET|POST /SharedPlaylist/Spike/Tags

**Description** : etiquettes et description d'une playlist, lues/ecrites **par le plugin**. GET `?playlistId={id}` ; POST `{ "playlistId": "string", "addTags": ["string"], "removeTags": ["string"], "overview": "string|null" }` (`overview` null = inchange). Ne remplace jamais l'ensemble des etiquettes : ajoute/retire seulement celles nommees. Verifie : etiquette `propager-lu=NON` (caractere `=`, casse), preservation des etiquettes du proprietaire, absence de boucle sur l'evenement de mise a jour de metadonnees (journalise dans `Events`, kind `ItemUpdated`).

**Response 200** : `{ "playlistId": "string", "tags": ["string"], "overview": "string|null" }`

### GET|POST /SharedPlaylist/Spike/Policy

**Description** : point 5 bis. GET `?userId={id}` = valeur actuelle de `Policy.AllowSharingPersonalItems` ; POST `{ "userId": "string", "allowSharingPersonalItems": true }` la modifie via `IUserManager` (mise a jour de la politique). Verifie que le plugin peut poser/lire cette permission (option future : « activer le partage pour les proprietaires de listes »).

**Response 200** : `{ "userId": "string", "allowSharingPersonalItems": false }`
