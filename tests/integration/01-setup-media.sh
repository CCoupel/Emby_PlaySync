#!/usr/bin/env bash
# 01-setup-media.sh — provisionne la bibliothèque LOCALE de test « PlaySync-Tests » sur emby2 (QUALIF UNIQUEMENT) (#61).
# Pourquoi : la bibliothèque de QUALIF est 100 % virtuelle (VirtualLib, /config/virtual/*.strm) ; chaque événement de session d'un
# média virtuel est relayé vers PROD (sessions fantômes). Les tests n'utilisent que des médias locaux (voir int-lib.sh, select_test_media).
# Actions (idempotentes, ré-exécutables sans effet de bord) :
#   1. génère avec ffmpeg les vidéos synthétiques MANQUANTES (11 min, 160x90, 1 fps, muettes) PlaySync-Test-01..20.mp4
#   2. les copie dans /config/test-media/ du pod (kubectl exec, deployment/emby2 UNIQUEMENT, namespace media)
#   3. crée la bibliothèque PlaySync-Tests (movies) si absente, déclenche un scan si nécessaire
#   4. attend l'indexation : >= 20 médias de >= 10 min sous /config/test-media/
# Usage : tests/integration/01-setup-media.sh [--check|--dry-run]
#   --check / --dry-run : ne modifie RIEN (ni pod, ni Emby) ; affiche l'état et sort 0 si complet, 1 sinon.
# Prérequis : private/qualif.env, private/kubeconfig.yml (gitignorés), kubectl ; ffmpeg seulement si des fichiers manquent.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

CHECK=0
case "${1:-}" in
  "") ;;
  --check|--dry-run) CHECK=1 ;;
  *) die "usage : $0 [--check|--dry-run]" ;;
esac

KUBE_DEPLOY="emby2"; KUBE_NS="media"; KUBECONFIG_FILE="$PRIVATE/kubeconfig.yml"   # QUALIF en dur : jamais « emby » (PROD)
LIB_NAME=${TEST_MEDIA_LIBRARY:-PlaySync-Tests}
MEDIA_DIR=${TEST_MEDIA_ROOT:-/config/test-media/}; MEDIA_DIR=${MEDIA_DIR%/}
NEED=20; DURATION=660                      # 20 vidéos de 11 min (>= 10 min = 6e9 ticks exigés par les scénarios)
qs() { jq -rn --arg v "$1" '$v|@uri'; }
kc() { KUBECONFIG="$KUBECONFIG_FILE" kubectl "$@"; }
pod_sh() { kc exec "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" -- sh -c "$1"; }
name_of() { printf 'PlaySync-Test-%02d.mp4' "$1"; }

echo "== Préconditions"
check_ignored "$QUALIF_ENV" "$KUBECONFIG_FILE"
guard_target
command -v kubectl >/dev/null || die "kubectl introuvable"
[[ -f $KUBECONFIG_FILE ]] || die "private/kubeconfig.yml introuvable"
if [[ $CHECK == 1 ]]; then echo "  mode --check : aucune modification"; fi

echo "== Fichiers dans $MEDIA_DIR (pod $KUBE_DEPLOY)"
present=$(pod_sh "ls -1 '$MEDIA_DIR' 2>/dev/null || true")
missing=()
for ((i=1; i<=NEED; i++)); do
  if ! grep -qxF "$(name_of "$i")" <<<"$present"; then missing+=("$i"); fi
done
echo "  ${#missing[@]} fichier(s) manquant(s) sur $NEED"

