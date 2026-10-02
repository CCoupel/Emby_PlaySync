# Procédure manuelle — v0.2.0 à v1.2.0 : étiquettes `remove-si-lu` / `propager-lu` / `propager-avancement` (trois familles depuis v1.2.0), retrait, propagation du lu et de l'avancement, permission de partage automatique, page PlaySync (QUALIF emby2 uniquement)

> **v1.2.0 (#56, #57, D21) — deux changements de comportement à connaître avant de dérouler ce document** : (1) `remove-si-lu=OUI` n'a d'effet que si `propager-lu=OUI` est **aussi** actif (§2, §2bis) ; (2) l'avancement de lecture relève de la famille **`propager-avancement`** (§5ter) : `propager-lu` ne propage plus que le flag lu. Ces deux points sont automatisés (I2, I18, I29, I32, I38-I40) ; les lignes ci-dessous sont la vérification humaine.

Prérequis : plugin déployé sur emby2 ; comptes `test_u1` (propriétaire), `test_u2` (Écriture), `test_u3` (Lecture) créés par `tests/integration/00-setup-users.sh` (mots de passe dans `private/test-users.env`). Ne jamais utiliser `user2` ni un compte réel. Noter pour chaque ligne le **client utilisé** (web, TV, mobile, version).

## 0. Règles d'environnement QUALIF (#61)
- **Aucun média virtuel.** La bibliothèque de QUALIF est VirtualLib (`/config/virtual/*.strm`) : chaque événement de session d'un tel média est relayé vers PROD (sessions fantômes `test_u*@spike`). Les scripts d'intégration n'utilisent **que** la bibliothèque locale `PlaySync-Tests` (`/config/test-media/`, 20 vidéos synthétiques de 11 min) et **refusent** (`die`, avant toute écriture) tout média sous `/config/virtual/`, en `.strm` ou hors de `TEST_MEDIA_ROOT`.
- **Provisionner d'abord** : `tests/integration/01-setup-media.sh` (idempotent ; `--check` / `--dry-run` n'écrit rien et sort 1 si incomplet). Prérequis : `private/qualif.env`, `private/kubeconfig.yml`, `kubectl`, `ffmpeg` (seulement si des vidéos manquent). Cible : `deployment/emby2`, namespace `media`, jamais `emby`. Ordre d'un run complet : `01` → `00` → `20` → `21` → `22` → `23` → `24` → `25` → `90`.
- **Un seul run QUALIF à la fois.** Les scripts purgent au démarrage les playlists `SPIKE*` de `test_u1` (#60) et ferment/déconnectent les sessions des comptes `test_*` en sortie : deux runs simultanés se supprimeraient mutuellement leurs playlists. Exception : un script imbriqué (I26 de 21 relance 20 avec `INT_NESTED=1`) ne purge ni ne déconnecte.
- **Sessions** : chaque script ferme (`Stopped`) ses sessions de lecture ouvertes puis déconnecte les jetons `test_u*` ; le résultat `SESSIONS.clean` vérifie qu'aucune session `test_u*` n'est restée en lecture. Les sessions fantômes déjà présentes sur PROD expirent d'elles-mêmes (aucune action PROD).
- Variables : `TEST_MEDIA_LIBRARY` (défaut `PlaySync-Tests`), `TEST_MEDIA_ROOT` (défaut `/config/test-media/`).

## 1. Préparer (client web, `test_u1`)
Créer « SPIKE-manuel-v02 » avec ≥ 3 films, la partager (menu « … » > « Gérer la collaboration » : `test_u2` Écriture, `test_u3` Lecture). Attendre au plus 5 min (ou lancer la tâche « Emby Shared Playlist — réconciliation » : Tableau de bord > Tâches planifiées).

| Étape | Attendu | OK ? |
|---|---|---|
| Ouvrir « Modifier les métadonnées » de la playlist | étiquettes `remove-si-lu=NON`, `propager-lu=NON` **et `propager-avancement=NON`** présentes (trois familles depuis v1.2.0 ; aucune n'est héritée d'une version précédente) | |
| Lire la description | message d'aide en français, lisible, cite `propager-lu=OUI`, `remove-si-lu=OUI` (« seulement si propager-lu=OUI ») et `propager-avancement=OUI` (texte `HelpText.V3`) | |
| Rouvrir plus tard | le message n'est pas réécrit par-dessus un texte que vous avez saisi | |

## 2. Activer le retrait (`test_u1`, web) — v1.2.0 : exige `propager-lu=OUI` EN PLUS
« Modifier les métadonnées » > Mot-clé : **ajouter `remove-si-lu=OUI` ET `propager-lu=OUI`, retirer `remove-si-lu=NON` ET `propager-lu=NON` dans la même édition**, Enregistrer. (Si `OUI` et `NON` d'une même famille restent ensemble, NON l'emporte : rien ne se passe. **Depuis v1.2.0, `remove-si-lu=OUI` SEUL ne retire plus rien.**)

| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` lit un film **jusqu'au bout** (générique compris) | le film disparaît de la playlist pour les 3 comptes | |
| `test_u2` relit un film **déjà lu** jusqu'au bout | il reste dans la playlist (aucune transition) | |
| `test_u2` décoche puis recoche « lu » sur un film de la playlist | il est retiré | |
| `test_u3` (Lecture) finit un film | il est retiré ; le « lu » est **posé** chez `test_u1`/`test_u2` (propager-lu actif, R4b) | |
| Arrêt à mi-film | rien ne change | |
| `propager-lu=OUI` seul (sans `remove-si-lu=OUI`) | le lu est propagé, le film **reste** dans la playlist (§2bis) | |
| **v1.2.0 (S3b)** `remove-si-lu=OUI` seul, `propager-lu=NON` : `test_u2` finit un film | **rien** : le film reste, aucun « lu » posé chez les autres, aucune étiquette modifiée (avant v1.2.0 il était retiré) | |

## 2bis. Propagation du lu (v0.3.0, `test_u1`, web ; v1.2.0 : ne propage plus la position)
Sur une NOUVELLE playlist (ou en retirant `remove-si-lu=OUI` d'abord, pour isoler l'effet) : « Modifier les métadonnées » > Mot-clé : **ajouter `propager-lu=OUI` ET retirer `propager-lu=NON` dans la même édition** (`remove-si-lu` laissé à NON).

| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` lit un film **jusqu'au bout** | `test_u1` et `test_u3` voient ce film marqué **lu** ; le film **reste** dans la playlist pour les 3 comptes | |
| `test_u3` (déjà lu) relit le même film jusqu'au bout | rien ne change chez lui (compteur/date intacts) | |
| Activer maintenant AUSSI `remove-si-lu=OUI` (les deux étiquettes actives ; `propager-lu=OUI` est déjà en place) | `test_u2` finit un autre film : il est **retiré** de la playlist ET marqué **lu** chez `test_u1`/`test_u3` | |
| `test_u2` décoche « lu » sur un film déjà propagé | rien ne se propage (le retour à « non lu » ne se propage jamais) | |
| `test_u2` arrête un film à mi-parcours (`propager-avancement=NON`) | **aucune position** proposée à `test_u1`/`test_u3` (v1.2.0 : `propager-lu` ne couvre plus l'avancement) | |

## 3. Lecture en file (Q7)
Lancer la lecture en file de la playlist avec `test_u2`, finir le 1er film pendant que la file avance : noter si le film suivant se lance correctement et si l'affichage de la file se met à jour ou reste périmé.

## 4. Clients TV / mobile (U8) — VÉRIFICATION MANUELLE RESTANTE (#28), NON BLOQUANTE
Répéter §2 (film fini, relu, décoche/recoche) sur TV et mobile ; noter si `test_u1` peut éditer les étiquettes depuis ces clients (attendu : web seulement). **Ce point reste ouvert au-delà de v0.4.0** : il ne bloque la clôture d'aucun milestone (décision explicite du teamleader) — le signaler comme point restant dans le message de fin de milestone plutôt que comme un défaut.

## 5. Page de configuration du plugin
Tableau de bord > Plugins > « Emby Shared Playlist » : la page s'ouvre, les options `EnableDiagnostics`, `GracePasses`, `LogToConsole`, `LogLevel`, `AutoEnableSharing` (v0.4.0, #26) sont lisibles et enregistrables sans erreur. L'encart d'aide (#25) est présent, lisible, cohérent avec le comportement réel (ne décrit pas la permission de partage comme une étape manuelle nécessaire).

## 5bis. Message d'aide (#51 ; v1.2.0 : V1/V2 -> V3)
Sur une playlist dont la description est encore le texte v0.2.0 (mentionnant « fonction à venir » pour `propager-lu`) **ou** le texte v0.3.0 (« l'avancement de lecture (position, pause) est aussi propagé »), attendre une passe de réconciliation (5 min, ou la lancer depuis Tableau de bord > Tâches planifiées) : le texte est remplacé par la version **V3** (trois options, `propager-lu` en premier, « (seulement si propager-lu=OUI) » sur `remove-si-lu`). Une description modifiée entre-temps par le propriétaire n'est jamais touchée.

## 5ter. Avancement de lecture (v0.3.1 ; v1.2.0 : famille `propager-avancement`, #56/#57)
Sur une playlist avec `propager-avancement=OUI` (ajouter `propager-avancement=OUI` ET retirer `propager-avancement=NON` dans la même édition ; `propager-lu` et `remove-si-lu` peuvent rester à NON), média non lu par personne. **Seuil de 30 s = position absolue dans le média** (reprendre à 40 min et arrêter 5 s plus tard, à 40 min 05 s, propage).

| Étape | Attendu | OK ? |
|---|---|---|
| `test_u2` lit un film, l'**arrête** vers le milieu (pas jusqu'au bout) | `test_u1` et `test_u3` : « Reprendre » propose la position d'arrêt de `test_u2` (pas de repli au début) | |
| `test_u2` reprend ce film **exactement à la même position**, l'arrête à nouveau sans avancer | rien ne change chez les autres (pas de nouvelle écriture visible) | |
| `test_u2` reprend et avance jusqu'à une position **plus tardive**, s'arrête | `test_u1`/`test_u3` reprennent maintenant à cette position plus tardive (dernier arrêt gagne) | |
| `test_u1` reprend le film et **finit sa lecture** (jusqu'au bout) | le film est marqué lu **pour `test_u1` par Emby** ; il n'est **pas** marqué lu chez les autres (`propager-lu=NON`) — activer `propager-lu=OUI` pour partager le « lu » ; la position d'arrêt finale de `test_u1` est propagée aux autres (S9f) | |
| **v1.2.0 (#57, S9d)** `test_u2` **relit** un film déjà marqué lu (par lui et par `test_u1`), le met en pause puis l'arrête vers le milieu | la position est propagée à `test_u1` **malgré l'état lu** (avant v1.2.0 : rien, `Skipped trigger-already-played`) ; le « lu » de chacun reste vrai ; le film n'est pas retiré (relire un média lu n'est pas une transition, R4c) | |
| **v1.2.0 (S9f / U14b)** `test_u2` **termine** un film (~99,5 %) avec `propager-avancement=OUI`, `propager-lu=NON` | `test_u1` se voit proposer « Reprendre » près de la fin ; **noter si Emby a posé le « lu » chez `test_u1` de lui-même** (hypothèse du spike U14b : non, `SaveUserData` n'applique pas la règle de fin). Si oui : vérifier qu'aucun retrait n'a lieu dans une AUTRE playlist de `test_u1` (S6) | |
| **Pause** (sans arrêter la lecture) : `test_u1` **met en pause** vers le milieu, sans jamais arrêter | `test_u2`/`test_u3`, en ouvrant le film, voient la position se rapprocher de celle de la pause de `test_u1` | **CONFIRMÉ** (vérification manuelle utilisateur, 2026-09-27) : pause ET arrêt se propagent bien en conditions réelles — I34 (`22-avancement.sh`) reste en SKIP côté script (la simulation REST `Sessions/Playing/Progress` avec `IsPaused` ne recrée pas de façon fiable le même événement qu'un vrai client), ce n'est PAS un défaut du plugin |
| Arrêt très bref (quelques secondes après le début) | aucune position n'est proposée aux autres (en dessous du seuil de 30 s) | |
| Arrêt à un pourcentage élevé (~95-99%) d'un film **non lu** | la position est proposée normalement aux autres (aucune garde ni marge de ratio ; la garde D-c a été **supprimée en v1.2.0**, #57 ; automatisé en I37/I39, `tests/integration/22-avancement.sh`) | |

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
| Ouvrir « Modifier les métadonnées » (natif) de cette playlist | `remove-si-lu=NON`, `propager-lu=NON` et `propager-avancement=NON` (v1.2.0) déjà posées, message d'aide déjà écrit (première détection immédiate, comme §1) | |
| Changer le niveau de `test_u2` en Lecture depuis PlaySync, puis retour Écriture | le changement se reflète dans « Gérer la collaboration » natif dans les deux sens | |
| Ajouter `test_u3` en Lecture, puis (avec le compte `test_u3`) tenter d'ajouter un média à la playlist | refusé (403, comportement natif Emby, pas un message du plugin) | |
| Depuis PlaySync, activer l'option « retirer si lu » (`remove-si-lu`) **et « propager le lu »** (v1.2.0 : le retrait exige les deux) | bascule immédiate (toggle/switch **natif Emby**, jamais une case à cocher HTML brute) ; `test_u2` finit un film de la playlist : il est retiré pour tous (voir §5sexies pour la dépendance) | |
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

## 5sexies. Page PlaySync — trois interrupteurs et dépendance « Retirer si lu » → « Propager le lu » (v1.2.0, #56, S10b, D21)

Prérequis : playlist partagée (premier partage fait depuis PlaySync, comme §5quinquies), trois étiquettes à NON. **Inspection DevTools du premier build QUALIF obligatoire** (leçon du GATE 4 de v1.1.0 : rendu des composants natifs `emby-toggle`, `require` explicite ; toggle atomique).

| Étape | Attendu | OK ? |
|---|---|---|
| Ouvrir PlaySync sur la playlist partagée (trois NON) | **trois** interrupteurs, dans l'ordre « Retirer si lu », « Propager le lu », « Propager l'avancement » (natifs Emby, jamais des cases HTML) ; « Retirer si lu » est **grisé** avec la mention « Nécessite « Propager le lu » » | |
| Activer « Propager le lu » | l'interrupteur bascule immédiatement ; « Retirer si lu » redevient **cochable** (mention disparue) ; étiquette `propager-lu=OUI` (NON retirée) dans « Modifier les métadonnées » | |
| Activer « Retirer si lu » | coché ; `test_u2` finit un film : retiré ET « lu » posé chez les autres (R4a + R4b) | |
| Désactiver « Propager le lu » | `propager-lu=NON` ; **`remove-si-lu=OUI` conservé** (D19) : l'interrupteur « Retirer si lu » reste **coché mais grisé**, mention « Inactif tant que « Propager le lu » est désactivé » ; `test_u2` finit un film : **aucun retrait**, aucun « lu » posé | |
| Réactiver « Propager le lu » | « Retirer si lu » coché et actif ; le retrait fonctionne de nouveau | |
| Activer « Propager l'avancement » | `propager-avancement=OUI` ; ni « Retirer si lu » ni « Propager le lu » ne changent ; arrêt de `test_u2` à mi-film : position proposée à `test_u1` (tableau B, indépendant) | |
| Rendu FR/EN | libellés FR « Retirer si lu », « Propager le lu », « Propager l'avancement » et mentions de dépendance ; repli EN hors `fr*`, jamais de clé brute | |
| Clients TV / mobile | rendu des trois interrupteurs noté (non bloquant, comme U8/#28) | |

## 5septies. Page PlaySync — créer une playlist (v1.2.0, #55, D22, S11)

Prérequis : `test_u1` avec la permission de partage. **Inspection DevTools obligatoire** au premier build QUALIF (composant natif `emby-input` chargé par le `require`, pas un `<input>` brut).
| **P34 (QUALIF, quota)** Un compte possède 10 playlists (dont des natives) : « Nouvelle playlist » puis valider | message sous le champ « limite de playlists atteinte » traduit FR/EN, **sans le nombre** ; rien créé ni supprimé ; même message si le nom saisi existe déjà | |
| Le même compte supprime une playlist (natif) puis recrée | accepté (9 possédées) | |
| Étape | Attendu | OK ? |
|---|---|---|
| Ouvrir PlaySync : bouton « Nouvelle playlist » en tête de page | bouton natif Emby visible ; le champ nom (natif) apparaît au clic, limité à 100 caractères | |
| Saisir « Films du dimanche », valider | une carte « non partagée », sans membre ni médias, s'insère à sa place dans le tri ; dans Emby natif la playlist existe, **vide**, de type vidéo, propriétaire `test_u1` ; « Modifier les métadonnées » : **aucune étiquette, aucun message d'aide** | |
| Recréer «  films du DIMANCHE  » (espaces et casse différents) | message sous le champ « Vous avez déjà une playlist portant ce nom » (FR) / équivalent EN ; aucune playlist créée | |
| Valider un nom vide, ou de plus de 100 caractères (coller un long texte) | message d'erreur traduit sous le champ (jamais le code brut `invalid-name`) | |
| Créer « Été » puis « Ete » ; « À  voir » (2 espaces) puis « À voir » | tous acceptés (accents et espaces internes significatifs) | |
| Avec un autre compte qui a la permission, créer « Films du dimanche » | accepté (unicité par propriétaire) | |
| Cliquer deux fois vite sur Valider avec le même nom | une seule playlist ; pas de doublon | |
| Depuis la carte de la nouvelle playlist, ajouter `test_u2` en Écriture | premier partage (S10) : trois étiquettes à NON et message d'aide posés immédiatement ; options utilisables | |
| Ajouter des médias depuis Emby (« Ajouter à une playlist ») à la playlist créée | les médias apparaissent (le compteur de la carte suit au rechargement) | |
| Rendu FR/EN, TV / mobile | libellés et messages traduits ; rendu TV/mobile noté (non bloquant) | |
| **P33 / F2 (QUALIF)** Noms hostiles : « ../x », « a/b », « a\\b », « <b>x</b> », un emoji | acceptés ; s'affichent **littéralement** (jamais interprétés comme HTML) dans PlaySync et dans Emby ; noter tout comportement inattendu d'Emby (troncature, échappement) | |
| Noms refusés : caractère de largeur nulle seul (U+200B), U+202E, saut de ligne, NUL (collés dans le champ) | message « nom invalide » traduit sous le champ ; rien créé | |
| Cliquer très vite plusieurs fois sur Créer, ou appuyer sur Entrée pendant la requête | bouton désactivé pendant la requête, Entrée ignorée ; une seule playlist | |

## 6. Nettoyage
`tests/integration/90-cleanup.sh` (option `--delete-users` pour supprimer aussi les comptes `test_*`). Consigner les résultats dans le rapport de recette (U8, Q7).
