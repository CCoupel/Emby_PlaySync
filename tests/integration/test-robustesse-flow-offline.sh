#!/usr/bin/env bash
# test-robustesse-flow-offline.sh — exécute 24-robustesse.sh (I41-I48, sans --restart) DE BOUT EN BOUT contre un faux
# Emby + faux moteur v0.5.0 local (fake_emby2.py) dans une copie temporaire du dépôt : aucun accès à emby2, aucun
# secret. Vérifie que le script lit bien les contrats et enchaîne correctement les scénarios.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/robflow.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18830}

mkdir -p "$W/tests/integration" "$W/private"
cp "$HERE"/lib.sh "$HERE"/int-lib.sh "$HERE"/24-robustesse.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n_work/\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"

users_env() {
  printf 'TEST_U1_ID=%s\nTEST_U1_PW=pw1\n' "$(printf '1%.0s' {1..32})"
  printf 'TEST_U2_ID=%s\nTEST_U2_PW=pw2\n' "$(printf '2%.0s' {1..32})"
  printf 'TEST_U3_ID=%s\nTEST_U3_PW=pw3\n' "$(printf '3%.0s' {1..32})"
}
POL='{"IsAdministrator":false,"BlockedTags":["x"]}'
snap() { jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}'; }

run() { # ARGS...
  python3 -W ignore "$HERE/fake_emby2.py" "$PORT" ok & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  users_env > "$W/private/test-users.env"
  snap > "$W/private/test-snapshot.json"
  set +e
  OUT=$(cd "$W" && env WAIT_SCALE=0.05 SPIKE_OUT="$W/out" \
        bash tests/integration/24-robustesse.sh "$@" 2>&1); RC=$?
  set -e
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
J() { ls -t "$W"/out/robustesse-*.json | head -1; }
status_of() { jq -r --arg id "$1" '[.results[]|select(.id==$id)|.status]|first // "absent"' "$(J)"; }

echo "== 1. moteur conforme : tous scénarios (sans --restart, I48 en SKIP attendu)"
run
[[ $RC == 0 ]] && ok "code 0 (aucun KO)" || { ko "rc=$RC"; echo "$OUT" | grep -E "^  \[KO\]" | head -60; }
for id in I41.owner I41.removed I41.noerror \
          I42.witness I42.noerror I42.gone \
          I43.nocrash I43.noerror I43.observed \
          I44.noerror I44.noplayed \
          I45.confirmempty I45.tags I45.noerror \
          I46.owner I46.readonly I46.extra1 I46.extra2 I46.latency I46.aggregate \
          I47.allremoved I47.latency I47.noerror \
          PROTECTED; do
  [[ $(status_of "$id") == OK ]] || ko "$id : $(status_of "$id")"
done
[[ $(status_of I48) == SKIP ]] && ok "I48 : SKIP (--restart absent, attendu hors ligne)" || ko "I48 : $(status_of I48)"
[[ $fail == 0 ]] && ok "tous les identifiants clés sont OK"

echo "== 2. scénario inconnu refusé"
run I99; [[ $RC != 0 ]] && ok "scénario inconnu refusé" || ko "scénario inconnu accepté"

echo "== 3. sous-ensemble (I41 seul)"
run I41
[[ $RC == 0 ]] && ok "code 0" || ko "rc=$RC"
[[ $(status_of I41.removed) == OK ]] && ok "I41 seul : OK" || ko "I41 seul : $(status_of I41.removed)"

[[ $fail == 0 ]] || { echo "ECHEC test-robustesse-flow-offline" >&2; exit 1; }
echo "test-robustesse-flow-offline : OK"