if [[ ${#missing[@]} -gt 0 && $CHECK == 0 ]]; then
  command -v ffmpeg >/dev/null || die "ffmpeg introuvable (nécessaire pour générer ${#missing[@]} vidéo(s))"
  pod_sh "mkdir -p '$MEDIA_DIR'"
  for i in "${missing[@]}"; do
    f=$(name_of "$i"); tmp="$SCRATCH/$f"
    # couleur distincte par fichier ; sans son ; faible débit (~1 Mo)
    ffmpeg -loglevel error -y -f lavfi -i "color=c=$(printf '0x%02x%02x%02x' $((i*12)) $((255-i*10)) $((60+i*5))):s=160x90:r=1" \
      -t "$DURATION" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -an -movflags +faststart "$tmp" || die "ffmpeg a échoué pour $f"
    kc exec -i "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" -- sh -c "cat > '$MEDIA_DIR/$f.part' && mv '$MEDIA_DIR/$f.part' '$MEDIA_DIR/$f'" < "$tmp" \
      || die "copie de $f vers le pod échouée"
    rm -f "$tmp"; echo "  [OK] $f copié"
  done
  missing=()   # tout est copié : sans cette remise à zéro, la condition de sortie « aucun manquant » restait fausse au 1er run
fi

echo "== Bibliothèque $LIB_NAME"
st=$(api GET /Library/VirtualFolders); [[ $st == 200 ]] || die "GET /Library/VirtualFolders -> $st"
LIB_ID=$(jq -r --arg n "$LIB_NAME" '[.[]|select(.Name==$n)|.ItemId][0] // empty' "$RESP")
CREATED=0
if [[ -z $LIB_ID ]]; then
  if [[ $CHECK == 1 ]]; then echo "  [KO] bibliothèque absente"
  else
    st=$(api POST "/Library/VirtualFolders?Name=$(qs "$LIB_NAME")&CollectionType=movies&Paths=$(qs "$MEDIA_DIR")&RefreshLibrary=false" \
         "$(jq -nc --arg p "$MEDIA_DIR" '{LibraryOptions:{PathInfos:[{Path:$p}]}}')")
    [[ $st == 2* ]] || die "création de la bibliothèque -> HTTP $st"
    CREATED=1; echo "  [OK] bibliothèque créée"
    st=$(api GET /Library/VirtualFolders); [[ $st == 200 ]] || die "GET /Library/VirtualFolders -> $st"
    LIB_ID=$(jq -r --arg n "$LIB_NAME" '[.[]|select(.Name==$n)|.ItemId][0] // empty' "$RESP")
    [[ -n $LIB_ID ]] || die "bibliothèque $LIB_NAME introuvable après création"
  fi
else
  echo "  [OK] bibliothèque présente"
fi

count_ready() { # nombre de médias >= 10 min, sous MEDIA_DIR, dans la bibliothèque
  [[ -n $LIB_ID ]] || { echo 0; return; }
  local s; s=$(api GET "/Items?Recursive=true&ParentId=$LIB_ID&IncludeItemTypes=Movie,Episode,Video&Fields=RunTimeTicks,Path&Limit=200")
  if [[ $s != 200 ]]; then echo 0; return; fi
  jq -r --arg root "$MEDIA_DIR/" '[.Items[]|select((.RunTimeTicks//0)>=6000000000 and ((.Path//"")|startswith($root)))]|length' "$RESP"
}

echo "== Indexation"
ready=$(count_ready)
if [[ $CHECK == 0 && $ready -lt $NEED ]]; then
  st=$(api POST /Library/Refresh ""); [[ $st == 2* ]] || echo "  [WARN] scan non déclenché (HTTP $st)"
  for ((t=0; t<60; t++)); do
    ready=$(count_ready); [[ $ready -ge $NEED ]] && break; sleep 5
  done
fi
echo "  médias indexés >= 10 min sous $MEDIA_DIR : $ready / $NEED"
if [[ $ready -ge $NEED && ${#missing[@]} -eq 0 ]]; then
  echo "== OK : bibliothèque $LIB_NAME prête ($ready médias)"; exit 0
fi
if [[ $CHECK == 1 ]]; then echo "== INCOMPLET (--check : rien modifié)"; exit 1; fi
die "indexation incomplète après 300 s ($ready / $NEED) : relancer le script (idempotent)"
