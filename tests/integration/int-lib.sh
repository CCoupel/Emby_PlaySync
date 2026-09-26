#!/usr/bin/env bash
# int-lib.sh — helpers de 20-etiquettes-retrait.sh (I1–I17), sourcé après tests/spike/lib.sh et probe-lib.sh.
# Fonctions génériques : journal/état Diagnostics, tâche planifiée, étiquettes, lecture simulée. Cible : emby2 uniquement.

DIAG=/SharedPlaylist/Diagnostics
DEFS='def n: (.//"")|tostring|ascii_downcase|gsub("-";"");'
qs() { jq -rn --arg v "$1" '$v|@uri'; }
now_ms() { date +%s%3N; }
nap() { sleep "$(awk -v n="$1" -v s="${WAIT_SCALE:-1}" 'BEGIN{printf "%.2f", n*s}')"; }   # attente mise à l'échelle (WAIT_SCALE ; 1 en réel)
is2xx() { [[ $1 == 2* ]]; }

# --- Diagnostics -------------------------------------------------------------------------------------
journal() { # [KINDS_CSV] [clear] -> JSON (clés en camelCase : ts kind userId itemId playlistId detail)
  api GET "$DIAG/Journal?clear=${2:-false}${1:+&kind=$1}" >/dev/null; jq -c . "$RESP"
}
jclear() { api GET "$DIAG/Journal?clear=true" >/dev/null; }
state() { api GET "$DIAG/State" >/dev/null; jq -c . "$RESP"; }
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
entries() { # PLAYLIST -> [{itemId, playlistItemId}] vu par u1
  api GET "/Playlists/$1/Items?UserId=$U1" "" "$T1" >/dev/null
  jq -c '[.Items[]|{itemId:.Id, playlistItemId:.PlaylistItemId}]' "$RESP"
}
count_item() { entries "$1" | jq -r --arg i "$2" "$DEFS"'[.[]|select((.itemId|n)==($i|n))]|length'; }
add_item() { api POST "/Playlists/$1/Items?Ids=$2&UserId=$U1" "" "$T1" >/dev/null; }
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
stays() { # PLAYLIST ITEM ATTENDU [SECONDES] : le nombre d'entrées reste ATTENDU pendant la fenêtre
  nap "${4:-4}"; [[ $(count_item "$1" "$2") == "$3" ]]
}
admin_item() { api GET "/Items?Ids=$1&Fields=Tags,TagItems,Overview" >/dev/null; jq -c '.Items[0]' "$RESP"; }
tags_of() { admin_item "$1" | jq -c '((.TagItems//[])|map(.Name)) + (.Tags//[]) | unique | sort'; }
overview_of() { admin_item "$1" | jq -r '.Overview // ""'; }
owner_edit() { # PLAYLIST AJOUTS_JSON RETRAITS_JSON [OVERVIEW_JSON] : enregistrement de l'éditeur natif (DTO complet)
  local pl=$1 add=$2 del=$3 ov=${4:-__keep__} st dto
  st=$(api GET "/Users/$U1/Items/$pl?Fields=Tags,TagItems,Overview" "" "$T1"); [[ $st == 200 ]] || die "lecture DTO playlist -> $st"
  dto=$(jq -c --argjson add "$add" --argjson del "$del" --arg ov "$ov" '
    (((.TagItems//[])|map(.Name)) + (.Tags//[]) | unique) as $cur
    | (($cur - $del) + $add | unique) as $new
    | .Tags=$new | .TagItems=($new|map({Name:.}))
    | if $ov=="__keep__" then . else .Overview=($ov|fromjson) end' "$RESP")
  apiok 204 POST "/Items/$pl" "$dto" "$T1"
}
# étiquettes de la famille en minuscules exactes (posées par le plugin)
NON_RM='remove-si-lu=NON'; NON_PR='propager-lu=NON'; OUI_RM='remove-si-lu=OUI'; OUI_PR='propager-lu=OUI'
has_tag() { jq -e --arg t "$2" 'index($t)!=null' <<<"$1" >/dev/null; }
tag_count() { jq -r --arg f "$2" '[.[]|select(ascii_downcase|gsub("\\s+";"")|startswith($f))]|length' <<<"$1"; }

# prime PLAYLIST : passe de réconciliation => première détection (les deux NON + message) ; attend qu'ils soient là
prime() {
  run_pass || die "passe de réconciliation non terminée en 60 s"
  local i; for ((i=0; i<10; i++)); do
    local t; t=$(tags_of "$1")
    if has_tag "$t" "$NON_RM" && has_tag "$t" "$NON_PR" && [[ -n $(overview_of "$1") ]]; then return 0; fi
    nap 1
  done; return 1
}

# lecture / marquage ------------------------------------------------------------------------------------
finish() { unmark "$1" "$2" "$3"; api POST "/Users/$1/PlayedItems/$3" "" "$2" >/dev/null; }   # UID TOKEN ITEM : (dé)coche puis TogglePlayed => transition non lu -> lu garantie
unmark() { api DELETE "/Users/$1/PlayedItems/$3" "" "$2" >/dev/null; }
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

# appels concurrents (fichiers de config propres ; ms cumulées) --------------------------------------------
bg_call() { # ID TAG METHODE CHEMIN [CORPS] [TOKEN]
  local id=$1; shift
  ( CFG="$SCRATCH/bg.$id.cfg"; RESP="$SCRATCH/bg.$id.resp"; BODYF="$SCRATCH/bg.$id.body"
    tag=$1; shift; t0=$(now_ms); st=$(api "$@"); t1=$(now_ms)
    echo "$st" > "$SCRATCH/bg.$id.st"; echo $((t1-t0)) >> "$SCRATCH/ms.$tag"; echo $((t1-t0)) >> "$SCRATCH/ms.ALL" ) &
}
