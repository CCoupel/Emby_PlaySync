#!/usr/bin/env bash
# test-int-flow-offline.sh — exécute 20-etiquettes-retrait.sh (I0–I17, avec --restart) DE BOUT EN BOUT contre un faux Emby
# + faux moteur v0.2.0 local (fake_emby2.py) dans une copie temporaire du dépôt : aucun accès à emby2, aucun secret.
# Vérifie que le script lit bien les contrats Diagnostics, enchaîne les scénarios et détecte un moteur défaillant.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO=$(cd "$HERE/../.." && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/intflow.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18788}

mkdir -p "$W/tests/spike" "$W/tests/integration" "$W/private" "$W/bin"
cp "$REPO"/tests/spike/lib.sh "$W/tests/spike/"
cp "$HERE"/20-etiquettes-retrait.sh "$HERE"/probe-lib.sh "$HERE"/int-lib.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n_work/\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"
{ printf 'TEST_U1_ID=%s\nTEST_U1_PW=pw1\n' "$(printf '1%.0s' {1..32})"
  printf 'TEST_U2_ID=%s\nTEST_U2_PW=pw2\n' "$(printf '2%.0s' {1..32})"
  printf 'TEST_U3_ID=%s\nTEST_U3_PW=pw3\n' "$(printf '3%.0s' {1..32})"; } > "$W/private/spike-users.env"
POL='{"IsAdministrator":false,"BlockedTags":["x"]}'
jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}' > "$W/private/spike-snapshot.json"
printf 'apiVersion: v1\n' > "$W/private/kubeconfig.yml"
cat > "$W/bin/kubectl" <<K
#!/usr/bin/env bash
[[ "\$*" == *"deployment/emby2"* && "\$*" == *"-n media"* ]] || { echo "kubectl : cible inattendue \$*" >&2; exit 9; }
case "\$*" in
  *"rollout restart"*) curl -s -X POST "http://127.0.0.1:$PORT/emby/__reset" >/dev/null ;;
  *"rollout status"*) ;;
  *logs*) if [[ -n \${FAKE_LOG_BAD:-} ]]; then echo "Error SQLiteException: database is locked"; else echo "Info ok token=SECRET"; fi ;;
esac
K
chmod +x "$W/bin/kubectl"

run() { # MODE ARGS_SCRIPT... (env via ENVX)
  python3 -W ignore "$HERE/fake_emby2.py" "$PORT" "$1" & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  shift; set +e
  OUT=$(cd "$W" && env PATH="$W/bin:$PATH" WAIT_SCALE=0.05 SPIKE_OUT="$W/out" ${ENVX:-} bash tests/integration/20-etiquettes-retrait.sh "$@" 2>&1); RC=$?
  set -e; kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
J() { ls -t "$W"/out/integration-*.json | head -1; }
status_of() { jq -r --arg id "$1" '[.results[]|select(.id==$id)|.status]|first // "absent"' "$(J)"; }

echo "== 1. moteur conforme : I0–I17 avec --restart"
ENVX="" run ok --restart
[[ $RC == 0 ]] && ok "code 0 (aucun KO)" || { ko "rc=$RC"; echo "$OUT" | grep -E "KO|ERREUR|ECHEC" | head -30; }
jq -e '.partial==false and .summary.ko==0' "$(J)" >/dev/null && ok "JSON de preuves complet, 0 KO" || ko "JSON : $(jq -c .summary "$(J)")"
for id in I0.state I1.tags I1.help I1.f1 I2.non I2.both I2.case I2.prop I2.space I2.oui I2.oui.views I3.removed I3.others I5.dup I5.all I5.journal I5.other I5.again I6.private I6.notag I6.public I7.stays I8a.noimmediate I8a.final I8a.active I8b.final I8c.pass2 I9.kept I9.rewritten I10.immediate I10.nodup I11.removed I12.reread I12.toggle I13.action I13.seen I14.distinct I14.once I14.same I15.removed I15.nodup I16.posed  I16.removal  I16.norepose I17.max I17.p95 I17.fast LOGS PROTECTED; do
  [[ $(status_of "$id") == OK ]] && : || ko "$id : $(status_of "$id")"
done
[[ $fail == 0 ]] && ok "tous les identifiants clés sont OK"
[[ $(status_of I4) == SKIP ]] && ok "I4 : SKIP explicite (Emby ne marque pas lu sur Progress dans le faux)" || ko "I4 : $(status_of I4)"

echo "== 2. sous-ensemble et scénario inconnu"
rm -f "$W"/out/*; ENVX="" run ok I1 I3
[[ $RC == 0 && $(jq '.results|map(select(.id|startswith("I1")))|length>0' "$(J)") == true && $(jq '[.results[]|select(.id|startswith("I2"))]|length' "$(J)") == 0 ]] && ok "I1 I3 seulement" || ko "sous-ensemble"
ENVX="" run ok I99; [[ $RC != 0 ]] && ok "scénario inconnu refusé" || ko "scénario inconnu accepté"

echo "== 3. moteur défaillant (ne retire rien)"
rm -f "$W"/out/*; ENVX="" run noremove I3 I5 I11
[[ $RC == 1 && $(status_of I3.removed) == KO && $(status_of I11.removed) == KO ]] && ok "retraits manquants => KO, code 1" || ko "noremove : rc=$RC $(status_of I3.removed)"

echo "== 4. logs Emby en erreur"
rm -f "$W"/out/*; ENVX="FAKE_LOG_BAD=1" run ok I1
[[ $RC == 1 && $(status_of LOGS) == KO ]] && ok "database is locked => LOGS KO" || ko "logs : rc=$RC $(status_of LOGS)"

echo "== 5. défaut simulé : repose sur événement d'une playlist déjà vue"
rm -f "$W"/out/*; ENVX="" run repose I8 I13
[[ $RC == 1 ]] && { [[ $(status_of I13.seen) == KO || $(status_of I8a.noimmediate) == KO ]] && ok "repose détectée (I13.seen / I8a.noimmediate KO)" || ko "repose non détectée"; } || ko "rc=$RC"

[[ $fail == 0 ]] || { echo "ECHEC test-int-flow-offline" >&2; exit 1; }
echo "test-int-flow-offline : OK"
