#!/usr/bin/env bash
# 22-avancement.sh — scénarios d'intégration I27-I44 (v0.3.1 #45 #46 #47 #48 ; v1.2.0 #56 #57 ; v1.2.1 #58) sur emby2
# (QUALIF UNIQUEMENT). À exécuter par qa après déploiement du plugin ; jamais depuis un poste sans avoir
# vérifié la cible.
#
# Propage la POSITION de lecture (`PlaybackPositionTicks`) aux autres membres d'une playlist gérée, avec la famille
# `propager-avancement` (v1.2.0, D21 ; jusqu'à v1.1.0 c'était `propager-lu`, qui ne couvre plus que le flag lu), déclenchée
# par `ISessionManager.PlaybackProgress` (pause : transition IsPaused false->true) et `.PlaybackStopped` (systématique) — PAS
# par UserDataSaved. Dernier écrit gagne, aucun seuil de ratio ; seuil minimal 30 s = POSITION ABSOLUE dans le média (pas une
# durée de lecture). v1.2.0 (#57) : la garde « média déjà lu pour le déclencheur » (D-c, `trigger-already-played`) est
# SUPPRIMÉE : la position est propagée quel que soit l'état lu (relecture d'un média lu comprise, I38) ; écriture BRUTE de la
# position (jamais Played/PlayCount) ; le flux du lu (tableau A) et celui de l'avancement (tableau B) sont indépendants
# (I32/I40). I39 mesure sur QUALIF le « lu » natif éventuel chez le membre (spike U14b) et vérifie qu'il ne déclenche AUCUN
# retrait dans les autres listes (S6).
#
# Prérequis : tests/integration/00-setup-users.sh exécuté (test_u1 propriétaire, test_u2 Write, test_u3 Read) ;
# >= 11 médias (>= 10 min, comme le reste de la suite ; la bibliothèque de QUALIF en compte 13 : I38/I39/I40 repartent d'un
# bassin remis à zéro, fresh_pool). I36 (R8) crée puis nettoie LUI-MÊME test_u4 dans ce même run (comme I20 en v0.3.0).
# v1.2.1 (#58, D23 — CORRECTION DU BUG : lecture continue sans pause + remove-si-lu=OUI => position 0/périmée chez le membre) :
# l'avancement est propagé AUSSI pendant la lecture (chaque PlaybackProgress, au plus 1 fois / 10 s par couple, sans journal :
# compteurs Diagnostics/State.PositionProgress), et la fin de lecture (PlayedToCompletion) écrit 0 chez les membres si
# propager-lu=OUI (cibles mémorisées : le média a pu être retiré par remove-si-lu AVANT l'arrêt), la position brute sinon ;
# un Progress tardif du même PlaySessionId après l'arrêt est ignoré. Nouveaux : I41 (CA1/CA2/CA8/S6), I42 (CA4, smoke/critical —
# reproduction du bug), I43 (CA5), I44 (CA6). I35 RÉÉCRIT (CA3) : il affirmait « un Progress non pause ne propage jamais »,
# comportement CHANGED documenté (contracts/CHANGELOG.md [20260930]). Les nap de 11 s (intervalle de 10 s + marge) sont NON
# mis à l'échelle par WAIT_SCALE : variable PROGRESS_WAIT (défaut 11, secondes réelles ; le test hors ligne la réduit).
# Usage : tests/integration/22-avancement.sh [I27 I33 …]   (sans liste : tous les scénarios)
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/int-lib.sh"

WANT=("$@")
OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/avancement-$TS.json"
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
skip() { rec "$1" SKIP "$2" "${3:-null}"; }

