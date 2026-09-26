#!/usr/bin/env bash
# 05-reentrancy-probe.sh — pilote la sonde U11 (#52) sur emby2 (QUALIF UNIQUEMENT) : peut-on écrire
# (RemoveFromPlaylist, UpdateToRepositoryAsync, SaveUserData) DEPUIS les gestionnaires d'événements Emby
# sans blocage, « database is locked » ni boucle ?  Build intermédiaire 0.2.0.a requise.
#
# Scénarios (déclenchés par REST ; la sonde du plugin agit dans les gestionnaires et journalise Kind=Probe,
# Detail « scenario=Pn durationMs=<n> echoes=<n> outcome=OK|KO ») :
#   P1 RemoveFromPlaylist / P2 UpdateToRepositoryAsync / P3 SaveUserData(autre test_*) depuis UserDataSaved
#      -> déclencheur : test_u2 marque lu un média d'une playlist SPIKE* (non lu -> lu), PITER itérations
#   P4 mise à jour de métadonnées depuis PlaylistItemsAdded/ItemUpdated (1re détection)
#      -> déclencheur : test_u1 crée une nouvelle playlist SPIKE*, PITER itérations
#   P5 rafale concurrente : >= 5 transitions simultanées (3 utilisateurs) sur la MÊME playlist, PROUNDS rondes
#   P6 édition d'étiquette par le propriétaire (REST) PENDANT le gestionnaire, PITER6 itérations
# Critères (issue #52) : aucun appel > 5 s ; ZÉRO « database is locked »/SQLITE_BUSY/exception dans les logs
# d'Emby pendant l'essai ; échos <= 1 par écriture ; latence du gestionnaire p95 <= 300 ms et max <= 2 s ;
# rafale : chaque retrait appliqué une seule fois ; chaque scénario observé, outcome=OK.
# Décision : immédiat si tout est OK ; repli Task.Run sous verrou si un critère est KO ; indéterminé si un
# critère n'est pas vérifiable (ex. logs kubectl indisponibles).
#
# Prérequis : 00-setup-users.sh exécuté ; plugin 0.2.0.a déployé (option EnableReentrancyProbe présente).
# Le script active EnableReentrancyProbe (config du plugin, sauvegarde/restauration automatiques) et ne touche
# que test_* et SPIKE* ; admin, cyril, user2 : lecture seule, comparés avant/après.
# Usage : tests/integration/05-reentrancy-probe.sh   (PITER=50 PROUNDS=10 PITER6=20 réglables)
source "$(dirname "${BASH_SOURCE[0]}")/../spike/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/probe-lib.sh"

PITER=${PITER:-50}; PROUNDS=${PROUNDS:-10}; PITER6=${PITER6:-20}
PLUGIN_ID="9ebe814e-9438-42b8-aa57-feea1ae92451"          # GUID fixe du plugin (Plugin.cs)
KUBE_DEPLOY="emby2"; KUBE_NS="media"                        # QUALIF en dur : jamais « emby »
KUBECONFIG_FILE="$PRIVATE/kubeconfig.yml"
CONFIG_BAK="$PRIVATE/reentrancy-config.bak.json"            # config d'origine (gitignoré) : restauration de secours
OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/reentrancy-$TS.json"
SPK=/SharedPlaylist/Spike
DEFS='def n: (.//"")|tostring|ascii_downcase|gsub("-";"");'
PROBE_DIR="$SCRATCH/probe"; mkdir -p "$PROBE_DIR"
CONFIG_CHANGED=0; DONE=0; NOTES=()

