#!/usr/bin/env bash
# test-propagation-flow-offline.sh — exécute 21-propagation.sh (I18-I26) DE BOUT EN BOUT contre un faux Emby
# + faux moteur v0.3.0 local (fake_emby2.py, propagation comprise) dans une copie temporaire du dépôt : aucun
# accès à emby2, aucun secret. Vérifie que le script lit bien les contrats, enchaîne les scénarios, détecte un
# moteur défaillant (ni retrait, ni propagation), et que le compte restreint (I20/R8, test_u4) est bien créé
# ET nettoyé dans le MÊME run (autonome, comme demandé : qa n'a rien à préparer pour R8).
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
# V1/V2/V3 exacts (marqueurs « fonction à venir », « aussi propagé », « Trois étiquettes ») : stub HelpText.cs lu par I25 via
# $ROOT/src/... (pas de HelpText réel ici) ; le faux moteur remplace V1 et V2 par V3 (causes v1-to-v3 / v2-to-v3, v1.2.0).
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
    public const string V2 =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Deux étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist.\n" +
        "- propager-lu=OUI : quand un média est lu par un membre, le flag « lu » est posé chez les autres ; l'avancement de lecture (position, pause) est aussi propagé.\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";
    public const string V3 =
        "Playlist partagée gérée par Emby Shared Playlist.\n" +
        "Trois étiquettes (Modifier les métadonnées > Mot-clé) règlent son comportement. Elles sont à NON par défaut : rien ne change.\n" +
        "- propager-lu=OUI : quand un membre passe un média à « lu », le « lu » est posé chez les autres membres.\n" +
        "- remove-si-lu=OUI : un média qui passe à « lu » est retiré de la playlist (seulement si propager-lu=OUI).\n" +
        "- propager-avancement=OUI : la position de lecture (pause, arrêt) est recopiée chez les autres membres, sans toucher au « lu ».\n" +
        "Pour activer une option, remplacez NON par OUI : ajoutez l'étiquette « ...=OUI » et retirez « ...=NON » (si les deux sont présentes, NON l'emporte).";
}
CS

users_env() {
  printf 'TEST_U1_ID=%s\nTEST_U1_PW=pw1\n' "$(printf '1%.0s' {1..32})"
  printf 'TEST_U2_ID=%s\nTEST_U2_PW=pw2\n' "$(printf '2%.0s' {1..32})"
  printf 'TEST_U3_ID=%s\nTEST_U3_PW=pw3\n' "$(printf '3%.0s' {1..32})"
}
POL='{"IsAdministrator":false,"BlockedTags":["x"]}'
snap() { jq -nSc --argjson p "$POL" '{users:["admin","cyril","user2"], policies:{admin:($p|.IsAdministrator=true), cyril:$p, user2:$p}}'; }

