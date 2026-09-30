#!/usr/bin/env bash
# test-avancement-flow-offline.sh — exécute 22-avancement.sh (I27-I44) DE BOUT EN BOUT contre un faux Emby
# + faux moteur v1.2.1 local (fake_emby2.py, propagation de position comprise, trois familles, aucune garde D-c, propagation périodique,
# règle de fin de lecture, cibles mémorisées, Progress tardif ignoré — intervalle réduit FAKE_MIN_INTERVAL/PROGRESS_WAIT) dans une copie temporaire du
# dépôt : aucun accès à emby2, aucun secret. Vérifie que le script lit bien les contrats et enchaîne les scénarios.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/avflow.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18810}

mkdir -p "$W/tests/integration" "$W/private" "$W/bin"
cp "$HERE"/lib.sh "$HERE"/int-lib.sh "$HERE"/22-avancement.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n_work/\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"
cat > "$W/bin/kubectl" <<'K'
#!/usr/bin/env bash
exit 9
K
chmod +x "$W/bin/kubectl"

users_env() {
  printf 'TEST_U1_ID=%s\nTEST_U1_PW=pw1\n' "$(printf '1%.0s' {1..32})"
  printf 'TEST_U2_ID=%s\nTEST_U2_PW=pw2\n' "$(printf '2%.0s' {1..32})"
  printf 'TEST_U3_ID=%s\nTEST_U3_PW=pw3\n' "$(printf '3%.0s' {1..32})"
}
POL='{"IsAdministrator":false,"BlockedTags":["x"]}'
snap() { jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}'; }

run() { # MODE ARGS...
  local mode=$1; shift
  FAKE_MIN_INTERVAL=1.5 python3 -W ignore "$HERE/fake_emby2.py" "$PORT" "$mode" & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  users_env > "$W/private/test-users.env"
  snap > "$W/private/test-snapshot.json"
  set +e
  OUT=$(cd "$W" && env PATH="$W/bin:$PATH" WAIT_SCALE=0.05 PROGRESS_WAIT=1.7 SPIKE_OUT="$W/out" \
        bash tests/integration/22-avancement.sh "$@" 2>&1); RC=$?
  set -e
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
J() { ls -t "$W"/out/avancement-*.json | head -1; }
status_of() { jq -r --arg id "$1" '[.results[]|select(.id==$id)|.status]|first // "absent"' "$(J)"; }

echo "== 1. moteur conforme : tous scénarios"
run ok
[[ $RC == 0 ]] && ok "code 0 (aucun KO)" || { ko "rc=$RC"; echo "$OUT" | grep -E "^  \[KO\]" | head -40; }
for id in I27.propagated I27.notplayed I28.propagated I28.journal \
          I29.A I29.B I29.C I29.D I29.E \
          I30.noposition I30.nojournal \
          I31.first I31.stillsame I31.samejournal \
          I32.removed I32.propagatedplayed I32.noposition I32.nojournal \
          I33.propagated I33.notransitivity I33.noecho \
          I37 I37.noguard I37.noerror \
          I38.pause I38.stop I38.flags I38.stays I38.journal I38.noguard I38.norm \
          I39.position I39.l1_stays I39.s6 I39.no_lu_chain I39.u14b \
          I40.A.position I40.A.nolu I40.A.stays I40.B.nopos I40.B.lu I40.B.stillnopos I40.B.stays \
          I35.first I35.throttled I35.nojournal \
          I41.t0 I41.t1 I41.t2 I41.counter I41.nojournal I41.pause I41.pausejournal I41.heartbeat I41.s6 I41.l1stays \
          I42.rm.continuous I42.rm.played I42.rm.removed I42.rm.zero I42.rm.journal \
          I42.norm.continuous I42.norm.played I42.norm.stays I42.norm.zero I42.norm.journal \
          I43.position I43.stays I43.u14b I44.zero I44.nolate \
          I36.others I36.aggregate I36.permember I36.noerror \
          PROTECTED; do
  [[ $(status_of "$id") == OK ]] || ko "$id : $(status_of "$id")"
done
[[ $fail == 0 ]] && ok "tous les identifiants clés sont OK"
[[ $(status_of I34) == OK ]] && ok "I34 (pause réelle) : propagée dans le faux moteur" || ko "I34 : $(status_of I34)"

echo "== 2. moteur qui ne propage jamais (nopropagate) : détecte le défaut"
run nopropagate I27
[[ $RC == 1 ]] && ok "code 1" || ko "rc=$RC"
[[ $(status_of I27.propagated) == KO ]] && ok "I27.propagated : KO (position jamais propagée)" || ko "I27.propagated : $(status_of I27.propagated)"

echo "== 2b. défauts v1.2.0 détectés (moteur défaillant simulé)"
run dcguard I38
[[ $RC == 1 ]] && ok "dcguard : code 1" || ko "dcguard rc=$RC"
[[ $(status_of I38.pause) == KO ]] && ok "I38.pause : KO (garde D-c « déclencheur déjà lu » encore active : #57 non corrigé)" || ko "I38.pause : $(status_of I38.pause)"
run legacyavancement I29
[[ $RC == 1 ]] && ok "legacyavancement : code 1" || ko "legacyavancement rc=$RC"
[[ $(status_of I29.E) == KO ]] && ok "I29.E : KO (propager-lu couvre encore la position : BREAKING D21 non appliqué)" || ko "I29.E : $(status_of I29.E)"
run nativeleak I39
[[ $RC == 1 ]] && ok "nativeleak : code 1" || ko "nativeleak rc=$RC"
[[ $(status_of I39.s6) == KO ]] && ok "I39.s6 : KO (le « lu » natif d'origine plugin retire le média d'une autre liste : violation de S6)" || ko "I39.s6 : $(status_of I39.s6)"
run nativeplayed I39
[[ $RC == 0 ]] && ok "nativeplayed : code 0 (lu natif d'origine plugin, sans effet moteur)" || { ko "nativeplayed rc=$RC"; echo "$OUT" | grep -E "^  \[KO\]" | head -20; }

echo "== 2c. défaut v1.2.0 du bug #58 détecté (pas de propagation en lecture continue, fin de lecture sans cibles mémorisées)"
run v120 I41 I42
[[ $RC == 1 ]] && ok "v120 : code 1" || ko "v120 rc=$RC"
[[ $(status_of I41.t1) == KO ]] && ok "I41.t1 : KO (le membre ne suit pas une lecture continue sans pause : bug #58)" || ko "I41.t1 : $(status_of I41.t1)"
[[ $(status_of I42.rm.continuous) == KO ]] && ok "I42.rm.continuous : KO (aucune propagation avant la fin : bug #58)" || ko "I42.rm.continuous : $(status_of I42.rm.continuous)"

echo "== 3. scénario inconnu refusé"
run ok I99; [[ $RC != 0 ]] && ok "scénario inconnu refusé" || ko "scénario inconnu accepté"

[[ $fail == 0 ]] || { echo "ECHEC test-avancement-flow-offline" >&2; exit 1; }
echo "test-avancement-flow-offline : OK"