now_ms() { date +%s%3N; }
qs() { jq -rn --arg v "$1" '$v|@uri'; }
ev_clear() { api GET "$SPK/Events?clear=true" >/dev/null; }
ev_get()   { api GET "$SPK/Events?clear=false" >/dev/null; jq -c . "$RESP"; }
tapi() { # TAG METHODE CHEMIN [CORPS] [TOKEN] : appel chronométré (ms ajoutées à ms.TAG et ms.ALL)
  local tag=$1; shift; local t0 t1 st
  t0=$(now_ms); st=$(api "$@"); t1=$(now_ms)
  echo $((t1-t0)) >> "$PROBE_DIR/ms.$tag"; echo $((t1-t0)) >> "$PROBE_DIR/ms.ALL"
  echo "$st"
}
bg_call() { # ID TAG METHODE CHEMIN [CORPS] [TOKEN] : appel concurrent (fichiers propres) ; statut -> bg.ID.st
  local id=$1; shift
  ( CFG="$SCRATCH/bg.$id.cfg"; RESP="$SCRATCH/bg.$id.resp"; BODYF="$SCRATCH/bg.$id.body"
    tag=$1; shift; t0=$(now_ms); st=$(api "$@"); t1=$(now_ms)
    echo "$st" > "$SCRATCH/bg.$id.st"
    echo $((t1-t0)) >> "$PROBE_DIR/ms.$tag"; echo $((t1-t0)) >> "$PROBE_DIR/ms.ALL" ) &
}
bg_owner_tag() { # ID PLAYLIST TAG : lecture-modification-écriture du DTO par le propriétaire (comme l'éditeur natif)
  local id=$1 pl=$2 tag=$3
  ( CFG="$SCRATCH/bg.$id.cfg"; RESP="$SCRATCH/bg.$id.resp"; BODYF="$SCRATCH/bg.$id.body"
    t0=$(now_ms)
    st=$(api GET "/Users/$U1/Items/$pl?Fields=Tags,TagItems,Overview" "" "$T1")
    if [[ $st == 200 ]]; then
      dto=$(jq -c --arg t "$tag" '(((.TagItems//[])|map(.Name)) + (.Tags//[]) + [$t] | unique) as $new | .Tags=$new | .TagItems=($new|map({Name:.}))' "$RESP")
      st=$(api POST "/Items/$pl" "$dto" "$T1")
    fi
    t1=$(now_ms)
    echo "$st" > "$SCRATCH/bg.$id.st"
    echo $((t1-t0)) >> "$PROBE_DIR/ms.P6owner"; echo $((t1-t0)) >> "$PROBE_DIR/ms.ALL" ) &
}
entries() { api GET "/Playlists/$1/Items?UserId=$3" "" "$2" >/dev/null; jq -c '[.Items[]|{itemId:.Id, playlistItemId:.PlaylistItemId}]' "$RESP"; }
has_item() { jq -e --arg i "$2" "$DEFS"'map(select((.itemId|n)==($i|n)))|length>0' <<<"$1" >/dev/null; }
add_item() { # PLAYLIST ITEM : ré-ajoute l'item si absent (par le propriétaire)
  local x; x=$(entries "$1" "$T1" "$U1")
  if ! has_item "$x" "$2"; then api POST "/Playlists/$1/Items?Ids=$2&UserId=$U1" "" "$T1" >/dev/null; fi
}
collect_probes() { # EVENTS_JSON -> ajoute durée/échos/outcome dans probe.<scénario>
  local sc d e o
  while read -r sc d e o; do
    [[ -n $sc ]] || continue
    echo "$d" >> "$PROBE_DIR/dur.$sc"; echo "$e" >> "$PROBE_DIR/echo.$sc"; echo "$o" >> "$PROBE_DIR/outcome.$sc"
  done < <(probe_rows "$1")
}
wait_probes() { # LISTE_SCENARIOS [TIMEOUT_S] -> $EV
  local i; for ((i=0; i<${2:-10}; i++)); do
    EV=$(ev_get); if probes_seen "$EV" "$1"; then return 0; fi; sleep 1
  done; return 1
}
unmark_all() { api DELETE "/Users/$U2/PlayedItems/$1" "" "$T2" >/dev/null; }

