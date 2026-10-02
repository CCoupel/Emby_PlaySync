#!/usr/bin/env bash
# 21-propagation.sh — scénarios d'intégration I18-I26 de la v0.3.0 (issues #20 #21 #13 #23 #51) sur emby2
# (QUALIF UNIQUEMENT). À exécuter par qa après déploiement du plugin ; jamais depuis un poste sans avoir
# vérifié la cible.
#
# Donne effet à l'étiquette `propager-lu` (posée/lue en NON depuis v0.2.0, sans effet jusqu'ici) : sur la même
# transition non lu -> lu qui déclenche déjà le retrait (`remove-si-lu`), si `propager-lu=OUI` est actif, le
# plugin pose le flag lu chez les autres membres de CETTE playlist qui ne l'ont pas déjà et ont accès au média.
# v1.2.0 (D21, tableau A, docs/chronogrammes.md §2) : `propager-lu` ne propage plus que le FLAG lu (jamais la position :
# `propager-avancement`, 22-avancement.sh) et `remove-si-lu` n'a d'effet que si `propager-lu=OUI` est aussi actif — la
# matrice I18 est donc mise à jour (le « retrait seul » E/F/G ne retire plus rien, S3b) ; I25 vérifie V1/V2 -> V3.
#
# Prérequis : tests/integration/00-setup-users.sh exécuté (test_u1 propriétaire, test_u2 Write, test_u3 Read) ;
# >= 13 médias (seuil abaissé le 2026-09-27, décision utilisateur : la bibliothèque de QUALIF n'en a que 13 ;
# 3 sont réservés en exclusivité à I23/S6a-c, les 10 restants sont recyclés pour le reste — voir le commentaire
# sur le bassin de médias plus bas). I20 (R8) crée puis nettoie LUI-MÊME test_u4 (aucune bibliothèque accessible)
# dans ce même run : aucun prérequis supplémentaire, aucun état laissé après un run complet.
# Usage : tests/integration/21-propagation.sh [I18 I23 …]   (sans liste : tous les scénarios)
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/int-lib.sh"

WANT=("$@")
OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/propagation-$TS.json"
RES="$SCRATCH/results.jsonl"; : > "$RES"
FAILS=0; DONE=0

rec() { # ID STATUS DESC [EVIDENCE_JSON]
  local ev=${4:-null}
  jq -e . <<<"$ev" >/dev/null 2>&1 || ev=$(jq -Rn --arg s "$ev" '$s')
  jq -nc --arg id "$1" --arg s "$2" --arg d "$3" --argjson e "$ev" '{id:$id,status:$s,desc:$d,evidence:$e}' >> "$RES"
  printf '  [%s] %s — %s\n' "$2" "$1" "$3"
  if [[ $2 == KO ]]; then FAILS=$((FAILS+1)); fi
  return 0
}
ck() { # ID DESC EVIDENCE cmd... : OK si cmd réussit, sinon KO
  local id=$1 d=$2 e=$3; shift 3
  if "$@"; then rec "$id" OK "$d" "$e"; else rec "$id" KO "$d" "$e"; fi
}
skip() { rec "$1" SKIP "$2" "null"; }

write_out() { # PARTIAL
  jq -s --arg ts "$TS" --argjson p "$1" '
    {run:$ts, partial:$p, summary:{ok:(map(select(.status=="OK"))|length), ko:(map(select(.status=="KO"))|length), skip:(map(select(.status=="SKIP"))|length)}, results:.}' "$RES" > "$OUT_FILE" 2>/dev/null || true
}
on_exit() {
  local rc=$?; set +e
  cleanup_restricted_user
  # Nettoyage final (retour qa, 2026-09-27) : ni ce script ni 20-etiquettes-retrait.sh (relancé par I26, en
  # sous-processus partageant private/test-state.json) ne se nettoient sinon quand ils sont imbriqués.
  cleanup_registered_playlists
  end_test_sessions   # #61 : Stopped sur les sessions ouvertes puis Logout des jetons de test
  if [[ $DONE == 0 && -s $RES ]]; then write_out true; echo "  (trap) preuves partielles : $OUT_FILE" >&2; fi
  rm -rf "$SCRATCH"; exit $rc
}
trap on_exit EXIT

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV"
[[ -f $USERS_ENV && -f $SNAPSHOT ]] || die "lancer tests/integration/00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)")
T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)")
T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")
purge_stale_test_playlists   # #60 : aucune playlist SPIKE résiduelle d'un run précédent (sauf imbriqué : INT_NESTED=1)

