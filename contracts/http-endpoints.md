# Contrats HTTP — Emby_PlaySync

> Endpoints exposés par le plugin via `IService` (préfixe `/emby` selon l'installation). Auth : header `X-Emby-Token` (clé API admin ou token de session admin).
> Les endpoints `Spike/*` (diagnostic temporaire de v0.1.0/v0.2.0) et la sonde de réentrance U11 ont été **retirés en v0.2.0 (#15)** : remplacés par `Diagnostics/*` ci-dessous. Leur historique (routes, format, garde-fous) reste dans `CHANGELOG.md` et dans git.

## Diagnostics (permanent — admin uniquement, v0.2.0)

> **Auth** : Admin (`[Authenticated(Roles = "Admin")]`). **404** si `PluginConfiguration.EnableDiagnostics == false` (défaut : `true`). Lecture seule sauf `clear`. **Ids seulement** : aucun nom d'utilisateur, de playlist ou de média, aucun secret. Casse : réponses en **PascalCase**. Erreurs : 401, 403, 404, 500 (`error: <Type>: <message>`).
> Déclenchement d'une passe de réconciliation pour les tests : pas d'endpoint plugin, utiliser la tâche planifiée Emby (`POST /ScheduledTasks/Running/{id}`, admin ; l'id de la tâche « Emby Shared Playlist — réconciliation » est lisible par `GET /ScheduledTasks`).
> **Trois familles d'étiquettes depuis v1.2.0** (D21 ; mêmes règles pour chacune : casse ignorée, espaces tolérés autour de `=`, OUI+NON ensemble => NON l'emporte, le plugin ne supprime jamais d'étiquette de lui-même, aucune étiquette de la famille => considérée NON et reposée en `=NON`, **sans héritage**) : `remove-si-lu` (retrait du média quand il devient lu ; **depuis v1.2.0, effectif seulement si `propager-lu` est aussi active**), `propager-lu` (propagation du **flag lu seul** depuis v1.2.0 ; de v0.3.1 à v1.1.0 elle couvrait aussi l'avancement) et `propager-avancement` (v1.2.0 : propagation de la **position de lecture seule**, indépendante de l'état lu). Dans les contrats, `Family` = `remove-si-lu` | `propager-lu` | `propager-avancement`.

### GET /SharedPlaylist/Diagnostics/Journal?clear={bool}&kind={liste}

**Description** : journal mémoire borné (500 entrées, plus récent en dernier) des décisions du plugin. `clear=true` vide après lecture (tout le journal). `kind` (liste séparée par des virgules, insensible à la casse) filtre : **sans `kind`, seules les décisions du moteur sont renvoyées** (`ScanPass`, `MarkerPosed`, `DescriptionWritten`, `MarkerSeen`, `Removal`, `Propagation`, `PositionPropagation`, `PermissionPass`, `PermissionPosed`, `Skipped`, `Error`) ; avec `kind`, exactement les kinds demandés.

**Response 200** :
```json
[ { "Ts": "ISO-8601", "Kind": "ScanPass|MarkerPosed|DescriptionWritten|MarkerSeen|Removal|Propagation|PositionPropagation|PermissionPass|PermissionPosed|Skipped|Error",
    "UserId": "string|null", "ItemId": "string|null", "PlaylistId": "string|null", "Detail": "string|null" } ]
```
`Kind` et `Detail` (ids et compteurs uniquement) :
- `ScanPass` : `playlists=<n> shared=<n> posed=<n> pending=<n> durationMs=<n>` (une entrée par passe).
- `MarkerPosed` : `<Family>=NON` posé (`Detail` = `family=<Family> cause=first-detection|grace-elapsed`). Toujours `=NON` (v1.2.0 : aucune pose héritée).
- `DescriptionWritten` : message d'aide écrit ou remplacé (`Detail` = `cause=first-detection|grace-elapsed|v1-to-v2|v1-to-v3|v2-to-v3`). v1.2.0 : le message écrit sur une description vide est `HelpText.V3` ; `v1-to-v3`/`v2-to-v3` = description identique caractère pour caractère à V1/V2 remplacée par V3 (D15 étendue) ; `v1-to-v2` n'est plus émis. `v1-to-v2` (#51) : la description était encore identique caractère pour caractère à `HelpText.V1` (v0.2.0) ; remplacée par `HelpText.V2` (propagation effective), à chaque première détection ou passe, sans grâce, sans état mémorisé.
- `MarkerSeen` : état d'une famille lu à l'événement de retrait (`Detail` = `family=remove-si-lu state=Oui|Non|Both|None`). v1.2.0 : une seconde entrée `family=propager-lu state=…` est journalisée au même endroit (R4a en dépend) ; `Skipped inactive` si l'une des deux n'est pas `Oui`.
- `Removal` : `PlaylistId`, `ItemId`, `UserId` (déclencheur), `Detail` = `entries=<n> durationMs=<n>` (`entries` = entrées réellement retirées du média, v1.2.2 ; durée du traitement dans le gestionnaire, verrou compris).
- `Propagation` (v0.3.0, #20 : flag lu) : `PlaylistId`, `ItemId`, `UserId` (declencheur de la transition), `Detail` = `members=<n> propagated=<n> alreadyPlayed=<n> noAccess=<n> lockBusy=<n>` (une seule entree par playlist, meme si `propagated=0`). `lockBusy` (v1.2.0) : membres ignores parce que le verrou (utilisateur, media) n'a pas ete obtenu dans le delai (ecriture concurrente du flux avancement chez ce membre) ; chacun donne aussi un `Skipped lock-busy` avec `UserId`=membre. Pas de nouvelle tentative : le membre sera traite au prochain evenement. Journalisee uniquement si `propager-lu=OUI` seule (independant de `remove-si-lu` ; c'est `Removal` qui depend de `propager-lu` depuis v1.2.0). N'ecrit jamais de position.
- `PositionPropagation` (v0.3.1, #45 : avancement de lecture) : `PlaylistId`, `ItemId`, `UserId` (declencheur), `Detail` = `members=<n> propagated=<n> samePosition=<n> noAccess=<n> lockBusy=<n> durationMs=<n>` (`lockBusy` v1.2.0 : meme sens que pour `Propagation`, verrou (utilisateur, media) occupe par le flux du lu). Declenchement (v1.2.0, D21) : **`ISessionManager.PlaybackProgress`** (transition `IsPaused` false->true) et **`ISessionManager.PlaybackStopped`**, si la position de l'evenement est >= 30 s (**position absolue dans le media**, `PlaybackSessionListener.MinPositionTicks`) et si `propager-avancement=OUI` seule — **pas** `UserDataSaved` (mecanisme distinct de `Propagation`/`Removal`, sans rapport avec `PlayedTransitionTracker`). **Aucune condition sur l'etat lu** du declencheur ni des membres (la garde v0.3.1 « media lu pour le declencheur » est supprimee, #57). Ecriture brute de la position (jamais `Played`/`PlayCount`). **v1.2.1 (D23)** : aussi sur **chaque `PlaybackProgress` en cours de lecture**, au plus **une propagation toutes les 10 s** par couple (declencheur, media) — la pause (transition) et l'arret restent immediats ; les `PlaybackProgress` periodiques qui propagent **ne sont PAS journalises** individuellement (bruit) : ils sont comptes dans `Diagnostics/State.PositionProgress` (voir ci-dessous) ; une entree `PositionPropagation` n'est journalisee que pour `trigger=pause|stop|completion`. `Detail` gagne en fin `trigger=<pause|stop|completion>` (ajout retrocompatible). **Fin de lecture** (`PlaybackStopEventArgs.PlayedToCompletion=true`, `trigger=completion`) : sur une playlist ou `propager-lu=OUI` est aussi active, la position ecrite chez les membres est **0** (plus de point de reprise, comme chez le declencheur ; seuil 30 s non applique) ; sinon, position d'arret brute (inchange). Les playlists ciblees a la fin sont celles memorisees pendant la lecture, meme si le media en a ete retire entre-temps (`remove-si-lu`). Un `PlaybackProgress` tardif du meme `PlaySessionId` apres l'arret est ignore. v1.2.0 : jamais sur un `PlaybackProgress` ordinaire. Jusqu'a v1.1.0 : famille `propager-lu` et exclusion si le media etait lu pour le declencheur.
- `Skipped` : pas d'action (`Detail` = `inactive|not-shared|already-seen|already-removed|stale-entry|marker-present|unknown-owner|reentrant|lock-busy|budget-exceeded|no-effect|no-access|already-played|echo-consumed|same-position|no-position|too-short`) — v1.2.0 : `trigger-already-played` **n'est plus emis** (garde supprimee, D21). `stale-entry` (v1.2.2, #59) : une tentative de retrait n'a retire aucune entree de la cible (identifiant d'entree Emby perime apres renumerotation) ; ni media perdu ni autre effet, le plugin retente (2 tentatives au plus) ; `PlaylistId` renseigne, non bruyante (journal + compteur). `no-access`/`already-played`/`same-position` sont journalises avec `UserId`=membre concerne (propagation du lu ou de l'avancement, #20/#45). `lock-busy` a deux portees : au niveau de la **playlist** (`UserId` absent, verrou de playlist non obtenu : toute la playlist est passee) et, depuis v1.2.0, au niveau d'un **membre** (`UserId`=membre, `PlaylistId` renseigne : verrou (utilisateur, media) non obtenu, seul ce membre est passe, compte dans `lockBusy`). `echo-consumed` est au niveau de l'evenement (pas de la playlist : `PlaylistId=null`), pose par `PlaybackListener` quand `PluginWriteTracker` reconnait l'ecriture (#21). `no-position`/`too-short` (v0.3.1, #45) sont AUSSI au niveau de l'evenement (`PlaylistId=null`, `UserId`=declencheur), poses par `PlaybackSessionListener` avant meme d'atteindre `PlaybackPositionEngine` (position absente, ou position absolue < 30 s ; `trigger-already-played`, garde D-c « media deja lu pour le declencheur », a existe de v0.3.1 a v1.1.0) : distinct de `already-played` (R7, #20), qui vise un MEMBRE deja lu par playlist, pas le declencheur au niveau de l'evenement.
- `PermissionPass` (v0.4.0, #26 : permission de partage) : une entrée par passe (démarrage, réconciliation), `Detail` = `users=<n> enabled=<n> alreadyEnabled=<n> durationMs=<n>`. Absente si `AutoEnableSharing=false` (aucune tentative, donc rien à journaliser).
- `PermissionPosed` (v0.4.0, #26) : `UserId` (compte concerné), `Detail` vide — `Policy.AllowSharingPersonalItems` posé à `true` (première fois vu à `false`) ; ne modifie que ce champ, jamais de révocation. Déclenché à la passe de réconciliation (tous les utilisateurs), au démarrage (première passe) et à la création d'un utilisateur (`IUserManager.UserCreated`).
- `Error` : exception isolée (`Detail` = type d'exception, sans message brut si celui-ci peut contenir des noms).

### GET /SharedPlaylist/Diagnostics/State

**Description** : état **en mémoire** du plugin (rien n'est persisté ; remis à zéro à chaque redémarrage), pour les tests d'intégration et le support.

**Response 200** :
```json
{ "SeenPlaylistIds": ["string"],
  "GraceCounters": { "<playlistId>": { "remove-si-lu": 0, "propager-lu": 0, "propager-avancement": 0, "description": 0 } },
  "LastPass": { "Ts": "ISO-8601|null", "DurationMs": 0, "PlaylistsSeen": 0, "SharedManaged": 0 },
  "Handler": { "Count": 0, "LastMs": 0, "MaxMs": 0 },
  "GracePasses": 2,
  "PositionProgress": { "Propagated": 0, "Throttled": 0, "LockBusy": 0 },
  "SkippedCounts": { "already-seen": 0, "reentrant": 0, "lock-busy": 0, "echo-consumed": 0, "no-access": 0, "already-played": 0, "same-position": 0 } }
```
`PositionProgress` (v1.2.1, D23) = compteurs depuis le demarrage des `PlaybackProgress` periodiques du flux avancement : `Propagated` (evenements ayant conduit a une propagation, non journalises), `Throttled` (ignores car < 10 s depuis la derniere propagation du couple), `LockBusy` (ignores car un verrou n'a pas ete obtenu en 250 ms ; le suivant reessaie). Cle ajoutee : retrocompatible.

`SkippedCounts` = **tous** les `Skipped` depuis le démarrage, par raison. Les raisons bruyantes (`already-seen`, `reentrant`, `not-shared`, `unknown-owner`, `echo-consumed` : une par événement) ne sont PAS inscrites dans le journal (500) : elles n'existent que dans ces compteurs (et en Debug dans le fichier de log) ; les autres (`lock-busy`, `inactive`, `already-removed`, `stale-entry`, `marker-present`, `budget-exceeded`, `no-effect`, `no-access`, `already-played`, `same-position`, `no-position`, `too-short` ; `trigger-already-played` jusqu'à v1.1.0) sont dans le journal ET comptées. `SeenPlaylistIds` = playlists ayant subi la première détection depuis le démarrage. `GraceCounters` = passes consécutives sans étiquette de la famille (ou avec description vide) ; clé `propager-avancement` depuis v1.2.0. `Handler` = traitements de transition effectués dans le gestionnaire d'événement, retrait/propagation du lu/propagation de l'avancement confondus (nombre, dernière et plus longue durée en ms) : sert à vérifier le budget de latence.

> **Config plugin** : `EnableDiagnostics` (bool, défaut `true`), `GracePasses` (int, défaut `2`, min 1), `LogToConsole` (bool, défaut `true`), `LogLevel` (`Off|Info|Debug`, défaut `Info`), **`AutoEnableSharing`** (bool, **défaut `false` depuis v1.0.0** — GATE PROD, `security-20260927-221434.md` M1 ; défaut `true` de v0.4.0 à v0.5.0, #26) — pose automatiquement `Policy.AllowSharingPersonalItems=true` pour tous les utilisateurs (existants et nouveaux) ; désactivé = aucune écriture, mais **ne révoque jamais** un accès déjà accordé. **Aucun état n'est stocké** (ni dans `PluginConfiguration`, ni dans un fichier). Le changement de défaut ne s'applique qu'à une configuration vierge (sérialisation XML standard d'Emby : une valeur déjà sauvegardée, explicite ou via tout enregistrement de la page de config, n'est jamais réécrite). La période de réconciliation est celle de la tâche planifiée Emby (déclencheurs par défaut : démarrage + 5 min ; modifiable dans le tableau de bord).

## Page utilisateur (v1.1.0, #39 ; v1.2.0, #55/#56)

> **Auth** : utilisateur authentifié (`[Authenticated]`, **pas** `Roles = "Admin"`), header `X-Emby-Token` = token de session de l'utilisateur.
> **Identité** : l'utilisateur demandeur est **toujours** celui de la session (contexte d'autorisation Emby) ; aucun endpoint n'accepte l'identifiant du demandeur en paramètre.
> **Porte d'entrée** : si `Policy.AllowSharingPersonalItems` du demandeur est `false` → **403** sur **tous** les endpoints ci-dessous (cohérent avec la page masquée, décision Q6 ; le plugin ne contourne jamais la permission).
> **Périmètre** : uniquement les playlists dont le demandeur est **propriétaire** (ligne de partage `ManageDelete`, ou propriétaire natif pour une playlist non encore partagée — mécanisme confirmé par le spike U13). Une playlist inexistante **ou non possédée** renvoie **404** (indiscernables : pas d'énumération par `PlaylistId`, anti-IDOR).
> **Niveaux** : seuls `Read` et `Write` peuvent être attribués (R2 : jamais `Manage`/`ManageDelete`, le propriétaire reste seul gestionnaire). La ligne du propriétaire n'est jamais modifiable ni supprimable.
> **Écritures** : sous le verrou de la playlist (`PlaylistLocks`, R12) + `WriteScope`, relecture après écriture. Verrou non obtenu en 5 s → **409** `busy`.
> **Casse** : PascalCase (sérialiseur Emby). **Erreurs** : corps `{ "Error": "<code>" }` — codes stables, **aucun texte localisé ni message d'exception** côté serveur (la page traduit FR/EN) ; 500 = `{ "Error": "internal" }`, détail uniquement dans les logs du serveur.
> **Noms** : ces endpoints exposent des **noms** d'utilisateurs et de playlists (contrairement à `Diagnostics/*`) — nécessaire à la page, visible uniquement du propriétaire (décision 5bis) ; le journal reste en identifiants seulement.
> **Page** : `PluginPageInfo` `EnableInUserMenu = true`, noms de ressources `PlaySyncUserPage` (HTML) / `PlaySyncUserScript` (JS), libellé de menu `PlaySync`.

### GET /SharedPlaylist/User/Playlists

**Description** : playlists dont le demandeur est propriétaire, partagées **ou non**, avec membres et état des trois familles d'étiquettes (deux avant v1.2.0).

**Response 200** :
```json
[ { "PlaylistId": "string", "Name": "string", "ItemCount": 0,
    "IsShared": true,
    "Members": [ { "UserId": "string", "Name": "string", "Level": "Read|Write" } ],
    "Options": { "remove-si-lu": "Oui|Non|Both|None", "propager-lu": "Oui|Non|Both|None", "propager-avancement": "Oui|Non|Both|None" } } ]
```
`Options` contient **toujours** les trois clés depuis v1.2.0 (ajout rétrocompatible). `Options` reflète l'**état** de chaque famille, pas son effet : `remove-si-lu` peut valoir `Oui` alors que le retrait est inactif faute de `propager-lu=Oui` (D21) ; c'est à la page de le signaler (S10b).
`Members` exclut le propriétaire. `IsShared` = au moins un membre (même définition que `PlaylistSnapshot.IsShared`). `Options` = `MarkerEvaluator.Evaluate` (lecture fraîche) : `Oui` = active, `Both` = conflit (NON l'emporte), `None` = aucune étiquette. Tri par `Name` (culture invariante).

**Errors** : 401, 403 (`sharing-disabled`)

### POST /SharedPlaylist/User/Playlists

**Description** (v1.2.0, #55, D22) : crée une playlist **vide**, **non partagée** (donc non gérée : aucune étiquette ni message d'aide avant le premier partage), de type **Vidéo**, dont le **demandeur** (identité de session) est propriétaire. Les membres s'ajoutent ensuite par `POST .../Members` (qui déclenche la première détection).

**Request body** :
```json
{ "Name": "string" }
```

**Response 200** : la playlist créée (même forme qu'un élément de `GET /SharedPlaylist/User/Playlists` : `IsShared=false`, `Members=[]`, `ItemCount=0`, `Options` = trois clés à `None`).

`Name` : espaces de bord retirés, normalisation Unicode **NFC** ; longueur **1 à 100** caractères après normalisation ; aucun caractère de contrôle (catégorie Unicode `Cc`). Le nom **stocké** est le nom normalisé. **Unicité par propriétaire** : refus si une playlist **possédée** par le demandeur (même périmètre que `GET User/Playlists`) porte un nom égal après la même normalisation, comparaison **insensible à la casse** (`OrdinalIgnoreCase`) ; les espaces **internes** sont significatifs. Vérification et création sont sérialisées par un verrou de création propre au demandeur (deux requêtes simultanées de même nom : une seule réussit). Les playlists d'autres comptes ne sont jamais consultées.

**Quota** (v1.2.0, audit sécurité M1) : refus si le demandeur **possède déjà** `MaxOwnedPlaylists` (= **10**, constante serveur unique) playlists ou plus, comptées par `ListOwnedPlaylists` (toutes ses playlists, partagées ou non, y compris celles créées nativement dans Emby ; recalculé à chaque demande, aucun état, R11). Au-delà de 10, rien n'est supprimé : seule la création par cet endpoint est refusée. La valeur n'est pas exposée dans la réponse.

**Ordre des vérifications** : `sharing-disabled` (403) → `invalid-name` (400) → verrou `create:<demandeur>`, attente **≤ 1 s** (409 `busy`) → lecture unique de `ListOwnedPlaylists` sous le verrou → `limit-reached` (409) → `name-exists` (409) → création. Le quota précède l'unicité (il dépend du compte, pas du nom saisi).

**Délai** : si Emby ne termine pas la création dans le délai d'attente (5 s), la réponse est 500 `internal` alors que la création peut aboutir ensuite ; la playlist apparaît au prochain `GET User/Playlists`. Un nouvel essai du même nom est refusé par `name-exists` (aucun doublon) ; le client doit **recharger la liste** après un 500 avant de proposer un nouvel essai.

**Errors** : 400 (`invalid-name` : vide/blanc, > 100 caractères, caractère de contrôle, absent/null), 401, 403 (`sharing-disabled`), 409 (`busy` : verrou de création non obtenu en **1 s** — audit M2 ; le délai de 5 s de l'appel Emby `CreatePlaylist` est distinct et inchangé ; `limit-reached` : le demandeur possède déjà 10 playlists ou plus ; `name-exists` : nom déjà utilisé par une playlist du demandeur), 500 (`internal`, y compris le dépassement du délai de création)

Textes côté page (le serveur ne renvoie que le code ; `invalid-name`, `name-exists` et `limit-reached` s'affichent **sous le champ**, les autres en toast) :
- `limit-reached` — FR : « Vous possédez déjà le nombre maximal de playlists. Supprimez-en une pour pouvoir en créer une nouvelle. » — EN : "You already own the maximum number of playlists. Delete one to be able to create a new one." (le nombre n'est pas écrit dans le texte, pour ne pas dupliquer la constante serveur).

### GET /SharedPlaylist/User/Users

**Description** : comptes pouvant être ajoutés comme membres (sélecteur).

**Response 200** :
```json
[ { "UserId": "string", "Name": "string" } ]
```
Exclut le demandeur et les comptes **désactivés**. Tri par `Name`. Aucun autre champ (ni rôle admin, ni politique, ni date).

**Errors** : 401, 403 (`sharing-disabled`)

### POST /SharedPlaylist/User/Playlists/{PlaylistId}/Members

**Description** : ajoute un membre ou change son niveau (upsert). Sur une playlist **non encore partagée**, crée le premier partage ; la playlist devient gérée et la **première détection** est exécutée immédiatement (pose `remove-si-lu=NON`, `propager-lu=NON`, `propager-avancement=NON` depuis v1.2.0, message d'aide si description vide), sans attendre la passe périodique.

**Request body** :
```json
{ "UserId": "string", "Level": "Read|Write" }
```

**Response 200** : la playlist mise à jour (même forme qu'un élément de `GET /SharedPlaylist/User/Playlists`).

`Level` : comparaison **exacte, sensible à la casse**, contre `{"Read","Write"}` uniquement (pas de tolérance casse/espaces comme pour les étiquettes — revue sécurité `security-design-20260928-143848.md` MOYENNE-1) ; toute autre valeur (y compris `read`, `WRITE `, `Manage`, chaîne vide, null) → `invalid-level`.

**Errors** : 400 (`invalid-level` : autre que `Read`/`Write` ; `invalid-user` : inconnu, désactivé ; `self` : le demandeur lui-même), 401, 403 (`sharing-disabled`), 404 (`not-found` : inexistante ou non possédée), 409 (`busy`)

### DELETE /SharedPlaylist/User/Playlists/{PlaylistId}/Members/{UserId}

**Description** : retire le partage d'un membre (D2). Retirer le dernier membre rend la playlist non partagée : elle n'est plus gérée (étiquettes conservées, inertes).

**Response 200** : la playlist mise à jour.

**Errors** : 400 (`self` : ligne du propriétaire), 401, 403 (`sharing-disabled`), 404 (`not-found` : playlist inexistante/non possédée, **ou** utilisateur non membre), 409 (`busy`)

### POST /SharedPlaylist/User/Playlists/{PlaylistId}/Options

**Description** : active ou désactive une famille d'étiquettes (**D19**) : remplacement **atomique** de toutes les étiquettes de la famille (toutes variantes reconnues par `MarkerEvaluator`, casse/espaces) par une seule étiquette canonique `<famille>=OUI` ou `<famille>=NON`, en une seule écriture sous le verrou ; les autres étiquettes (étrangères, autre famille) sont intactes. Idempotent. Réservé aux playlists **partagées** (sur une playlist non partagée, le plugin ne gère rien : 409 `not-shared`).

**Request body** :
```json
{ "Family": "remove-si-lu|propager-lu|propager-avancement", "Enabled": true }
```

`Family` : comparaison **exacte**, sensible à la casse, contre les trois noms. `remove-si-lu` est accepté quel que soit l'état de `propager-lu` (l'étiquette est un réglage ; la dépendance D21 est appliquée par le moteur et signalée par la page). Chaque bascule ne remplace que les étiquettes de **sa** famille (D19) : désactiver `propager-lu` ne modifie pas `remove-si-lu`.

**Response 200** : la playlist mise à jour.

**Errors** : 400 (`invalid-family`), 401, 403 (`sharing-disabled`), 404 (`not-found`), 409 (`busy`, `not-shared`)

### Journal (`Diagnostics/Journal`) — nouveaux kinds (v1.1.0)

- `ShareChanged` : `PlaylistId`, `UserId` = membre concerné, `Detail` = `action=add|update|remove level=Read|Write|-` (ids seulement ; le demandeur est le propriétaire de la playlist).
- `MarkerSet` : `PlaylistId`, `UserId` = propriétaire, `Detail` = `family=<Family> value=OUI|NON removed=<n>` (D19, action explicite de l'utilisateur). `<Family>` inclut `propager-avancement` depuis v1.2.0.
- `PlaylistCreated` (v1.2.0, #55) : `PlaylistId`, `UserId` = propriétaire (demandeur), `Detail` vide (ids seulement ; jamais le nom).
- Tous (`ShareChanged`, `MarkerSet`, `PlaylistCreated`) font partie des « décisions du moteur » renvoyées sans filtre `kind`.
