# Cadrage du site marketing — PlaySync

> Validé par le CDP le 2026-09-28 (v1.0.0). Sert de référence pour les mises à jour suivantes —
> ne pas re-questionner ces points à chaque release, seulement les faire évoluer si le CDP le
> demande explicitement.

- **Nom affiché** : PlaySync (variante « PlaySync for Emby » / « PlaySync pour Emby » utilisée en
  sous-titre dans le header et l'eyebrow du hero quand le contexte doit être précisé). Tous les
  liens techniques (GitHub, Releases, Documentation) pointent vers le dépôt tel quel : `CCoupel/Emby_PlaySync`
  depuis son renommage le 2026-09-28 (ex-`CCoupel/Emby_shared_playlist`).
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
- **v1.1.0** (préparé le 2026-09-29, en attente de validation/publication) : ajout de la carte
  « Page utilisateur dédiée » (section Solutions) — nouvelle interface dans le menu Emby
  permettant au propriétaire de gérer membres et options sans éditer les étiquettes. Badge
  `Nouveau v1.1.0` posé (première apparition). Version affichée dans le header mise à jour
  (`v1.1.0`) ; correction en passant de la valeur par défaut affichée d'`AutoEnableSharing`
  (`true` → `false`, obsolète depuis la décision de sécurité v1.0.0, non liée à cette release).
- **v1.2.0** (préparé le 2026-09-29, en attente de publication) : version affichée `v1.2.0` ; carte
  Lu partagé recadrée (le flag seul) ; deux nouvelles cartes en section Solutions — « Propager
  l'avancement, à part » et « Créer une playlist depuis PlaySync » — avec badge `Nouveau v1.2.0`
  (première apparition). `current-major` reste `1`. Tableau de configuration : ajout de
  `propager-avancement`, précisions sur `remove-si-lu` (dépend de `propager-lu`) et `propager-lu`.
