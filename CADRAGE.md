# Cadrage du site marketing — PlaySync

> Validé par le CDP le 2026-09-28 (v1.0.0). Sert de référence pour les mises à jour suivantes —
> ne pas re-questionner ces points à chaque release, seulement les faire évoluer si le CDP le
> demande explicitement.

- **Nom affiché** : PlaySync (variante « PlaySync for Emby » / « PlaySync pour Emby » utilisée en
  sous-titre dans le header et l'eyebrow du hero quand le contexte doit être précisé). Le nom du
  dépôt GitHub (`CCoupel/Emby_shared_playlist`) n'est jamais renommé : tous les liens techniques
  (GitHub, Releases, Documentation) pointent vers le dépôt tel quel.
- **Public cible** : administrateurs de serveurs Emby personnels ou familiaux gérant plusieurs
  comptes utilisateurs.
- **Proposition de valeur** : « Partagez vos listes « À voir » entre comptes Emby, sans jamais
  perdre le fil de qui a vu quoi. » — 3 bénéfices clés : retrait automatique des médias vus,
  progression de lecture synchronisée, partage natif sans friction.
- **Ton** : vouvoiement neutre, accessible mais technique (public averti, habitué à
  l'auto-hébergement).
- **Identité visuelle** :
  - Typographies : Fraunces (titres), Work Sans (corps), IBM Plex Mono (code/utilitaire).
  - Couleurs : vert vif et chaleureux — voir tokens dans `assets/style.css` (`--accent`
    `#278239` clair / `#4CC26B` sombre) associé à un accent chaud ambre (`--accent-warm`
    `#F5A623` clair / `#FFC24D` sombre) sur fond crème `#FBF3E3` (clair) ou vert très sombre
    `#12190F` (sombre). Remplace une première proposition en vert sauge/crème jugée trop terne.
- **Sections** : Problématiques, Solutions, Architecture, Déploiement uniquement — pas de FAQ ni
  de Roadmap (choix explicite, à ne pas réintroduire sans nouvelle demande).
- **Visuels** : aucune capture fournie à ce jour → le diagramme d'architecture est dessiné en SVG
  inline (composants réels du plugin, pas un placeholder générique) ; pas d'autre visuel dans
  cette version minimale.
- **Appel à l'action principal** : « Voir sur GitHub » + « Téléchargements » (page Releases) ;
  lien Documentation vers `docs/chronogrammes.md`.
- **Langues** : FR/EN, commutateur dans le header (persistant par navigateur via
  `localStorage`, non partagé entre visiteurs).
- **URL cible** : branche `gh-pages` du dépôt, pas de domaine personnalisé.

## Historique des releases

- **v1.0.0** (préparé le 2026-09-28, publié le 2026-09-28 sur signal du CDP — mise en production
  réelle actée : merge main, tag `v1.0.0`, déploiement confirmé) : lancement initial du site.
  Badges `Nouveau v1.0.0` posés sur les 4 fonctionnalités phares de la section Solutions (partage
  natif, retrait automatique, propagation, diagnostics) — première apparition de chacune.
