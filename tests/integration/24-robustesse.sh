#!/usr/bin/env bash
# 24-robustesse.sh — scénarios d'intégration I41-I48 de la v0.5.0 (issues #30 #31 #33 #34) sur emby2
# (QUALIF UNIQUEMENT). À exécuter par qa après déploiement du plugin ; jamais depuis un poste sans avoir
# vérifié la cible.
#
# Ce lot est majoritairement une VÉRIFICATION (l'investigation du planner conclut que l'architecture existante
# couvre déjà le cycle de vie : relecture fraîche systématique, R8 centralisé, aucun état persisté) — pas un
# nouveau moteur. Scénarios : fin de partage en cours d'usage (#30 : membre retiré, playlist supprimée,
# propriétaire supprimé — jamais sur un compte réel, toujours un compte de test dédié créé/supprimé ici), média
# inexistant/playlist vide (#31, D-d : simulation sans toucher au contenu réel), passage à l'échelle — 5+
# membres, 10+ playlists gérées simultanément (#33), redémarrage en cours d'usage à plus grande échelle
# (#34, extension d'I10 v0.2.0).
#
# Prérequis : tests/integration/00-setup-users.sh exécuté (test_u1/u2/u3) ; >= 10 médias (>= 10 min).
# Usage : tests/integration/24-robustesse.sh [--restart] [I41 I46 …]
#   --restart : exécute aussi I48 (redémarre deployment/emby2 via kubectl ; KUBECONFIG=private/kubeconfig.yml)
#   sans liste : tous les scénarios (I48 seulement avec --restart)
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/int-lib.sh"

DO_RESTART=0; WANT=()
for a in "$@"; do
  case $a in
    --restart) DO_RESTART=1 ;;
    I[0-9]*) WANT+=("$a") ;;
    *) die "usage : $0 [--restart] [I41 I42 …]" ;;
  esac
done
KUBE_DEPLOY="emby2"; KUBE_NS="media"; KUBECONFIG_FILE="$PRIVATE/kubeconfig.yml"   # QUALIF en dur

OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/robustesse-$TS.json"
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
  cleanup_test_accounts
  cleanup_registered_playlists
  if [[ $DONE == 0 && -s $RES ]]; then write_out true; echo "  (trap) preuves partielles : $OUT_FILE" >&2; fi
  rm -rf "$SCRATCH"; exit $rc
}
trap on_exit EXIT

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV" "$KUBECONFIG_FILE"
[[ -f $USERS_ENV && -f $SNAPSHOT ]] || die "lancer tests/integration/00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
relogin() {
  T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)"); T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)"); T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")
}
relogin

