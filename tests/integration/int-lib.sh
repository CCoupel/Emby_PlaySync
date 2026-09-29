#!/usr/bin/env bash
# int-lib.sh — helpers de 20-etiquettes-retrait.sh (I1–I17), sourcé après lib.sh.
# stats_json et scan_log sont repris de l'ex tests/integration/probe-lib.sh (sonde U11, supprimée avec #15).
# Fonctions génériques : journal/état Diagnostics, tâche planifiée, étiquettes, lecture simulée. Cible : emby2 uniquement.

DIAG=/SharedPlaylist/Diagnostics
DEFS='def n: (.//"")|tostring|ascii_downcase|gsub("-";"");'
qs() { jq -rn --arg v "$1" '$v|@uri'; }
now_ms() { date +%s%3N; }
nap() { sleep "$(awk -v n="$1" -v s="${WAIT_SCALE:-1}" 'BEGIN{printf "%.2f", n*s}')"; }   # attente mise à l'échelle (WAIT_SCALE ; 1 en réel)
is2xx() { [[ $1 == 2* ]]; }


# stats_json FICHIER : un nombre par ligne -> {"n","p50","p95","max"} (rang le plus proche)
stats_json() {
  python3 - "$1" <<'PY'
import sys, json, math
try:
    v = sorted(float(x) for x in open(sys.argv[1]).read().split())
except FileNotFoundError:
    v = []
def pct(p):
    if not v: return None
    x = v[max(0, math.ceil(p / 100 * len(v)) - 1)]
    return int(x) if x == int(x) else x
print(json.dumps({"n": len(v), "p50": pct(50), "p95": pct(95), "max": (int(v[-1]) if v[-1] == int(v[-1]) else v[-1]) if v else None}))
PY
}

# scan_log FICHIER -> {"lines","locked","exceptions","pluginErrors","excerpt":[...]} ; secrets masqués.
scan_log() {
  local f=$1
  local mask='s/(api_key|apikey|token|x-emby-token|password|pw|authorization)([=:"[:space:]]+)[^&" ,;]+/\1\2***/Ig'
  local locked exc plug
  locked=$(grep -Eic 'database is locked|SQLITE_BUSY' "$f" || true)
  exc=$(grep -Eic 'exception' "$f" || true)
  plug=$(grep -F '[EmbySharedPlaylist]' "$f" | grep -Eic 'error|exception|fail' || true)
  local ex
  ex=$({ grep -Ei 'database is locked|SQLITE_BUSY|exception|\[EmbySharedPlaylist\].*(error|fail)' "$f" || true; } | head -n 10 | cut -c1-240 | sed -E "$mask" | jq -R . | jq -sc .)
  jq -nc --argjson l "$(wc -l < "$f" | tr -d ' ')" --argjson k "${locked:-0}" --argjson e "${exc:-0}" --argjson p "${plug:-0}" --argjson x "${ex:-[]}" \
    '{lines:$l, locked:$k, exceptions:$e, pluginErrors:$p, excerpt:$x}'
}

# --- Diagnostics -------------------------------------------------------------------------------------
journal() { # [KINDS_CSV] [clear] -> JSON (clés en camelCase : ts kind userId itemId playlistId detail)
  api GET "$DIAG/Journal?clear=${2:-false}${1:+&kind=$1}" >/dev/null; jq -c . "$RESP"
}
jclear() { api GET "$DIAG/Journal?clear=true" >/dev/null; }
state() { api GET "$DIAG/State" >/dev/null; jq -c . "$RESP"; }
# echo_sum : compteurs agrégés already-seen + reentrant (Diagnostics/State.skippedCounts) ; un « écho » = événement de retour d'une écriture du plugin
echo_sum() { state | jq -r '((.skippedCounts["already-seen"]//0) + (.skippedCounts["reentrant"]//0))'; }
# jkv DETAIL CLE -> valeur de « CLE=valeur » dans un Detail
jkv() { grep -o "\\b$2=[^ ]*" <<<"$1" | head -n1 | cut -d= -f2-; }
# jcount JOURNAL_JSON PLAYLIST KIND [DETAIL_REGEX] -> nombre d'entrées
jcount() {
  jq -r --arg p "$2" --arg k "$3" --arg r "${4:-.*}" "$DEFS"'[.[]|select(.kind==$k and (.playlistId|n)==($p|n) and ((.detail//"")|test($r)))]|length' <<<"$1"
}

