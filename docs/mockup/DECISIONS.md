# Contraintes de conception

> Contraintes durables issues des refus/corrections de l utilisateur, par composant.
> Le planner les respecte, QA les vérifie. Tenu par le CDP.

## plugin (user-page) — page PlaySync du menu utilisateur

- **Niveau Lecture/Écriture d'un membre** : toujours un **interrupteur** (`emby-toggle` atomique), sur chaque ligne de membre comme sur la ligne d'ajout — **jamais une liste déroulante**. La seule liste déroulante de la section Membres est le choix du compte à ajouter. (Retour utilisateur GATE 2 v1.2.0, 2026-09-29.)
- **Garder le visuel livré** : toute évolution de la page part du rendu réellement livré (v1.1.0 après GATE 4 : grille membres 50 % | 10 % | 40 % sur 80 % de largeur, propriétaire absent de la liste, cartes par playlist) — aucune régression visuelle. (Retour utilisateur GATE 2 v1.2.0, 2026-09-29.)
