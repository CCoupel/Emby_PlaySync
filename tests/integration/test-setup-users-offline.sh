#!/usr/bin/env bash
# test-setup-users-offline.sh — 00-setup-users.sh contre le faux Emby (aucun accès à emby2) (#61, réserve M4) :
# les 4 médias de test viennent de la bibliothèque PlaySync-Tests ; un média virtuel est refusé AVANT toute création de compte.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/setupusers.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18796}
mkdir -p "$W/tests/integration" "$W/private"
cp "$HERE"/lib.sh "$HERE"/int-lib.sh "$HERE"/00-setup-users.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"
run() { # FAKE_VIRTUAL(0|1)
  rm -f "$W"/private/test-*
  FAKE_NO_TESTUSERS=1 FAKE_VIRTUAL=$1 python3 -W ignore "$HERE/fake_emby2.py" "$PORT" noautoshare & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  set +e; OUT=$(cd "$W" && bash tests/integration/00-setup-users.sh 2>&1); RC=$?; set -e
  USERS_AFTER=$(curl -s "http://127.0.0.1:$PORT/emby/Users")
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
echo "== 1. médias locaux PlaySync-Tests : setup complet"
run 0
[[ $RC == 0 ]] && ok "code 0" || { ko "rc=$RC"; echo "$OUT" | tail; }
echo "$OUT" | grep -q "4 médias" && ok "accès aux 4 médias de test vérifié" || ko "vérification des médias absente"
echo "== 2. médias virtuels : refus avant toute création de compte"
run 1
[[ $RC == 2 ]] && echo "$OUT" | grep -q "médias virtuels interdits en QUALIF (#61)" && ok "die (code 2) sur média virtuel" || { ko "rc=$RC"; echo "$OUT" | tail -5; }
[[ $(jq '[.[]|select(.Name|startswith("test_u"))]|length' <<<"$USERS_AFTER") == 0 ]] && ok "aucun compte test_* créé" || ko "comptes créés malgré le refus"
[[ $fail == 0 ]] || { echo "ECHEC test-setup-users-offline" >&2; exit 1; }
echo "test-setup-users-offline : OK"
