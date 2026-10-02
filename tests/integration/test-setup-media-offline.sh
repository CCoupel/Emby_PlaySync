#!/usr/bin/env bash
# test-setup-media-offline.sh — 01-setup-media.sh --check contre le faux Emby + un faux kubectl (aucun accès à emby2) (#61).
# Vérifie : état complet => code 0 ; fichiers manquants => code 1 ; en --check AUCUNE écriture (le faux kubectl refuse tout sauf `ls`),
# jamais d'autre cible que deployment/emby2 -n media.
set -euo pipefail
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
W=$(mktemp -d "${TMPDIR:-/tmp}/setupmedia.XXXXXX"); SRV=""
trap '[[ -z $SRV ]] || kill $SRV 2>/dev/null; rm -rf "$W"' EXIT
fail=0; ok() { echo "  [OK] $1"; }; ko() { echo "  [KO] $1"; fail=1; }
PORT=${PORT:-18797}
mkdir -p "$W/tests/integration" "$W/private" "$W/bin"
cp "$HERE"/lib.sh "$HERE"/01-setup-media.sh "$W/tests/integration/"
(cd "$W" && git init -q && printf 'private/*\n' > .gitignore)
printf 'EMBY_URL=http://127.0.0.1:%s/emby\nEMBY_API_KEY=fakekey\n' "$PORT" > "$W/private/qualif.env"
printf 'apiVersion: v1\n' > "$W/private/kubeconfig.yml"
cat > "$W/bin/ffmpeg" <<'F'
#!/usr/bin/env bash
echo "$*" >> "$FFLOG"; for a; do last=$a; done; echo fake > "$last"   # dernier argument = fichier de sortie
F
chmod +x "$W/bin/ffmpeg"
cat > "$W/bin/kubectl" <<'K'
#!/usr/bin/env bash
echo "$*" >> "$KLOG"
[[ "$*" == *"deployment/emby2"* && "$*" == *"-n media"* ]] || { echo "kubectl : cible inattendue $*" >&2; exit 9; }
if [[ "$*" == *"ls -1 "* ]]; then cat "$PODFILES" 2>/dev/null || true; exit 0; fi
[[ ${ALLOW_WRITE:-0} == 1 ]] || { echo "kubectl : écriture refusée en --check : $*" >&2; exit 8; }
if [[ "$*" == *"mv "* ]]; then cat >/dev/null; grep -o "PlaySync-Test-[0-9]*\.mp4'$" <<<"$*" | tr -d "'" >> "$PODFILES"; fi   # copie : le fichier apparaît dans le pod
exit 0
K
chmod +x "$W/bin/kubectl"
run() { # FAKE_FILES [options du script]
  local n=$1; local ARGS=--check; if [[ ${2:-} == --run ]]; then ARGS=; fi   # --run = exécution réelle (sans option)
  : > "$W/podfiles"; for ((i=1; i<=n; i++)); do printf 'PlaySync-Test-%02d.mp4\n' "$i" >> "$W/podfiles"; done
  python3 -W ignore "$HERE/fake_emby2.py" "$PORT" ok & SRV=$!
  for _ in $(seq 25); do curl -s -o /dev/null "http://127.0.0.1:$PORT/emby/System/Info" && break; sleep 0.2; done
  set +e
  OUT=$(cd "$W" && env PATH="$W/bin:$PATH" KLOG="$W/kubectl.log" FFLOG="$W/ffmpeg.log" PODFILES="$W/podfiles" ALLOW_WRITE="${ALLOW_WRITE:-0}" bash tests/integration/01-setup-media.sh ${ARGS} 2>&1); RC=$?
  set -e; kill $SRV 2>/dev/null || true; wait $SRV 2>/dev/null || true; SRV=""
}
echo "== 1. --check, 20 fichiers présents et indexés"
: > "$W/kubectl.log"; run 20
[[ $RC == 0 ]] && ok "code 0 (complet)" || { ko "rc=$RC"; echo "$OUT" | tail; }
echo "$OUT" | grep -Eq ": (2[0-9]) / 20" && ok "au moins 20 médias indexés" || ko "décompte d'indexation"
echo "== 2. --check, fichiers manquants"
: > "$W/kubectl.log"; run 17
[[ $RC == 1 ]] && ok "code 1 (incomplet)" || { ko "rc=$RC"; echo "$OUT" | tail; }
echo "$OUT" | grep -q "3 fichier(s) manquant(s)" && ok "3 manquants détectés" || ko "décompte des manquants"
cp "$W/kubectl.log" "$W/kubectl.check.log"   # journal des runs --check (section 3)
echo "== 2b. 1er run RÉEL : 3 fichiers manquants => générés, copiés, puis code 0 (régression missing=())"
: > "$W/kubectl.log"; : > "$W/ffmpeg.log"; ALLOW_WRITE=1 run 17 --run
[[ $RC == 0 ]] && ok "code 0 une fois tout copié et indexé" || { ko "rc=$RC"; echo "$OUT" | tail; }
[[ $(wc -l < "$W/ffmpeg.log") == 3 ]] && ok "3 vidéos générées (seulement les manquantes)" || ko "ffmpeg appelé $(wc -l < "$W/ffmpeg.log") fois"
[[ $(wc -l < "$W/podfiles") == 20 ]] && ok "20 fichiers dans le pod après copie" || ko "pod : $(wc -l < "$W/podfiles") fichiers"
echo "$OUT" | grep -q "bibliothèque PlaySync-Tests prête" && ok "message final « prête »" || ko "message final absent"
ALLOW_WRITE=1 run 20 --run; [[ $RC == 0 ]] && [[ ! -s "$W/ffmpeg.log" || $(wc -l < "$W/ffmpeg.log") == 3 ]] && ok "2e run (déjà complet) : code 0, rien de regénéré" || ko "idempotence : rc=$RC"

echo "== 3. --check ne modifie rien et ne vise que deployment/emby2"
[[ $(grep -vc "ls -1" "$W/kubectl.check.log" || true) == 0 ]] && ok "seulement des lectures (ls) côté pod" || ko "écriture côté pod : $(cat "$W/kubectl.check.log")"
! grep -E "deployment/emby( |$)" "$W/kubectl.check.log" && ok "jamais deployment/emby (PROD)" || ko "cible PROD"
[[ $fail == 0 ]] || { echo "ECHEC test-setup-media-offline" >&2; exit 1; }
echo "test-setup-media-offline : OK"
