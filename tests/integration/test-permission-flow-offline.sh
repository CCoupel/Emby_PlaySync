#!/usr/bin/env bash
# test-permission-flow-offline.sh — exécute 23-permission.sh (I36-I41) DE BOUT EN BOUT contre un faux Emby + faux
# moteur v0.4.0 local (fake_emby2.py, simulation AutoSharing comprise) dans une copie temporaire du dépôt : aucun
# accès à emby2, aucun secret. Vérifie que le script lit bien les contrats et enchaîne correctement les scénarios.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/permflow.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18820}

mkdir -p "$W/tests/integration" "$W/private"
cp "$HERE"/lib.sh "$HERE"/int-lib.sh "$HERE"/23-permission.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n_work/\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"

users_env() {
  printf 'TEST_U1_ID=%s\nTEST_U1_PW=pw1\n' "$(printf '1%.0s' {1..32})"
  printf 'TEST_U2_ID=%s\nTEST_U2_PW=pw2\n' "$(printf '2%.0s' {1..32})"
  printf 'TEST_U3_ID=%s\nTEST_U3_PW=pw3\n' "$(printf '3%.0s' {1..32})"
}
POL='{"IsAdministrator":false,"BlockedTags":["x"]}'
snap() { jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}'; }

run() { # MODE ARGS...
  local mode=$1; shift
  python3 -W ignore "$HERE/fake_emby2.py" "$PORT" "$mode" & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  users_env > "$W/private/test-users.env"
  snap > "$W/private/test-snapshot.json"
  set +e
  OUT=$(cd "$W" && env WAIT_SCALE=0.05 SPIKE_OUT="$W/out" \
        bash tests/integration/23-permission.sh "$@" 2>&1); RC=$?
  set -e
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
J() { ls -t "$W"/out/permission-*.json | head -1; }
status_of() { jq -r --arg id "$1" '[.results[]|select(.id==$id)|.status]|first // "absent"' "$(J)"; }

echo "== 1. moteur conforme : tous scénarios"
run ok
[[ $RC == 0 ]] && ok "code 0 (aucun KO)" || { ko "rc=$RC"; echo "$OUT" | grep -E "^  \[KO\]" | head -40; }
for id in I36.u1 I36.u2 I36.u3 I36.journal1 I36.journal2 I36.journal3 I36.summary \
          I37.immediate I37.journal \
          I38.setup I38.unchanged I38.noposed \
          I39.noimmediate I39.nopass I39.nojournal \
          I40.setup I40.unchecked I40.reactivated I40.journal \
          I41.setup I41.untouched I41.nojournal \
          PROTECTED; do
  [[ $(status_of "$id") == OK ]] || ko "$id : $(status_of "$id")"
done
[[ $fail == 0 ]] && ok "tous les identifiants clés sont OK"

echo "== 2. moteur qui ne pose jamais la permission (noautoshare) : détecte le défaut"
run noautoshare I36
[[ $RC == 1 ]] && ok "code 1" || ko "rc=$RC"
[[ $(status_of I36.u1) == KO ]] && ok "I36.u1 : KO (permission jamais posée)" || ko "I36.u1 : $(status_of I36.u1)"

echo "== 3. scénario inconnu refusé"
run ok I99; [[ $RC != 0 ]] && ok "scénario inconnu refusé" || ko "scénario inconnu accepté"

[[ $fail == 0 ]] || { echo "ECHEC test-permission-flow-offline" >&2; exit 1; }
echo "test-permission-flow-offline : OK"
