# Contrats HTTP — Emby_shared_playlist

> Endpoints exposés par le plugin via `IService` (préfixe `/emby` selon l'installation). Auth : header `X-Emby-Token` (clé API admin ou token de session admin).
> Les endpoints `Spike/*` sont **temporaires** (v0.1.0) : diagnostic du partage natif, à retirer ou masquer derrière `EnableSpikeEndpoints` une fois le design tranché.

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
