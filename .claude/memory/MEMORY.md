# MEMORY.md — Emby_PlaySync

> Mis à jour le 2026-10-02 en fin de session (/end-session). Source de vérité pour
> `/start-session`. À tenir à jour à chaque session.

## Version courante

- **v1.2.1** — déployée en PROD (namespace `media`, `deployment/emby`), tag `v1.2.1`,
  merge `2828ea9`, release https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.1
- Déploiement PROD : 2026-10-02 (scale 0 / terminaison / copie / scale 1, base saine)
- Branche courante : `main` (HEAD `9c9c1f4`, aligné `origin/main`), aucune branche `milestone/*` ouverte
  - `milestone/v1.2.1` conservée (non supprimée), contient tout le travail du bugfix #58
- QUALIF (`emby2`) : plugin 1.2.1.0, `.prev` = 1.2.0.0 (PROD aussi)
- Site marketing publié : https://ccoupel.github.io/Emby_PlaySync/ (v1.2.1, gh-pages seulement)
- **Posts réseaux v1.2.0 et v1.2.1 : rédigés, NON publiés** (docs/releases/vX/social-posts.md) — publication manuelle

## Travail en cours

- Aucun cycle FEATURE/BUGFIX/HOTFIX en cours — session close proprement, team dissoute
- Milestone GitHub : **v1.2.1 fermé** (#58 fermée)
- **Aucun milestone ouvert** — backlog v1.3.0 en attente de cadrage

## Décisions techniques v1.2.1 (bugfix #58, D23, session 2026-09-30/10-02)

### Cause du bugfix #58
- **Avancement propagé seulement à pause/arrêt** (v1.2.0) — en lecture continue sans pause, position n'était jamais écrite chez les membres
- **Fin de lecture** : Emby pose « lu » **avant** `PlaybackStopped` (spike U15 confirmé : `PlayedToCompletion=true` à 100 % et 95 %)
  - Avec `remove-si-lu=OUI` actif, le média était déjà retiré quand `PlaybackStopped` arrivait → position non écrite
  - Symptôme observé : média « lu » chez le membre, position périmée (~99 %)

### Correctif (D23, spec docs/chronogrammes.md R10/S9g)
- **Événements écoutés** : `PlaybackStart` (nouveau), `PlaybackProgress` (chaque), `PlaybackStopped` (+ `PlayedToCompletion`)
- **Propagation continue** : à chaque `ISessionManager.PlaybackProgress`, au plus **une fois toutes les 10 s par couple** (déclencheur, média)
  - **Throttle** : 10 s absolu par couple ; si verrou occupé délai 250 ms → ignoré sans journal, compteur `LockBusy`
  - **Pause/arrêt** : immédiats (pas d'attente throttle)
- **Seuil 30 s** : conservé (position absolue dans le média)
- **Fin de lecture** (`PlayedToCompletion=true`) :
  - Si `propager-lu=OUI` aussi actif → position écrite = **0** (aucun point de reprise, miroir du déclencheur)
  - Sinon → position d'arrêt brute (~99–100 %)
- **Cibles de playlists** : mémorisées pendant la lecture dans `PlaybackSyncTracker.TargetPlaylistIds` (péremption 5 min, liste vide re-résolue)
- **Race Progress/Stop** : `PlaySessionId` fermé par `OnStop` avant que tout `Progress` tardif ré-écrive (garde `IsOpen` sous verrou)
- **Repli incertitude `PlayedToCompletion`** : si absent/faux, utiliser UserData du déclencheur (`Played=true` ET position = 0) — mais U15 confirme fiable à 100 % et 95 %
- **Journal** : `PositionPropagation.Detail` gagne `trigger=<pause|stop|completion>` ; les Progress périodiques ne sont **pas journalisés** (bruit), compteurs `Diagnostics.PositionProgress { Propagated, Throttled, LockBusy }` à la place
- **Handler** : `State.Handler` ne mesure plus les Periodic (p95/max non biaisées par ~1/10s)

### Nouveau tracker : `PlaybackSyncTracker` (remplace `PauseTransitionTracker`)
- Mémoire LRU borné 2000, thread-safe, logique pure
- Par couple (user, item) : `PlaySessionId`, `LastPaused`, `LastPropagatedAt`, `LastPropagatedTicks`, `Closed`, `TargetPlaylistIds`
- Décisions retournées : `PauseTransition`, `Periodic`, `Throttled`, `Ignored`, `Closed`

## Incidents et leçons (session 2026-09-30/10-02)

### INCIDENT 2026-10-02 — Sessions fantômes (PROD, diagnostic)
**Contexte** : L'UI Emby PROD montrait 9 « lectures » (`test_u1`, `test_u2`, `test_u3`, spike) depuis le 30/09

**Cause établie** : Relais VirtualLib (emby2 QUALIF vers PROD, médias virtuels `/config/virtual/DadoursTV`)
- VirtualLib 1.10.0 sur emby2 relayait les `PlaybackStart` des tests QA sans les `PlaybackStopped` correspondants
- Le heartbeat interne de VirtualLib gardait les sessions "ouvertes"
- Les vraies lectures PROD (très peu) n'étaient pas affectées, mais les compteurs UI gonflés

**Résolu par** : Redémarrage emby2, puis redémarrage PROD (sessions fermées)

**Leçons** :
1. **Tests QUALIF ne doivent pas lire de médias virtuels** (#61 ouverte) — cibles de test : médias locaux ou Emby URL
2. **Avant tout DEPLOY PROD**, vérifier lectures actives :
   - Consulter logs PROD (pas de clé API PROD disponible dans `private/`)
   - S'attendre à quelques vraies lectures, identifier vs. fantômes de test
3. **Distinguer vraies lectures et artefacts** : logs + durée + compte + URL média

### (2) QA v1.2.1 : VALIDATED WITH RESERVATIONS
- **Unitaires** : 900/900 ✓
- **Intégration QUALIF** : 22-avancement.sh 77 OK / 0 KO / 0 SKIP ; 20-etiquettes-retrait.sh 70 OK / 1 KO / 1 SKIP ; 21-propagation.sh 56 OK / 2 KO
- **Réserves** :
  - I16.echo.removal (+2) : pré-existant en v1.2.0 et v1.2.1, seuil ≤ 1 probablement obsolète (#59)
  - I23.S6a.propagation : intermittent (flaky), dépend de résidus d'état, OK quand rejoué seul (#60)
  - Aucun lien avec #58 (flux lu et code non modifiés)
- **NON vérifié** : aucune lecture réelle Emby Web (cadence réelle des `PlaybackProgress`, CA1/CA4 en réel) ; coût résolution playlists pour média hors playlist non mesurable sur emby2 ; `Handler` max 323 ms (> 300 ms cible, p95 non exposé) — à re-mesurer en PROD

### (3) Procédure PROD : sauvegarde library.db
- **AVANT** : pas de documentation, sauvegarde pouvait être à chaud (possiblement incohérente)
- **APRÈS** (deploy.prod.md, commit 9c9c1f4) :
  ```
  1. Scale 0 et attendre terminaison complète du pod
  2. Copie à froid de library.db + library.db-wal + library.db-shm
  3. Backup dans backups/prod/<timestamp>/
  ```
- **Important** : jamais de copie à chaud, jamais `rollout restart` (utiliser scale 0/wait/scale 1)
- Sauvegarde du 02/10 avant v1.2.1 (/config/backup-pre-v1.2.1-20261002-091809/ dans le pod PROD) avait été à chaud

### (4) Site marketing vit sur gh-pages seulement
- **UNIQUE source** : branche `gh-pages` → https://ccoupel.github.io/Emby_PlaySync/
- Dossier `MARKETING/` **retiré** de `main` en commit 3c24ca6 (doublon historique)
- **Consigne agent** : travailler dans un worktree `gh-pages` (_work/site, gitignoré), jamais de site sur `main/milestone/*`
- **Ambiguïté template résolue** : users doivent corriger `marketing-release.template.md` (l.55-62/270/305/314/318), `marketing.md` (l.122), `init-project` (l.951)

### (5) Règle oubliée : `/deploy prod` dispatche systématiquement `marketing PREPARE`
- **Décision** : Phase 6 du cycle (Deployment + Communication)
- **NON fait au déploiement v1.2.1** : agent `marketing-release` n'est pas permanent (spawné à la demande)
- Rattrapé a posteriori sur demande utilisateur
- À documenter et respecter pour v1.3.0+

### (6) Issues ouvertes et backlog impacté
- **#59** : I16.echo.removal (+2 rare), pré-existant, probable seuil obsolète
- **#60** : I23.S6a intermittent, flaky/test (aucun impact opérationnel)
- **#61** : Tests QUALIF doivent éviter médias virtuels VirtualLib
- **Backlog v1.3.0** (inchangé) : B12 (anti-écho durcir), `UserItemLocks` (risques concurrence), repli `RemoveListItemsByItemIds`, `PlaylistEntryReader`, `DeleteShare`, comptage playlists PROD v1.2.0, clients TV/mobile (#28)

### (7) SDK .NET 6.0.428 hors PATH WSL
- Rappel CLAUDE.md : chemin complet requis en WSL `/mnt/c/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe`

### (8) Équipe/Outils — incohérences à remonter
- **TeamCreate indisponible** dans la session
- **Adresse teammates** : répondent à `team-lead` (label de tâche), pas à `main` (adresse du protocole template)
- **Protocole** : `.claude/agents/context/TEAMMATES_PROTOCOL.template.md` (pas de fichier sans suffixe)
- **Template v3.7.3** (8c49482, inchangé) : à corriger pour unifier adresses `main`/`team-lead`

## Règles critiques projet

- Ne jamais déployer sur `deployment/emby` (PROD) sauf via `/deploy prod` sur ordre explicite utilisateur
- Un seul milestone en développement à la fois, branche `milestone/vX.Y.Z`
- Spec de référence : `docs/chronogrammes.md` (règles R1–R12, scénarios S1–S11, décisions D3/D9/D19/D20/D21/D23)
- PROD SQLite : arrêt propre avant redémarrage (pas `rollout restart`, utiliser scale 0/wait/scale 1)
- Sauvegarde library.db à **froid** (scale 0 + terminaison complète du pod) avant tout déploiement
- Marketing : travail seulement sur branche `gh-pages`, jamais sur `main/milestone/*`
- `/deploy prod` doit dispatcher automatiquement `marketing PREPARE` (Phase 6)
- Tests QUALIF : éviter médias virtuels VirtualLib (isolation emby2 vs PROD)

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

**Clôture session 2026-10-02** : v1.2.1 en PROD, docs finalisées, procédures corrigées, leçons documentées.
