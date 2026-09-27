#!/usr/bin/env bash
# test-propagation-flow-offline.sh — exécute 21-propagation.sh (I18-I26) DE BOUT EN BOUT contre un faux Emby
# + faux moteur v0.3.0 local (fake_emby2.py, propagation comprise) dans une copie temporaire du dépôt : aucun
# accès à emby2, aucun secret. Vérifie que le script lit bien les contrats, enchaîne les scénarios et détecte
# un moteur défaillant (ni retrait, ni propagation), ainsi que le SKIP propre de I20 sans compte restreint.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/propflow.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18799}

mkdir -p "$W/tests/integration" "$W/private" "$W/bin" "$W/src/EmbySharedPlaylist/Reconciliation"
cp "$HERE"/lib.sh "$HERE"/int-lib.sh "$HERE"/21-propagation.sh "$HERE"/20-etiquettes-retrait.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n_work/\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"
cat > "$W/bin/kubectl" <<'K'
#!/usr/bin/env bash
[[ "$*" == *"deployment/emby2"* && "$*" == *"-n media"* ]] || { echo "kubectl : cible inattendue $*" >&2; exit 9; }
[[ "$*" == *logs* ]] && echo "Info tout va bien"
K
chmod +x "$W/bin/kubectl"
# V1 exact (le seul « fonction à venir » restant) : stub HelpText.cs lu par I25 via $ROOT/src/... (pas de #51 réel ici).
cat > "$W/src/EmbySharedPlaylist/Reconciliation/HelpText.cs" <<'CS'
namespace EmbySharedPlaylist.Reconciliation;
public static class HelpText
{
    public const string V1 =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Deux étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist.\n" +
        "- propager-lu=OUI : l'état de lecture (lu, avancement) est propagé aux autres membres (fonction à venir).\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";
}
CS

users_env() { # USERS_ENV_PATH [avec-restreint]
  { printf 'TEST_U1_ID=%s\nTEST_U1_PW=pw1\n' "$(printf '1%.0s' {1..32})"
    printf 'TEST_U2_ID=%s\nTEST_U2_PW=pw2\n' "$(printf '2%.0s' {1..32})"
    printf 'TEST_U3_ID=%s\nTEST_U3_PW=pw3\n' "$(printf '3%.0s' {1..32})"
    if [[ ${1:-} == restricted ]]; then printf 'TEST_U_RESTRICTED_ID=%s\nTEST_U_RESTRICTED_PW=pwr\n' "$(printf '4%.0s' {1..32})"; fi
  }
}
POL='{"IsAdministrator":false,"BlockedTags":["x"]}'
snap() { jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}'; }

