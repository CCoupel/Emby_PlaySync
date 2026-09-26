#!/usr/bin/env bash
# Bibliothèque commune du spike (sourcée par 00-setup-users.sh, 10-run-spike.sh, 90-cleanup.sh).
# Cible : emby2 (QUALIF) UNIQUEMENT. URL et clé viennent de private/qualif.env (jamais affichées).
set -euo pipefail
umask 077

SPIKE_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$SPIKE_DIR/../.." && pwd)
PRIVATE="$ROOT/private"
QUALIF_ENV="$PRIVATE/qualif.env"
USERS_ENV="$PRIVATE/spike-users.env"      # ids + mots de passe des comptes test_* (gitignoré)
SNAPSHOT="$PRIVATE/spike-snapshot.json"   # état avant setup : utilisateurs + politiques protégées
STATE="$PRIVATE/spike-state.json"         # ids des playlists créées par le spike
# Figé (non surchargeable). « emby2-Testing » = nom de QUALIF, confirmé le 2026-09-26 via /System/Info (dev-plugin).
readonly EXPECTED_SERVER_NAME="emby2-Testing"
PROTECTED_USERS=(admin cyril user2)       # comptes réels : lecture seule, JAMAIS modifiés
TEST_USERS=(test_u1 test_u2 test_u3)

die() { echo "ERREUR : $*" >&2; exit 2; }
for t in curl jq python3; do command -v "$t" >/dev/null || die "outil manquant : $t"; done

SCRATCH=$(mktemp -d "${SPIKE_SCRATCH_BASE:-${TMPDIR:-/tmp}}/spike.XXXXXX")
trap 'rm -rf "$SCRATCH"' EXIT
RESP="$SCRATCH/resp.json"
CFG="$SCRATCH/curl.cfg"
BODYF="$SCRATCH/body.json"

# envget FICHIER CLE : lit CLE=valeur sans exécuter le fichier, retire CR et guillemets.
envget() {
  sed -n "s/^$2=//p" "$1" | tail -1 | tr -d '\r' | sed -e 's/^"//' -e 's/"$//' -e "s/^'//" -e "s/'\$//"
}

load_env() {
  [[ -f $QUALIF_ENV ]] || die "private/qualif.env introuvable"
  tr -d '\r' < "$QUALIF_ENV" > "$SCRATCH/qualif.env"
  EMBY_URL=$(envget "$SCRATCH/qualif.env" EMBY_URL); EMBY_URL=${EMBY_URL%/}
  EMBY_API_KEY=$(envget "$SCRATCH/qualif.env" EMBY_API_KEY)
  [[ -n $EMBY_URL && -n $EMBY_API_KEY ]] || die "EMBY_URL / EMBY_API_KEY manquants dans private/qualif.env"
}

# api METHODE CHEMIN [CORPS_JSON] [TOKEN] -> affiche le code HTTP, corps dans $RESP.
# URL, token et corps passent par un fichier de config curl (rien dans argv).
# TOKEN "-" = aucun en-tête d'auth (login). EXTRA_HDR (optionnel) : en-tête supplémentaire.
api() {
  local m=$1 p=$2 body=${3:-} tok=${4:-$EMBY_API_KEY}
  {
    printf 'url = "%s%s"\n' "$EMBY_URL" "$p"
    if [[ $tok != "-" ]]; then printf 'header = "X-Emby-Token: %s"\n' "$tok"; fi
    printf 'header = "Accept: application/json"\n'
    if [[ -n ${EXTRA_HDR:-} ]]; then printf 'header = "%s"\n' "$EXTRA_HDR"; fi
    if [[ -n $body ]]; then
      printf '%s' "$body" > "$BODYF"
      printf 'header = "Content-Type: application/json"\n'
      printf 'data-binary = "@%s"\n' "$BODYF"
    fi
  } > "$CFG"
  : > "$RESP"
  curl -sS -K "$CFG" -X "$m" -o "$RESP" -w '%{http_code}' --max-time 30 2>/dev/null || echo 000
}

# apiok ATTENDU METHODE CHEMIN [CORPS] [TOKEN] : échoue si le code n'est pas ATTENDU (ex. 204 ou 2xx).
apiok() {
  local want=$1; shift
  local st; st=$(api "$@")
  # shellcheck disable=SC2053
  if [[ $st != $want ]]; then die "$2 $3 -> HTTP $st (attendu $want)"; fi
}

