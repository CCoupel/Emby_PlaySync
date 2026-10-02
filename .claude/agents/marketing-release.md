# Adaptation projet — marketing-release

Le site vit UNIQUEMENT sur la branche `gh-pages` (MARKETING/CADRAGE.md d'origine : URL cible = gh-pages).
Il n'y a PAS de dossier `MARKETING/` : ne jamais commiter le site (`index.html`, `assets/`, `locales/`,
`CADRAGE.md`) sur `main`, `milestone/*` ou hotfix.

Pour PREPARE/PUBLISH, travailler dans un worktree git de `gh-pages` situé HORS suivi, dans `_work/site`
(gitignoré) : `git worktree add _work/site gh-pages` (ou `git worktree prune` d'abord si besoin). Ce chemin
remplace `MARKETING/` dans toutes les consignes du template (détection du site, `CADRAGE.md`, aperçu).

- PUBLISH = commit + push depuis ce worktree sur `gh-pages`.
- Les release notes et posts (`docs/releases/vX.Y.Z/`) restent sur la branche de code.
- Si le remote est injoignable : `MARKETING BLOQUE`, jamais d'initialisation à tort.