st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video&Fields=RunTimeTicks&SortBy=SortName&Limit=200")
[[ $st == 200 ]] || die "GET /Items -> $st"
mapfile -t M < <(jq -r '[.Items[]|select((.RunTimeTicks//0)>=6000000000)|.Id][0:20][]' "$RESP")
[[ ${#M[@]} -ge 10 ]] || die "moins de 10 médias (>= 10 min) : I41-I48 en ont besoin (demander à l'utilisateur)"
reset_pool_full "${M[@]}"   # remise à zéro complète (lu+position), comme les 3 autres scripts (#47) : bassin partagé
echo "  [OK] bassin de ${#M[@]} médias remis à zéro (lu=false, position=0)"
echo 0 > "$SCRATCH/next_m"
next_media() {   # un média frais par sous-cas ; pas de recyclage (10 suffisent, cf. plus haut)
  local n; n=$(cat "$SCRATCH/next_m")
  [[ $n -lt ${#M[@]} ]] || die "next_media : plus de médias disponibles (>${#M[@]} demandés)"
  echo "${M[$n]}"; echo $((n+1)) > "$SCRATCH/next_m"
}

st=$(api GET "$DIAG/State"); [[ $st == 200 ]] || die "Diagnostics/State -> HTTP $st : plugin v0.5.0 non déployé ou EnableDiagnostics=false"
GRACE=$(jq -r '.gracePasses // 2' "$RESP")
echo "  [OK] ${#M[@]} médias ; GracePasses=$GRACE"

# new_pl_owned OWNER_ID OWNER_TOKEN NOM ITEM_IDS_CSV -> id : variante de new_pl (int-lib.sh) à propriétaire choisi.
# UNIQUEMENT pour I43 (propriétaire supprimé) : jamais test_u1/u2/u3 (comptes permanents de toute la suite), un
# compte de test DÉDIÉ créé/supprimé par ce scénario lui-même (D-e : « jamais sur un compte réel » — étendu ici à
# « jamais sur un compte permanent de la suite » non plus, pour ne rien casser des autres scripts).
new_pl_owned() {
  local oid=$1 otok=$2 st id
  st=$(api POST "/Playlists?Name=$(qs "$3")&MediaType=Video&Ids=$4&UserId=$oid" "" "$otok")
  [[ $st == 200 ]] || die "création playlist $3 (propriétaire dédié) -> HTTP $st"
  id=$(jq -r '.Id' "$RESP"); register_playlist "$id"; echo "$id"
}

# ---------------------------------------------------------------- scénarios
i41() {
  echo "== I41 — membre retiré du partage en cours d'usage : disparaît des candidats à la transition suivante (#30)"
  local pl item st p3 p1 j
  pl=$(shared_pl "SPIKE-I41" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  st=$(api POST /Items/Access "$(jq -nc --arg p "$pl" --arg u "$U3" '{ItemIds:[$p],UserIds:[$u],ItemAccess:"None"}')" "$T1")
  if [[ $st != 2* ]]; then
    skip I41 "retrait d'accès via ItemAccess=None a répondu HTTP $st (valeur d'énumération à confirmer sur QUALIF) : scénario non exécutable tel quel sur ce serveur"
    return
  fi
  jclear
  finish "$U2" "$T2" "$item"
  wait_played "$U1" "$T1" "$item" true 10 || true
  p1=$(played_of "$U1" "$T1" "$item"); p3=$(played_of "$U3" "$T3" "$item")
  ck I41.owner "propriétaire (toujours membre) reçoit la propagation" "{\"u1\":\"$p1\"}" test "$p1" = true
  ck I41.removed "u3 (accès retiré AVANT la transition) N'A PAS reçu la propagation : disparu des candidats" "{\"u3\":\"$p3\"}" test "$p3" != true
  j=$(journal Error)
  ck I41.noerror "aucune entrée Error" "$j" test "$(jq 'length' <<<"$j")" = 0
}

i42() {
  echo "== I42 — playlist supprimée juste avant une transition : aucune exception, playlist témoin traitée normalement (#30)"
  local pl item other_pl other_item j
  pl=$(shared_pl "SPIKE-I42" "$(next_media)"); item=$(entries "$pl" | jq -r '.[0].itemId')
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu oui; set_marker_state "$pl" propager-lu oui
  other_pl=$(shared_pl "SPIKE-I42-temoin" "$(next_media)"); other_item=$(entries "$other_pl" | jq -r '.[0].itemId')
  prime "$other_pl" || true
  set_marker_state "$other_pl" remove-si-lu oui; set_marker_state "$other_pl" propager-lu oui
  api DELETE "/Items/$pl" >/dev/null   # supprime la playlist SOUS le plugin (D-a : Get()->null, retombe sur l'instantané ou échoue silencieusement)
  jclear
  finish "$U2" "$T2" "$item"          # média de la playlist SUPPRIMÉE : plus aucun membre ne la voit (partage disparu avec elle)
  finish "$U2" "$T2" "$other_item"    # témoin, DANS LA MÊME FENÊTRE : doit fonctionner normalement malgré l'autre suppression
  ck I42.witness "playlist témoin traitée normalement (retrait) malgré la suppression de l'autre" "null" wait_count "$other_pl" "$other_item" 0 10
  j=$(journal Error)
  ck I42.noerror "aucune entrée Error journalisée (aucun crash imputable à la playlist supprimée)" "$j" test "$(jq 'length' <<<"$j")" = 0
  local gst gcnt; gst=$(api GET "/Items?Ids=$pl"); gcnt=$(jq -r '.Items|length' "$RESP")
  ck I42.gone "la playlist supprimée est bien invisible (GET /Items?Ids= -> aucun résultat)" "{\"status\":\"$gst\",\"count\":$gcnt}" test "$gst/$gcnt" = "200/0"
}

i43() {
  echo "== I43 — propriétaire supprimé (compte de test DÉDIÉ, jamais test_u1/u2/u3) : comportement observé et consigné, aucun crash exigé comme seul critère (#30, D-e)"
  local ownerid ownertok pl item st1 st2 obs j
  read -r ownerid ownertok <<<"$(create_and_login_test_account "SPIKE-I43-owner")"
  item=$(next_media)
  pl=$(new_pl_owned "$ownerid" "$ownertok" "SPIKE-I43" "$item")
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$pl" --arg a "$U2" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Write"}')" "$ownertok"
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$pl" --arg a "$U3" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Read"}')" "$ownertok"
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non "$ownerid" "$ownertok"; set_marker_state "$pl" propager-lu oui "$ownerid" "$ownertok"
  jclear
  api DELETE "/Users/$ownerid" >/dev/null   # supprime le PROPRIÉTAIRE (pas la playlist directement)
  st1=$(api GET "/Playlists/$pl/Items?UserId=$U2" "" "$T2")
  if [[ $st1 == 200 ]]; then
    obs="playlist toujours visible pour u2 après suppression du propriétaire (Emby n'a pas supprimé la playlist avec le compte)"
  else
    obs="playlist disparue pour u2 après suppression du propriétaire (HTTP $st1 : Emby l'a supprimée avec le compte)"
  fi
  run_pass || true
  st2=$(api GET "$DIAG/State")
  ck I43.nocrash "aucun crash observable : Diagnostics/State répond toujours (HTTP $st2) après la passe qui suit la suppression" "{\"observation\":\"$obs\"}" test "$st2" = 200
  j=$(journal Error)
  ck I43.noerror "aucune entrée Error imprévue journalisée" "$j" test "$(jq 'length' <<<"$j")" = 0
  rec I43.observed OK "$obs — les deux issues sont acceptables (D-e), aucune n'est présumée" "null"
}

i44() {
  echo "== I44 — média inexistant simulé (D-d, itemId ne correspondant à aucun item réel) dans une playlist gérée : R8/no-access, aucune exception (#31)"
  local pl fakeitem st j
  fakeitem="99999999999"
  pl=$(shared_pl "SPIKE-I44" "$(next_media)"); prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  set_marker_state "$pl" propager-avancement oui   # v1.2.0 : l'avancement visé ci-dessous relève de sa propre famille
  st=$(api POST "/Playlists/$pl/Items?Ids=$fakeitem&UserId=$U1" "" "$T1")
  if [[ $st != 2* ]]; then
    skip I44 "Emby refuse d'ajouter un itemId inexistant à une playlist (HTTP $st) : simulation D-d non réalisable via cette API sur ce serveur"
    return
  fi
  jclear
  finish "$U2" "$T2" "$fakeitem"        # propagation du lu visée sur un média qui n'existe pas
  apiok '2*' POST /Sessions/Playing "$(jq -nc --arg i "$fakeitem" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s44",PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$T2"
  apiok '2*' POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$fakeitem" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:"s44",PlayMethod:"DirectPlay",PositionTicks:400000000,CanSeek:true}')" "$T2"   # avancement visé aussi
  nap 3
  j=$(journal Error)
  ck I44.noerror "aucune entrée Error (média inexistant traité comme R8, jamais une exception)" "$j" test "$(jq 'length' <<<"$j")" = 0
  ck I44.noplayed "u1 n'est pas marqué lu pour ce média inexistant" "null" test "$(played_of "$U1" "$T1" "$fakeitem")" != true
}

i45() {
  echo "== I45 — playlist gérée VIDE (toutes les entrées retirées) : aucune action, aucune exception (#31)"
  local pl item j
  item=$(next_media)
  pl=$(shared_pl "SPIKE-I45" "$item")
  drop_item "$pl" "$item"   # vide explicitement (retrait direct par le propriétaire, indépendant du plugin)
  ck I45.confirmempty "playlist bien vide avant la première détection" "null" test "$(entries "$pl" | jq length)" = 0
  jclear
  prime "$pl" || true
  ck I45.tags "étiquettes NON/NON posées malgré l'absence d'entrées (aucune hypothèse de collection non vide)" \
    "$(tags_of "$pl")" test "$(tag_count "$(tags_of "$pl")" remove-si-lu)/$(tag_count "$(tags_of "$pl")" propager-lu)" = "1/1"
  j=$(journal Error)
  ck I45.noerror "aucune entrée Error" "$j" test "$(jq 'length' <<<"$j")" = 0
}

i46() {
  echo "== I46 — 5+ membres simultanés sur une même playlist : propagation du lu pour tous, latence mesurée (#33)"
  local pl item i id tok before_max after_max j
  local -a extra_ids=() extra_toks=()
  item=$(next_media)
  pl=$(shared_pl "SPIKE-I46" "$item")   # u1 (propriétaire) + u2 (Write) + u3 (Read) = 3 déjà
  for i in 1 2; do   # + 2 comptes dédiés = 5 membres au total
    read -r id tok <<<"$(create_and_login_test_account "SPIKE-I46-m$i")"
    apiok 204 POST /Items/Access "$(jq -nc --arg p "$pl" --arg u "$id" '{ItemIds:[$p],UserIds:[$u],ItemAccess:"Write"}')" "$T1"
    extra_ids+=("$id"); extra_toks+=("$tok")
  done
  prime "$pl" || true
  set_marker_state "$pl" remove-si-lu non; set_marker_state "$pl" propager-lu oui
  before_max=$(state | jq -r '.handler.maxMs // 0')
  jclear
  finish "$U2" "$T2" "$item"
  wait_played "$U1" "$T1" "$item" true 10 || true
  ck I46.owner "propriétaire propagé" "null" test "$(played_of "$U1" "$T1" "$item")" = true
  ck I46.readonly "membre Read (u3) propagé" "null" test "$(played_of "$U3" "$T3" "$item")" = true
  ck I46.extra1 "membre supplémentaire 1 (5e membre) propagé" "null" test "$(played_of "${extra_ids[0]}" "${extra_toks[0]}" "$item")" = true
  ck I46.extra2 "membre supplémentaire 2 propagé" "null" test "$(played_of "${extra_ids[1]}" "${extra_toks[1]}" "$item")" = true
  after_max=$(state | jq -r '.handler.maxMs // 0')
  ck I46.latency "latence maximale du gestionnaire toujours < 2000 ms (budget, #33)" "{\"before\":$before_max,\"after\":$after_max}" test "$after_max" -lt 2000
  j=$(journal Propagation)
  ck I46.aggregate "propagation agrégée : au moins 4 autres membres (members>=4)" "$j" test "$(jcount "$j" "$pl" Propagation '(^|[^A-Za-z])members=[4-9]')" -ge 1
  for id in "${extra_ids[@]}"; do api DELETE "/Users/$id" >/dev/null || true; done   # nettoyés ICI : "fin des scénarios" ne doit voir que test_u1/u2/u3
}

i47() {
  echo "== I47 — 10+ playlists gérées simultanément par le même propriétaire, média présent dans TOUTES : chacune traitée, budget respecté (#33)"
  local shared_item n=12 i pl hs j
  local -a pls=()
  shared_item=$(next_media)
  for ((i=0; i<n; i++)); do
    pl=$(shared_pl "SPIKE-I47-$i" "$shared_item")
    prime "$pl" || true
    set_marker_state "$pl" remove-si-lu oui; set_marker_state "$pl" propager-lu oui   # v1.2.0 (D21) : le retrait exige propager-lu=OUI
    pls+=("$pl")
  done
  jclear
  finish "$U2" "$T2" "$shared_item"
  local ok_count=0
  for pl in "${pls[@]}"; do
    if wait_count "$pl" "$shared_item" 0 10; then ok_count=$((ok_count+1)); fi
  done
  ck I47.allremoved "les $n playlists ont TOUTES retiré le média (aucune oubliée sous le volume)" "{\"ok\":$ok_count,\"total\":$n}" test "$ok_count" = "$n"
  hs=$(state | jq -r '.handler.maxMs // 0')
  ck I47.latency "latence maximale toujours < 2000 ms malgré le volume (#33)" "{\"maxMs\":$hs}" test "$hs" -lt 2000
  j=$(journal Error)
  ck I47.noerror "aucune entrée Error" "$j" test "$(jq 'length' <<<"$j")" = 0
}

i48() {
  echo "== I48 — redémarrage en cours d'usage à plus grande échelle (extension d'I10 v0.2.0) : plusieurs listes actives, reprise propre (#34)"
  if [[ $DO_RESTART != 1 ]]; then skip I48 "option --restart absente"; return; fi
  if ! command -v kubectl >/dev/null || [[ ! -f $KUBECONFIG_FILE ]]; then skip I48 "kubectl ou private/kubeconfig.yml absent"; return; fi
  local i pl st ta ok=0 all_ok=1 j
  local -a pls=() items=("$(next_media)" "$(next_media)")
  for i in 1 2 3; do
    pl=$(shared_pl "SPIKE-I48-$i" "${items[0]},${items[1]}")
    prime "$pl" || true
    set_marker_state "$pl" remove-si-lu oui; set_marker_state "$pl" propager-lu oui
    pls+=("$pl")
  done
  ta=$(tags_of "${pls[0]}")
  KUBECONFIG="$KUBECONFIG_FILE" kubectl rollout restart "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" >/dev/null
  KUBECONFIG="$KUBECONFIG_FILE" kubectl rollout status "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" --timeout=300s >/dev/null || { rec I48 KO "redémarrage non terminé en 300 s" "null"; return; }
  for ((i=0; i<90; i++)); do
    if api GET /System/Info >/dev/null && [[ $(jq -r '.ServerName//""' "$RESP") == "$EXPECTED_SERVER_NAME" ]]; then ok=1; break; fi
    nap 2
  done
  if [[ $ok != 1 ]]; then rec I48 KO "serveur injoignable après redémarrage" "null"; return; fi
  relogin
  st=000
  for ((i=0; i<60; i++)); do
    st=$(api GET "$DIAG/State")
    if [[ $st == 200 ]]; then break; fi
    nap 2
  done
  ck I48.up "Diagnostics/State répond après redémarrage (HTTP $st)" "null" test "$st" = 200
  ck I48.nodup "playlist déjà complète (I48-1) : aucune étiquette dupliquée après redémarrage (mémoire remise à zéro, R11)" \
    "$(tags_of "${pls[0]}")" test "$(tags_of "${pls[0]}")" = "$ta"
  jclear
  finish "$U2" "$T2" "${items[0]}"
  for pl in "${pls[@]}"; do
    if ! wait_count "$pl" "${items[0]}" 0 10; then all_ok=0; fi
  done
  ck I48.resume "les 3 playlists reprennent normalement le retrait après redémarrage" "{\"ok\":$all_ok}" test "$all_ok" = 1
  j=$(journal Error)
  ck I48.noerror "aucune entrée Error après la reprise" "$j" test "$(jq 'length' <<<"$j")" = 0
}

ALL=(I41 I42 I43 I44 I45 I46 I47 I48)
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