# ---------------------------------------------------------------- restauration / preuves partielles
write_out() { # PARTIAL
  local resf="$PROBE_DIR/results.json"
  [[ -s $resf ]] || echo '{}' > "$resf"
  jq --argjson p "$1" --arg ts "$TS" '. + {run:$ts, partial:$p}' "$resf" > "$OUT_FILE" 2>/dev/null || true
}
restore_config() {
  if [[ $CONFIG_CHANGED == 1 && -s $CONFIG_BAK ]]; then
    local st; st=$(api POST "/Plugins/$PLUGIN_ID/Configuration" "$(cat "$CONFIG_BAK")")
    if [[ $st == 2* || $st == 204 ]]; then rm -f "$CONFIG_BAK"; CONFIG_CHANGED=0; echo "  configuration du plugin restaurée (sonde désactivée)"
    else echo "  ATTENTION : restauration de la configuration échouée (HTTP $st) — copie dans private/reentrancy-config.bak.json" >&2; fi
  fi
}
on_exit() {
  local rc=$?; set +e
  restore_config
  if [[ $DONE == 0 && -n ${U1:-} ]]; then write_out true; echo "  (trap) preuves partielles : $OUT_FILE" >&2; fi
  rm -rf "$SCRATCH"; exit $rc
}
trap on_exit EXIT

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV" "$CONFIG_BAK" "$KUBECONFIG_FILE"
[[ -f $USERS_ENV && -f $SNAPSHOT ]] || die "lancer tests/spike/00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)")
T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)")
T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")

