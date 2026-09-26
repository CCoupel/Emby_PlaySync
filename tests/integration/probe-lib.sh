#!/usr/bin/env bash
# probe-lib.sh — fonctions PURES (testables hors ligne) de la sonde de ré-entrance U11 (#52).
# Sourcé par 05-reentrancy-probe.sh et test-probe-offline.sh (après tests/spike/lib.sh).

# Filtre jq : événements Kind=Probe -> [{scenario, durationMs, echoes, outcome, ...}] (clés de Detail « k=v k=v »,
# ordre libre ; les clés d'événement sont déjà en camelCase grâce à la normalisation de lib.sh).
PROBE_JQ='[.[]|select(.kind=="Probe")|((.detail//"")|[scan("(\\w+)=(\\S+)")]|map({(.[0]):.[1]})|add)|select(.scenario!=null)]'

# probe_rows EVENTS_JSON -> lignes « scenario durationMs echoes outcome lockWaitMs echoKinds removed » ("-" si absent)
probe_rows() {
  jq -r "$PROBE_JQ | .[] | \"\(.scenario) \(.durationMs//0) \(.echoes//0) \(.outcome//\"?\") \(.lockWaitMs//0) \(.echoKinds//\"-\") \(.removed//\"-\")\"" <<<"$1" 2>/dev/null || true
}

# probe_removed_sum EVENTS_JSON SCENARIO : somme des « removed=<n> » des entrées Probe du scénario
probe_removed_sum() {
  jq -r --arg s "$2" "$PROBE_JQ | map(select(.scenario==\$s) | (.removed//\"0\") | tonumber? // 0) | add // 0" <<<"$1" 2>/dev/null || echo 0
}

# probes_seen EVENTS_JSON "P1 P2 P3" : 0 si tous les scénarios listés sont présents
probes_seen() {
  jq -e --arg want "$2" "$PROBE_JQ | (map(.scenario)|unique) as \$s | (\$want|split(\" \")) - \$s == []" <<<"$1" >/dev/null 2>&1
}

# stats_json FICHIER : un nombre par ligne -> {"n","p50","p95","max"} (rang le plus proche)
stats_json() {
  python3 - "$1" <<'PY'
import sys, json, math
try:
    v = sorted(float(x) for x in open(sys.argv[1]).read().split())
except FileNotFoundError:
    v = []
def pct(p):
    if not v: return None
    x = v[max(0, math.ceil(p / 100 * len(v)) - 1)]
    return int(x) if x == int(x) else x
print(json.dumps({"n": len(v), "p50": pct(50), "p95": pct(95), "max": (int(v[-1]) if v[-1] == int(v[-1]) else v[-1]) if v else None}))
PY
}

# scan_log FICHIER -> {"lines","locked","exceptions","pluginErrors","excerpt":[...]} ; secrets masqués.
scan_log() {
  local f=$1
  local mask='s/(api_key|apikey|token|x-emby-token|password|pw|authorization)([=:"[:space:]]+)[^&" ,;]+/\1\2***/Ig'
  local locked exc plug
  locked=$(grep -Eic 'database is locked|SQLITE_BUSY' "$f" || true)
  exc=$(grep -Eic 'exception' "$f" || true)
  plug=$(grep -F '[EmbySharedPlaylist]' "$f" | grep -Eic 'error|exception|fail' || true)
  local ex
  ex=$({ grep -Ei 'database is locked|SQLITE_BUSY|exception|\[EmbySharedPlaylist\].*(error|fail)' "$f" || true; } | head -n 10 | cut -c1-240 | sed -E "$mask" | jq -R . | jq -sc .)
  jq -nc --argjson l "$(wc -l < "$f" | tr -d ' ')" --argjson k "${locked:-0}" --argjson e "${exc:-0}" --argjson p "${plug:-0}" --argjson x "${ex:-[]}" \
    '{lines:$l, locked:$k, exceptions:$e, pluginErrors:$p, excerpt:$x}'
}

# decide RESULTS_JSON -> "immédiat" | "repli Task.Run sous verrou" | "indéterminé"
# critères : booléens (null = non vérifiable). Un seul false => repli ; sinon un null => indéterminé.
decide() {
  jq -r '[.criteria[]] as $c
    | if ($c|map(.ok)|any(.==false)) then "repli Task.Run sous verrou"
      elif ($c|map(.ok)|any(.==null)) then "indéterminé"
      else "immédiat" end' <<<"$1"
}

# Programme jq d'agrégation des résultats (args : scen calls trig logs prot it rounds it6 m123 nr dr m4 m5 b5 m6 l6 e6 notes)
RESULTS_JQ='  ([$scen[]|.handlerMs.p95]|map(select(.!=null))|max) as $p95max
  | ([$scen[]|.handlerMs.max]|map(select(.!=null))|max) as $hmax
  | {iterations:{p123:$it,p4:$it,p5Rounds:$rounds,p6:$it6},
     scenarios:$scen, restCallsMs:$calls, triggerCallsMs:$trig, emby_logs:$logs, protectedAccountsUnchanged:$prot,
     observed:{missing:{P1P2P3:$m123,P4:$m4,P5:$m5,P6:$m6}, p123RemovalMissing:$nr, p123RemovalDuplicated:$dr, p5BadRounds:$b5, p6LostTags:$l6, p6OwnerEditErrors:$e6},
     notes:$notes,
     criteria:{
       noCallOver5s:{ok:(($calls.max // 0) <= 5000), detail:"max appel REST \($calls.max // 0) ms"},
       noLockNoException:{ok:(if $logs==null then null else ($logs.locked==0 and $logs.exceptions==0 and $logs.pluginErrors==0) end), detail:(if $logs==null then "logs indisponibles" else "locked=\($logs.locked) exceptions=\($logs.exceptions) pluginErrors=\($logs.pluginErrors)" end)},
       noLoop:{ok:(([$scen[]|.maxEchoes]|max) <= $echomax), detail:"échos max \([$scen[]|.maxEchoes]|max) (seuil \($echomax)) ; types : \([$scen|to_entries[]|"\(.key): \(.value.echoKinds)"]|join(" | "))"},
       handlerLatency:{ok:(($p95max // 0) <= 300 and ($hmax // 0) <= 2000), detail:"p95 max \($p95max) ms, max \($hmax) ms"},
       burstOnce:{ok:($b5==0 and $m5==0), detail:"rondes anormales \($b5), non observées \($m5)"},
       removalOnce:{ok:($nr==0 and $dr==0), detail:"retrait absent \($nr), dupliqué \($dr)"},
       allObserved:{ok:($m123==0 and $m4==0 and $m5==0 and $m6==0), detail:"scénarios non observés P1-3:\($m123) P4:\($m4) P5:\($m5) P6:\($m6)"},
       allOutcomeOk:{ok:(([$scen[]|.outcomeKo]|add)==0), detail:"outcome!=OK : \([$scen[]|.outcomeKo]|add)"},
       ownerTagsKept:{ok:($l6==0 and $e6==0), detail:"étiquettes perdues \($l6), éditions en erreur \($e6)"},
       protectedAccounts:{ok:$prot, detail:"admin/cyril/user2 inchangés"}}}'
