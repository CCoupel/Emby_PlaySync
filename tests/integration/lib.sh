#!/usr/bin/env bash
# lib.sh — bibliothèque commune des tests d'intégration (sourcée par 00-setup-users.sh, 90-cleanup.sh, 20-etiquettes-retrait.sh, int-lib.sh).
# Reprise de tests/spike/lib.sh (v0.1.0, supprimé avec #15) : mêmes garde-fous, mêmes fonctions.
# Cible : emby2 (QUALIF) UNIQUEMENT. URL et clé viennent de private/qualif.env (jamais affichées).
set -euo pipefail
umask 077

INT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT=$(cd "$INT_DIR/../.." && pwd)
PRIVATE="$ROOT/private"
QUALIF_ENV="$PRIVATE/qualif.env"
USERS_ENV="$PRIVATE/test-users.env"      # ids + mots de passe des comptes test_* (gitignoré)
SNAPSHOT="$PRIVATE/test-snapshot.json"   # état avant setup : utilisateurs + politiques protégées
STATE="$PRIVATE/test-state.json"         # ids des playlists créées par les scripts d'intégration
# Figé (non surchargeable). « emby2-Testing » = nom de QUALIF, confirmé le 2026-09-26 via /System/Info (dev-plugin).
readonly EXPECTED_SERVER_NAME="emby2-Testing"
PROTECTED_USERS=(admin cyril user2)       # comptes réels : lecture seule, JAMAIS modifiés
TEST_USERS=(test_u1 test_u2 test_u3)

die() { echo "ERREUR : $*" >&2; exit 2; }
for t in curl jq python3; do command -v "$t" >/dev/null || die "outil manquant : $t"; done

SCRATCH=$(mktemp -d "${SPIKE_SCRATCH_BASE:-${TMPDIR:-/tmp}}/int.XXXXXX")
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

# cfgq VALEUR : échappe antislashs et guillemets doubles pour une valeur entre guillemets d'un fichier de config curl.
cfgq() {
  local v=$1
  [[ $v != *$'\n'* && $v != *$'\r'* ]] || die "valeur avec saut de ligne refusée dans la config curl"
  v=${v//\\/\\\\}
  printf '%s' "${v//\"/\\\"}"
}

# api METHODE CHEMIN [CORPS_JSON] [TOKEN] -> affiche le code HTTP, corps dans $RESP.
# URL, token et corps passent par un fichier de config curl (rien dans argv).
# TOKEN "-" = aucun en-tête d'auth (login). EXTRA_HDR (optionnel) : en-tête supplémentaire.
api() {
  local m=$1 p=$2 body=${3:-} tok=${4:-$EMBY_API_KEY}
  {
    printf 'url = "%s"\n' "$(cfgq "$EMBY_URL$p")"
    if [[ $tok != "-" ]]; then printf 'header = "X-Emby-Token: %s"\n' "$(cfgq "$tok")"; fi
    printf 'header = "Accept: application/json"\n'
    if [[ -n ${EXTRA_HDR:-} ]]; then printf 'header = "%s"\n' "$(cfgq "$EXTRA_HDR")"; fi
    if [[ -n $body ]]; then
      printf '%s' "$body" > "$BODYF"
      printf 'header = "Content-Type: application/json"\n'
      printf 'data-binary = "@%s"\n' "$(cfgq "$BODYF")"
    fi
  } > "$CFG"
  : > "$RESP"
  local code
  code=$(curl -sS -K "$CFG" -X "$m" -o "$RESP" -w '%{http_code}' --max-time 30 2>/dev/null) || code=000
  # Les endpoints /SharedPlaylist/* (Diagnostics) renvoient du PascalCase (sérialiseur Emby) : on normalise les clés en
  # camelCase (1re lettre en minuscule) pour que les filtres jq ne dépendent pas de la casse.
  if [[ $p == /SharedPlaylist/* && -s $RESP ]]; then
    if jq -c 'walk(if type=="object" then with_entries(.key |= ((.[0:1]|ascii_downcase) + .[1:])) else . end)' "$RESP" > "$RESP.n" 2>/dev/null; then
      mv "$RESP.n" "$RESP"
    else
      rm -f "$RESP.n"
    fi
  fi
  echo "$code"
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
  jq -r --arg n "$1" 'first(.[]|select(.Name==$n)|.Id) // empty' "$RESP"
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
  if [[ "$exp_users" != "$now_users" ]]; then echo "  [KO] $label : liste des utilisateurs différente"; ok=1; fi
  local pol_exp pol_now norm
  pol_exp=$(jq -S -c '.policies' "$SNAPSHOT"); pol_now=$(jq -S -c '.policies' <<<"$now")
  # AllowSharingPersonalItems (v0.4.0, #26, D8) : le plugin peut légitimement poser ce champ à true pour TOUT compte,
  # y compris protégé, dès la première passe de réconciliation qui tourne après le déploiement de v0.4.0 (même une
  # déclenchée par un autre script pour une tout autre raison, ex. prime()/run_pass) — jamais l'inverse (D-e : aucune
  # révocation). Neutralisé dans la comparaison SEULEMENT dans ce sens (false/absent -> true, par compte) ; une
  # révocation (true -> false) ou toute AUTRE différence de politique reste détectée normalement.
  norm='
    def tolerate($u):
      ($u.e.AllowSharingPersonalItems // false) as $ve | ($u.n.AllowSharingPersonalItems // false) as $vn |
      if ($ve != true) and ($vn == true)
      then {e: ($u.e + {AllowSharingPersonalItems:"tolerated"}), n: ($u.n + {AllowSharingPersonalItems:"tolerated"})}
      else $u end;
    ($exp[0]) as $exp | ($now[0]) as $now |
    ([($exp|keys)[] as $k | {($k): (tolerate({e:($exp[$k]//{}), n:($now[$k]//{})}))}] | add // {}) as $merged
    | {exp: ($merged|with_entries(.value|=.e)), now: ($merged|with_entries(.value|=.n))}'
  local both; both=$(jq -nc --slurpfile exp <(echo "$pol_exp") --slurpfile now <(echo "$pol_now") "$norm")
  pol_exp=$(jq -S -c '.exp' <<<"$both"); pol_now=$(jq -S -c '.now' <<<"$both")
  if [[ "$pol_exp" != "$pol_now" ]]; then
    echo "  [KO] $label : politique d'un compte protégé modifiée"; ok=1
  fi
  if [[ $ok == 0 ]]; then echo "  [OK] $label : utilisateurs et politiques de ${PROTECTED_USERS[*]} inchangés"; fi
  return $ok
}

# login NOM MOT_DE_PASSE -> affiche le token de session (jamais journalisé)
# DeviceId dérivé du NOM (unique et stable par compte, ex. "spike-test_u1") : un DeviceId partagé entre
# test_u1/u2/u3 était une piste plausible (I28/I33, dev-plugin #47) pour un comportement de session Emby
# confondant (éviction/collision par appareil), sans lien avec le code du plugin.
login() {
  local st
  st=$(EXTRA_HDR="X-Emby-Authorization: MediaBrowser Client=\"spike\", Device=\"spike\", DeviceId=\"spike-$1\", Version=\"1\"" \
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

# need_int LIBELLE VALEUR : arrêt clair si VALEUR n'est pas un entier (ticks, durées).
need_int() { [[ $2 =~ ^[0-9]+$ ]] || die "$1 : valeur non entière ('$2') — média sans durée ou lecture impossible"; }