st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video&SortBy=SortName&Limit=50")
[[ $st == 200 ]] || die "GET /Items -> $st"
mapfile -t M < <(jq -r '.Items[0:6][].Id' "$RESP")
[[ ${#M[@]} -ge 5 ]] || die "moins de 5 médias : la rafale P5 exige >= 5 transitions (demander à l'utilisateur)"
echo "  [OK] ${#M[@]} médias disponibles"

# Sonde : option de configuration du plugin (sauvegarde -> activation ; restauration par trap)
st=$(api GET "/Plugins/$PLUGIN_ID/Configuration"); [[ $st == 200 ]] || die "configuration du plugin illisible (HTTP $st) : plugin non chargé ?"
cp "$RESP" "$CONFIG_BAK"; chmod 600 "$CONFIG_BAK"
KEY=$(jq -r 'keys[]|select(ascii_downcase=="enablereentrancyprobe")' "$CONFIG_BAK")
[[ -n $KEY ]] || die "option EnableReentrancyProbe absente : build intermédiaire 0.2.0.a non déployée"
SPK_KEY=$(jq -r 'keys[]|select(ascii_downcase=="enablespikeendpoints")' "$CONFIG_BAK")
NEWCFG=$(jq -c --arg k "$KEY" --arg s "$SPK_KEY" '.[$k]=true | if $s!="" then .[$s]=true else . end' "$CONFIG_BAK")
CONFIG_CHANGED=1
apiok '2*' POST "/Plugins/$PLUGIN_ID/Configuration" "$NEWCFG"
st=$(api GET "$SPK/Events?clear=true"); [[ $st == 200 ]] || die "Spike/Events -> HTTP $st (EnableSpikeEndpoints ?)"
echo "  [OK] sonde activée (configuration d'origine sauvegardée dans private/, restaurée en fin de script)"

# Logs d'Emby : kubectl sur emby2 uniquement
LOGS_OK=0; T_START=$(date -u +%Y-%m-%dT%H:%M:%SZ)
if command -v kubectl >/dev/null && [[ -f $KUBECONFIG_FILE ]]; then
  if KUBECONFIG="$KUBECONFIG_FILE" kubectl logs "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" --tail=1 >/dev/null 2>&1; then LOGS_OK=1; fi
fi
if [[ $LOGS_OK == 1 ]]; then echo "  [OK] logs de $KUBE_DEPLOY lisibles (kubectl) depuis $T_START"
else echo "  [WARN] logs non lisibles (kubectl ou private/kubeconfig.yml absent) : critère « zéro erreur log » = indéterminé"; NOTES+=("logs kubectl indisponibles"); fi

# Playlists de l'essai (propriétaire u1 ; u2 = Write, u3 = Read)
new_pl() { # NOM ITEM_IDS_CSV
  local st id; st=$(api POST "/Playlists?Name=$(qs "$1")&MediaType=Video&Ids=$2&UserId=$U1" "" "$T1")
  [[ $st == 200 ]] || die "création playlist $1 -> HTTP $st"
  id=$(jq -r '.Id' "$RESP"); register_playlist "$id"; echo "$id"
}
share_pl() { # PLAYLIST
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$1" --arg a "$U2" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Write"}')" "$T1"
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$1" --arg a "$U3" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Read"}')" "$T1"
}
ALLM=$(IFS=,; echo "${M[*]}")
PL1=$(new_pl "SPIKE-reent-p123" "${M[0]}"); share_pl "$PL1"
PL5=$(new_pl "SPIKE-reent-p5" "$ALLM"); share_pl "$PL5"
PL6=$(new_pl "SPIKE-reent-p6" "${M[0]}"); share_pl "$PL6"
echo "  [OK] playlists SPIKE-reent-p123 / -p5 / -p6 créées et partagées"

# ---------------------------------------------------------------- P1–P3 : UserDataSaved (transition non lu -> lu)
echo "== P1–P3 — écritures depuis UserDataSaved ($PITER itérations)"
p123_missing=0; p123_dupremoval=0; p123_notremoved=0
for ((i=1; i<=PITER; i++)); do
  unmark_all "${M[0]}"; add_item "$PL1" "${M[0]}"
  ev_clear
  st=$(tapi trigger123 POST "/Users/$U2/PlayedItems/${M[0]}" "" "$T2")
  if [[ $st != 2* ]]; then NOTES+=("P1-3 it$i : déclencheur HTTP $st"); fi
  if wait_probes "P1 P2 P3" 10; then :; else p123_missing=$((p123_missing+1)); fi
  collect_probes "$EV"
  rem=$(jq -r --arg p "$PL1" "$DEFS"'[.[]|select(.kind=="PlaylistItemsRemoved" and (.playlistId|n)==($p|n))]|length' <<<"$EV")
  x=$(entries "$PL1" "$T1" "$U1")
  if has_item "$x" "${M[0]}"; then p123_notremoved=$((p123_notremoved+1)); fi
  if [[ $rem -gt 1 ]]; then p123_dupremoval=$((p123_dupremoval+1)); fi
  if (( i % 10 == 0 )); then echo "  … $i/$PITER"; fi
done
echo "  scénarios non observés : $p123_missing ; retrait absent : $p123_notremoved ; retrait dupliqué : $p123_dupremoval"

# ---------------------------------------------------------------- P4 : métadonnées depuis PlaylistItemsAdded/ItemUpdated
echo "== P4 — métadonnées depuis PlaylistItemsAdded/ItemUpdated, 1re détection ($PITER itérations)"
p4_missing=0
for ((i=1; i<=PITER; i++)); do
  ev_clear
  st=$(tapi trigger4 POST "/Playlists?Name=$(qs "SPIKE-reent-p4-$i")&MediaType=Video&Ids=${M[1]}&UserId=$U1" "" "$T1")
  pid=$(jq -r '.Id // empty' "$RESP")
  if [[ -n $pid ]]; then register_playlist "$pid"; fi
  if wait_probes "P4" 10; then :; else p4_missing=$((p4_missing+1)); fi
  collect_probes "$EV"
  if [[ -n $pid ]]; then api DELETE "/Items/$pid" >/dev/null; fi
  if (( i % 10 == 0 )); then echo "  … $i/$PITER"; fi
done
echo "  scénarios non observés : $p4_missing"

# ---------------------------------------------------------------- P5 : rafale concurrente
echo "== P5 — rafale : ${#M[@]} transitions simultanées (3 utilisateurs) sur la même playlist, $PROUNDS rondes"
p5_missing=0; p5_bad=0
TOKS=("$T2" "$T3" "$T1"); UIDS=("$U2" "$U3" "$U1")
for ((r=1; r<=PROUNDS; r++)); do
  for k in "${!M[@]}"; do
    u=$((k%3)); api DELETE "/Users/${UIDS[$u]}/PlayedItems/${M[$k]}" "" "${TOKS[$u]}" >/dev/null
    add_item "$PL5" "${M[$k]}"
  done
  x=$(entries "$PL5" "$T1" "$U1"); before=$(jq 'length' <<<"$x")
  ev_clear; rm -f "$SCRATCH"/bg.*.st
  for k in "${!M[@]}"; do
    u=$((k%3)); bg_call "$r-$k" trigger5 POST "/Users/${UIDS[$u]}/PlayedItems/${M[$k]}" "" "${TOKS[$u]}"
  done
  wait
  if wait_probes "P5" 12; then :; else p5_missing=$((p5_missing+1)); fi
  sleep 2; EV=$(ev_get); collect_probes "$EV"
  after=$(entries "$PL5" "$T1" "$U1" | jq 'length')
  # chaque entrée retirée exactement une fois
  dup=$(jq -r --arg p "$PL5" "$DEFS"'[.[]|select(.kind=="PlaylistItemsRemoved" and (.playlistId|n)==($p|n))|.entryId] | group_by(.) | map(select(length>1)) | length' <<<"$EV")
  cnt=$(jq -r --arg p "$PL5" "$DEFS"'[.[]|select(.kind=="PlaylistItemsRemoved" and (.playlistId|n)==($p|n))]|length' <<<"$EV")
  if [[ $after -ne 0 || $dup -ne 0 || $cnt -ne $before ]]; then
    p5_bad=$((p5_bad+1)); NOTES+=("P5 ronde $r : avant=$before après=$after retraits=$cnt doublons=$dup")
  fi
done
echo "  rondes anormales : $p5_bad ; scénario non observé : $p5_missing"

# ---------------------------------------------------------------- P6 : édition d'étiquette pendant le gestionnaire
echo "== P6 — étiquette du propriétaire pendant le gestionnaire ($PITER6 itérations)"
p6_missing=0; p6_lost=0; p6_err=0; EXPECTED_TAGS=()
for ((i=1; i<=PITER6; i++)); do
  unmark_all "${M[0]}"; add_item "$PL6" "${M[0]}"
  tag="p6-tag-$i"; ev_clear; rm -f "$SCRATCH"/bg.p6*.st
  bg_owner_tag "p6o$i" "$PL6" "$tag"
  bg_call "p6t$i" trigger6 POST "/Users/$U2/PlayedItems/${M[0]}" "" "$T2"
  wait
  if wait_probes "P6" 10; then :; else p6_missing=$((p6_missing+1)); fi
  collect_probes "$EV"
  os=$(cat "$SCRATCH/bg.p6o$i.st" 2>/dev/null || echo 000)
  if [[ $os != 2* ]]; then p6_err=$((p6_err+1)); NOTES+=("P6 it$i : édition propriétaire HTTP $os"); fi
  EXPECTED_TAGS+=("$tag"); sleep 1
  api GET "$SPK/Tags?playlistId=$PL6" >/dev/null
  if [[ $os == 2* ]] && ! jq -e --argjson want "$(printf '%s\n' "${EXPECTED_TAGS[@]}" | jq -R . | jq -sc .)" '(.tags//[]) as $t | ($want - $t)|length==0' "$RESP" >/dev/null; then
    p6_lost=$((p6_lost+1)); NOTES+=("P6 it$i : étiquette(s) du propriétaire perdues")
    # on reprend l'attendu sur l'état réel pour ne compter chaque perte qu'une fois
    mapfile -t EXPECTED_TAGS < <(jq -r '(.tags//[])[]|select(startswith("p6-tag-"))' "$RESP")
  fi
  if (( i % 10 == 0 )); then echo "  … $i/$PITER6"; fi
done
echo "  scénario non observé : $p6_missing ; éditions en erreur : $p6_err ; étiquettes perdues : $p6_lost"

# ---------------------------------------------------------------- logs et bilan
echo "== Logs d'Emby pendant l'essai"
if [[ $LOGS_OK == 1 ]]; then
  KUBECONFIG="$KUBECONFIG_FILE" kubectl logs "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" --since-time="$T_START" > "$SCRATCH/emby.log" 2>/dev/null || true
  LOGSCAN=$(scan_log "$SCRATCH/emby.log")
  jq -r '"  lignes=\(.lines) locked=\(.locked) exceptions=\(.exceptions) erreursPlugin=\(.pluginErrors)"' <<<"$LOGSCAN"
else
  LOGSCAN='null'
fi

echo "== Comptes protégés"
if compare_protected "fin de l'essai" "${TEST_USERS[@]}"; then PROT=true; else PROT=false; fi

# statistiques par scénario
SCEN='{}'
for sc in P1 P2 P3 P4 P5 P6; do
  outs=$(sort "$PROBE_DIR/outcome.$sc" 2>/dev/null | uniq -c | awk '{printf "%s:%s ",$2,$1}' || true)
  ko=$(grep -vc '^OK$' "$PROBE_DIR/outcome.$sc" 2>/dev/null || true)
  maxecho=$(sort -n "$PROBE_DIR/echo.$sc" 2>/dev/null | tail -n1 || true)
  SCEN=$(jq -c --arg s "$sc" --argjson d "$(stats_json "$PROBE_DIR/dur.$sc")" --arg o "$outs" --argjson ko "${ko:-0}" --argjson me "${maxecho:-0}" \
    '. + {($s): {handlerMs:$d, outcomes:$o, outcomeKo:$ko, maxEchoes:$me}}' <<<"$SCEN")
done
CALLS=$(stats_json "$PROBE_DIR/ms.ALL")
TRIG=$(jq -nc --argjson a "$(stats_json "$PROBE_DIR/ms.trigger123")" --argjson b "$(stats_json "$PROBE_DIR/ms.trigger4")" \
  --argjson c "$(stats_json "$PROBE_DIR/ms.trigger5")" --argjson d "$(stats_json "$PROBE_DIR/ms.trigger6")" '{trigger123:$a,trigger4:$b,trigger5:$c,trigger6:$d}')

RESULTS=$(jq -nc --argjson scen "$SCEN" --argjson calls "$CALLS" --argjson trig "$TRIG" --argjson logs "$LOGSCAN" --argjson prot "$PROT" \
  --argjson it "$PITER" --argjson rounds "$PROUNDS" --argjson it6 "$PITER6" \
  --argjson m123 "$p123_missing" --argjson nr "$p123_notremoved" --argjson dr "$p123_dupremoval" --argjson m4 "$p4_missing" \
  --argjson m5 "$p5_missing" --argjson b5 "$p5_bad" --argjson m6 "$p6_missing" --argjson l6 "$p6_lost" --argjson e6 "$p6_err" \
  --argjson notes "$(printf '%s\n' "${NOTES[@]:-}" | jq -R . | jq -sc 'map(select(length>0))')" \
  "$RESULTS_JQ")
DECISION=$(decide "$RESULTS")
echo "$(jq -c --arg d "$DECISION" '. + {decision:$d}' <<<"$RESULTS")" > "$PROBE_DIR/results.json"
write_out false; DONE=1

echo
echo "== Bilan (détail : $OUT_FILE)"
jq -r '.criteria|to_entries[]|"  \(if .value.ok==true then "OK " elif .value.ok==false then "KO " else "?? " end) \(.key) — \(.value.detail)"' "$PROBE_DIR/results.json"
jq -r '.scenarios|to_entries[]|"  \(.key): gestionnaire n=\(.value.handlerMs.n) p50=\(.value.handlerMs.p50) p95=\(.value.handlerMs.p95) max=\(.value.handlerMs.max) ms, échos max \(.value.maxEchoes), outcomes \(.value.outcomes)"' "$PROBE_DIR/results.json"
echo "DECISION : $DECISION"
restore_config
[[ $DECISION == "immédiat" ]]