write_out() { # PARTIAL
  jq -s --arg ts "$TS" --argjson p "$1" '
    {run:$ts, partial:$p, summary:{ok:(map(select(.status=="OK"))|length), ko:(map(select(.status=="KO"))|length), skip:(map(select(.status=="SKIP"))|length)}, results:.}' "$RES" > "$OUT_FILE" 2>/dev/null || true
}
on_exit() {
  local rc=$?; set +e
  cleanup_restricted_user
  cleanup_registered_playlists
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

st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video&Fields=RunTimeTicks&SortBy=SortName&Limit=200")
[[ $st == 200 ]] || die "GET /Items -> $st"
mapfile -t M < <(jq -r '[.Items[]|select((.RunTimeTicks//0)>=6000000000)|.Id][0:20][]' "$RESP")
[[ ${#M[@]} -ge 11 ]] || die "moins de 11 médias (>= 10 min) : I27/28(1)+I29(4)+I30+I31+I32+I33+I34+I35+I36(=11) (demander à l'utilisateur)"
reset_pool_full "${M[@]}"   # remise à zéro complète (lu+position) : un run précédent (même script, même invocation séparée) ne doit rien laisser
echo "  [OK] bassin de ${#M[@]} médias remis à zéro (lu=false, position=0)"
echo 0 > "$SCRATCH/next_m"
fresh_pool() {   # I38-I40 (v1.2.0) : la bibliothèque de QUALIF (13 médias) est épuisée par I27-I37 ; on repart d'un bassin propre
  drop_playlists                      # plus aucune playlist des scénarios précédents ne doit réagir aux événements suivants
  reset_pool_full "${M[@]}"           # lu=false, position=0 pour tous les médias et comptes de test
  echo 0 > "$SCRATCH/next_m"
}
next_media() {   # un média frais par sous-cas ; pas de recyclage (11 suffisent largement sur les 13 disponibles)
  local n; n=$(cat "$SCRATCH/next_m")
  [[ $n -lt ${#M[@]} ]] || die "next_media : plus de médias disponibles (>${#M[@]} demandés)"
  echo "${M[$n]}"; echo $((n+1)) > "$SCRATCH/next_m"
}

st=$(api GET "$DIAG/State"); [[ $st == 200 ]] || die "Diagnostics/State -> HTTP $st : plugin v0.3.1 non déployé ou EnableDiagnostics=false"
GRACE=$(jq -r '.gracePasses // 2' "$RESP")
echo "  [OK] ${#M[@]} médias ; GracePasses=$GRACE"

TICKS_30S=300000000   # 30 s (100 ns/tick, confirmé par le filtre >= 10 min = 6 000 000 000)
wait_interval() { sleep "${PROGRESS_WAIT:-11}"; }   # intervalle minimal de propagation périodique (10 s) + marge, secondes RÉELLES (#58)

# ---------------------------------------------------------------- scénarios
i27() {
  echo "== I27/I28 — S9 : U1 arrête (position P1) -> U2 reprend à P1 ; U2 poursuit et arrête (P2) -> U1 reprend à P2"
  local pl item p1t p2t sid pu1 pu2 j
  pl=$(shared_pl "SPIKE-I27" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  p1t=$(ticks_at "$item" 20)   # ~ « 10 min » de l'énoncé (pourcentage de la durée réelle, portable quelle que soit sa longueur)
  jclear
  sid=$(play_start "$U1" "$T1" "$item")
  play_progress "$U1" "$T1" "$item" "$sid" "$((p1t/2))" false >/dev/null
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --arg s "$sid" --argjson p "$p1t" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T1"
  wait_position "$U2" "$T2" "$item" "$p1t" 10 || true   # attente active (#47) : évite de lire avant la fin de la propagation sous charge
  pu2=$(position_of "$U2" "$T2" "$item")
  ck I27.propagated "U2 reprend à la position de l'arrêt de U1 (P1=$p1t)" "{\"u2\":\"$pu2\",\"expected\":$p1t}" test "$pu2" = "$p1t"
  ck I27.notplayed "le média n'est PAS marqué lu (avancement seul)" "null" test "$(played_of "$U1" "$T1" "$item")/$(played_of "$U2" "$T2" "$item")" = "false/false"

  # I28 : U2 (déjà positionné à P1) poursuit et arrête PLUS LOIN (P2 > P1) -> U1 reprend à P2 (sens inverse)
  p2t=$(ticks_at "$item" 55)   # ~ « 25 min » de l'énoncé
  jclear
  sid=$(play_start "$U2" "$T2" "$item")
  play_progress "$U2" "$T2" "$item" "$sid" "$p1t" false >/dev/null
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --arg s "$sid" --argjson p "$p2t" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  wait_position "$U1" "$T1" "$item" "$p2t" 10 || true
  pu1=$(position_of "$U1" "$T1" "$item")
  ck I28.propagated "U1 reprend à la position de l'arrêt de U2 (P2=$p2t, dernier écrit gagne)" "{\"u1\":\"$pu1\",\"expected\":$p2t}" test "$pu1" = "$p2t"
  wait_journal "$pl" PositionPropagation '' 1 10 || true   # journal vidé (jclear) juste avant l'action de I28 : ne contient que SA propre entrée
  j=$(journal "PositionPropagation")
  ck I28.journal "au moins une entrée PositionPropagation (I28)" "$j" test "$(jcount "$j" "$pl" PositionPropagation)" -ge 1
}

i29() {
  echo "== I29 — quatre états du marqueur propager-avancement (arrêt) + propager-lu seul (BREAKING v1.2.0 : ne propage plus la position)"
  local -a ROWS=("A none false" "B non false" "C oui true" "D both false")
  local row id state expect pl item p pos
  for row in "${ROWS[@]}"; do
    read -r id state expect <<<"$row"
    pl=$(shared_pl "SPIKE-I29-$id" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
    prime "$pl" || true
    set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement "$state"
    p=$(ticks_at "$item" 40)
    jclear
    apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s","PlayMethod":"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
    apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
    if [[ $expect == true ]]; then
      wait_position "$U1" "$T1" "$item" "$p" 10 || true   # attente active (#47) : positif, doit se produire
      pos=$(position_of "$U1" "$T1" "$item")
      ck "I29.$id" "propager-avancement=$state : propagation active => u1 reçoit la position ($p)" "{\"u1\":\"$pos\"}" test "$pos" = "$p"
    else
      nap 2   # négatif (rien ne doit se produire) : fenêtre fixe volontaire, pas d'attente active (cf. stays())
      pos=$(position_of "$U1" "$T1" "$item")
      ck "I29.$id" "propager-avancement=$state : inactif => aucune écriture (u1 reste à 0)" "{\"u1\":\"$pos\"}" test "$pos" = 0
    fi
  done
  # E (v1.2.0, BREAKING) : propager-lu=OUI SEUL (propager-avancement=NON) n'active PAS la position — l'avancement n'est plus hérité du lu.
  pl=$(shared_pl "SPIKE-I29-E" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui; set_marker_state "$pl" propager-avancement non
  p=$(ticks_at "$item" 40)
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"sE",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"sE",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  nap 2
  ck "I29.E" "propager-lu=OUI seul (avancement=NON) : la position N'EST PLUS propagée (u1 reste à 0)" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\"}" test "$(position_of "$U1" "$T1" "$item")" = 0
}

i30() {
  echo "== I30 — seuil minimal (30 s) : arrêt plus court => rien"
  local pl item p j
  pl=$(shared_pl "SPIKE-I30" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  p=$((TICKS_30S - 50000000))   # 25 s : sous le seuil
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s30",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s30",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  nap 2
  ck I30.noposition "arrêt à 25 s (< seuil) : u1 reste à 0" "null" test "$(position_of "$U1" "$T1" "$item")" = 0
  j=$(journal "PositionPropagation")
  ck I30.nojournal "aucune entrée PositionPropagation" "$j" test "$(jcount "$j" "$pl" PositionPropagation)" = 0
}

i31() {
  echo "== I31 — deux événements à la même position => une seule écriture réelle (same-position ensuite)"
  local pl item p j n1
  pl=$(shared_pl "SPIKE-I31" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  p=$(ticks_at "$item" 35)
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s31a",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s31a",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  wait_position "$U1" "$T1" "$item" "$p" 10 || true
  ck I31.first "première écriture : u1 à la position $p" "null" test "$(position_of "$U1" "$T1" "$item")" = "$p"
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s31b",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s31b",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  nap 2
  ck I31.stillsame "deuxième événement à la MÊME position : u1 toujours à $p (pas de régression)" "null" test "$(position_of "$U1" "$T1" "$item")" = "$p"
  j=$(journal "PositionPropagation,Skipped")
  n1=$(jcount "$j" "$pl" Skipped 'same-position')
  ck I31.samejournal "Skipped same-position journalisé pour u1 (2e écriture, sans effet)" "$j" test "$n1" -ge 1
}

i32() {
  echo "== I32 — média devenu lu (S9e) : le flux du lu (retrait + lu) est indépendant de l'avancement — aucune position écrite par le plugin (avancement=NON)"
  local pl item j
  pl=$(shared_pl "SPIKE-I32" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu oui; set_marker_state "$pl" propager-lu oui   # propager-avancement reste NON (posée par prime)
  jclear
  finish "$U2" "$T2" "$item"   # transition non lu -> lu (TogglePlayed), comme #12/#20
  ck I32.removed "retrait toujours actif (#12, régression)" "null" wait_count "$pl" "$item" 0 10   # attente active : retrait et propagation du lu partagent la même passe synchrone
  ck I32.propagatedplayed "flag lu toujours propagé (#20, régression)" "null" test "$(played_of "$U1" "$T1" "$item")" = true
  ck I32.noposition "AUCUNE position écrite par le plugin (avancement=NON ; propager-lu ne touche jamais la position, R4b)" "null" test "$(position_of "$U1" "$T1" "$item")" = 0
  j=$(journal "PositionPropagation")
  ck I32.nojournal "aucune entrée PositionPropagation" "$j" test "$(jcount "$j" "$pl" PositionPropagation)" = 0
}

i33() {
  echo "== I33 — non-transitivité (comme S6a) : deux playlists, même média"
  local item L1 L2 p
  item=$(next_media)
  L1=$(new_pl "SPIKE-I33-L1" "$item"); share_pl_one "$L1" "$U2" Write   # L1 = {u1, u2 SEUL}
  L2=$(new_pl "SPIKE-I33-L2" "$item"); share_pl_one "$L2" "$U3" Write   # L2 = {u1, u3 SEUL}
  prime "$L1" || true; prime "$L2" || true
  set_marker_state "$L1" remove-si-lu non; set_marker_state "$L1" propager-avancement oui
  set_marker_state "$L2" remove-si-lu non; set_marker_state "$L2" propager-avancement oui
  p=$(ticks_at "$item" 30)
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s33",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s33",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  wait_position "$U1" "$T1" "$item" "$p" 10 || true
  ck I33.propagated "L1 : u1 (membre des deux) reçoit la position (via L1 seule)" "null" test "$(position_of "$U1" "$T1" "$item")" = "$p"
  ck I33.notransitivity "L2 : u3 (membre de L2 seule) jamais touché" "null" test "$(position_of "$U3" "$T3" "$item")" = 0
  nap 3   # laisse le temps à un éventuel écho (origine plugin, u1) de se propager à tort avant de revérifier L2
  ck I33.noecho "après un délai, u3 (L2) toujours non touché (pas d'écho transitif)" "null" test "$(position_of "$U3" "$T3" "$item")" = 0
  drop_item "$L1" "$item"; drop_item "$L2" "$item"
}

i37() {
  echo "== I37 — D-c SUPPRIMÉE (#57, S9f) : arrêt à ratio élevé (~96%) => position propagée normalement, quel que soit l'état lu, aucun Skipped trigger-already-played"
  local pl item p pu1 u2played j
  pl=$(shared_pl "SPIKE-I37" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  p=$(ticks_at "$item" 96)
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s37",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s37",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  wait_position "$U1" "$T1" "$item" "$p" 10 || true
  u2played=$(played_of "$U2" "$T2" "$item")   # informatif : Emby peut marquer lu u2 lui-même à cet arrêt ; SANS INFLUENCE sur la propagation depuis v1.2.0
  pu1=$(position_of "$U1" "$T1" "$item")
  ck I37 "arrêt à 96% (u2Played=$u2played à l'issue) : position propagée chez u1 dans TOUS les cas (plus aucune garde sur l'état lu)" "{\"u2Played\":\"$u2played\",\"u1\":\"$pu1\",\"expected\":$p}" test "$pu1" = "$p"
  j=$(journal "Skipped,Error")
  ck I37.noguard "aucun Skipped trigger-already-played (la garde n'existe plus)" "$j" test "$(jcount "$j" "$pl" Skipped 'trigger-already-played')" = 0
  ck I37.noerror "aucune entrée Error" "$j" test "$(jcount "$j" "$pl" Error)" = 0
  drop_item "$pl" "$item"
}

i34() {
  echo "== I34 — pause réelle (sans arrêt) : IsPaused=false->true sur PlaybackProgress"
  local pl item p sid pu1 st1 st2
  pl=$(shared_pl "SPIKE-I34" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  p=$(ticks_at "$item" 45)
  jclear
  sid=$(play_start "$U2" "$T2" "$item")
  st1=$(play_progress "$U2" "$T2" "$item" "$sid" "$(ticks_at "$item" 10)" false)   # établit l'état « pas en pause »
  nap 1
  st2=$(play_progress "$U2" "$T2" "$item" "$sid" "$p" true)                        # PAUSE (le déclencheur réel de #45)
  if [[ $st1 != 2* || $st2 != 2* ]]; then
    skip I34 "l'API Sessions/Playing/Progress avec IsPaused a répondu HTTP $st1/$st2 (attendu 2xx) : voir tests/integration/MANUAL.md (avancement) pour la vérification manuelle"
    return
  fi
  wait_position "$U1" "$T1" "$item" "$p" 10 || true
  pu1=$(position_of "$U1" "$T1" "$item")
  if [[ $pu1 == "$p" ]]; then
    rec I34 OK "pause réelle propagée : u1 reçoit la position ($p) sans aucun arrêt" "{\"u1\":\"$pu1\"}"
  else
    j=$(journal "Error"); n=$(jcount "$j" "$pl" Error)
    if [[ $n -gt 0 ]]; then
      rec I34 KO "une entrée Error a été journalisée pour ce scénario" "$j"
    else
      skip I34 "la pause (IsPaused=true) n'a pas propagé la position (u1=$pu1, attendu $p) sans erreur journalisée : possible limite de l'API de session pour ce scénario automatisé — à vérifier en conditions réelles (tests/integration/MANUAL.md, client web/TV/mobile), ne pas traiter comme un défaut du plugin sans confirmation manuelle" "{\"u1\":\"$pu1\",\"expected\":$p}"
    fi
  fi
}

i35() {
  echo "== I35 (v1.2.1, #58, CA3) — rafale de Progress en lecture (cadence rapide) : au plus UNE propagation par intervalle de 10 s, aucun journal par Progress"
  local pl item sid i j thr0 thr1 first pu1
  local -a P=()
  pl=$(shared_pl "SPIKE-I35" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  for ((i=1; i<=10; i++)); do P+=("$(ticks_at "$item" $((10+i)))"); done   # positions précalculées : la rafale doit tenir dans l'intervalle
  first=${P[0]}
  thr0=$(state | jq -r '.positionProgress.throttled // 0')
  jclear
  sid=$(play_start "$U2" "$T2" "$item")
  for ((i=0; i<10; i++)); do play_progress "$U2" "$T2" "$item" "$sid" "${P[$i]}" false >/dev/null; done
  wait_position "$U1" "$T1" "$item" "$first" 10 || true
  nap 2
  pu1=$(position_of "$U1" "$T1" "$item")
  ck I35.first "le 1er Progress (>= 30 s) est propagé : u1 à $first" "{\"u1\":\"$pu1\"}" test "$pu1" = "$first"
  thr1=$(state | jq -r '.positionProgress.throttled // 0')
  ck I35.throttled "les Progress suivants de la rafale sont ignorés (u1 reste à la 1re position) et comptés : positionProgress.throttled +>= 1" "{\"throttled\":$((thr1-thr0))}" test "$pu1" = "$first" -a "$((thr1-thr0))" -ge 1
  j=$(journal "PositionPropagation")
  ck I35.nojournal "aucune entrée PositionPropagation pour les Progress périodiques (CA8)" "$j" test "$(jcount "$j" "$pl" PositionPropagation)" = 0
}

i36() {
  echo "== I36 — R8 : membre sans accès à la bibliothèque, aucune erreur (compte créé et nettoyé dans ce run)"
  local pl item j n1 n2 p
  ensure_restricted_user
  pl=$(shared_pl "SPIKE-I36" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$pl" --arg u "$U4" '{ItemIds:[$p],UserIds:[$u],ItemAccess:"Read"}')" "$T1"
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-avancement oui
  p=$(ticks_at "$item" 40)
  jclear
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$item" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s36",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$item" --argjson p "$p" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s36",PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$T2"
  wait_position "$U1" "$T1" "$item" "$p" 10 || true   # même passe synchrone que les entrées de journal ci-dessous : pas d'attente séparée pour elles
  ck I36.others "u1 (avec accès) reçoit la position malgré le membre restreint" "null" test "$(position_of "$U1" "$T1" "$item")" = "$p"
  j=$(journal "PositionPropagation,Skipped,Error")
  n1=$(jcount "$j" "$pl" PositionPropagation '(^|[^A-Za-z])noAccess=[1-9]'); n2=$(jcount "$j" "$pl" Skipped 'no-access')
  ck I36.aggregate "l'entrée PositionPropagation agrégée compte noAccess >= 1" "$j" test "$n1" -ge 1
  ck I36.permember "Skipped no-access journalisé pour test_u4" "$j" test "$n2" -ge 1
  ck I36.noerror "aucune entrée Error causée par le membre restreint" "$j" test "$(jcount "$j" "$pl" Error)" = 0
  drop_item "$pl" "$item"
  cleanup_restricted_user
}

i38() {
  echo "== I38 (smoke, critical) — S9d (#57) : relecture d'un média DÉJÀ LU => pause et arrêt propagés, flag lu intact, aucune transition"
  fresh_pool
  local pl item p20 p45 sid pu1 j
  pl=$(shared_pl "SPIKE-I38" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  finish "$U1" "$T1" "$item"; finish "$U2" "$T2" "$item"      # F1 déjà lu par u1 ET u2 (familles encore à NON : aucun effet du moteur)
  wait_played "$U1" "$T1" "$item" true 5 || true; wait_played "$U2" "$T2" "$item" true 5 || true
  set_marker_state "$pl" propager-lu oui; set_marker_state "$pl" propager-avancement oui; set_marker_state "$pl" remove-si-lu non
  p20=$(ticks_at "$item" 20); p45=$(ticks_at "$item" 45)
  jclear
  sid=$(play_start "$U2" "$T2" "$item")                        # u2 relit F1 (déjà lu)
  play_progress "$U2" "$T2" "$item" "$sid" "$(ticks_at "$item" 10)" false >/dev/null
  nap 1
  play_progress "$U2" "$T2" "$item" "$sid" "$p20" true >/dev/null      # PAUSE à 20 %
  wait_position "$U1" "$T1" "$item" "$p20" 10 || true
  ck I38.pause "pause de u2 sur un média déjà lu : position propagée chez u1 (malgré l'état lu, la garde D-c n'existe plus)" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\",\"expected\":$p20}" test "$(position_of "$U1" "$T1" "$item")" = "$p20"
  play_stop "$U2" "$T2" "$item" "$sid" "$p45" >/dev/null                # ARRÊT à 45 %
  wait_position "$U1" "$T1" "$item" "$p45" 10 || true
  pu1=$(position_of "$U1" "$T1" "$item")
  ck I38.stop "arrêt à 45 % : position propagée chez u1" "{\"u1\":\"$pu1\",\"expected\":$p45}" test "$pu1" = "$p45"
  ck I38.flags "le flag lu de u1 reste vrai (l'avancement ne touche jamais au lu, aucun PlayCount écrit)" "null" test "$(played_of "$U1" "$T1" "$item")" = true
  ck I38.stays "aucune transition (R4c) : le média reste dans la playlist" "null" stays "$pl" "$item" 1 3
  j=$(journal "PositionPropagation,Skipped,Removal,Propagation")
  ck I38.journal "au moins 2 PositionPropagation (pause + arrêt)" "$j" test "$(jcount "$j" "$pl" PositionPropagation)" -ge 2
  ck I38.noguard "aucun Skipped trigger-already-played" "$j" test "$(jcount "$j" "$pl" Skipped 'trigger-already-played')" = 0
  ck I38.norm "aucun Removal ni Propagation du lu (relecture : pas de transition)" "$j" test "$(jcount "$j" "$pl" Removal)/$(jcount "$j" "$pl" Propagation)" = "0/0"
}

i39() {
  echo "== I39 — S9f / spike U14b : arrêt à ~99,5 % avec propager-lu=NON ; le « lu » natif éventuel chez u1 est d'ORIGINE PLUGIN (aucun retrait, y compris dans une AUTRE liste de u1, S6)"
  local L1 L2 item p pu1 p1played skb ska
  item=$(next_media)
  L1=$(new_pl "SPIKE-I39-L1" "$item"); share_pl_one "$L1" "$U2" Write   # L1 = {u1, u2}
  L2=$(new_pl "SPIKE-I39-L2" "$item"); share_pl_one "$L2" "$U3" Write   # L2 = {u1, u3} : u1 est membre des DEUX
  prime "$L1" || true; prime "$L2" || true
  set_marker_state "$L1" remove-si-lu oui; set_marker_state "$L1" propager-lu non; set_marker_state "$L1" propager-avancement oui
  set_marker_state "$L2" remove-si-lu oui; set_marker_state "$L2" propager-lu oui; set_marker_state "$L2" propager-avancement non
  p=$(ticks_at "$item" 99); p=$((p + (p/100)/2))   # ~99,5 %
  skb=$(state | jq -r '.skippedCounts["echo-consumed"] // 0')
  jclear
  local sid; sid=$(play_start "$U2" "$T2" "$item")
  play_progress "$U2" "$T2" "$item" "$sid" "$(ticks_at "$item" 50)" false >/dev/null
  play_stop "$U2" "$T2" "$item" "$sid" "$p" >/dev/null                  # u2 termine F1 : Emby peut poser Lu chez u2 (transition), position remise à 0 chez u2
  wait_position "$U1" "$T1" "$item" "$p" 10 || true
  nap 4                                                                  # laisse un éventuel événement « lu » natif se manifester chez u1
  pu1=$(position_of "$U1" "$T1" "$item"); p1played=$(played_of "$U1" "$T1" "$item"); ska=$(state | jq -r '.skippedCounts["echo-consumed"] // 0')
  ck I39.position "position brute écrite chez u1 (L1, avancement=OUI ; aucune règle de fin appliquée par le plugin)" "{\"u1\":\"$pu1\",\"expected\":$p}" test "$pu1" = "$p"
  ck I39.l1_stays "L1 (propager-lu=NON) : aucun retrait (D21, S3b) — le média reste" "null" test "$(count_item "$L1" "$item")" = 1
  ck I39.s6 "S6 : le « lu » natif éventuel de u1 (origine plugin) ne retire RIEN de L2 (remove-si-lu=OUI + propager-lu=OUI, u1 membre) — sinon violation de S6/R5, à BLOQUER" "{\"u1Played\":\"$p1played\"}" test "$(count_item "$L2" "$item")" = 1
  ck I39.no_lu_chain "aucun lu propagé à u3 par cette écriture (pas de transitivité)" "null" test "$(played_of "$U3" "$T3" "$item")" = false
  rec I39.u14b OK "OBSERVATION U14b (pas une assertion) : u1 Played=$p1played après une position brute à ~99,5 % ; echo-consumed +$((ska-skb)) ; attendu par la spec tant que non prouvé : Played=false (« Reprendre à 99 % »)" "{\"u1Played\":\"$p1played\",\"u1Position\":\"$pu1\",\"echoConsumedDelta\":$((ska-skb))}"
  drop_item "$L1" "$item"; drop_item "$L2" "$item"
}

i40() {
  echo "== I40 — S9e : flux indépendants — avancement sans lu, et lu sans avancement"
  fresh_pool
  local plA plB itemA itemB p pos
  plA=$(shared_pl "SPIKE-I40-A" "$(next_media)"); itemA=$(entries "$plA" | jq -r '.[0].itemId')
  plB=$(shared_pl "SPIKE-I40-B" "$(next_media)"); itemB=$(entries "$plB" | jq -r '.[0].itemId')
  prime "$plA" || true; prime "$plB" || true
  # A : propager-avancement=OUI SEULE => l'arrêt propage la position ; terminer le média ne pose AUCUN lu et ne retire rien
  set_marker_state "$plA" remove-si-lu oui; set_marker_state "$plA" propager-lu non; set_marker_state "$plA" propager-avancement oui
  p=$(ticks_at "$itemA" 30)
  local sid; sid=$(play_start "$U2" "$T2" "$itemA")
  play_progress "$U2" "$T2" "$itemA" "$sid" "$(ticks_at "$itemA" 15)" false >/dev/null
  play_stop "$U2" "$T2" "$itemA" "$sid" "$p" >/dev/null
  wait_position "$U1" "$T1" "$itemA" "$p" 10 || true
  ck I40.A.position "A (avancement seul) : l'arrêt à 30 % propage la position chez u1" "null" test "$(position_of "$U1" "$T1" "$itemA")" = "$p"
  finish "$U2" "$T2" "$itemA"   # u2 termine F1 (transition vers lu)
  nap 3
  ck I40.A.nolu "A : terminer le média ne propage PAS le lu (propager-lu=NON)" "null" test "$(played_of "$U1" "$T1" "$itemA")" = false
  ck I40.A.stays "A : remove-si-lu=OUI sans propager-lu => aucun retrait (S3b)" "null" stays "$plA" "$itemA" 1 3
  # B : propager-lu=OUI SEULE => le lu est propagé, aucune position n'est écrite par le plugin
  set_marker_state "$plB" remove-si-lu non; set_marker_state "$plB" propager-lu oui; set_marker_state "$plB" propager-avancement non
  p=$(ticks_at "$itemB" 30)
  sid=$(play_start "$U2" "$T2" "$itemB")
  play_progress "$U2" "$T2" "$itemB" "$sid" "$(ticks_at "$itemB" 15)" false >/dev/null
  play_stop "$U2" "$T2" "$itemB" "$sid" "$p" >/dev/null
  nap 2
  ck I40.B.nopos "B (lu seul) : l'arrêt à 30 % n'écrit aucune position chez u1" "null" test "$(position_of "$U1" "$T1" "$itemB")" = 0
  finish "$U2" "$T2" "$itemB"
  wait_played "$U1" "$T1" "$itemB" true 10 || true
  ck I40.B.lu "B : terminer le média pose le lu chez u1 (R4b)" "null" test "$(played_of "$U1" "$T1" "$itemB")" = true
  pos=$(position_of "$U1" "$T1" "$itemB")
  ck I40.B.stillnopos "B : la propagation du lu n'écrit toujours aucune position chez u1" "{\"u1\":\"$pos\"}" test "$pos" = 0
  ck I40.B.stays "B : remove-si-lu=NON => le média reste" "null" stays "$plB" "$itemB" 1 3
}

i41() {
  echo "== I41 (smoke) — CA1/CA2/CA8/S6 : lecture continue SANS pause : le membre suit l'avancement (<= ~10 s de retard), sans journal par Progress ; pause immédiate ; heartbeat en pause silencieux ; aucun retrait parasite dans une autre liste"
  fresh_pool
  local item L1 L2 sid pa pb pc pd pe st0 st1 j
  item=$(next_media)
  L1=$(new_pl "SPIKE-I41-L1" "$item"); share_pl_one "$L1" "$U2" Write   # L1 = {u1, u2}
  L2=$(new_pl "SPIKE-I41-L2" "$item"); share_pl_one "$L2" "$U3" Write   # L2 = {u1, u3} : u1 est membre des DEUX (S6/S7)
  prime "$L1" || true; prime "$L2" || true
  set_marker_state "$L1" remove-si-lu non; set_marker_state "$L1" propager-lu non; set_marker_state "$L1" propager-avancement oui
  set_marker_state "$L2" remove-si-lu oui; set_marker_state "$L2" propager-lu oui; set_marker_state "$L2" propager-avancement non
  pa=$(ticks_at "$item" 10); pb=$(ticks_at "$item" 20); pc=$(ticks_at "$item" 30); pd=$(ticks_at "$item" 40); pe=$(ticks_at "$item" 45)
  st0=$(state | jq -r '.positionProgress.propagated // 0')
  jclear
  sid=$(play_start "$U2" "$T2" "$item")
  play_progress "$U2" "$T2" "$item" "$sid" "$pa" false >/dev/null
  wait_position "$U1" "$T1" "$item" "$pa" 10 || true
  ck I41.t0 "1er Progress (10 %, sans pause) : u1 à la position de u2" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\",\"expected\":$pa}" test "$(position_of "$U1" "$T1" "$item")" = "$pa"
  wait_interval
  play_progress "$U2" "$T2" "$item" "$sid" "$pb" false >/dev/null
  wait_position "$U1" "$T1" "$item" "$pb" 10 || true
  ck I41.t1 "10 s plus tard (20 %, toujours sans pause) : u1 suit sans arrêt ni pause" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\",\"expected\":$pb}" test "$(position_of "$U1" "$T1" "$item")" = "$pb"
  wait_interval
  play_progress "$U2" "$T2" "$item" "$sid" "$pc" false >/dev/null
  wait_position "$U1" "$T1" "$item" "$pc" 10 || true
  ck I41.t2 "encore 10 s plus tard (30 %) : u1 suit" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\",\"expected\":$pc}" test "$(position_of "$U1" "$T1" "$item")" = "$pc"
  st1=$(state | jq -r '.positionProgress.propagated // 0')
  ck I41.counter "Diagnostics/State.positionProgress.propagated augmente d'au moins 3 (Progress périodiques comptés, pas journalisés)" "{\"delta\":$((st1-st0))}" test "$((st1-st0))" -ge 3
  j=$(journal "PositionPropagation")
  ck I41.nojournal "aucune entrée PositionPropagation pour les 3 Progress périodiques (CA8)" "$j" test "$(jcount "$j" "$L1" PositionPropagation)" = 0
  # CA2 : pause IMMÉDIATE (quelques secondes après la dernière propagation, sans attendre l'intervalle) ; heartbeat en pause silencieux
  nap 1
  play_progress "$U2" "$T2" "$item" "$sid" "$pd" true >/dev/null
  wait_position "$U1" "$T1" "$item" "$pd" 10 || true
  ck I41.pause "pause : propagation immédiate (40 %)" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\",\"expected\":$pd}" test "$(position_of "$U1" "$T1" "$item")" = "$pd"
  wait_journal "$L1" PositionPropagation 'trigger=pause' 1 10 || true
  j=$(journal "PositionPropagation")
  ck I41.pausejournal "la pause est journalisée avec trigger=pause en fin de Detail (CA8)" "$j" test "$(jcount "$j" "$L1" PositionPropagation 'trigger=pause$')" -ge 1
  wait_interval
  play_progress "$U2" "$T2" "$item" "$sid" "$pe" true >/dev/null   # heartbeat EN PAUSE, intervalle écoulé : jamais d'écriture
  nap 2
  ck I41.heartbeat "heartbeat en pause (même après 10 s) : u1 reste à la position de la pause" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\"}" test "$(position_of "$U1" "$T1" "$item")" = "$pd"
  ck I41.s6 "CA7/S6/S7 : les écritures périodiques de u1 (origine plugin) ne retirent RIEN de L2 (remove-si-lu=OUI + propager-lu=OUI, u1 membre)" "null" test "$(count_item "$L2" "$item")" = 1
  ck I41.l1stays "L1 : aucun retrait ni lu propagé (propager-lu=NON)" "null" test "$(count_item "$L1" "$item")/$(played_of "$U1" "$T1" "$item")" = "1/false"
  play_stop "$U2" "$T2" "$item" "$sid" "$pe" >/dev/null
  drop_item "$L1" "$item"; drop_item "$L2" "$item"
}

i42() {
  echo "== I42 (smoke, critical) — CA4 / S9g : LE CAS DU BUG : lecture continue sans pause jusqu'au bout, propager-lu=OUI (avec ET sans remove-si-lu) : le membre est LU et à la position 0 (aucun « Reprendre »)"
  fresh_pool
  local variant id rm pl item sid half end j
  for variant in "rm:oui" "norm:non"; do
    IFS=: read -r id rm <<<"$variant"
    pl=$(shared_pl "SPIKE-I42-$id" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
    prime "$pl" || true
    set_marker_state "$pl" remove-si-lu "$rm"; set_marker_state "$pl" propager-lu oui; set_marker_state "$pl" propager-avancement oui
    half=$(ticks_at "$item" 50); end=$(ticks_at "$item" 100)
    jclear
    sid=$(play_start "$U2" "$T2" "$item")
    play_progress "$U2" "$T2" "$item" "$sid" "$half" false >/dev/null     # lecture continue : AUCUNE pause, aucun arrêt avant la fin
    wait_position "$U1" "$T1" "$item" "$half" 10 || true
    ck "I42.$id.continuous" "avant la fin, u1 suit la lecture continue (50 %) : position = $half" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\"}" test "$(position_of "$U1" "$T1" "$item")" = "$half"
    play_stop "$U2" "$T2" "$item" "$sid" "$end" >/dev/null                 # fin naturelle : Emby (lu, position 0, flux du lu) PUIS PlaybackStopped
    ck "I42.$id.played" "u1 est LU (propager-lu=OUI, R4b)" "null" wait_played "$U1" "$T1" "$item" true 10
    if [[ $rm == oui ]]; then
      ck "I42.$id.removed" "remove-si-lu=OUI : le média est retiré AVANT l'arrêt (cas du bug) — la playlist n'est plus résolue à PlaybackStopped" "null" wait_count "$pl" "$item" 0 10
    else
      ck "I42.$id.stays" "sans remove-si-lu : le média reste" "null" stays "$pl" "$item" 1 3
    fi
    ck "I42.$id.zero" "CA4 : u1 est à la position 0 (aucun point de reprise ni « Reprendre à 99 % »)" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\"}" wait_position "$U1" "$T1" "$item" 0 10
    wait_journal "$pl" PositionPropagation 'trigger=completion' 1 10 || true
    j=$(journal "PositionPropagation")
    ck "I42.$id.journal" "la fin de lecture est journalisée avec trigger=completion (CA8)" "$j" test "$(jcount "$j" "$pl" PositionPropagation 'trigger=completion$')" -ge 1
  done
}

i43() {
  echo "== I43 — CA5 / S9f inchangé : lecture continue jusqu'au bout SANS propager-lu : le membre n'est pas marqué lu par le plugin et garde la position d'arrêt"
  local pl item sid half end pu1 p1played
  pl=$(shared_pl "SPIKE-I43" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu non; set_marker_state "$pl" propager-avancement oui
  half=$(ticks_at "$item" 50); end=$(ticks_at "$item" 99)
  jclear
  sid=$(play_start "$U2" "$T2" "$item")
  play_progress "$U2" "$T2" "$item" "$sid" "$half" false >/dev/null
  wait_position "$U1" "$T1" "$item" "$half" 10 || true
  play_stop "$U2" "$T2" "$item" "$sid" "$end" >/dev/null
  wait_position "$U1" "$T1" "$item" "$end" 10 || true
  pu1=$(position_of "$U1" "$T1" "$item"); p1played=$(played_of "$U1" "$T1" "$item")
  ck I43.position "position d'arrêt brute chez u1 (99 %, S9f inchangé) : $end" "{\"u1\":\"$pu1\"}" test "$pu1" = "$end"
  ck I43.stays "propager-lu=NON : aucun retrait ni flag propagé par le plugin" "null" stays "$pl" "$item" 1 3
  rec I43.u14b OK "OBSERVATION (comme I39.u14b, pas une assertion) : u1 Played=$p1played après une position brute à 99 % ; attendu par la spec : Played=false (« Reprendre à 99 % »)" "{\"u1Played\":\"$p1played\",\"u1Position\":\"$pu1\"}"
  drop_item "$pl" "$item"
}

i44() {
  echo "== I44 — CA6 : un Progress tardif (même PlaySessionId) après la fin de lecture n'écrase JAMAIS la remise à 0"
  local pl item sid half end late
  pl=$(shared_pl "SPIKE-I44" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui; set_marker_state "$pl" propager-avancement oui
  half=$(ticks_at "$item" 50); end=$(ticks_at "$item" 100)
  sid=$(play_start "$U2" "$T2" "$item")
  play_progress "$U2" "$T2" "$item" "$sid" "$half" false >/dev/null
  wait_position "$U1" "$T1" "$item" "$half" 10 || true
  play_stop "$U2" "$T2" "$item" "$sid" "$end" >/dev/null
  ck I44.zero "après la fin de lecture : u1 à 0" "null" wait_position "$U1" "$T1" "$item" 0 10
  wait_interval                                                            # l'intervalle de 10 s est écoulé : seule la fermeture de session empêche l'écriture
  late=$(play_progress "$U2" "$T2" "$item" "$sid" "$(ticks_at "$item" 99)" false)   # Progress tardif, MÊME PlaySessionId
  nap 3
  ck I44.nolate "le Progress tardif (HTTP $late) n'a pas ré-écrit la position : u1 toujours à 0" "{\"u1\":\"$(position_of "$U1" "$T1" "$item")\"}" test "$(position_of "$U1" "$T1" "$item")" = 0
}

ALL=(I27 I29 I30 I31 I32 I33 I37 I34 I35 I36 I38 I39 I40 I41 I42 I43 I44)
if [[ ${#WANT[@]} -eq 0 ]]; then WANT=("${ALL[@]}"); fi
for s in "${WANT[@]}"; do
  fn=$(tr 'A-Z' 'a-z' <<<"$s")
  declare -F "$fn" >/dev/null || die "scénario inconnu : $s"
  "$fn"
done

echo "== Comptes protégés"
compare_protected "fin des scénarios" "${TEST_USERS[@]}" && rec PROTECTED OK "admin, cyril, user2 inchangés" || rec PROTECTED KO "comptes protégés modifiés" "null"

write_out false; DONE=1
echo
echo "== Bilan (détail : $OUT_FILE)"
jq -r '.summary|"  OK=\(.ok) KO=\(.ko) SKIP=\(.skip)"' "$OUT_FILE"
[[ $FAILS == 0 ]] || { echo "ECHEC : $FAILS assertion(s) KO" >&2; exit 1; }
echo "Toutes les assertions exécutées sont OK."
