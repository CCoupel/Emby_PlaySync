# MEMORY.md — Emby_PlaySync

> Mis à jour le 2026-10-04 en fin de session (/end-session). Source de vérité pour `/start-session`.
> À tenir à jour à chaque session.

## Version courante

- **v1.2.2** — déployée en PROD (namespace `media`, `deployment/emby`), tag `v1.2.2`,
  merge `43b88f8` (main), release https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.2
- DLL : `EmbySharedPlaylist.dll` sha256 `6217b3d9…03bb` (223 KB), déployée le 2026-10-03
  - Déploiement PROD : scale 0 → pod busybox de sauvegarde sur PVC emby-config-claim → `/config/backup-pre-v1.2.2-20261003-121847/` (library.db + .wal/.shm) → DLL versionnée → scale 1 ; integrity_check non exécuté (sqlite3 absent)
  - `.prev` = 1.2.1.0 (PROD) ; QUALIF emby2 1.2.2.1 `.prev` = 1.2.2.0
- Branche `milestone/v1.2.2` : poussée, mergée dans `main`, non supprimée
  - `main` = `513a850`, aligné `origin/main`
- **Template Claude : v3.9.1** (`d2d8143`), sync faite le 2026-10-03 (était v3.9.0)
- **Milestone GitHub v1.2.2 fermé** ; issues #59 #60 #61 fermées
- **Aucun milestone ouvert** — ne créer v1.3.0 que pour une feature
- Site marketing : v1.2.1/v1.2.2 rédigés (docs/releases/vX/social-posts.md), **NON publiés** — publication manuelle
- **Posts réseaux v1.2.0 v1.2.1 v1.2.2** : rédigés, NON publiés (publication manuelle)

## Travail en cours

Aucun. Cycle v1.2.2 clos (PROD + doc + CI validés).

## Décisions techniques v1.2.2 (bugfix #59 #60 #61, D24, session 2026-10-02/10-04)

### Découverte : deux défauts PLUGIN cachés sous 3 "bugs de test"

#### (a) Fuite du WriteScope vers le worker de rafraîchissement (I16 reentrant +2, première détection manquée)
- **Cause** : `AsyncLocal<int> Depth` utilisé pour `WriteScope.Active` (Core/WriteScope.cs)
  - `RemoveFromPlaylist` met en file un rafraîchissement asynchrone via `Task.Run` qui **capture l'ExecutionContext**
  - ExecutionContext inclut une copie du `Depth` AsyncLocal = 1 (on est dans le scope)
  - À la sortie du scope côté plugin, `Dispose()` restaure `Depth` **dans le contexte du plugin** uniquement
  - Le worker Emby garde `Depth = 1` pendant toute la vie de la file de rafraîchissement
  - `ItemUpdated` émis par le worker est compté `reentrant` (fausse ré-entrance), première détection ignorée

#### (b) Retrait du MAUVAIS média sur retraits rapprochés (I14.once, I14c.parN 5/45 en v1.2.1)
- **Cause** : `RemoveFromPlaylist` met en file un rafraîchissement complet (**asynchrone**) qui réécrit toutes les entrées ListItems
  - Table ListItems **sans AUTOINCREMENT** ; SQLite réutilise `max(rowid)+1` → les nouveaux rowids sont les mêmes, décalés sur d'autres ItemId
  - `EmbyPlaylistGateway.RemoveOneEntry` lit l'`EntryId` (rowid), puis appelle `RemoveFromPlaylist(playlist, [entryId])`
  - Si le worker réécrit ListItems **entre la lecture et la suppression**, l'EntryId lu est périmé et désigne un **autre média**
  - **Défaut présent en PROD v1.2.1 et avant** — condition de course sur même playlist, quelques ms à quelques secondes d'intervalle (deux retraits parallèles, sélection multiple)

### Correctif (D24 : F1 WriteScope à identité + F2-b retrait sûr + F3 RemoveOutcome)

#### F1 — WriteScope à jeton chaîné et révoqué
- Remplace `AsyncLocal<int>` par `AsyncLocal<ScopeToken?>`
- Chaque `Enter()` crée un jeton chaîné au parent, révoqué à `Dispose()` (volatile bool)
- `Active` parcourt la chaîne : faux dès que le jeton est révoqué, **indépendamment du contexte**
- Une copie capturée par `Task.Run` est inactive dès la sortie du scope (protège contre la fuite ExecutionContext)
- API inchangée (`Enter()`, `Active`, `Dispose()`)