# Refuse d'écrire si la cible n'est pas QUALIF (lecture seule d'abord).
guard_target() {
  load_env
  local st; st=$(api GET /System/Info)
  [[ $st == 200 ]] || die "serveur injoignable ou clé refusée (HTTP $st)"
  local name; name=$(jq -r '.ServerName // ""' "$RESP")
  [[ $name == "$EXPECTED_SERVER_NAME" ]] || die "cible '$name' != '$EXPECTED_SERVER_NAME' : arrêt (QUALIF uniquement)"
  # Garde complémentaire : hôte attendu, lu dans private/qualif.env (clé EXPECTED_HOST, non versionné).
  local want_host have_host
  want_host=$(envget "$SCRATCH/qualif.env" EXPECTED_HOST)
  have_host=$(printf '%s' "$EMBY_URL" | sed -E 's#^[a-zA-Z]+://##; s#[/:].*$##')
  if [[ -n $want_host ]]; then
    [[ $have_host == "$want_host" ]] || die "hôte de EMBY_URL différent de EXPECTED_HOST (private/qualif.env) : arrêt"
    echo "Cible vérifiée : $name (hôte conforme à EXPECTED_HOST)"
  else
    echo "Cible vérifiée : $name (ATTENTION : EXPECTED_HOST absent de private/qualif.env, garde d'hôte inactive)"
  fi
}

check_ignored() {
  local f
  for f in "$@"; do
    git -C "$ROOT" check-ignore -q "${f#"$ROOT"/}" || die "$f n'est pas gitignoré : arrêt"
  done
}

user_id_by_name() { # stdout : id ou vide ; utilise /Users courant
  api GET /Users >/dev/null
  jq -r --arg n "$1" '.[]|select(.Name==$n)|.Id' "$RESP" | head -1
}

# Snapshot JSON (stdout) : noms de tous les utilisateurs + Policy des comptes protégés existants.
snapshot_users() {
  local st list pol='{}' n id
  st=$(api GET /Users); [[ $st == 200 ]] || die "GET /Users -> $st"
  list=$(jq -c '[.[]|{Name,Id}]' "$RESP")
  for n in "${PROTECTED_USERS[@]}"; do
    id=$(jq -r --arg n "$n" '.[]|select(.Name==$n)|.Id' <<<"$list")
    if [[ -z $id ]]; then continue; fi
    st=$(api GET "/Users/$id"); [[ $st == 200 ]] || die "GET /Users/<$n> -> $st"
    pol=$(jq -c --arg n "$n" --argjson p "$pol" '$p + {($n): .Policy}' "$RESP")
  done
  jq -nSc --argjson u "$list" --argjson p "$pol" '{users:($u|map(.Name)|sort), policies:$p}'
}

# compare_protected LIBELLE [NOMS_EN_PLUS...] : compare l'état courant au snapshot ;
# les utilisateurs attendus = ceux du snapshot + NOMS_EN_PLUS. Retourne 0 si identique.
compare_protected() {
  local label=$1; shift
  [[ -f $SNAPSHOT ]] || { echo "  [WARN] $label : pas de snapshot ($SNAPSHOT), comparaison impossible"; return 1; }
  local now extra; now=$(snapshot_users)
  extra=$(printf '%s\n' "$@" | jq -R . | jq -sc 'map(select(length>0))')
  local exp_users; exp_users=$(jq -c --argjson e "$extra" '(.users + $e)|sort' "$SNAPSHOT")
  local now_users; now_users=$(jq -c '.users|sort' <<<"$now")
  local ok=0
  if [[ $exp_users != "$now_users" ]]; then echo "  [KO] $label : liste des utilisateurs différente"; ok=1; fi
  if [[ $(jq -S -c '.policies' "$SNAPSHOT") != $(jq -S -c '.policies' <<<"$now") ]]; then
    echo "  [KO] $label : politique d'un compte protégé modifiée"; ok=1
  fi
  if [[ $ok == 0 ]]; then echo "  [OK] $label : utilisateurs et politiques de ${PROTECTED_USERS[*]} inchangés"; fi
  return $ok
}

# login NOM MOT_DE_PASSE -> affiche le token de session (jamais journalisé)
login() {
  local st
  st=$(EXTRA_HDR='X-Emby-Authorization: MediaBrowser Client="spike", Device="spike", DeviceId="spike-1", Version="1"' \
       api POST /Users/AuthenticateByName "$(jq -nc --arg u "$1" --arg p "$2" '{Username:$u,Pw:$p}')" "-")
  [[ $st == 200 ]] || die "authentification de $1 refusée (HTTP $st)"
  jq -r '.AccessToken' "$RESP"
}

# register_playlist ID : mémorise l'id pour 90-cleanup.sh
register_playlist() {
  [[ -f $STATE ]] || echo '[]' > "$STATE"
  jq -c --arg id "$1" '. + [$id] | unique' "$STATE" > "$STATE.tmp" && mv "$STATE.tmp" "$STATE"
}

# Politique par défaut d'un compte, sans le champ testé (pour comparer u1/u2/u3)
policy_without_share() { jq -S -c '.Policy | del(.AllowSharingPersonalItems)' "$RESP"; }
