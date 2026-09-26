# Changelog des contrats

## [20260926] — Squelette plugin + spike partage natif (v0.1.0)

- **[NEW]** `POST /SharedPlaylist/Spike/Setup` — crée et partage une playlist de test (temporaire)
- **[NEW]** `GET /SharedPlaylist/Spike/Playlists` — playlists vues par un utilisateur (temporaire)
- **[NEW]** `POST /SharedPlaylist/Spike/RemoveItem` — retrait plugin d'une entrée (temporaire)
- **[NEW]** `POST /SharedPlaylist/Spike/MarkPlayed` — écriture du flag lu marquée plugin/utilisateur (temporaire)
- **[NEW]** `GET /SharedPlaylist/Spike/Events` — journal d'événements (temporaire)
- **[NEW]** `GET /SharedPlaylist/Spike/Shares` — partages d'une playlist lus par le plugin (temporaire)
- **[NEW]** `GET|POST /SharedPlaylist/Spike/Tags` — étiquettes/description écrites par le plugin (temporaire)
- **[NEW]** `GET|POST /SharedPlaylist/Spike/Policy` — lecture/ecriture de `Policy.AllowSharingPersonalItems` (temporaire)

- **[NEW]** `POST /SharedPlaylist/Spike/SetPosition` — écrit la position de lecture d'un utilisateur de test sans marquer lu (temporaire, #44)
- **[CHANGED]** `GET /SharedPlaylist/Spike/Events` (#44) : champs `positionTicks` et `lastPlayedDate` ; paramètre `saveReason` (filtre) ; `PlaybackProgress` désormais journalisé sans condition (remplace la règle `played=true`)
- **[CHANGED]** `GET /SharedPlaylist/Spike/Events` : champ `entryId` ajouté (PlaylistItemId des événements `PlaylistItems*`) ; `saveReason` porte aussi l'`ItemUpdateType` pour `ItemUpdated` .
- **[CHANGED]** `GET Spike/Playlists` : `canLeaveSharedContent` documenté (déjà renvoyé). `POST Spike/Setup` : description alignée sur le code (partage `Write` des membres uniquement ; `Manage` du propriétaire = `CreatePlaylist`) ; propriétaire présent dans `memberUserIds` → 400 ; comptes administrateurs refusés par les garde-fous d'écriture (revue C2/S4).
- **[CHANGED]** `Spike/*` : garde-fous d'écriture (comptes `test_*`, playlists `SPIKE*`, 400 sinon) ; corps des erreurs 500 = `error: <Type>: <message>`.

SDK : aucune divergence de signature constatée (Emby 4.9.3.0, `libs/`) — `IPlaylistManager`, `ILibraryManager.SaveUserItemShares`, `IItemRepository.GetUserItemShares`, `IUserDataManager.SaveUserData/UserDataSaved`, `IUserManager.GetUserPolicy/UpdateUserPolicy`, `BaseItem.SetTags` compilent tels que décrits par réflexion. Les identifiants internes sont des `Int64` (les DTO REST les exposent en chaîne).

Aucun BREAKING. Ces endpoints sont diagnostiques et n'ont pas vocation à être stables.

Note : `Spike/Managed` et `ManagedPlaylistIds` sont abandonnés (D6 : une playlist partagée avec au moins un membre est gérée, pas de liste d'identifiants). Aucune page de gestion des partages n'est prévue en v0.1.0.