run() { # MODE ARGS...
  local mode=$1; shift
  REPO_ROOT="$W" python3 -W ignore "$HERE/fake_emby2.py" "$PORT" "$mode" & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  users_env > "$W/private/test-users.env"
  snap > "$W/private/test-snapshot.json"
  set +e
  OUT=$(cd "$W" && env PATH="$W/bin:$PATH" WAIT_SCALE=0.05 SPIKE_OUT="$W/out" \
        bash tests/integration/21-propagation.sh "$@" 2>&1); RC=$?
  set -e
  USERS_AFTER=$(curl -s "http://127.0.0.1:$PORT/emby/Users")   # avant de tuer le serveur : sert à vérifier le nettoyage de test_u4
  kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
J() { ls -t "$W"/out/propagation-*.json | head -1; }
status_of() { jq -r --arg id "$1" '[.results[]|select(.id==$id)|.status]|first // "absent"' "$(J)"; }

echo "== 1. moteur conforme : tous scénarios sauf I26 (régression testée séparément)"
run ok I18 I19 I20 I21 I22 I23 I24 I25
[[ $RC == 0 ]] && ok "code 0 (aucun KO)" || { ko "rc=$RC"; echo "$OUT" | tail -30; }
for id in I18.A.removed I18.A.propagated I18.C.removed I18.C.propagated I18.E.removed I18.E.propagated \
          I19.unchanged I19.othernew I19.aggregate I19.permember \
          I20.others I20.aggregate I20.permember I20.noerror \
          I21.nopropagation I21.untouched I22.read \
          I23.S6a.L1removed I23.S6a.L2untouched I23.S6a.propagation I23.S6a.noecho I23.S6a.nojournalL2 \
          I23.S6b.bothremoved I23.S6b.bothpropagated I23.S6c.L2removed I23.S6c.L1untouched I23.S6c.notouch \
          I24.propagated I24.echoconsumed I24.once \
          I25.replaced I25.journal I25.nov1v2 I25.stillhelp I25.novenir I25.idempotent I25.v2replaced I25.v2journal I25.untouched I25.alreadyV3 PROTECTED; do
  [[ $(status_of "$id") == OK ]] || ko "$id : $(status_of "$id")"
done
[[ $fail == 0 ]] && ok "tous les identifiants clés sont OK"
ok "I20 exécuté (le compte restreint est créé par le scénario lui-même, plus de SKIP possible)"
[[ $(jq -e '[.[]|select(.Name=="test_u4")]|length' <<<"$USERS_AFTER") == 0 ]] && ok "test_u4 n'existe plus après le run (créé ET nettoyé dans le même run)" || ko "test_u4 encore présent après le run : fuite de compte"

echo "== 1b. défaut v1.2.0 : retrait SANS propager-lu (retraitseul, comportement v0.2.0-v1.1.0) : la matrice I18 (lignes E/F/G) le détecte"
run retraitseul I18
[[ $RC == 1 ]] && ok "code 1" || ko "rc=$RC"
[[ $(status_of I18.E.removed) == KO ]] && ok "I18.E.removed : KO (remove-si-lu=OUI seul retire encore : D21/S3b non appliqué)" || ko "I18.E.removed : $(status_of I18.E.removed)"
[[ $(status_of I18.C.removed) == OK ]] && ok "I18.C.removed : OK (les deux actives : retrait attendu)" || ko "I18.C.removed : $(status_of I18.C.removed)"

echo "== 2. moteur qui ne retire jamais (noremove) : matrice I18 détecte le défaut"
run noremove I18
[[ $RC == 1 ]] && ok "code 1 (au moins un KO)" || ko "rc=$RC"
[[ $(status_of I18.C.removed) == KO ]] && ok "I18.C.removed : KO (retrait attendu, absent)" || ko "I18.C.removed : $(status_of I18.C.removed)"
[[ $(status_of I18.C.propagated) == OK ]] && ok "I18.C.propagated : toujours OK (propagation indépendante du retrait)" || ko "I18.C.propagated : $(status_of I18.C.propagated)"

echo "== 3. moteur qui ne propage jamais (nopropagate) : matrice I18 et I19 détectent le défaut"
run nopropagate I18 I19
[[ $RC == 1 ]] && ok "code 1" || ko "rc=$RC"
[[ $(status_of I18.C.propagated) == KO ]] && ok "I18.C.propagated : KO (propagation attendue, absente)" || ko "I18.C.propagated : $(status_of I18.C.propagated)"
[[ $(status_of I18.C.removed) == OK ]] && ok "I18.C.removed : toujours OK (retrait indépendant de la propagation)" || ko "I18.C.removed : $(status_of I18.C.removed)"
[[ $(status_of I19.othernew) == KO ]] && ok "I19.othernew : KO (u1 jamais propagé)" || ko "I19.othernew : $(status_of I19.othernew)"

echo "== 4. scénario inconnu refusé"
run ok I99; [[ $RC != 0 ]] && ok "scénario inconnu refusé" || ko "scénario inconnu accepté"

echo "== 5. I26 (régression I0-I17) exécute réellement 20-etiquettes-retrait.sh"
run ok I26
[[ $RC == 0 ]] && ok "code 0" || { ko "rc=$RC"; echo "$OUT" | tail -40; }
[[ $(status_of I26) == OK ]] && ok "I26 : régression v0.2.0 verte" || ko "I26 : $(status_of I26)"
echo "$OUT" | grep -q "nettoyage final" && ok "nettoyage final exécuté (imbrication I26)" || ko "nettoyage final absent de la sortie"
[[ ! -s "$W/private/test-state.json" ]] && ok "private/test-state.json vidé après coup (playlists des deux scripts nettoyées)" || ko "test-state.json encore rempli après le run"

[[ $fail == 0 ]] || { echo "ECHEC test-propagation-flow-offline" >&2; exit 1; }
echo "test-propagation-flow-offline : OK"
