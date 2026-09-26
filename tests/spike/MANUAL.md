# Procédure manuelle — spike partage natif (issue #1, QUALIF emby2 uniquement)

Prérequis : `00-setup-users.sh` et `10-run-spike.sh` exécutés (comptes `test_u1/u2/u3`, mots de passe dans `private/spike-users.env`, jamais à recopier ailleurs). Ne jamais utiliser `user2` ni un compte réel.

## 1. Activer le partage pour le propriétaire (test_u1 seulement)
Tableau de bord > Utilisateurs > `test_u1` > profil : cocher **« Permettre le partage de contenus personnels tels que des listes de lecture avec d'autres utilisateurs sur ce serveur »** (déjà posé par le script 00 ; à vérifier).

## 2. Partager (client web, connecté en `test_u1`)
Créer une playlist « SPIKE-manuel » avec ≥ 2 médias > menu « … » > **« Gérer la collaboration »** > `test_u2` = Écriture, `test_u3` = Lecture.

## 3. Étiquette de propagation (client web, `test_u1`)
Menu « … » > **« Modifier les métadonnées »** > section **Mot-clé** (champ « Étiquette ») :
1. ajouter `propager-lu=OUI` **et** retirer `propager-lu=NON` dans la **même** édition, puis Enregistrer ;
2. variante (mesure seulement, U3) : ajouter OUI, enregistrer, puis rouvrir et retirer NON, enregistrer.
Règle attendue : sans OUI le plugin ne propage rien ; NON l'emporte si les deux sont présentes ; le plugin ne supprime jamais d'étiquette ; s'il n'y a aucune étiquette `propager-lu*`, il pose NON (jamais en réaction à une édition en cours). Le cas normal est donc l'édition unique (1).
Noter si l'éditeur accepte le caractère `=` et la casse (U3, H4).

## 4. Vérification TV / mobile (`test_u2`, puis `test_u3`)
| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` voit « SPIKE-manuel » (web, TV, mobile) | playlist visible, même contenu | |
| `test_u2` ajoute puis retire un média | accepté (Écriture) ; `test_u1` voit les changements | |
| `test_u3` tente d'ajouter / retirer | refusé (Lecture) | |
| `test_u2` lit un média **jusqu'au bout** (lecture réelle, générique compris) | marqué lu pour `test_u2` seulement | |
| Éditer les étiquettes depuis TV / mobile (`test_u1`) | noter possible / impossible (H4) | |
| **Avancement (#44)** : `test_u1` lance un média commun de la playlist (web, puis TV/mobile), l'arrête à mi-parcours ; puis `test_u2` ouvre la playlist | « Reprendre » propose la position de `test_u1` (constat de départ : sans propagation active, u2 repart du début) ; noter le client utilisé | |

Après la lecture : relever les événements avec `GET /emby/SharedPlaylist/Spike/Events` (clé admin) et vérifier une entrée `UserDataSaved` pour `test_u2` (SaveReason, `played=true`, `pluginWrite=false`) — c'est la preuve U1 en conditions réelles.

## 5. Nettoyage
`tests/spike/90-cleanup.sh` (ajouter `--delete-users` pour supprimer aussi les comptes `test_*`). Consigner les résultats des étapes 3 et 4 dans le rapport de spike (U1, U3, U8).