# --- tâche planifiée de réconciliation ---------------------------------------------------------------
TASK_ID=""
task_id() {
  if [[ -n $TASK_ID ]]; then echo "$TASK_ID"; return; fi
  api GET /ScheduledTasks >/dev/null
  TASK_ID=$(jq -r 'first(.[]|select((.Name//"")|test("réconciliation|reconciliation";"i")))|.Id // empty' "$RESP")
  echo "$TASK_ID"
}
run_pass() { # exécute UNE passe et attend sa fin (State.lastPass.ts change) ; échoue si > 60 s
  local id before i after
  id=$(task_id); [[ -n $id ]] || die "tâche planifiée « réconciliation » introuvable (plugin non chargé ?)"
  before=$(state | jq -r '.lastPass.ts // ""')
  apiok '2*' POST "/ScheduledTasks/Running/$id"
  for ((i=0; i<60; i++)); do
    after=$(state | jq -r '.lastPass.ts // ""')
    if [[ -n $after && $after != "$before" ]]; then return 0; fi
    nap 1
  done
  return 1
}

# --- playlists, lecture, étiquettes ------------------------------------------------------------------
new_pl() { # NOM ITEM_IDS_CSV -> id (créée par u1, enregistrée pour le nettoyage)
  local st id; st=$(api POST "/Playlists?Name=$(qs "$1")&MediaType=Video&Ids=$2&UserId=$U1" "" "$T1")
  [[ $st == 200 ]] || die "création playlist $1 -> HTTP $st"
  id=$(jq -r '.Id' "$RESP"); register_playlist "$id"; echo "$id" >> "$SCRATCH/pls.run"; echo "$id"
}
drop_playlists() { # supprime les playlists des scénarios précédents (une playlist OUI restante réagirait aux transitions suivantes)
  local id
  [[ -s $SCRATCH/pls.run ]] || return 0
  while read -r id; do api DELETE "/Items/$id" >/dev/null; done < "$SCRATCH/pls.run"
  : > "$SCRATCH/pls.run"
}
share_pl() { # PLAYLIST : u2 = Write, u3 = Read
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$1" --arg a "$U2" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Write"}')" "$T1"
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$1" --arg a "$U3" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Read"}')" "$T1"
}
shared_pl() { local id; id=$(new_pl "$1" "$2"); share_pl "$id"; echo "$id"; }
share_pl_one() { # PLAYLIST USERID LEVEL(Write|Read) : partage ciblé (S6, un seul membre en plus du propriétaire)
  apiok 204 POST /Items/Access "$(jq -nc --arg p "$1" --arg u "$2" --arg l "$3" '{ItemIds:[$p],UserIds:[$u],ItemAccess:$l}')" "$T1"
}
entries() { # PLAYLIST -> [{itemId, playlistItemId}] vu par u1
  api GET "/Playlists/$1/Items?UserId=$U1" "" "$T1" >/dev/null
  jq -c '[.Items[]|{itemId:.Id, playlistItemId:.PlaylistItemId}]' "$RESP"
}
count_item() { entries "$1" | jq -r --arg i "$2" "$DEFS"'[.[]|select((.itemId|n)==($i|n))]|length'; }
add_item() { api POST "/Playlists/$1/Items?Ids=$2&UserId=$U1" "" "$T1" >/dev/null; }
# drop_item PLAYLIST ITEM : retire TOUTES les entrées de ITEM de PLAYLIST, par le propriétaire, indépendamment de
# remove-si-lu (nettoyage explicite) — sert à rendre un média réutilisable par un scénario ultérieur sans qu'il ne
# reste candidat (ListSharedPlaylistsOfUserContaining) dans une playlist plus ancienne encore partagée avec les mêmes membres.
drop_item() {
  local eids
  mapfile -t eids < <(entries "$1" | jq -r --arg i "$2" "$DEFS"'.[]|select((.itemId|n)==($i|n))|.playlistItemId')
  [[ ${#eids[@]} -gt 0 ]] || return 0
  local csv; csv=$(IFS=,; echo "${eids[*]}")
  api POST "/Playlists/$1/Items/Delete?EntryIds=$csv&UserId=$U1" "" "$T1" >/dev/null
}
# reset_played ITEM : remet u1/u2/u3 à « non lu » sur ITEM (baseline connue avant réutilisation d'un média).
reset_played() { unmark "$U1" "$T1" "$1"; unmark "$U2" "$T2" "$1"; unmark "$U3" "$T3" "$1"; }
wait_count() { # PLAYLIST ITEM ATTENDU TIMEOUT_S : attend que le nombre d'entrées du média soit ATTENDU
  local i; for ((i=0; i<${4:-10}; i++)); do
    [[ $(count_item "$1" "$2") == "$3" ]] && return 0; nap 1
  done; return 1
}
wait_empty() { # PLAYLIST TIMEOUT_S : attend que la playlist n'ait plus aucune entrée
  local i; for ((i=0; i<${2:-10}; i++)); do
    [[ $(entries "$1" | jq length) == 0 ]] && return 0; nap 1
  done; return 1
}

# --- attente active (#47, qa-integration-v031 : des `nap N` fixes lisaient l'état avant que la propagation/pose
# n'ait eu le temps de s'exécuter, sous charge — rafales de créations de playlists qui ralentissent le TRAITEMENT
# DE LA FILE, pas le traitement lui-même : Handler.MaxMs reste << budget). Interrogent l'état toutes les 1 s (mise
# à l'échelle par WAIT_SCALE, comme nap) jusqu'à un TIMEOUT_S raisonnable, au lieu d'un délai fixe potentiellement
# trop court. Un appel réussi rend la main dès que la condition est vraie (pas d'attente supplémentaire).
# _wait_trace LABEL ATTEMPT T0_MS : trace le délai observé dans OUT_DIR/wait-delays.log si OUT_DIR est défini
# (suggestion qa : objectiver un futur diagnostic de latence sous charge) ; silencieux si le fichier n'est pas
# accessible ou si OUT_DIR n'est pas défini (ex. exécuté hors d'un des 3 scripts qui le posent).
_wait_trace() {
  [[ -n ${OUT_DIR:-} ]] || return 0
  printf '%s %s attempt=%s delayMs=%s\n' "$(date -Iseconds)" "$1" "$2" "$(( $(now_ms) - $3 ))" >> "$OUT_DIR/wait-delays.log" 2>/dev/null || true
}
wait_position() { # UID TOKEN ITEM ATTENDU [TIMEOUT_S=10]
  local i t0=$(now_ms); for ((i=0; i<${5:-10}; i++)); do
    [[ $(position_of "$1" "$2" "$3") == "$4" ]] && { _wait_trace "position item=$3 attendu=$4" "$((i+1))" "$t0"; return 0; }
    nap 1
  done; return 1
}
wait_played() { # UID TOKEN ITEM ATTENDU(true|false) [TIMEOUT_S=10]
  local i t0=$(now_ms); for ((i=0; i<${5:-10}; i++)); do
    [[ $(played_of "$1" "$2" "$3") == "$4" ]] && { _wait_trace "played item=$3 attendu=$4" "$((i+1))" "$t0"; return 0; }
    nap 1
  done; return 1
}
# wait_grace_counter PLAYLIST FAMILLE ATTENDU [TIMEOUT_S=10] : attend que graceCounters[PLAYLIST][FAMILLE] atteigne
# ATTENDU (une passe déclenchée par run_pass a démarré/terminé sans garantir que CETTE playlist, parmi d'autres en
# file, a déjà eu son compteur mis à jour — qa-integration-v031, I8a).
wait_grace_counter() {
  local pl=$1 fam=$2 want=$3 timeout=${4:-10} i gs t0=$(now_ms)
  for ((i=0; i<timeout; i++)); do
    gs=$(state | jq -r --arg p "$pl" --arg f "$fam" "$DEFS"'.graceCounters|to_entries|map(select((.key|n)==($p|n)))|.[0].value[$f] // 0')
    [[ $gs == "$want" ]] && { _wait_trace "grace playlist=$pl famille=$fam attendu=$want" "$((i+1))" "$t0"; return 0; }
    nap 1
  done
  return 1
}
# wait_journal PLAYLIST KIND [DETAIL_REGEX] [MIN=1] [TIMEOUT_S=10] : attend au moins MIN entrées KIND (Detail~=REGEX)
# journalisées pour PLAYLIST (lecture seule, clear=false : ne consomme rien, les journal()/jcount() qui suivent
# dans le scénario restent valables).
wait_journal() {
  local pl=$1 kind=$2 re=${3:-.*} min=${4:-1} timeout=${5:-10} i j n t0=$(now_ms)
  for ((i=0; i<timeout; i++)); do
    j=$(journal "$kind")
    n=$(jcount "$j" "$pl" "$kind" "$re")
    if [[ $n -ge $min ]]; then
      _wait_trace "journal playlist=$pl kind=$kind min=$min" "$((i+1))" "$t0"
      return 0
    fi
    nap 1
  done
  return 1
}

stays() { # PLAYLIST ITEM ATTENDU [SECONDES] : le nombre d'entrées reste ATTENDU pendant la fenêtre
  nap "${4:-4}"; [[ $(count_item "$1" "$2") == "$3" ]]
}
admin_item() { api GET "/Items?Ids=$1&Fields=Tags,TagItems,Overview" >/dev/null; jq -c '.Items[0]' "$RESP"; }
tags_of() { admin_item "$1" | jq -c '((.TagItems//[])|map(.Name)) + (.Tags//[]) | unique | sort'; }
overview_of() { admin_item "$1" | jq -r '.Overview // ""'; }
owner_edit() { # PLAYLIST AJOUTS_JSON RETRAITS_JSON [OVERVIEW_JSON] [OWNER_UID] [OWNER_TOK] : enregistrement de
               # l'éditeur natif (DTO complet), par défaut le propriétaire habituel (test_u1) ; UID/TOK explicites
               # pour une playlist dont le propriétaire est un autre compte (I43, v0.5.0 : new_pl_owned).
  local pl=$1 add=$2 del=$3 ov=${4:-__keep__} ouid=${5:-$U1} otok=${6:-$T1} st dto
  st=$(api GET "/Users/$ouid/Items/$pl?Fields=Tags,TagItems,Overview" "" "$otok"); [[ $st == 200 ]] || die "lecture DTO playlist -> $st"
  dto=$(jq -c --argjson add "$add" --argjson del "$del" --arg ov "$ov" '
    (((.TagItems//[])|map(.Name)) + (.Tags//[]) | unique) as $cur
    | (($cur - $del) + $add | unique) as $new
    | .Tags=$new | .TagItems=($new|map({Name:.}))
    | if $ov=="__keep__" then . else .Overview=($ov|fromjson) end' "$RESP")
  apiok 204 POST "/Items/$pl" "$dto" "$otok"
}
# étiquettes de la famille en minuscules exactes (posées par le plugin)

# set_marker_state PLAYLIST FAMILLE(remove-si-lu|propager-lu|propager-avancement) ETAT(none|non|oui|both) [OWNER_UID] [OWNER_TOK] :
# atteint l'état en UNE édition du propriétaire (ajoute/retire ce qu'il faut ; ne touche jamais à l'autre famille
# ni aux étiquettes du propriétaire). UID/TOK optionnels transmis à owner_edit (défaut test_u1, comportement
# inchangé pour tous les appels existants) — nécessaires pour une playlist dont le propriétaire n'est pas
# test_u1 (I43, v0.5.0 : new_pl_owned).
set_marker_state() {
  local pl=$1 fam=$2 state=$3 ouid=${4:-$U1} otok=${5:-$T1} add=() del=()
  case $state in
    none) del=("$fam=NON" "$fam=OUI") ;;
    non)  add=("$fam=NON"); del=("$fam=OUI") ;;
    oui)  add=("$fam=OUI"); del=("$fam=NON") ;;
    both) add=("$fam=OUI" "$fam=NON") ;;
    *) die "set_marker_state : état inconnu '$state'" ;;
  esac
  owner_edit "$pl" "$(printf '%s\n' "${add[@]}" | jq -R . | jq -sc 'map(select(length>0))')" \
             "$(printf '%s\n' "${del[@]}" | jq -R . | jq -sc 'map(select(length>0))')" \
             "__keep__" "$ouid" "$otok"
}

# --- compte restreint autonome (R8, v0.3.0) : créé puis nettoyé DANS LE MÊME RUN par 21-propagation.sh -----------
RESTRICTED_USER_NAME="test_u4"
U4=""; T4=""   # id / token, mêmes conventions que U1..U3/T1..T3 ; vides tant que ensure_restricted_user() n'a pas tourné

ensure_restricted_user() {   # crée test_u4 sans accès à AUCUNE bibliothèque (EnableAllFolders=false, EnabledFolders=[])
  local n=$RESTRICTED_USER_NAME st id pw
  st=$(api GET /Users); [[ $st == 200 ]] || die "GET /Users -> $st"
  if jq -e --arg n "$n" '.[]|select(.Name==$n)' "$RESP" >/dev/null; then
    echo "  [WARN] $n existait déjà (run précédent interrompu ?) : suppression avant recréation" >&2
    id=$(jq -r --arg n "$n" '.[]|select(.Name==$n)|.Id' "$RESP")
    api DELETE "/Users/$id" >/dev/null
  fi
  st=$(api POST /Users/New "$(jq -nc --arg n "$n" '{Name:$n}')")
  [[ $st == 200 || $st == 204 ]] || die "création de $n -> HTTP $st"
  id=$(jq -r '.Id // empty' "$RESP"); [[ -n $id ]] || id=$(user_id_by_name "$n")
  [[ -n $id ]] || die "id de $n introuvable"
  U4=$id; echo "$id" > "$SCRATCH/restricted_user_id"   # filet de sécurité : cleanup_restricted_user (trap) le retrouve même après un die()
  pw=$(python3 -c 'import secrets;print(secrets.token_urlsafe(18))')
  apiok 204 POST "/Users/$id/Password" "$(jq -nc --arg p "$pw" '{NewPw:$p}')"
  st=$(api GET "/Users/$id"); [[ $st == 200 ]] || die "GET $n -> $st"
  pol=$(jq -c '.Policy | .EnableAllFolders=false | .EnabledFolders=[]' "$RESP")
  apiok 204 POST "/Users/$id/Policy" "$pol"
  st=$(api GET "/Users/$id"); [[ $st == 200 ]] || die "GET $n (relecture) -> $st"
  [[ $(jq -r '.Policy.IsAdministrator' "$RESP") == false ]] || die "$n est administrateur"
  [[ $(jq -r '.Policy.EnableAllFolders' "$RESP") == false ]] || die "$n : la restriction n'est pas effective"
  T4=$(login "$n" "$pw")
  echo "  [OK] $n créé (id $id), EnableAllFolders=false, EnabledFolders=[] (aucune bibliothèque)"
}

cleanup_restricted_user() {   # supprime test_u4 si ce run l'a créé ; idempotent, jamais d'erreur fatale (appelé aussi par le trap)
  local id=${U4:-}
  [[ -n $id ]] || id=$(cat "${SCRATCH:-/nonexistent}/restricted_user_id" 2>/dev/null || true)
  [[ -n $id ]] || return 0
  api DELETE "/Users/$id" >/dev/null || true
  rm -f "${SCRATCH:-/nonexistent}/restricted_user_id"; U4=""; T4=""
  echo "  [OK] $RESTRICTED_USER_NAME supprimé"
}

# --- permission de partage automatique (v0.4.0, #26) ------------------------------------------------------
PLUGIN_ID="9ebe814e-9438-42b8-aa57-feea1ae92451"   # src/EmbySharedPlaylist/Plugin.cs

plugin_cfg() { api GET "/Plugins/$PLUGIN_ID/Configuration" >/dev/null; jq -c . "$RESP"; }
# plugin_cfg_set JSON_COMPLET : même mécanisme que configScript.js (relit toute la config, poste tout le corps).
plugin_cfg_set() { apiok 204 POST "/Plugins/$PLUGIN_ID/Configuration" "$1"; }
# set_auto_sharing true|false : bascule l'interrupteur global (config plugin, pas un compte).
set_auto_sharing() { plugin_cfg_set "$(plugin_cfg | jq -c --argjson v "$1" '.AutoEnableSharing=$v')"; }

policy_of() { api GET "/Users/$1" >/dev/null; jq -c '.Policy' "$RESP"; }
sharing_of() { policy_of "$1" | jq -r '.AllowSharingPersonalItems'; }
# jcount_user JOURNAL_JSON KIND USERID -> nombre d'entrées KIND pour cet UserId (permission, sans playlist associée,
# contrairement à jcount qui filtre par PlaylistId).
jcount_user() { jq -r --arg k "$2" --arg u "$3" '[.[]|select(.kind==$k and (.userId//"")==$u)]|length' <<<"$1"; }
# set_sharing UID true|false : action ADMIN manuelle directe (simule un (dé)cochage dans le tableau de bord), PAS
# une action du plugin — relit la Policy complète puis ne change QUE ce champ (même prudence que EmbyUserPolicyGateway).
set_sharing() {
  api GET "/Users/$1" >/dev/null
  apiok 204 POST "/Users/$1/Policy" "$(jq -c --argjson v "$2" '.Policy|.AllowSharingPersonalItems=$v' "$RESP")"
}
wait_sharing() { # UID ATTENDU(true|false) [TIMEOUT_S=10]
  local i t0=$(now_ms); for ((i=0; i<${3:-10}; i++)); do
    [[ $(sharing_of "$1") == "$2" ]] && { _wait_trace "sharing uid=$1 attendu=$2" "$((i+1))" "$t0"; return 0; }
    nap 1
  done; return 1
}
# create_test_account PREFIX -> id (nom "PREFIX-<aléatoire>", mot de passe non conservé) ; à nettoyer via
# cleanup_test_accounts (idempotent, aussi appelée par le trap) — jamais admin/cyril/user2/test_u1/u2/u3 (comptes
# permanents, non créés/supprimés par cette fonction).
create_test_account() {
  local n="${1}-$RANDOM$RANDOM" st id
  st=$(api POST /Users/New "$(jq -nc --arg n "$n" '{Name:$n}')")
  [[ $st == 200 || $st == 204 ]] || die "création de $n -> HTTP $st"
  id=$(jq -r '.Id // empty' "$RESP"); [[ -n $id ]] || id=$(user_id_by_name "$n")
  [[ -n $id ]] || die "id de $n introuvable"
  echo "$id" >> "$SCRATCH/perm_accounts.run"
  echo "$id"
}
# create_and_login_test_account PREFIX -> "ID TOKEN" (un seul stdout, séparés par un espace) : compte de test
# complet (créé, mot de passe posé, connecté), suivi dans perm_accounts.run comme create_test_account (même
# nettoyage, cleanup_test_accounts). Sert aux scénarios à plus grande échelle (v0.5.0, #34 : 5+ membres).
create_and_login_test_account() {
  local n="${1}-$RANDOM$RANDOM" st id pw tok
  st=$(api POST /Users/New "$(jq -nc --arg n "$n" '{Name:$n}')")
  [[ $st == 200 || $st == 204 ]] || die "création de $n -> HTTP $st"
  id=$(jq -r '.Id // empty' "$RESP"); [[ -n $id ]] || id=$(user_id_by_name "$n")
  [[ -n $id ]] || die "id de $n introuvable"
  echo "$id" >> "$SCRATCH/perm_accounts.run"
  pw=$(python3 -c 'import secrets;print(secrets.token_urlsafe(18))')
  apiok 204 POST "/Users/$id/Password" "$(jq -nc --arg p "$pw" '{NewPw:$p}')"
  tok=$(login "$n" "$pw")
  echo "$id $tok"
}
cleanup_test_accounts() {
  [[ -s ${SCRATCH:-/nonexistent}/perm_accounts.run ]] || return 0
  local id
  while IFS= read -r id; do if [[ -n $id ]]; then api DELETE "/Users/$id" >/dev/null || true; fi; done < "$SCRATCH/perm_accounts.run"
  : > "$SCRATCH/perm_accounts.run"
}

# cleanup_registered_playlists : supprime les playlists créées PAR CE RUN (register_playlist, private/test-state.json),
# après vérification du nom côté serveur (SPIKE*). Complément de 90-cleanup.sh (nettoyage manuel, à part) : un script
# qui en imbrique un autre (ex. I26 dans 21-propagation.sh, qui relance 20-etiquettes-retrait.sh en sous-processus
# partageant le même private/test-state.json) doit se nettoyer lui-même, sinon les playlists des deux scripts
# s'accumulent sans qu'aucun des deux n'ait vocation à les balayer par nom (rôle de 90-cleanup.sh).
cleanup_registered_playlists() {
  [[ -f ${STATE:-} ]] || return 0
  local id nm st count=0
  while IFS= read -r id; do
    [[ -n $id ]] || continue
    st=$(api GET "/Items?Ids=$id")
    nm=$(jq -r '.Items[0].Name // empty' "$RESP" 2>/dev/null || true)
    [[ -n $nm ]] || continue                 # déjà supprimée (par ex. l'un des scénarios l'a déjà retirée)
    [[ $nm == SPIKE* ]] || continue           # garde : jamais autre chose qu'une playlist de test
    st=$(api DELETE "/Items/$id")
    [[ $st == 2* ]] && count=$((count+1))
  done < <(jq -r '.[]' "$STATE" | sort -u)
  rm -f "$STATE"
  echo "  [OK] nettoyage final : $count playlist(s) enregistrée(s) supprimée(s)"
}

NON_RM='remove-si-lu=NON'; NON_PR='propager-lu=NON'; OUI_RM='remove-si-lu=OUI'; OUI_PR='propager-lu=OUI'
NON_AV='propager-avancement=NON'; OUI_AV='propager-avancement=OUI'   # v1.2.0 (#56, D21) : 3e famille, indépendante de propager-lu
has_tag() { jq -e --arg t "$2" 'index($t)!=null' <<<"$1" >/dev/null; }
tag_count() { jq -r --arg f "$2" '[.[]|select(ascii_downcase|gsub("\\s+";"")|startswith($f))]|length' <<<"$1"; }

# prime PLAYLIST : passe de réconciliation => première détection (les TROIS NON depuis v1.2.0 + message) ; attend qu'ils soient là
prime() {
  run_pass || die "passe de réconciliation non terminée en 60 s"
  local i; for ((i=0; i<10; i++)); do
    local t; t=$(tags_of "$1")
    if has_tag "$t" "$NON_RM" && has_tag "$t" "$NON_PR" && has_tag "$t" "$NON_AV" && [[ -n $(overview_of "$1") ]]; then return 0; fi
    nap 1
  done; return 1
}

# lecture / marquage ------------------------------------------------------------------------------------
finish() { unmark "$1" "$2" "$3"; api POST "/Users/$1/PlayedItems/$3" "" "$2" >/dev/null; }   # UID TOKEN ITEM : (dé)coche puis TogglePlayed => transition non lu -> lu garantie
unmark() { api DELETE "/Users/$1/PlayedItems/$3" "" "$2" >/dev/null; }

# reset_pool_full ITEM... : remise à zéro COMPLÈTE (Played=false ET position=0) pour test_u1/u2/u3 (et test_u4 si
# une session est active, T4 défini) sur chaque média listé — à faire UNE FOIS en tête de script (pas par
# scénario, contrairement à assert_baseline qui ne remet que le lu) : un bassin de médias partagé entre les 3
# scripts ET entre invocations séparées (QUALIF) peut porter un résidu (lu, position) d'un run précédent — cause
# racine confirmée d'I27/I28/I33/I34/I18.E/I18.G (qa-integration-v031-20260927-210414-verified.md) : la garde D-c
# faisait alors exactement son travail (refuser de propager pour un compte déjà lu à l'instant de l'événement ;
# garde SUPPRIMÉE en v1.2.0, #57 : le résidu ne bloque plus la position, mais reste un état de départ à normaliser),
# ce n'était pas un défaut du plugin. DELETE PlayedItems remet Played=false (ne propage jamais, R6) ; un
# aller-retour Playing/Stopped à ticks=0 remet la position à 0 SANS jamais franchir le seuil de 30 s (#45) :
# TryExtract rejette l'événement avant même d'atteindre le moteur — aucune propagation ne peut être déclenchée
# par cette remise à zéro elle-même.
_reset_one() { # UID TOKEN ITEM
  unmark "$1" "$2" "$3"
  local sid; sid="reset-$RANDOM$RANDOM"
  api POST /Sessions/Playing "$(jq -nc --arg i "$3" --arg s "$sid" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$2" >/dev/null
  api POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$3" --arg s "$sid" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$2" >/dev/null
}
reset_pool_full() {
  local it
  for it in "$@"; do
    _reset_one "$U1" "$T1" "$it"
    _reset_one "$U2" "$T2" "$it"
    _reset_one "$U3" "$T3" "$it"
    if [[ -n ${T4:-} ]]; then _reset_one "$U4" "$T4" "$it"; fi   # set -e : jamais `[[ ]] && cmd` en position de dernière commande
  done
}
runtime_of() { api GET "/Users/$U1/Items/$1" "" "$T1" >/dev/null; jq -r '.RunTimeTicks // empty' "$RESP"; }
play_to() { # UID TOKEN ITEM POURCENT [progress-only] : Playing, Progress à mi-chemin, Stopped à POURCENT % (ou seulement Progress)
  local tok=$2 item=$3 pct=$4 rt sid
  rt=$(runtime_of "$item"); need_int "RunTimeTicks($item)" "$rt"
  sid="int-$RANDOM$RANDOM"
  body() { jq -nc --arg i "$item" --arg s "$sid" --argjson p "$1" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}'; }
  api POST /Sessions/Playing "$(body 0)" "$tok" >/dev/null
  api POST /Sessions/Playing/Progress "$(body $((rt*pct/100)))" "$tok" >/dev/null
  if [[ ${5:-} != progress-only ]]; then api POST /Sessions/Playing/Stopped "$(body $((rt*pct/100)))" "$tok" >/dev/null; fi
}
played_of() { api GET "/Users/$1/Items/$3" "" "$2" >/dev/null; jq -r '.UserData.Played' "$RESP"; }
position_of() { api GET "/Users/$1/Items/$3" "" "$2" >/dev/null; jq -r '.UserData.PlaybackPositionTicks // 0' "$RESP"; }

# --- session de lecture (v0.3.1, avancement #45) : Start puis Progress/Stopped à la demande, sur la MÊME session
# (PlaySessionId) pour ressembler à un vrai client. play_to() (v0.2.0/v0.3.0, ci-dessus) reste pour les scénarios de
# « lu » ; ces fonctions donnent le contrôle explicite du POSITIONNEMENT et de IsPaused nécessaire à l'avancement.
play_start() { # UID TOKEN ITEM -> SID (session)
  local sid; sid="int-$RANDOM$RANDOM"
  api POST /Sessions/Playing "$(jq -nc --arg i "$3" --arg s "$sid" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:0,CanSeek:true}')" "$2" >/dev/null
  echo "$sid"
}
play_progress() { # UID TOKEN ITEM SID POSITION_TICKS IS_PAUSED(true|false) -> HTTP status
  api POST /Sessions/Playing/Progress "$(jq -nc --arg i "$3" --arg s "$4" --argjson p "$5" --argjson pa "$6" \
    '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,IsPaused:$pa,CanSeek:true}')" "$2"
}
play_stop() { # UID TOKEN ITEM SID POSITION_TICKS -> HTTP status
  api POST /Sessions/Playing/Stopped "$(jq -nc --arg i "$3" --arg s "$4" --argjson p "$5" \
    '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}')" "$2"
}
ticks_at() { # ITEM POURCENT -> ticks (position à ce pourcentage de la durée), avec need_int sur la durée
  local rt; rt=$(runtime_of "$1"); need_int "RunTimeTicks($1)" "$rt"; echo $((rt*$2/100))
}

# appels concurrents (fichiers de config propres ; ms cumulées) --------------------------------------------
bg_call() { # ID TAG METHODE CHEMIN [CORPS] [TOKEN]
  local id=$1; shift
  ( CFG="$SCRATCH/bg.$id.cfg"; RESP="$SCRATCH/bg.$id.resp"; BODYF="$SCRATCH/bg.$id.body"
    tag=$1; shift; t0=$(now_ms); st=$(api "$@"); t1=$(now_ms)
    echo "$st" > "$SCRATCH/bg.$id.st"; echo $((t1-t0)) >> "$SCRATCH/ms.$tag"; echo $((t1-t0)) >> "$SCRATCH/ms.ALL" ) &
}

# --- v1.2.0 (#56, #57, D21) : trois familles indépendantes ------------------------------------------------------
# enable_removal PLAYLIST [OWNER_UID] [OWNER_TOK] : active le retrait EFFECTIF = remove-si-lu=OUI ET propager-lu=OUI
# (R4a subordonnée à propager-lu depuis v1.2.0 : remove-si-lu=OUI SEUL ne retire plus rien, S3b). Une seule édition.
enable_removal() {
  local pl=$1 ouid=${2:-$U1} otok=${3:-$T1}
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]" "__keep__" "$ouid" "$otok"
}
# enable_avancement PLAYLIST [OWNER_UID] [OWNER_TOK] : active la propagation de la position (propager-avancement=OUI),
# sans toucher aux deux autres familles (avancement indépendant du lu).
enable_avancement() {
  local pl=$1 ouid=${2:-$U1} otok=${3:-$T1}
  owner_edit "$pl" "[\"$OUI_AV\"]" "[\"$NON_AV\"]" "__keep__" "$ouid" "$otok"
}
