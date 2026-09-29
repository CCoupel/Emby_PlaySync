# MEMORY.md — Emby_PlaySync

> Mis à jour le 2026-09-29 en fin de session (/end-session). Source de vérité pour
> `/start-session`. À tenir à jour à chaque session.

## Version courante

- **v1.2.0** — déployée en PROD (namespace `media`, `deployment/emby`), tag `v1.2.0`,
  merge `c7e70f7`, release https://github.com/CCoupel/Emby_PlaySync/releases/tag/v1.2.0
- Branche courante : `main` (HEAD `1a78c2c`, aligné `origin/main`), aucune branche `milestone/*` ouverte
  - `milestone/v1.2.0` conservée (non supprimée), contient tout le travail des issues #55/#56/#57
- Site marketing publié : https://ccoupel.github.io/Emby_PlaySync/ (v1.2.0)
- **Posts réseaux v1.2.0 : NON publiés** (copiés dans `docs/releases/v1.2.0/social-posts.md`)

## Travail en cours

- Aucun cycle FEATURE/BUGFIX/HOTFIX en cours — session close proprement, team dissoute
- Milestone GitHub : **v1.2.0 fermé** (#55, #56, #57 toutes fermées)
- **Aucun milestone ouvert** — backlog en attente de cadrage pour v1.3.0

## Décisions techniques v1.2.0 (session 2026-09-28/29)

### Décorrélation lu / avancement (D21, #56 + #57)
- **`propager-lu`** = **flag lu seul** (transition non lu → lu, alias « Propager le lu »)
- **`propager-avancement`** = **position seule** (pause/arrêt ≥ 30 s, alias « Propager l'avancement »)
- **Indépendants** : chacun fonctionne isolé, sans dépendre de l'autre
- **`remove-si-lu` subordonné à `propager-lu`** : n'a d'effet que si `propager-lu=OUI` active
  - Playlist avec `remove-si-lu=OUI` + `propager-lu=NON` → retrait inerte (changement v1.1.0→v1.2.0)
  - Playlist avec `propager-lu=OUI` seule → avancement cesse d'être propagé (changement v1.1.0→v1.2.0)

### Aucune migration ni rétrocompatibilité
- Famille absente sur une playlist existante = NON (posé à la première détection)
- Étiquettes existantes inchangées, simplement réinterprétées (« silence dans les changements »)
- Aucun héritage de `propager-lu` vers `propager-avancement`

### Relecture d'un média déjà lu (#57)
- Garde D-c (`trigger-already-played`) **supprimée**
- Position propagée à **chaque pause/arrêt** (≥ 30 s), même pour un média déjà lu
- Permet relecture avec synchronisation sans reposer le flag lu

### Seuil de position : 30 s = position **absolue** dans le média (conservé)
- Pas une durée de lecture ni une avance depuis la reprise
- Reprendre à 40 min et arrêter 5 s plus tard (40 min 05 s) **propage**
- Arrêter à 25 s depuis le début **ne propage pas**

### Pas d'UpdatePlayState : position brute écrite
- Émis S9f (U14b) : Emby **ne pose pas le « lu »** sur une position ~99 % écrite par le plugin (confirmé QUALIF réel)
- Membre voit « Reprendre à 99 % » **sans** être marqué « lu »
- Parade : activer `propager-lu=OUI` pour aussi propager le flag

### Création de playlist (#55)
- Nom unique par propriétaire (insensible casse, NFC, accents significatifs)
- **Normalisation** : trim, NFC, rejet Cc/Cf/Zl/Zp/Cn
- Quota : **10 playlists possédées** (code 409 `limit-reached`)
- Verrou de création : **1 s** (code 409 `busy`, au lieu de 5 s global)
- Emby assainit lui-même les noms de dossier (`../x` → `x [playlist]`, etc.) — F2 vérifié QUALIF

### Anti-écho compté par (utilisateur, média)
- **`PluginWriteTracker`** : compteur par couple, pas une entrée unique
- Permet deux écritures successives (lu + position) reconnues chacune comme écho
- Risque résiduel (B12, conditionnelle) : U14b si écho asynchrone en deux événements → alors durcir

### Verrou partagé (utilisateur, média) pour lu/position
- **`UserItemLocks`** : nouveau, réentrant avec timeout
- Sérialise lu et position chez même membre, garantit pas de perte
- Ordre de verrous : playlist (verrou interne), puis (user, media) — cohérent, pas de deadlock

### Message d'aide V3
- Trois étiquettes, trois lignes, dépendance explicitée
- V1 et V2 détectés et remplacés (`v1-to-v3`, `v2-to-v3`) ; textes perso jamais écrasés

### Trois interrupteurs PlaySync
- « Retirer si lu » → grisé tant que « Propager le lu » décochée
- « Propager le lu » → flag lu
- « Propager l'avancement » → position
- Chacun replace atomiquement toutes les étiquettes de sa famille (D19)

## Incidents et leçons (session 2026-09-29)

### INCIDENT DEPLOY PROD v1.2.0 (2026-09-29, grave, NON imputable au plugin)
**Contexte** : `deployment/emby` PROD sur Kubernetes namespace `media`, SQLite `library.db` sur PVC locale

**Incident** : Après redémarrage (scale 0 → 1), `library.db` crash boucle : « database disk image is malformed »
- Avant chargement des plugins (v1.2.0 jamais exécuté)
- PROD restaurée par l'utilisateur depuis sauvegarde de la nuit précédente
- Données de la journée avant l'incident **potentiellement perdues**

**Hypothèse utilisateur** : Arrêt trop brutal lors du scale 0 (pas de grâce) → corruption SQLite WAL

**Cause NON établie** dans la session. Plugin v1.2.0 reste sain en PROD.

**Correction procédure déploiement PROD** (tâche **environments/deploy.prod**) :
```
AVANT :  rollout restart → Emby redémarre instantanément
APRÈS :  scale 0/wait 10s/scale 1 → fermeture propre SQLite WAL
```

**À faire avant tout futur déploiement PROD** :
1. Sauvegarde datée de la base juste avant
2. Vérifier aucune lecture active (`Sessions/Active`)
3. Arrêt propre Emby (attendre termination grace period, pas `rollout restart`)
4. Comptage playlists impactées par changements comportement (pas d'accès admin PROD cette fois)

### (2) Couverture tests : 63,6 % global (seuil 70 % non atteint)
- Logique pure : Core 98,4 %, Engine 97,6 %, Marker 96,1 %, Reconciliation 95,7 %, UserPage 98,9 %
- **Lacune structurelle** : adaptateurs Emby 10,1 % (non testables en unitaire)
- **Risques documentés** (non bloquants) : repli `RemoveListItemsByItemIds`, stratégies `PlaylistEntryReader`, `DeleteShare` non transactionnel
- **Décision** : seuil global 70 % inchangé dans `project-config.json` ; zones logique pures bien couvertes

### (3) SDK .NET 6.0.428 hors PATH WSL
- Ajouté à CLAUDE.md section « Build local (WSL) »
- Path WSL : `/mnt/c/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe`
- Path Windows : `C:\Users\cyril\AppData\Local\Microsoft\dotnet\dotnet.exe`
- Agents doivent utiliser le chemin complet (pas `dotnet` seul)

### (4) Comptage playlists PROD impactées par changements de comportement
- **NON effectué** : pas d'accès admin QUALIF/PROD en fin de session
- **À faire avant v1.3.0** : compter via `/Diagnostics/State` les playlists en `remove-si-lu=OUI` + `propager-lu≠OUI` et celles en `propager-lu=OUI` (mesurer impact des deux BREAKING)
- Pistes prévention : release notes, guide v1.2.0, mentions dans MANUAL.md

### (5) Template Claude Code v3.7.3 (2026-09-29)
- Synchronisé via `/init-project` (8c49482ee38f9172edbb99bbf7aa4bdc56e5c5bf)
- Adresse teamleader : **`main`** dans template, **`team-lead`** en session réelle
  - **À remonter** : protocole template doit évoluer ou adresse local doit être unifié

### (6) .claude/project-config.json version périmée
- Porte `"version": "0.5.0"` alors que plugin en 1.2.0
- À corriger à la prochaine session

### (7) _work/ purgé à la clôture
- Posts v1.2.0 copiés vers `docs/releases/v1.2.0/social-posts.md` avant purge
- Rapports QA/security/planner archivés dans les mémoires individuelles de session ; aucun regret

## Règles critiques projet

- Ne jamais déployer sur `deployment/emby` (PROD) sauf via `/deploy prod` sur ordre explicite utilisateur
- Un seul milestone en développement à la fois, branche `milestone/vX.Y.Z`
- Spec de référence : `docs/chronogrammes.md` (règles R1–R12, scénarios S1–S10, décisions D3/D9/D19/D20/D21)
- Avant tout rendu de composant natif Emby (`emby-toggle`, `emby-select`, etc.) dans une page de
  plugin côté **menu utilisateur** (`EnableInUserMenu`) : toujours envelopper la création
  d'éléments dans un `require(['emby-toggle', 'emby-select', ...], function(){...})` — ne pas
  compter sur un chargement implicite (leçon GATE 4 v1.1.0)
- SDK .NET 6.0.428 mode utilisateur Windows : chemin complet requis en WSL (voir CLAUDE.md)
- PROD SQLite : arrêt propre avant redémarrage (pas `rollout restart`, utiliser scale 0/wait/scale 1)

## Backlog identifié pour v1.3.0

| Risque / Opportunité | Priorité | Notes |
|---|---|---|
| B12 — Anti-écho compté par écriture (durcir si U14b en deux événements) | moyenne | Conditionnelle ; résultat u14b déjà OK |
| Croissance `UserItemLocks` sur gros serveurs | faible | Décision D18 : acceptable, à surveiller |
| Repli `RemoveListItemsByItemIds` jamais vérifié en réel | moyenne | Couvert par tests avec relecture post-appel |
| Stratégies `PlaylistEntryReader` (sans utilisateur) non testées | faible | Garde-fou : relecture après retrait |
| `DeleteShare` non transactionnel | faible | Accepté : droit de gestion PROD reste intact |
| Comptage playlists PROD impactées par changements v1.2.0 | moyen | Avant v1.3.0 : mesurer adoption |
| Clients TV/mobile (issue #28) | faible | Non bloquant, backlog indéfini |

## Template Claude Code

- v3.7.3 (synchronisé 2026-09-29, `8c49482ee38f9172edbb99bbf7aa4bdc56e5c5bf`)
