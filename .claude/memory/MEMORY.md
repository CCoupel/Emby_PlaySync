# MEMORY.md — Emby_PlaySync

> Mis à jour le 2026-09-29 en fin de session (/end-session). Source de vérité pour
> `/start-session`. À tenir à jour à chaque session.

## Version courante

- **v1.1.0** — déployée en PROD (namespace `media`, `deployment/emby`), tag `v1.1.0`
- Branche courante : `main` (HEAD `1654ba6`), aucune branche `milestone/*` ouverte
- Site marketing publié : https://ccoupel.github.io/Emby_PlaySync/

## Travail en cours

- Aucun cycle FEATURE/BUGFIX/HOTFIX en cours — session close proprement, team fermée
- Milestone GitHub actif : **v1.2.0** (créé cette session, 3 issues non cadrées : #55, #56, #57)

## Décisions techniques de la session (2026-09-28/29)

- **Template Claude Code** synchronisé v3.6.0 → v3.7.0 (terminologie CDP→teamleader, jalons de
  progression `EN COURS`, questions utilisateur numérotées, tests organisés par lot §15)
- **Livraison v1.1.0 (#39, page utilisateur PlaySync)** : page dans le menu utilisateur Emby,
  gestion des membres/niveaux d'accès/options de partage sans éditer les étiquettes manuellement,
  5 nouveaux endpoints non-admin, spec amendée D19 (bascule atomique d'étiquettes par action
  utilisateur explicite) et D20. Audit sécurité complet (GO, 1 point moyen corrigé : atomicité de
  `DeleteShare` vis-à-vis de la ligne `ManageDelete` du propriétaire).
- **GATE 4 (validation manuelle QUALIF) exceptionnellement long** : 17 allers-retours de
  correction avant validation, presque tous liés au rendu des composants natifs Emby
  (`emby-toggle`/`emby-select`) dans le contexte "page menu utilisateur" (différent du contexte
  admin déjà maîtrisé). Cause racine principale : les modules JS/CSS Emby (`emby-toggle` etc.) ne
  sont chargés par le framework AMD du client QUE s'ils sont explicitement `require()`d — rien ne
  les charge automatiquement pour une page de plugin côté menu utilisateur (contrairement au
  contexte admin/dashboard qui les précharge). Un 2e bug distinct (widget toggle décomposé/réutilisé
  comme conteneur générique au lieu d'être traité comme un widget atomique) a causé un débordement
  de grille CSS très difficile à diagnostiquer sans inspection DevTools réelle.
- **Leçon retenue** : dev-plugin n'a aucun accès `dotnet`/navigateur dans son environnement —
  toute vérification de rendu visuel réel dépend entièrement de captures/inspections DevTools
  fournies par l'utilisateur. Les hypothèses de code pur (sans preuve DOM) ont échoué à plusieurs
  reprises ; l'inspection DevTools réelle (HTML rendu + computed styles) a résolu chaque blocage
  en un coup une fois obtenue.
- **Backlog créé pour v1.2.0** (non cadré) :
  - #55 — Créer une nouvelle playlist depuis PlaySync (hors périmètre initial de #39)
  - #56 — Séparer la synchronisation du flag lu de celle de l'avancement de lecture (2 toggles
    indépendants au lieu d'un seul `propager-lu`)
  - #57 — Bug : relire un média déjà marqué lu ne resynchronise plus l'état chez les autres
    membres (comportement du moteur existant depuis v0.3.0, pas introduit par #39)

## Règles critiques projet

- Ne jamais déployer sur `deployment/emby` (PROD) sauf via `/deploy prod` sur ordre explicite utilisateur
- Un seul milestone en développement à la fois, branche `milestone/vX.Y.Z`
- Spec de référence : `docs/chronogrammes.md` (règles R1–R9, §9 guide utilisateur PlaySync)
- Avant tout rendu de composant natif Emby (`emby-toggle`, `emby-select`, etc.) dans une page de
  plugin côté **menu utilisateur** (`EnableInUserMenu`) : toujours envelopper la création
  d'éléments dans un `require(['emby-toggle', 'emby-select', ...], function(){...})` — ne pas
  compter sur un chargement implicite (voir décisions de session ci-dessus)

## Template Claude Code

- Synchronisé en v3.7.0 (`792cbe8f`) le 2026-09-28 via `/init-project` option d