run() { # MODE RESTRICTED(yes|no) ARGS...
  local mode=$1 restr=$2; shift 2
  local fru; fru=$([[ $restr == yes ]] && echo 1 || echo 0)
  REPO_ROOT="$W" FAKE_RESTRICTED_USER="$fru" python3 -W ignore "$HERE/fake_emby2.py" "$PORT" "$mode" & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  users_env "$([[ $restr == yes ]] && echo restricted || echo "")" > "$W/private/test-users.env"
  snap > "$W/private/test-snapshot.json"
  if [[ $restr == yes ]]; then   # comme le ferait 00-setup-users.sh en réel : EnableAllFolders=false, EnabledFolders=[]
    curl -s -X POST "http://127.0.0.1:$PORT/emby/Users/$(printf '4%.0s' {1..32})/Policy" \
      -d '{"EnableAllFolders":false,"EnabledFolders":[]}' -o /dev/null
  fi
  set +e
  OUT=$(cd "$W" && env PATH="$W/bin:$PATH" WAIT_SCALE=0.05 SPIKE_OUT="$W/out" \
        bash tests/integration/21-propagation.sh "$@" 2>&1); RC=$?
  set -e
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
J() { ls -t "$W"/out/propagation-*.json | head -1; }
status_of() { jq -r --arg id "$1" '[.results[]|select(.id==$id)|.status]|first // "absent"' "$(J)"; }

echo "== 1. moteur conforme (avec compte restreint) : tous scénarios sauf I26 (regression testée séparément)"
run ok yes I18 I19 I20 I21 I22 I23 I24 I25
[[ $RC == 0 ]] && ok "code 0 (aucun KO)" || { ko "rc=$RC"; echo "$OUT" | grep -E "^  \[KO\]" | head -30; }
for id in I18.A.removed I18.A.propagated I18.C.removed I18.C.propagated I18.E.removed I18.E.propagated \
          I19.unchanged I19.othernew I20.noaccess I20.others I21.nopropagation I21.untouched \
          I22.read I23.S6a.L1removed I23.S6a.L2untouched I23.S6a.propagation I23.S6a.noecho I23.S6a.nojournalL2 \
          I23.S6b.bothremoved I23.S6b.bothpropagated I23.S6c.L2removed I23.S6c.L1untouched I23.S6c.notouch \
          I24.propagated I24.noloop I24.once I25.replaced I25.stillhelp I25.novenir I25.idempotent I25.untouched I25.alreadyV2 PROTECTED; do
  [[ $(status_of "$id") == OK ]] || ko "$id : $(status_of "$id")"
done
[[ $fail == 0 ]] && ok "tous les identifiants clés sont OK"
[[ $(status_of I20) == absent ]] && ok "I20 exécuté (pas de SKIP, compte restreint présent)" || ko "I20 statut inattendu"

echo "== 2. sans compte restreint : I20 SKIP explicite, reste inchangé"
run ok no I20 I21
[[ $RC == 0 ]] && ok "code 0" || ko "rc=$RC"
[[ $(status_of I20) == SKIP ]] && ok "I20 : SKIP (test_u_restricted absent)" || ko "I20 : $(status_of I20)"
[[ $(status_of I21.nopropagation) == OK ]] && ok "I21 toujours exécuté normalement" || ko "I21 : $(status_of I21.nopropagation)"

echo "== 3. moteur qui ne retire jamais (noremove) : matrice I18 détecte le défaut"
run noremove yes I18
[[ $RC == 1 ]] && ok "code 1 (au moins un KO)" || ko "rc=$RC"
[[ $(status_of I18.C.removed) == KO ]] && ok "I18.C.removed : KO (retrait attendu, absent)" || ko "I18.C.removed : $(status_of I18.C.removed)"
[[ $(status_of I18.C.propagated) == OK ]] && ok "I18.C.propagated : toujours OK (propagation indépendante du retrait)" || ko "I18.C.propagated : $(status_of I18.C.propagated)"

echo "== 4. moteur qui ne propage jamais (nopropagate) : matrice I18 et I19/I21/I22 détectent le défaut"
run nopropagate yes I18 I19
[[ $RC == 1 ]] && ok "code 1" || ko "rc=$RC"
[[ $(status_of I18.C.propagated) == KO ]] && ok "I18.C.propagated : KO (propagation attendue, absente)" || ko "I18.C.propagated : $(status_of I18.C.propagated)"
[[ $(status_of I18.C.removed) == OK ]] && ok "I18.C.removed : toujours OK (retrait indépendant de la propagation)" || ko "I18.C.removed : $(status_of I18.C.removed)"
[[ $(status_of I19.othernew) == KO ]] && ok "I19.othernew : KO (u1 jamais propagé)" || ko "I19.othernew : $(status_of I19.othernew)"

echo "== 5. scénario inconnu refusé"
run ok yes I99; [[ $RC != 0 ]] && ok "scénario inconnu refusé" || ko "scénario inconnu accepté"

echo "== 6. I26 (régression I0-I17) exécute réellement 20-etiquettes-retrait.sh"
run ok yes I26
[[ $RC == 0 ]] && ok "code 0" || { ko "rc=$RC"; echo "$OUT" | tail -40; }
[[ $(status_of I26) == OK ]] && ok "I26 : régression v0.2.0 verte" || ko "I26 : $(status_of I26)"

[[ $fail == 0 ]] || { echo "ECHEC test-propagation-flow-offline" >&2; exit 1; }
echo "test-propagation-flow-offline : OK"
