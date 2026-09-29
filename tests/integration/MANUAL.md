# Procédure manuelle — v0.2.0 à v0.4.0 : étiquettes `remove-si-lu` / `propager-lu`, retrait, propagation du lu et de l'avancement, permission de partage automatique (QUALIF emby2 uniquement)

Prérequis : plugin déployé sur emby2 ; comptes `test_u1` (propriétaire), `test_u2` (Écriture), `test_u3` (Lecture) créés par `tests/integration/00-setup-users.sh` (mots de passe dans `private/test-users.env`). Ne jamais utiliser `user2` ni un compte réel. Noter pour chaque ligne le **client utilisé** (web, TV, mobile, version).

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

## 2bis. Propagation du lu (v0.3.0, `test_u1`, web)
Sur une NOUVELLE playlist (ou en retirant `remove-si-lu=OUI` d'abord, pour isoler l'effet) : « Modifier les métadonnées » > Mot-clé : **ajouter `propager-lu=OUI` ET retirer `propager-lu=NON` dans la même édition** (`remove-si-lu` laissé à NON).

| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` lit un film **jusqu'au bout** | `test_u1` et `test_u3` voient ce film marqué **lu** ; le film **reste** dans la playlist pour les 3 comptes | |
| `test_u3` (déjà lu) relit le même film jusqu'au bout | rien ne change chez lui (compteur/date intacts) | |
| Activer maintenant AUSSI `remove-si-lu=OUI` (les deux étiquettes actives) | `test_u2` finit un autre film : il est **retiré** de la playlist ET marqué **lu** chez `test_u1`/`test_u3` | |
| `test_u2` décoche « lu » sur un film déjà propagé | rien ne se propage (le retour à « non lu » ne se propage jamais) | |

## 3. Lecture en file (Q7)
Lancer la lecture en file de la playlist avec `test_u2`, finir le 1er film pendant que la file avance : noter si le film suivant se lance correctement et si l'affichage de la file se met à jour ou reste périmé.

## 4. Clients TV / mobile (U8) — VÉRIFICATION MANUELLE RESTANTE (#28), NON BLOQUANTE
Répéter §2 (film fini, relu, décoche/recoche) sur TV et mobile ; noter si `test_u1` peut éditer les étiquettes depuis ces clients (attendu : web seulement). **Ce point reste ouvert au-delà de v0.4.0** : il ne bloque la clôture d'aucun milestone (décision explicite du teamleader) — le signaler comme point restant dans le message de fin de milestone plutôt que comme un défaut.

## 5. Page de configuration du plugin
Tableau de bord > Plugins > « Emby Shared Playlist » : la page s'ouvre, les options `EnableDiagnostics`, `GracePasses`, `LogToConsole`, `LogLevel`, `AutoEnableSharing` (v0.4.0, #26) sont lisibles et enregistrables sans erreur. L'encart d'aide (#25) est présent, lisible, cohérent avec le comportement réel (ne décrit pas la permission de partage comme une étape manuelle nécessaire).

## 5bis. Message d'aide (#51)
Sur une playlist dont la description est encore le texte v0.2.0 (mentionnant « fonction à venir » pour `propager-lu`), attendre une passe de réconciliation (5 min, ou la lancer depuis Tableau de bord > Tâches planifiées) : le texte est remplacé par une version qui décrit la propagation comme active. Une description modifiée entre-temps par le propriétaire n'est jamais touchée.

## 5ter. Avancement de lecture (v0.3.1, `propager-lu=OUI` seul, sans `remove-si-lu`)
Sur une playlist avec `propager-lu=OUI` (retirer `propager-lu=NON` dans la même édition), média non lu par personne.

| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` lit un film, l'**arrête** vers le milieu (pas jusqu'au bout) | `test_u1` et `test_u3` : « Reprendre » propose la position d'arrêt de `test_u2` (pas de repli au début) | |
| `test_u2` reprend ce film **exactement à la même position**, l'arrête à nouveau sans avancer | rien ne change chez les autres (pas de nouvelle écriture visible) | |
| `test_u2` reprend et avance jusqu'à une position **plus tardive**, s'arrête | `test_u1`/`test_u3` reprennent maintenant à cette position plus tardive (dernier arrêt gagne) | |
| `test_u1` reprend le film et **finit sa lecture** (jusqu'au bout) | le film est marqué lu chez tous les membres concernés (comme en v0.3.0) ; aucune position n'est proposée sur ce film une fois lu | |
| **Pause** (sans arrêter la lecture) : `test_u1` **met en pause** vers le milieu, sans jamais arrêter | `test_u2`/`test_u3`, en ouvrant le film, voient la position se rapprocher de celle de la pause de `test_u1` | **CONFIRMÉ** (vérification manuelle utilisateur, 2026-09-27) : pause ET arrêt se propagent bien en conditions réelles — I34 (`22-avancement.sh`) reste en SKIP côté script (la simulation REST `Sessions/Playing/Progress` avec `IsPaused` ne recrée pas de façon fiable le même événement qu'un vrai client), ce n'est PAS un défaut du plugin |
| Arrêt très bref (quelques secondes après le début) | aucune position n'est proposée aux autres (en dessous du seuil de 30 s) | |
| Arrêt à un pourcentage élevé (~95-99%) d'un film **non lu** | la position est proposée normalement aux autres (garde D-c : pas de marge de ratio ; automatisé en I37, `tests/integration/22-avancement.sh`) — si ce n'est pas le cas, noter si Emby a lui-même marqué le film lu à ce stade (config serveur, hors du plugin) | |

## 5quater. Permission de partage automatique (v0.4.0, #26)
Couvert automatiquement par `tests/integration/23-permission.sh` (I36-I41) : démarrage/passe, `UserCreated` immédiat, compte déjà actif, `AutoEnableSharing=false`, décochage manuel réactivé, interrupteur qui ne révoque rien. Vérification manuelle complémentaire (non bloquante) :

| Étape | Attendu | OK ? |
|---|---|---|
| Créer un nouveau compte dans le tableau de bord Emby (pas via un script) | la case « Autoriser le partage des éléments personnels » (`AllowSharingPersonalItems`) apparaît cochée quasiment immédiatement, sans attendre 5 min | |
| Décocher manuellement cette case pour `test_u2` (Tableau de bord > Utilisateurs) | à la passe suivante (au plus 5 min, ou déclenchée à la main), la case est **recochée automatiquement** | **Comportement VOULU (D-e)**, pas un défaut : le plugin ne mémorise aucun décochage individuel. Seul l'interrupteur global `AutoEnableSharing` (page de config, §5) empêche cela, pour **tous** les comptes à la fois |
| Décocher `AutoEnableSharing` dans la page de config | aucun compte n'est plus retouché à la passe suivante ; un compte déjà coché **le reste** (aucune révocation) | |

## 5quinquies. Page utilisateur « PlaySync » (v1.1.0, #39, D19/D20/S10)

**Status QUALIF** : VALIDATED WITH RESERVATIONS (qa-20260928-162052.md) — tous les tests automatisés passent (62/62 P1-P23 OK, P-busy 1 SKIP attendu), mais contrôle visuel n'a pas pu être exécuté en raison d'une session navigateur persistante sur le profil Chrome partagé. Fortement corroboré par vérification API brute et revue du code ; comportement serveur confirmé sans ambiguïté. À couvrir visuellement avant GATE PROD.

Couvert automatiquement par `tests/integration/25-page-utilisateur.sh` (P1-P23 : autorisation, IDOR/404, niveaux
interdits, premier partage, bascule d'option D19, retrait du dernier membre, chaîne moteur complète). Vérification
manuelle complémentaire (client web ; TV/mobile notés non bloquants comme U8) — noter le **client utilisé** (web,
TV, mobile, version) sur chaque ligne.

**RÉSERVE — À vérifier avant la transition en production** :
- Entrée de menu PlaySync visible pour un compte avec permission, absente ou en 403 pour un compte sans.
- Rendu effectif de la page (formulaires, interrupteurs, dialogue de confirmation natif).
- Dialogue de confirmation natif Emby (jamais `window.confirm()` du navigateur) au retrait d'un membre.
- Toasts d'erreur natifs traduits (jamais de code brut d'erreur, jamais d'`alert()`).
- Rendu FR/EN complet (libellés et messages d'erreur).
- TV/mobile (non bloquant, comme U8, à noter si accessible).

### Entrée de menu (visible/masquée selon la permission)

| Étape | Attendu | OK ? |
|---|---|---|
| Se connecter avec `test_u1` (permission de partage accordée, §5quater) puis ouvrir le menu utilisateur (avatar, en haut à droite) | une entrée « PlaySync » est visible | |
| Ouvrir « PlaySync » | la page s'ouvre (pas de 404, pas d'écran blanc) ; playlists dont `test_u1` est propriétaire (partagées ou non) | |
| Se connecter avec un compte SANS la permission (`Policy.AllowSharingPersonalItems=false` — Tableau de bord > Utilisateurs > Profil, décocher) puis ouvrir le menu utilisateur | selon la décision GATE U13-b (repli éventuel, voir Notes du plan) : soit l'entrée est absente, soit elle est visible mais la page affiche un état « sans permission » clair (jamais une erreur brute/écran blanc) | |
| Tenter d'appeler un endpoint `User/*` directement (ex. `<EMBY_URL>/emby/SharedPlaylist/User/Playlists` avec le token de ce compte) | 403, jamais de fuite de données | |

### Gestion d'une playlist (client web, `test_u1`)

| Étape | Attendu | OK ? |
|---|---|---|
| Créer une nouvelle playlist « À voir » (hors PlaySync, comme d'habitude), NE PAS la partager, puis ouvrir PlaySync | la playlist apparaît, marquée non partagée, options grisées/désactivées, bouton pour ajouter un premier membre | |
| Ajouter `test_u2` en Écriture depuis PlaySync | la playlist devient « partagée » IMMÉDIATEMENT (pas d'attente de 5 min) ; retrouvée dans le menu natif « … » > « Gérer la collaboration » avec `test_u2` en Écriture | |
| Ouvrir « Modifier les métadonnées » (natif) de cette playlist | `remove-si-lu=NON` et `propager-lu=NON` déjà posées, message d'aide déjà écrit (première détection immédiate, comme §1) | |
| Changer le niveau de `test_u2` en Lecture depuis PlaySync, puis retour Écriture | le changement se reflète dans « Gérer la collaboration » natif dans les deux sens | |
| Ajouter `test_u3` en Lecture, puis (avec le compte `test_u3`) tenter d'ajouter un média à la playlist | refusé (403, comportement natif Emby, pas un message du plugin) | |
| Depuis PlaySync, activer l'option « retirer si lu » (`remove-si-lu`) | bascule immédiate (toggle/switch **natif Emby**, jamais une case à cocher HTML brute) ; `test_u2` finit un film de la playlist : il est retiré pour tous | |
| Retirer un membre (`test_u3`) depuis PlaySync | une **boîte de dialogue de confirmation Emby native** apparaît avant le retrait effectif (jamais un `window.confirm()` du navigateur — reconnaissable : bouton natif Emby, pas le style du navigateur) ; après confirmation, `test_u3` n'apparaît plus, ni côté PlaySync ni côté « Gérer la collaboration » natif | |
| Retirer le dernier membre restant (`test_u2`) | confirmation demandée de la même façon ; la playlist redevient « non partagée » dans PlaySync ; les étiquettes restent en place mais inertes (vérifiable via « Modifier les métadonnées ») | |

### Erreurs et toasts

| Étape | Attendu | OK ? |
|---|---|---|
| Provoquer une erreur (ex. rouvrir un onglet PlaySync périmé pointant vers une playlist supprimée entre-temps, puis tenter une action) | un **toast/notification Emby natif** apparaît avec un message **traduit** et compréhensible (jamais un code brut du type `not-found`, jamais une `alert()` du navigateur) | |
| Reproduire dans le navigateur en anglais (langue du compte/`navigator.language` réglée hors `fr*`) | tous les libellés de la page ET le message d'erreur repassent en anglais | |

### Rendu FR/EN

| Étape | Attendu | OK ? |
|---|---|---|
| Ouvrir PlaySync avec un client/navigateur en français | libellés, boutons, dialogue de confirmation en français | |
| Changer la langue du client Emby (ou `navigator.language`) vers une langue hors français (ex. anglais, espagnol) | repli sur l'anglais (jamais de clé de traduction brute affichée, ex. `addMember`) | |

### Clients TV / mobile (comme U8, #28) — NON BLOQUANT

Ouvrir PlaySync (ou noter si l'entrée de menu est absente) sur TV et mobile ; noter le rendu et si les actions (ajout/retrait/bascule) sont utilisables. Comme pour U8 (§4), ce point ne bloque aucun milestone.

## 6. Nettoyage
`tests/integration/90-cleanup.sh` (option `--delete-users` pour supprimer aussi les comptes `test_*`). Consigner les résultats dans le rapport de recette (U8, Q7).