select_test_media 24   # #61 : bibliothèque locale PlaySync-Tests uniquement ; refus de tout média /config/virtual/
# Seuil abaissé (décision utilisateur, 2026-09-27) : la bibliothèque de QUALIF n'a que 13 médias de >= 10 min.
# Réduction de couverture documentée : au lieu d'un média jamais réutilisé par sous-cas, un petit bassin est
# recyclé (round-robin) pour I18/I19/I20/I21/I22/I24/I25, chaque réutilisation étant précédée d'une remise à
# « non lu » (reset_played) ET chaque scénario retirant explicitement son média de sa propre playlist en fin de
# course (drop_item) — sans quoi une playlist plus ancienne encore partagée avec les mêmes membres resterait
# candidate (ListSharedPlaylistsOfUserContaining) et fausserait un scénario ultérieur réutilisant ce média (même
# classe de bug que la sonde U11, cf. tests/spike, où un média partagé masquait plusieurs scénarios).
# I23 (S6a-c) reste sur des médias JAMAIS réutilisés : ces playlists (L1/L2/L1b/L2b/L1c/L2c) ne sont ni nettoyées
# ni supprimées pendant le run, et un média recyclé y resterait candidat indéfiniment (le point le plus sensible).
[[ ${#M[@]} -ge 13 ]] || die "moins de 13 médias (>= 10 min) dans la bibliothèque (demander à l'utilisateur)"
reset_pool_full "${M[@]}"   # remise à zéro complète (lu+position) : un run précédent (même script, même invocation séparée) ne doit rien laisser
echo "  [OK] bassin de ${#M[@]} médias remis à zéro (lu=false, position=0)"
S6_MEDIA=("${M[0]}" "${M[1]}" "${M[2]}")
POOL=("${M[@]:3}")   # le reste (>= 10 si 13 médias) : recyclé
[[ ${#POOL[@]} -ge 1 ]] || die "aucun média disponible pour le bassin recyclé (après réservation de 3 pour S6a-c)"
echo "  [OK] ${#M[@]} médias : 3 réservés à I23 (S6a-c, jamais recyclés), ${#POOL[@]} recyclés pour le reste"

# Compteur sur FICHIER, comme next_media() : s6_media() est appelée en $(...) (sous-shell), un simple
# NEXT_S6++ y serait perdu (déjà rencontré et corrigé une fois pour next_media() dans ce même script).
echo 0 > "$SCRATCH/next_s6"
s6_media() {   # UN SEUL appel par sous-cas de I23, JAMAIS recyclé
  local n; n=$(cat "$SCRATCH/next_s6")
  [[ $n -lt ${#S6_MEDIA[@]} ]] || die "s6_media : plus de médias réservés à I23 (S6a-c) que prévu (${#S6_MEDIA[@]})"
  echo "${S6_MEDIA[$n]}"; echo $((n+1)) > "$SCRATCH/next_s6"
}

# Compteur sur FICHIER (pas une variable shell) : next_media() est presque toujours appelée en $(...), donc dans un
# sous-shell — un simple compteur bash y serait perdu (invisible au shell appelant). Le fichier, lui, persiste réellement.
echo 0 > "$SCRATCH/next_m"
next_media() {   # bassin recyclé (round-robin) : remet à « non lu » AVANT de rendre un média déjà utilisé
  local n idx id
  n=$(cat "$SCRATCH/next_m"); idx=$((n % ${#POOL[@]})); id=${POOL[$idx]}
  echo $((n+1)) > "$SCRATCH/next_m"
  if [[ $n -ge ${#POOL[@]} ]]; then
    reset_played "$id"
    # attente active (#47, qa-integration-v031 : I18.E — un précédent « lu » pas encore retombé au moment où le média
    # recyclé était rendu à un scénario suivant) : confirme que le reset est bien retombé côté serveur avant de rendre
    # la main, au lieu de faire confiance à la réponse HTTP synchrone de TogglePlayed seule.
    wait_played "$U1" "$T1" "$id" false 10 || true
    wait_played "$U2" "$T2" "$id" false 10 || true
    wait_played "$U3" "$T3" "$id" false 10 || true
  fi
  echo "$id"
}

st=$(api GET "$DIAG/State"); [[ $st == 200 ]] || die "Diagnostics/State -> HTTP $st : plugin v0.3.0 non déployé ou EnableDiagnostics=false"
GRACE=$(jq -r '.gracePasses // 2' "$RESP")
echo "  [OK] GracePasses=$GRACE"

# ---------------------------------------------------------------- scénarios
i18() {
  echo "== I18 — tableau A intégral (remove-si-lu × propager-lu, chacun dans {absente, NON, OUI, OUI+NON}) ; retrait seulement si les DEUX sont OUI (v1.2.0)"
  # 7 lignes utiles (RM=OUI,PR=OUI compte une seule fois) : chaque famille testée dans ses 4 états face à l'autre = OUI.
  local -a ROWS=(
    "A none oui false true"    "B non  oui false true"    "C oui  oui true  true"    "D both oui false true"
    "E oui  none false false"  "F oui  non  false false"  "G oui  both false false"   # v1.2.0 (S3b) : remove-si-lu=OUI SANS propager-lu active => RIEN
  )
  local row id rm pr removed propagated pl item j n p1 p3
  for row in "${ROWS[@]}"; do
    read -r id rm pr removed propagated <<<"$row"
    pl=$(shared_pl "SPIKE-I18-$id" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
    prime "$pl" || true
    set_marker_state "$pl" remove-si-lu "$rm"
    set_marker_state "$pl" propager-lu "$pr"
    jclear
    finish "$U2" "$T2" "$item"
    if [[ $removed == true ]]; then
      ck "I18.$id.removed" "RM=$rm/PR=$pr : retrait actif => média retiré" "null" wait_count "$pl" "$item" 0 10
    else
      ck "I18.$id.removed" "RM=$rm/PR=$pr : retrait inactif => média reste" "null" stays "$pl" "$item" 1 4
    fi
    if [[ $propagated == true ]]; then
      wait_played "$U1" "$T1" "$item" true 10 || true   # attente active (#47) : positif, doit se produire
      wait_played "$U3" "$T3" "$item" true 10 || true
      p1=$(played_of "$U1" "$T1" "$item"); p3=$(played_of "$U3" "$T3" "$item")
      ck "I18.$id.propagated" "RM=$rm/PR=$pr : propagation active => u1 et u3 passent lu" "{\"u1\":\"$p1\",\"u3\":\"$p3\"}" test "$p1/$p3" = "true/true"
    else
      nap 2   # négatif (rien ne doit se produire) : fenêtre fixe volontaire, pas d'attente active
      p1=$(played_of "$U1" "$T1" "$item"); p3=$(played_of "$U3" "$T3" "$item")
      ck "I18.$id.propagated" "RM=$rm/PR=$pr : propagation inactive => u1 et u3 restent non lus" "{\"u1\":\"$p1\",\"u3\":\"$p3\"}" test "$p1/$p3" = "false/false"
    fi
    j=$(journal Propagation)
    n=$(jcount "$j" "$pl" Propagation)
    if [[ $propagated == true ]]; then
      ck "I18.$id.journal" "une entrée Propagation journalisée" "$j" test "$n" -ge 1
    else
      ck "I18.$id.journal" "aucune entrée Propagation (famille inactive)" "$j" test "$n" = 0
    fi
    drop_item "$pl" "$item"   # média recyclé (bassin réduit) : ne doit rester candidat dans AUCUNE playlist
  done
}

i19() {
  echo "== I19 — R7 : membre déjà lu, aucune écriture, compteur/date/PlayCount intacts"
  local pl item before after j n1 n2 p1
  pl=$(shared_pl "SPIKE-I19" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non   # propagation seule : le média reste, pas d'interférence du retrait
  set_marker_state "$pl" propager-lu oui
  finish "$U3" "$T3" "$item"                # u3 marque lu LUI-MÊME (action native, pas le plugin) : état « avant »
  wait_played "$U3" "$T3" "$item" true 10 || true
  before=$(api GET "/Users/$U3/Items/$item" "" "$T3" >/dev/null; jq -c '.UserData|{Played,LastPlayedDate,PlayCount}' "$RESP")
  jclear
  finish "$U2" "$T2" "$item"                # u2 déclenche : propagation visée sur u1 (neuf) ET u3 (déjà lu)
  wait_played "$U1" "$T1" "$item" true 10 || true   # attente active (#47) : positif (u1, neuf), même passe que u3 (déjà lu, inchangé)
  after=$(api GET "/Users/$U3/Items/$item" "" "$T3" >/dev/null; jq -c '.UserData|{Played,LastPlayedDate,PlayCount}' "$RESP")
  ck I19.unchanged "compteur/date/flag de u3 (déjà lu) inchangés après une transition qui le concerne" "{\"before\":$before,\"after\":$after}" test "$before" = "$after"
  p1=$(played_of "$U1" "$T1" "$item")
  ck I19.othernew "u1 (pas encore lu) est bien propagé par la même transition" "{\"u1\":\"$p1\"}" test "$p1" = true
  j=$(journal "Propagation,Skipped,Removal,Error,MarkerSeen")
  n1=$(jcount "$j" "$pl" Propagation '(^|[^A-Za-z])alreadyPlayed=[1-9]'); n2=$(jcount "$j" "$pl" Skipped 'already-played')
  ck I19.aggregate "l'entrée Propagation agrégée compte alreadyPlayed >= 1" "$j" test "$n1" -ge 1
  ck I19.permember "Skipped already-played journalisé pour u3" "$j" test "$n2" -ge 1
  drop_item "$pl" "$item"   # média recyclé
}

i20() {
  echo "== I20 — R8 : membre sans accès à la bibliothèque, aucune erreur (compte créé et nettoyé dans ce run)"
  local pl item j n1 n2 p1
  ensure_restricted_user   # crée test_u4 (U4/T4), EnableAllFolders=false ; nettoyé en fin de scénario ET par le trap si interruption
  pl=$(shared_pl "SPIKE-I20" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$pl" --arg u "$U4" '{ItemIds:[$p],UserIds:[$u],ItemAccess:"Read"}')" "$T1"
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  jclear
  finish "$U2" "$T2" "$item"
  wait_played "$U1" "$T1" "$item" true 10 || true
  # Pas de contrôle par GET /Users/{id}/Items/{item} générique (confirmé par qa : cette route REST renvoie 200 pour
  # test_u4 malgré EnableAllFolders=false — sémantique différente de BaseItem.IsVisibleStandalone(user), utilisé en
  # interne par le plugin, qui détecte correctement l'absence d'accès). La preuve retenue est ci-dessous : Propagation
  # agrégée noAccess>=1 ET Skipped no-access journalisé.
  p1=$(played_of "$U1" "$T1" "$item")
  ck I20.others "les autres membres (u1) sont propagés normalement malgré le membre restreint" "{\"u1\":\"$p1\"}" test "$p1" = true
  j=$(journal "Propagation,Skipped,Removal,Error,MarkerSeen")
  n1=$(jcount "$j" "$pl" Propagation '(^|[^A-Za-z])noAccess=[1-9]'); n2=$(jcount "$j" "$pl" Skipped 'no-access')
  ck I20.aggregate "l'entrée Propagation agrégée compte noAccess >= 1" "$j" test "$n1" -ge 1
  ck I20.permember "Skipped no-access journalisé pour test_u4" "$j" test "$n2" -ge 1
  ck I20.noerror "aucune entrée Error causée par le membre restreint" "$j" test "$(jcount "$j" "$pl" Error)" = 0
  drop_item "$pl" "$item"   # média recyclé
  cleanup_restricted_user
}

i21() {
  echo "== I21 — R6 : décocher (retour à « non lu ») ne propage jamais"
  local pl item j p1 p3before p3after
  pl=$(shared_pl "SPIKE-I21" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  finish "$U2" "$T2" "$item"   # transition lu -> propagation normale attendue (u1 ET u3, tous deux membres)
  wait_played "$U1" "$T1" "$item" true 10 || true; wait_played "$U3" "$T3" "$item" true 10 || true
  p1=$(played_of "$U1" "$T1" "$item"); p3before=$(played_of "$U3" "$T3" "$item")
  ck I21.setup "propagation initiale bien effective (u1 ET u3 lus)" "{\"u1\":\"$p1\",\"u3\":\"$p3before\"}" test "$p1/$p3before" = "true/true"
  jclear
  unmark "$U2" "$T2" "$item"   # retour à « non lu » : R6
  nap 2
  j=$(journal "Propagation")
  ck I21.nopropagation "aucune entrée Propagation causée par le retour à non lu" "$j" test "$(jcount "$j" "$pl" Propagation)" = 0
  p3after=$(played_of "$U3" "$T3" "$item")
  ck I21.untouched "u3 (déjà propagé) n'est pas retouché par ce retour arrière (toujours lu, sans nouvelle écriture)" "{\"before\":\"$p3before\",\"after\":\"$p3after\"}" test "$p3before" = "$p3after"
  drop_item "$pl" "$item"   # média recyclé
}

i22() {
  echo "== I22 — membre Read propage aussi bien qu'un membre Write ou le propriétaire"
  local pl item p1 p2
  pl=$(shared_pl "SPIKE-I22" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  jclear
  finish "$U3" "$T3" "$item"   # u3 = Read
  wait_played "$U1" "$T1" "$item" true 10 || true; wait_played "$U2" "$T2" "$item" true 10 || true
  p1=$(played_of "$U1" "$T1" "$item"); p2=$(played_of "$U2" "$T2" "$item")
  ck I22.read "membre Read (u3) déclenche la propagation vers propriétaire et Write" "{\"u1\":\"$p1\",\"u2\":\"$p2\"}" test "$p1/$p2" = "true/true"
  drop_item "$pl" "$item"   # média recyclé
}

i23() {
  echo "== I23 — S6a-c : deux playlists partagées, même média, absence de transitivité"
  local item L1 L2 j n m p1 p3 p2 p3b p2c
  # --- S6a : U12 (test_u2, MEMBRE DE L1 SEULEMENT) lit F1 -> seule L1 est traitée
  item=$(s6_media)
  L1=$(new_pl "SPIKE-I23-L1" "$item"); share_pl_one "$L1" "$U2" Write   # L1 = {U1, U12=test_u2}
  L2=$(new_pl "SPIKE-I23-L2" "$item"); share_pl_one "$L2" "$U3" Write   # L2 = {U1, U21=test_u3}
  prime "$L1" || true; prime "$L2" || true
  set_marker_state "$L1" remove-si-lu oui; set_marker_state "$L1" propager-lu oui
  set_marker_state "$L2" remove-si-lu oui; set_marker_state "$L2" propager-lu oui
  # pré-condition d'environnement (#60) : F1 ne doit figurer dans aucune autre playlist visible de test_u2/test_u3 que L1/L2,
  # sinon la propagation observée peut venir d'un résidu (ex. SPIKE-I17) et non du plugin
  local stray; stray=$(playlists_containing "$item" "$L1" "$L2" | paste -sd, -)
  ck I23.S6a.isolated "pré-condition : F1 dans aucune autre playlist partagée que L1/L2 (pollution d'environnement sinon)" "{\"others\":\"$stray\"}" test -z "$stray"
  jclear
  finish "$U2" "$T2" "$item"
  ck I23.S6a.L1removed "L1 : F1 retiré" "null" wait_count "$L1" "$item" 0 10   # attente active : retrait et propagation (même playlist) partagent la passe
  ck I23.S6a.L2untouched "L2 : F1 TOUJOURS présent (aucune transitivité)" "null" test "$(count_item "$L2" "$item")" = 1
  wait_played "$U1" "$T1" "$item" true 10 || true   # attente active (#47, comme I3.others) : la propagation suit le retrait dans la même passe, pas de lecture immédiate
  p1=$(played_of "$U1" "$T1" "$item"); p3=$(played_of "$U3" "$T3" "$item")
  ck I23.S6a.propagation "propriétaire (membre des deux) propagé ; U3 (L2 seule) jamais touché" "{\"u1\":\"$p1\",\"u3\":\"$p3\"}" test "$p1/$p3" = "true/false"
  nap 3   # laisser le temps à un éventuel écho (origine plugin, U1) de se propager à tort avant de vérifier L2
  ck I23.S6a.noecho "l'écho du propriétaire (origine plugin) n'a rien déclenché sur L2 (F1 toujours présent)" "null" test "$(count_item "$L2" "$item")" = 1
  j=$(journal "Propagation,Skipped,Removal,Error,MarkerSeen"); n=$(jcount "$j" "$L2" Removal); m=$(jcount "$j" "$L2" Propagation)
  ck I23.S6a.nojournalL2 "aucune entrée Removal/Propagation sur L2 causée par cet écho" "$j" test "$n/$m" = "0/0"

  # --- S6b : le propriétaire (test_u1, membre des deux) lit un NOUVEL F2 -> les deux listes traitées
  local F2 L1b L2b
  F2=$(s6_media)
  L1b=$(new_pl "SPIKE-I23-L1b" "$F2"); share_pl_one "$L1b" "$U2" Write; prime "$L1b" || true
  set_marker_state "$L1b" remove-si-lu oui; set_marker_state "$L1b" propager-lu oui
  L2b=$(new_pl "SPIKE-I23-L2b" "$F2"); share_pl_one "$L2b" "$U3" Write; prime "$L2b" || true
  set_marker_state "$L2b" remove-si-lu oui; set_marker_state "$L2b" propager-lu oui
  jclear
  finish "$U1" "$T1" "$F2"
  wait_count "$L1b" "$F2" 0 10 || true; wait_count "$L2b" "$F2" 0 10 || true
  ck I23.S6b.bothremoved "les DEUX listes perdent F2" "null" test "$(count_item "$L1b" "$F2")/$(count_item "$L2b" "$F2")" = "0/0"
  wait_played "$U2" "$T2" "$F2" true 10 || true; wait_played "$U3" "$T3" "$F2" true 10 || true   # attente active avant lecture
  p2=$(played_of "$U2" "$T2" "$F2"); p3b=$(played_of "$U3" "$T3" "$F2")
  ck I23.S6b.bothpropagated "les DEUX cotés propagés (membre de L1 et membre de L2)" "{\"u2\":\"$p2\",\"u3\":\"$p3b\"}" test "$p2/$p3b" = "true/true"

  # --- S6c : U21 (test_u3, MEMBRE DE L2 SEULEMENT) lit un NOUVEL F3 -> symétrique de S6a
  local F3 L1c L2c
  F3=$(s6_media)
  L1c=$(new_pl "SPIKE-I23-L1c" "$F3"); share_pl_one "$L1c" "$U2" Write; prime "$L1c" || true
  set_marker_state "$L1c" remove-si-lu oui; set_marker_state "$L1c" propager-lu oui
  L2c=$(new_pl "SPIKE-I23-L2c" "$F3"); share_pl_one "$L2c" "$U3" Write; prime "$L2c" || true
  set_marker_state "$L2c" remove-si-lu oui; set_marker_state "$L2c" propager-lu oui
  jclear
  finish "$U3" "$T3" "$F3"
  ck I23.S6c.L2removed "L2 : F3 retiré" "null" wait_count "$L2c" "$F3" 0 10
  ck I23.S6c.L1untouched "L1 : F3 TOUJOURS présent" "null" test "$(count_item "$L1c" "$F3")" = 1
  p2c=$(played_of "$U2" "$T2" "$F3")
  ck I23.S6c.notouch "membre exclusif de L1 (u2) jamais touché" "{\"u2\":\"$p2c\"}" test "$p2c" = false
}

i24() {
  echo "== I24 — S7 : un seul écho consommé par écriture de propagation (SkippedCounts.echo-consumed), pas de boucle"
  local pl item ec_before ec_after j2 n
  pl=$(shared_pl "SPIKE-I24" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  nap 2
  ec_before=$(state | jq -r '.skippedCounts["echo-consumed"] // 0')
  finish "$U2" "$T2" "$item"
  wait_played "$U1" "$T1" "$item" true 10 || true; wait_played "$U3" "$T3" "$item" true 10 || true
  ec_after=$(state | jq -r '.skippedCounts["echo-consumed"] // 0')
  ck I24.propagated "propagation effective (u1, u3)" "null" test "$(played_of "$U1" "$T1" "$item")/$(played_of "$U3" "$T3" "$item")" = "true/true"
  # 2 écritures plugin (MarkPlayed u1, MarkPlayed u3) : AU PLUS un écho consommé par écriture (non garanti selon
  # qu'Emby réémette UserDataSaved pour une écriture du plugin — confirmé par dev-plugin, donc pas de borne basse) ;
  # une valeur > 2 trahirait en revanche une boucle (plus d'échos que d'écritures).
  ck I24.echoconsumed "echo-consumed <= 2 (au plus une consommation par écriture MarkPlayed, u1 et u3 ; pas de boucle)" "{\"before\":$ec_before,\"after\":$ec_after}" test "$((ec_after-ec_before))" -le 2
  nap 6
  j2=$(journal "Propagation"); n=$(jcount "$j2" "$pl" Propagation)
  ck I24.once "une seule vague de propagation (pas de repropagation après coup)" "$j2" test "$n" -le 1
  drop_item "$pl" "$item"   # média recyclé
}

i25() {
  echo "== I25 — #51 puis v1.2.0 : remplacement conditionnel du message d'aide (V1 et V2 -> V3)"
  # V1, V2 et V3 extraits à l'exécution de src/EmbySharedPlaylist/Reconciliation/HelpText.cs, byte-exacts, jamais recopiés à la
  # main : V1 = la constante contenant « fonction à venir » ; V2 = celle contenant « aussi propagé » ; V3 = « Trois étiquettes ».
  local hf V1 V2 V3 pl_a pl_a2 pl_b pl_c ov ov2 j
  hf="$ROOT/src/EmbySharedPlaylist/Reconciliation/HelpText.cs"
  [[ -f $hf ]] || die "HelpText.cs introuvable ($hf)"
  help_const() { # MARQUEUR -> texte de la constante HelpText qui le contient
    python3 - "$hf" "$1" <<'PY'
import re, sys
src = open(sys.argv[1], encoding='utf-8').read()
# un « ; » peut figurer DANS un littéral (V2) : le corps de la constante n'est fait que de littéraux, « + » et espaces
for m in re.finditer(r'public const string \w+\s*=\s*((?:"(?:[^"\\]|\\.)*"|\s|\+)+);', src, re.S):
    segs = re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))
    if not segs:
        continue
    text = ''.join(segs).replace('\\n', '\n')
    if sys.argv[2] in text:
        print(text, end='')
        break
else:
    sys.exit("constante introuvable (marqueur : %s)" % sys.argv[2])
PY
  }
  V1=$(help_const 'fonction à venir') || die "extraction de V1 échouée"
  V2=$(help_const 'aussi propagé') || die "extraction de V2 échouée"
  V3=$(help_const 'Trois étiquettes') || die "extraction de V3 échouée (HelpText.V3 absent : #56 non livré ?)"
  [[ -n $V1 && -n $V2 && -n $V3 ]] || die "V1/V2/V3 vide : vérifier HelpText.cs"

  pl_a=$(shared_pl "SPIKE-I25a" "$(next_media)")
  owner_edit "$pl_a" '[]' '[]' "$(jq -Rn --arg v "$V1" '$v')"
  run_pass || true
  ov=$(overview_of "$pl_a")
  ck I25.replaced "description == V1 exacte => remplacée par V3 (une passe suffit)" "{\"len\":${#ov}}" test "$ov" = "$V3"
  j=$(journal "DescriptionWritten")
  ck I25.journal "DescriptionWritten cause=v1-to-v3 journalisé pour cette playlist" "$j" test "$(jcount "$j" "$pl_a" DescriptionWritten 'v1-to-v3')" -ge 1
  ck I25.nov1v2 "v1-to-v2 n'est plus jamais émis (CHANGELOG v1.2.0)" "$j" test "$(jcount "$j" "$pl_a" DescriptionWritten 'v1-to-v2')" = 0
  ck I25.stillhelp "V3 mentionne les TROIS étiquettes" "{\"ov\":$(jq -Rn --arg v "$ov" '$v')}" \
    bash -c '[[ $0 == *remove-si-lu=OUI* && $0 == *propager-lu=OUI* && $0 == *propager-avancement=OUI* ]]' "$ov"
  ck I25.novenir "« fonction à venir » n'apparaît plus" "null" bash -c '[[ $0 != *"fonction à venir"* ]]' "$ov"
  run_pass || true
  ck I25.idempotent "une 2e passe ne change plus rien (pas de boucle)" "null" test "$(overview_of "$pl_a")" = "$ov"

  pl_a2=$(shared_pl "SPIKE-I25a2" "$(next_media)")
  owner_edit "$pl_a2" '[]' '[]' "$(jq -Rn --arg v "$V2" '$v')"
  run_pass || true
  ov2=$(overview_of "$pl_a2")
  ck I25.v2replaced "description == V2 exacte (v0.3.0-v1.1.0) => remplacée par V3" "null" test "$ov2" = "$V3"
  j=$(journal "DescriptionWritten"); ck I25.v2journal "DescriptionWritten cause=v2-to-v3 journalisé pour cette playlist" "$j" test "$(jcount "$j" "$pl_a2" DescriptionWritten 'v2-to-v3')" -ge 1

  pl_b=$(shared_pl "SPIKE-I25b" "$(next_media)")
  owner_edit "$pl_b" '[]' '[]' '"Ma description personnelle, différente de V1"'
  run_pass || true
  ck I25.untouched "description modifiée par le propriétaire : jamais touchée" "null" test "$(overview_of "$pl_b")" = "Ma description personnelle, différente de V1"

  pl_c=$(shared_pl "SPIKE-I25c" "$(next_media)")
  owner_edit "$pl_c" '[]' '[]' "$(jq -Rn --arg v "$V3" '$v')"   # déjà le nouveau texte
  run_pass || true
  ck I25.alreadyV3 "description déjà égale à V3 : inchangée" "null" test "$(overview_of "$pl_c")" = "$V3"
}

i26() {
  echo "== I26 — régression complète I0-I17 (retrait, propager-lu requis depuis v1.2.0) : ne doit pas casser"
  local script="$INT_DIR/20-etiquettes-retrait.sh"
  [[ -x $script ]] || die "20-etiquettes-retrait.sh introuvable ou non exécutable"
  if INT_NESTED=1 bash "$script"; then
    rec I26 OK "I0-I17 rejoués : tous verts" "null"
  else
    rec I26 KO "I0-I17 rejoués : au moins un KO (voir sa propre sortie/JSON)" "null"
  fi
}

ALL=(I18 I19 I20 I21 I22 I23 I24 I25 I26)
if [[ ${#WANT[@]} -eq 0 ]]; then WANT=("${ALL[@]}"); fi
for s in "${WANT[@]}"; do
  fn=$(tr 'A-Z' 'a-z' <<<"$s")
  declare -F "$fn" >/dev/null || die "scénario inconnu : $s"
  "$fn"
done

check_no_test_sessions   # #61 : aucune session test_u* en lecture
echo "== Comptes protégés"
compare_protected "fin des scénarios" "${TEST_USERS[@]}" && rec PROTECTED OK "admin, cyril, user2 inchangés" || rec PROTECTED KO "comptes protégés modifiés" "null"

write_out false; DONE=1
echo
echo "== Bilan (détail : $OUT_FILE)"
jq -r '.summary|"  OK=\(.ok) KO=\(.ko) SKIP=\(.skip)"' "$OUT_FILE"
[[ $FAILS == 0 ]] || { echo "ECHEC : $FAILS assertion(s) KO" >&2; exit 1; }
echo "Toutes les assertions exécutées sont OK."