#### F2-b — Retrait sûr avec attente du rafraîchissement, relecture, compensation
- **IProviderManager injecté** via DI dans 7 classes (Diagnostics, PlaybackListener, etc.)
- **Avant écriture** : attente bornée ≤ 1 s (poll 10 ms) que la playlist ne soit plus dans `GetRefreshQueue()` / `GetRefreshProgress()`
- **Après suppression** : relecture de l'`EntryId` pour détecter renumérotation (aucun > 0 = périmé)
- **Compensation** : si `after.Count < before.Count && before.Count > 1`, ré-ajout via `AddToPlaylist` (journal `Error wrong-entry`)
- **Repli** : si première attente expire, pas de ré-ajout (journal `Error wrong-entry-unconfirmed`)
- L'ordre de la liste peut changer en cas de compensation

#### F3 — RemoveResult, TargetRemaining, Skipped stale-entry
- `RemoveOneEntry` retourne `RemoveResult { Removed | NoEffect | NotFound, TargetRemaining, OtherLost }`
- Boucle n'incrémente `entries` que sur `Removed` (entrées **réellement** supprimées)
- Tentatives sans effet (entryId périmé) → retentée (max 2), journalisé `Skipped stale-entry` (non bruyant, inclus en `Diagnostics/State.skippedCounts`)
- `TargetRemaining = -1` si attente initiale expirée (parade F3-1 : risque d'arrêt prématuré sur `TargetRemaining == 0`)

### Latence et tests (#60 #61)

- **Latence** : isolé p95 31–44 ms (1.2.1 baseline ~48 ms), rafale p95 314–393 ms (1.2.2.0 était 410–500 ms)
  - **I17.p95** (300 ms) porte sur retraits isolés ; **I17.burst** (p95 ≤ 1 s, max ≤ 2 s) sur rafales
  - Assouplissement I17 accepté par l'utilisateur (prix de la protection contre le mauvais média)
- **Tests** (#60 #61) :
  - Purge SPIKE*/INT_NESTED, bibliothèque locale PlaySync-Tests (20 vidéos ffmpeg, tests/integration/01-setup-media.sh idempotent)
  - Ordre complet : 01 → 00 → 20–25 → 90 ; un seul run QUALIF à la fois
  - Nouveaux I14.integrity, I14c (séquentiel 6 médias), I14p (rafale parallèle C_ITER=15)
  - I16 scindé reentrant/deferred ≤ 1 chacun ; I16.posed corrigé (jclear avant création, stabilisation 15 s)
  - Garde-fou assert_local_media (refus `/config/virtual/`), Stopped/Logout des sessions, SESSIONS.clean
  - EMBY_API_KEY : CR final dans private/qualif.env (corriger avec `tr -d '\r\n'`)

## Leçons (v1.2.2, session 2026-10-02/10-04)

### (1) Diagnostic : les 3 "bugs de test" cachaient 2 défauts PLUGIN
- **Processus** : I16/I23/I14c étaient censés être des défauts de test (charges, races, timeouts)
- **Découverte** : décompilation IL des DLL Emby du pod QUALIF (lecture seule, `/system/Emby.*.dll` via `ilspycmd`)
  a révélé la fuite ExecutionContext + la renumérotation de ListItems (sans AUTOINCREMENT)
- **Leçon** : avant dev, vérifier les diagnostics en QUALIF réel (pas juste analyse statique)
- **Preuve** : I14c séquentiel était vert sur v1.2.1 (pas de race), il a fallu I14p parallèle pour montrer la défaillance

### (2) QA v1.2.2 : VALIDATED
- **Unitaires** : 950/950 ✓
- **Intégration QUALIF** : I14/I14c/I14p (45 retraits parallèles ×3) 0 rouge ; I16 vert 3/3 ; I17.p95 isolé ≤ 300 ms ✓ ; I17.burst p95 ≤ 1 s ✓ ; SESSIONS.clean 9/9 ✓
- **Aucune réserve bloquante** — stale-entry 1/693 (comportement attendu F3), latence rafale p95 ~315 ms (< 1 s cible)

### (3) Procédure PROD v1.2.2 : sauvegarde à froid via pod busybox
- **Chemin correct** : `/config/data/library.db` (et `.wal`, `.shm`)
- **Procédure** :
  1. Scale 0, attendre terminaison complète du pod
  2. **Pod busybox temporaire monté sur PVC `emby-config-claim`** (copie à froid)
  3. Sauvegarde dans backups/prod/`<timestamp>`/
  4. DLL déployée sur le pod normal
  5. Scale 1
- **Limitation** : sqlite3 **absent** du pod busybox ; integrity_check non réalisable (alternative documentée : reprise manuelle si KO)
- **Jamais** `rollout restart` (incohérence SQLite possible)

### (4) Variante F2-a (retrait par ItemId) non retenue
- **Raison** : non vérifiée en réel, API interne, plus d'événement `PlaylistItemsRemoved` (rupture pour clients tiers)
- **Repli si latence rafale gêne en PROD** : basculer à F2-a sans retest (moins de latence, accepté par l'utilisateur comme contingency)

### (5) Règles QUALIF (#61) strictes et robustes
- **Aucun média virtuel** : tests locaux sur PlaySync-Tests (ffmpeg) + garde-fou assert_local_media
- **Un seul run à la fois** : purge SPIKE*, Stopped/Logout en sortie
- **EMBY_API_KEY** : cr final dans private/qualif.env (corriger avec `tr -d '\r\n'`)
- **media.coupel.net** : non joignable depuis WSL (passer par pod / port-forward)

### (6) Points ouverts pour v1.3.0+
- **Surveillance en PROD** : ratio stale-entry/Removal, latence rafale réelle (max 2 s spécification)
- **Test I10 --restart** : jamais exécuté (skip dans QUALIF)
- **Repli F2-a** : à vérifier en réel si F2-b gêne
- **Clients TV/mobile** (#28) : édition étiquettes non garantie

## Règles critiques projet

- Ne jamais déployer sur `deployment/emby` (PROD) sauf via `/deploy prod` sur ordre explicite utilisateur
- Un seul milestone en développement à la fois, branche `milestone/vX.Y.Z`
- Spec de référence : `docs/chronogrammes.md` (règles R1–R12, scénarios S1–S11, décisions D3/D9/D19/D20/D21/D23/D24)
  - **D24 (v1.2.2)** : retrait sûr (WriteScope à jeton F1 + attente rafraîchissement F2-b + RemoveOutcome F3)
  - Latence isolée p95 ≤ 300 ms ; rafale sérialisation par playlist p95 ≤ 1 s, max ≤ 2 s
- PROD SQLite : arrêt propre avant redémarrage (pas `rollout restart`, utiliser scale 0/wait/scale 1)
- Sauvegarde library.db à **froid** (scale 0 + pod busybox temporaire sur PVC + copie `/config/data/library.db{,.wal,.shm}`) avant déploiement
  - **Chemin correct** : `/config/data/library.db` (non `/config/plugins/`)
  - sqlite3 absent du pod : integrity_check non réalisable tel quel
- Marketing : travail seulement sur branche `gh-pages` (worktree `MARKETING/`), jamais sur `main/milestone/*`
- Jamais de travail direct sur `main` ; pipeline de release = contrat C1–C9 (`agents/infra`, section 3bis), audité avant PUBLISH PROD
- `/deploy prod` doit dispatcher automatiquement `marketing PREPARE` (Phase 6)
- Tests QUALIF : éviter médias virtuels VirtualLib (isolation emby2 vs PROD) ; un seul run à la fois
- EMBY_API_KEY : nettoyer CR final (`tr -d '\r\n'` avant export)

## Backlog v1.3.0

| Risque / Opportunité | Priorité | Notes |
|---|---|---|
| B12 — Anti-écho compté par écriture (durcir si U14b en deux événements) | moyenne | Conditionnelle ; resultat U14b déjà OK |
| `UserItemLocks` borné + TTL | moyenne | Risques concurrence ; actuellement non borné |
| Repli `RemoveListItemsByItemIds` (Emby 4.10+) | basse | API interne, retrait par ItemId si no EntryId ; non vérifié en réel |
| `PlaylistEntryReader` stratégies multiples | basse | Couverture partielle des cas Emby (versions SDK) |
| `DeleteShare` transactionnel | basse | Retrait membre non rollbackable si retrait playlist échoue |
| Comptage playlists PROD impactées v1.2.0 | moyenne | BREAKING behaviors — mesurer adoption real |
| Clients TV/mobile #28 | basse | Visibilité playlist + édition étiquettes non garanties |
| Perf Handler (p95) | basse | Mesure complète sur vraie charge PROD (323 ms max > 300 ms cible) |

---

**Clôture session 2026-10-04** : v1.2.2 en PROD (patch D24 : WriteScope à jeton + retrait sûr + RemoveOutcome)
; template v3.9.1 synchronisé ; milestone v1.2.2 fermé (poussé, mergé, issues #59 #60 #61 fermées)
; aucun milestone ouvert. Prochaine étape : cadrage v1.3.0 (feature nouvelle ou accumulation de bugs).
