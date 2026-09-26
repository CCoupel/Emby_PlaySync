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

Aucun BREAKING. Ces endpoints sont diagnostiques et n'ont pas vocation à être stables.

Note : `Spike/Managed` et `ManagedPlaylistIds` sont abandonnés (D6 : une playlist partagée avec au moins un membre est gérée, pas de liste d'identifiants). Aucune page de gestion des partages n'est prévue en v0.1.0.
