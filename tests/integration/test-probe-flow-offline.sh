#!/usr/bin/env bash
# test-probe-flow-offline.sh — exécute 05-reentrancy-probe.sh DE BOUT EN BOUT contre un faux Emby local
# (fake_emby.py) dans une copie temporaire du dépôt : aucun accès à emby2, aucun secret.
# Vérifie le flux (préconditions, P1–P6, restauration de la config, décision) dans 4 situations.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/probeflow.XXXXXX")
SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18777}

mkdir -p "$W/tests/spike" "$W/tests/integration" "$W/private" "$W/bin"
cp "$REPO"/tests/spike/lib.sh "$W/tests/spike/"
cp "$HERE"/05-reentrancy-probe.sh "$HERE"/probe-lib.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n_work/\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"
cat > "$W/private/spike-users.env" <<'U'
TEST_U1_ID=11111111111111111111111111111111
TEST_U1_PW=pw1
TEST_U2_ID=22222222222222222222222222222222
TEST_U2_PW=pw2
TEST_U3_ID=33333333333333333333333333333333
TEST_U3_PW=pw3
U

# ids attendus par le faux serveur : 32 caractères identiques
sed -i 's/^TEST_U1_ID=.*/TEST_U1_ID='"$(printf '1%.0s' {1..32})"'/;s/^TEST_U2_ID=.*/TEST_U2_ID='"$(printf '2%.0s' {1..32})"'/;s/^TEST_U3_ID=.*/TEST_U3_ID='"$(printf '3%.0s' {1..32})"'/' "$W/private/spike-users.env"
POL='{"IsAdministrator":false,"BlockedTags":["x","y"]}'
jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}' > "$W/private/spike-snapshot.json"
printf 'apiVersion: v1\n' > "$W/private/kubeconfig.yml"
cat > "$W/bin/kubectl" <<'K'
#!/usr/bin/env bash
[[ "$*" == *"deployment/emby2"* && "$*" == *"-n media"* ]] || { echo "kubectl : cible inattendue $*" >&2; exit 9; }
if [[ -n ${FAKE_LOG_BAD:-} ]]; then echo "Error SQLiteException: database is locked"; else echo "Info tout va bien api_key=SECRET"; fi
K
chmod +x "$W/bin/kubectl"

run() { # MODE [env...] -> stdout du script ; code de retour dans RC
  python3 "$HERE/fake_emby.py" "$PORT" "$1" & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  shift
  set +e
  OUT=$(cd "$W" && env "$@" PITER=3 PROUNDS=2 PITER6=2 SPIKE_OUT="$W/out" bash tests/integration/05-reentrancy-probe.sh 2>&1); RC=$?
  set -e
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
decision() { grep -o 'DECISION : .*' <<<"$OUT" | sed 's/DECISION : //'; }

echo "== 1. tout OK (kubectl factice propre)"
run ok "PATH=$W/bin:$PATH"
[[ $(decision) == "immédiat" && $RC == 0 ]] && ok "décision immédiat, code 0" || { ko "décision '$(decision)' rc=$RC"; echo "$OUT" | tail -120; }
grep -q "configuration du plugin restaurée" <<<"$OUT" && ok "configuration restaurée en fin de script" || ko "restauration absente"
grep -q "SECRET" <<<"$OUT" && ko "secret de log affiché" || ok "aucun secret de log dans la sortie"
[[ -s $(ls "$W"/out/reentrancy-*.json | head -1) ]] && ok "JSON de preuves écrit" || ko "pas de JSON"
jq -e '.partial==false and (.scenarios|keys==["P1","P2","P3","P4","P5","P6"]) and .criteria.burstOnce.ok==true' "$W"/out/reentrancy-*.json >/dev/null && ok "P1–P6 observés, rafale OK" || ko "contenu du JSON"
[[ ! -e $W/private/reentrancy-config.bak.json ]] && ok "sauvegarde de config supprimée après restauration" || ko "sauvegarde de config restante"

echo "== 2. gestionnaire lent (900 ms)"
rm -f "$W"/out/*; run slow "PATH=$W/bin:$PATH"
[[ $(decision) == "repli Task.Run sous verrou" && $RC != 0 ]] && ok "p95 > 300 ms => repli, code non nul" || ko "décision '$(decision)' rc=$RC"

echo "== 3. retrait dupliqué (rafale)"
rm -f "$W"/out/*; run dup "PATH=$W/bin:$PATH"
[[ $(decision) == "repli Task.Run sous verrou" ]] && ok "retrait dupliqué => repli" || ko "décision '$(decision)'"

echo "== 4. logs : erreur SQLite / kubectl absent"
rm -f "$W"/out/*; run ok "PATH=$W/bin:$PATH" FAKE_LOG_BAD=1
[[ $(decision) == "repli Task.Run sous verrou" ]] && ok "database is locked dans les logs => repli" || ko "décision '$(decision)'"
rm -f "$W"/out/*; mv "$W/private/kubeconfig.yml" "$W/private/kubeconfig.off"; run ok "PATH=$W/bin:$PATH"
[[ $(decision) == "indéterminé" ]] && ok "logs indisponibles => indéterminé (pas « immédiat »)" || ko "décision '$(decision)'"

[[ $fail == 0 ]] || { echo "ECHEC test-probe-flow-offline" >&2; exit 1; }
echo "test-probe-flow-offline : OK"
