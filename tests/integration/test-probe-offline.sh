#!/usr/bin/env bash
# test-probe-offline.sh — test hors ligne de probe-lib.sh (aucun accès à emby2, aucun secret) :
# parsing des événements Probe, statistiques, analyse/masquage des logs, agrégation et décision.
source "$(dirname "${BASH_SOURCE[0]}")/../spike/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/probe-lib.sh"
fail=0
ok() { echo "  [OK] $1"; }
ko() { echo "  [KO] $1"; fail=1; }
D="$SCRATCH"

echo "== événements Probe"
EV='[{"ts":"t","kind":"UserDataSaved","userId":"a"},
     {"ts":"t","kind":"Probe","detail":"scenario=P1 durationMs=12 echoes=1 outcome=OK"},
     {"ts":"t","kind":"Probe","detail":"outcome=KO echoes=2 scenario=P2 durationMs=340 lockWaitMs=7 removed=3 echoKinds=PlaylistItemsRemoved:1,ItemUpdated:1"},
     {"ts":"t","kind":"Probe","detail":"pas de paires"}]'
rows=$(probe_rows "$EV")
[[ $(wc -l <<<"$rows") -eq 2 ]] && ok "2 événements Probe analysés (la ligne sans scenario= est ignorée)" || ko "probe_rows : $rows"
grep -qx 'P1 12 1 OK 0 - -' <<<"$rows" && ok "P1 : durée/échos/outcome extraits" || ko "P1 : $rows"
grep -qx 'P2 340 2 KO 7 PlaylistItemsRemoved:1,ItemUpdated:1 3' <<<"$rows" && ok "ordre des clés libre, lockWaitMs, echoKinds, removed (P2)" || ko "P2 : $rows"
[[ $(probe_removed_sum "$EV" P2) == 3 && $(probe_removed_sum "$EV" P1) == 0 ]] && ok "probe_removed_sum" || ko "removed_sum"
probes_seen "$EV" "P1 P2" && ok "probes_seen : P1 P2 présents" || ko "probes_seen"
probes_seen "$EV" "P1 P3" && ko "probes_seen : P3 absent jugé présent" || ok "probes_seen : P3 absent détecté"

echo "== stats_json"
printf '%s\n' 10 20 30 40 50 60 70 80 90 100 > "$D/n"
s=$(stats_json "$D/n")
[[ $(jq -r '.n' <<<"$s") == 10 && $(jq -r '.p50' <<<"$s") == 50 && $(jq -r '.p95' <<<"$s") == 100 && $(jq -r '.max' <<<"$s") == 100 ]] && ok "n/p50/p95/max corrects" || ko "stats : $s"
[[ $(stats_json "$D/absent" | jq -r '.n') == 0 && $(stats_json "$D/absent" | jq -r '.p95') == null ]] && ok "fichier absent : n=0, percentiles null" || ko "stats vide"

echo "== scan_log"
cat > "$D/log.txt" <<'L'
2026-09-26 12:00:00 Info HttpServer: GET /Items?api_key=SECRETKEY123 200
2026-09-26 12:00:01 Info [EmbySharedPlaylist] Removal ok
2026-09-26 12:00:02 Error SQLiteException: database is locked
2026-09-26 12:00:03 Error [EmbySharedPlaylist] failure token=abc123 in handler
2026-09-26 12:00:04 Info X-Emby-Token: TOPSECRET fine
L
r=$(scan_log "$D/log.txt")
[[ $(jq -r '.locked' <<<"$r") == 1 ]] && ok "« database is locked » compté" || ko "locked : $r"
[[ $(jq -r '.exceptions' <<<"$r") == 1 && $(jq -r '.pluginErrors' <<<"$r") == 1 ]] && ok "exceptions et erreurs du plugin comptées" || ko "exceptions : $r"
if grep -Eq 'SECRETKEY123|abc123|TOPSECRET' <<<"$r"; then ko "secret non masqué dans l'extrait"; else ok "secrets masqués dans l'extrait"; fi
: > "$D/clean.txt"; echo "Info tout va bien" >> "$D/clean.txt"
r=$(scan_log "$D/clean.txt")
[[ $(jq -r '.locked+.exceptions+.pluginErrors' <<<"$r") == 0 ]] && ok "log propre : zéro" || ko "log propre : $r"

echo "== agrégation et décision"
build() { # LOGS_JSON MAXECHO P95 HMAX BAD5 CALLMAX
  local sc; sc=$(jq -nc --argjson e "$2" --argjson p "$3" --argjson m "$4" '{P1:{handlerMs:{n:50,p50:5,p95:$p,max:$m},outcomes:"OK:50 ",outcomeKo:0,maxEchoes:$e}}')
  jq -nc --argjson scen "$sc" --argjson calls "{\"n\":9,\"p50\":1,\"p95\":2,\"max\":$6}" --argjson trig '{}' --argjson logs "$1" --argjson prot true --argjson echomax 1 \
    --argjson it 50 --argjson rounds 10 --argjson it6 20 --argjson m123 0 --argjson nr 0 --argjson dr 0 --argjson m4 0 --argjson m5 0 \
    --argjson b5 "$5" --argjson m6 0 --argjson l6 0 --argjson e6 0 --argjson notes '[]' "$RESULTS_JQ"
}
CLEAN='{"lines":5,"locked":0,"exceptions":0,"pluginErrors":0,"excerpt":[]}'
R=$(build "$CLEAN" 1 120 900 0 800); [[ $(decide "$R") == "immédiat" ]] && ok "tout OK => immédiat" || ko "décision immédiat : $(decide "$R")"
R=$(build "$CLEAN" 1 350 900 0 800); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "p95 > 300 ms => repli" || ko "p95"
R=$(build "$CLEAN" 1 120 2500 0 800); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "max > 2 s => repli" || ko "max"
R=$(build "$CLEAN" 2 120 900 0 800); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "2 échos => repli (boucle)" || ko "échos"
R=$(build "$CLEAN" 1 120 900 1 800); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "ronde de rafale anormale => repli" || ko "rafale"
R=$(build "$CLEAN" 1 120 900 0 6000); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "appel > 5 s => repli" || ko "appel lent"
R=$(build '{"lines":9,"locked":1,"exceptions":1,"pluginErrors":0,"excerpt":[]}' 1 120 900 0 800); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "database is locked => repli" || ko "locked"
R=$(build null 1 120 900 0 800); [[ $(decide "$R") == "indéterminé" ]] && ok "logs indisponibles => indéterminé (jamais « immédiat »)" || ko "logs null : $(decide "$R")"
R=$(build null 1 350 900 0 800); [[ $(decide "$R") == "repli Task.Run sous verrou" ]] && ok "un KO l'emporte sur l'indéterminé" || ko "priorité KO"

[[ $fail == 0 ]] || { echo "ECHEC test-probe-offline" >&2; exit 1; }
echo "test-probe-offline : OK"
