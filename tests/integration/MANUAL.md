# Procédure manuelle — v0.2.0 : étiquettes `remove-si-lu` / `propager-lu` et retrait du média lu (QUALIF emby2 uniquement)

Prérequis : plugin v0.2.0 déployé sur emby2 ; comptes `test_u1` (propriétaire), `test_u2` (Écriture), `test_u3` (Lecture) créés par `tests/spike/00-setup-users.sh` (mots de passe dans `private/spike-users.env`). Ne jamais utiliser `user2` ni un compte réel. Noter pour chaque ligne le **client utilisé** (web, TV, mobile, version).

## 1. Préparer (client web, `test_u1`)
Créer « SPIKE-manuel-v02 » avec ≥ 3 films, la partager (menu « … » > « Gérer la collaboration » : `test_u2` Écriture, `test_u3` Lecture). Attendre au plus 5 min (ou lancer la tâche « Emby Shared Playlist — réconciliation » : Tableau de bord > Tâches planifiées).

| Étape | Attendu | OK ? |
|---|---|---|
| Ouvrir « Modifier les métadonnées » de la playlist | étiquettes `remove-si-lu=NON` et `propager-lu=NON` présentes | |
| Lire la description | message d'aide en français, lisible, cite `remove-si-lu=OUI` et `propager-lu=OUI` | |
| Rouvrir plus tard | le message n'est pas réécrit par-dessus un texte que vous avez saisi | |

## 2. Activer le retrait (`test_u1`, web)
« Modifier les métadonnées » > Mot-clé : **ajouter `remove-si-lu=OUI` ET retirer `remove-si-lu=NON` dans la même édition**, Enregistrer. (Si les deux étiquettes restent, NON l'emporte : rien ne se passe.)

| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` lit un film **jusqu'au bout** (générique compris) | le film disparaît de la playlist pour les 3 comptes | |
| `test_u2` relit un film **déjà lu** jusqu'au bout | il reste dans la playlist (aucune transition) | |
| `test_u2` décoche puis recoche « lu » sur un film de la playlist | il est retiré | |
| `test_u3` (Lecture) finit un film | il est retiré ; le « lu » de `test_u1`/`test_u2` n'est pas modifié | |
| Arrêt à mi-film | rien ne change | |
| `propager-lu=OUI` seul (sans `remove-si-lu=OUI`) | aucun effet en v0.2.0 | |

## 3. Lecture en file (Q7)
Lancer la lecture en file de la playlist avec `test_u2`, finir le 1er film pendant que la file avance : noter si le film suivant se lance correctement et si l'affichage de la file se met à jour ou reste périmé.

## 4. Clients TV / mobile (U8)
Répéter §2 (film fini, relu, décoche/recoche) sur TV et mobile ; noter si `test_u1` peut éditer les étiquettes depuis ces clients (attendu : web seulement).

## 5. Page de configuration du plugin
Tableau de bord > Plugins > « Emby Shared Playlist » : la page s'ouvre, les options `EnableDiagnostics`, `GracePasses`, `LogToConsole`, `LogLevel` sont lisibles et enregistrables sans erreur.

## 6. Nettoyage
`tests/spike/90-cleanup.sh` (option `--delete-users` pour supprimer aussi les comptes `test_*`). Consigner les résultats dans le rapport de recette (U8, Q7).
