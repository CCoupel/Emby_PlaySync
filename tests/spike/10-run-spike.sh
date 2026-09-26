#!/usr/bin/env bash
# 10-run-spike.sh — scénarios U1–U6 et U10 (avancement de lecture, #44) du spike (issue #1) sur emby2 (QUALIF uniquement).
# Prérequis : 00-setup-users.sh exécuté ; plugin déployé avec EnableSpikeEndpoints=true.
# Sortie : tableau console + JSON de preuves (SPIKE_OUT, défaut _work/spike-out/spike-evidence-<ts>.json).
# Aucun secret dans la sortie (tokens/mots de passe jamais journalisés).
# Assertions : `hard` = comportement déjà acquis ou règle produit (KO => code de sortie 1) ;
#              `probe` = incertitude à lever (KO = résultat du spike, pas une erreur du script).
#
# Règles produit testées (décisions utilisateur), lecture retenue :
#   - propagation du « lu » seulement si `propager-lu=OUI` présent ET `propager-lu=NON` absent ;
#     sans OUI, le plugin ne propage rien (legacy) ; NON l'emporte en cas de conflit ;
#   - le plugin ne supprime JAMAIS une étiquette ; seule action d'écriture : poser NON quand
#     aucune étiquette `propager-lu*` n'existe.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}
mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S)
OUT_FILE="$OUT_DIR/spike-evidence-$TS.json"
RES="$SCRATCH/results.jsonl"; : > "$RES"
HARD_FAIL=0
SPK=/SharedPlaylist/Spike
DEFS='def n: (.//"")|tostring|ascii_downcase|gsub("-";"");'

rec() { # U ID KIND STATUS DESC [EVIDENCE_JSON]
  local ev=${6:-null}
  jq -nc --arg u "$1" --arg id "$2" --arg k "$3" --arg s "$4" --arg d "$5" --argjson e "$ev" \
    '{u:$u,id:$id,kind:$k,status:$s,desc:$d,evidence:$e}' >> "$RES"
  printf '  [%s] %s.%s (%s) %s\n' "$4" "$1" "$2" "$3" "$5"
  if [[ $4 == KO && $3 == hard ]]; then HARD_FAIL=1; fi
  return 0
}
ck() { # U ID KIND DESC EVIDENCE cmd... : OK si cmd réussit
  local u=$1 id=$2 kind=$3 d=$4 e=$5; shift 5
  if "$@"; then rec "$u" "$id" "$kind" OK "$d" "$e"; else rec "$u" "$id" "$kind" KO "$d" "$e"; fi
}
is2xx() { [[ $1 == 2* ]]; }
is4xx() { [[ $1 == 4* ]]; }
all2xx() { local x; for x in "$@"; do [[ $x == 2* ]] || return 1; done; }
saved_ok() { [[ $1 == 200 ]] && jq -e '.saved==true and .playedAfter==true' "$RESP" >/dev/null; }
removed_ok() { [[ $1 == 200 ]] && jq -e --arg e "$2" '.removed==true and ((.entriesAfter|map(.playlistItemId))|index($e))==null' <<<"$3" >/dev/null; }
qs() { jq -rn --arg v "$1" '$v|@uri'; }
jc() { jq -c "$@"; }            # jq compact sur stdin

ev_clear() { api GET "$SPK/Events?clear=true" >/dev/null; }
ev_get()   { api GET "$SPK/Events?clear=false" >/dev/null; jq -c . "$RESP"; }
ev_wait()  { # FILTRE_JQ TIMEOUT_S : attend qu'un filtre soit vrai sur le journal ; résultat dans $EV
  local i; for ((i=0; i<${2:-10}; i++)); do
    EV=$(ev_get)
    if jq -e "$DEFS $1" <<<"$EV" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done; return 1
}
now_ms() { date +%s%3N; }

# Règle produit de référence (spécification exécutable, casse ignorée, espaces tolérés autour de « = »)
ENGINE='def norm: gsub("\\s+";"")|ascii_downcase;
def marks: map(norm)|map(select(startswith("propager-lu")));
def engine: (marks) as $m
  | if ($m|length)==0 then {propagate:false, add:["propager-lu=NON"]}
    elif ($m|index("propager-lu=non"))!=null then {propagate:false, add:[]}
    elif ($m|index("propager-lu=oui"))!=null then {propagate:true, add:[]}
    else {propagate:false, add:[]} end;'
engine_of() { jq -c "$ENGINE engine" <<<"$1"; }

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$STATE"
[[ -f $USERS_ENV ]] || die "lancer 00-setup-users.sh d'abord"
[[ -f $SNAPSHOT ]] || die "snapshot absent : lancer 00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)")
T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)")
T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")
is_test_user() { [[ $1 == "$U1" || $1 == "$U2" || $1 == "$U3" ]]; }

st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video&Fields=RunTimeTicks&SortBy=SortName&Limit=200")
[[ $st == 200 ]] || die "GET /Items -> $st"
mapfile -t M < <(jq -r '[.Items[]|select((.RunTimeTicks//0)>=6000000000)|.Id][0:4][]' "$RESP")
[[ ${#M[@]} -ge 4 ]] || die "moins de 4 médias exploitables"
M1=${M[0]}; M2=${M[1]}; M3=${M[2]}; M4=${M[3]}
runtime() { api GET "/Users/$U1/Items/$1" "" "$T1" >/dev/null; jq -r '.RunTimeTicks' "$RESP"; }

new_playlist() { # NOM ITEM_IDS_CSV -> id
  local st; st=$(api POST "/Playlists?Name=$(qs "$1")&MediaType=Video&Ids=$2&UserId=$U1" "" "$T1")
  [[ $st == 200 ]] || die "création playlist $1 -> HTTP $st"
  local id; id=$(jq -r '.Id' "$RESP"); register_playlist "$id"; echo "$id"
}
entries() { # PLAYLIST TOKEN UID -> [{itemId, playlistItemId}]
  api GET "/Playlists/$1/Items?UserId=$3" "" "$2" >/dev/null
  jq -c '[.Items[]|{itemId:.Id, playlistItemId:.PlaylistItemId}]' "$RESP"
}

PLA=$(new_playlist "SPIKE-A" "$M1,$M2,$M3")
PLP=$(new_playlist "SPIKE-privee" "$M1")
PLT=$(new_playlist "SPIKE-tags" "$M1")
echo "  [OK] playlists SPIKE-A, SPIKE-privee, SPIKE-tags créées par test_u1 (ids dans private/spike-state.json)"

st=$(api GET "$SPK/Shares?playlistId=$PLA")
case $st in
  200) rec SMOKE a hard OK "GET Spike/Shares répond (plugin chargé, endpoints activés)" ;;
  404) die "Spike/* renvoie 404 : plugin non déployé ou EnableSpikeEndpoints=false" ;;
  *)   rec SMOKE a hard KO "GET Spike/Shares répond (HTTP $st)" ;;
esac

# ---------------------------------------------------------------- partage natif (acquis, hard) + U4
echo "== U4 — découverte des partages"
ev_clear
st=$(api POST /Items/Access "$(jc -n --arg p "$PLA" --arg a "$U2" --arg b "$U3" '{ItemIds:[$p],UserIds:[$a],ItemAccess:"Write"}')" "$T1")
ck ACQ share-u2 hard "u1 partage PLA en Write avec u2 (POST /Items/Access -> 204)" "{\"status\":\"$st\"}" test "$st" = 204
st=$(api POST /Items/Access "$(jc -n --arg p "$PLA" --arg b "$U3" '{ItemIds:[$p],UserIds:[$b],ItemAccess:"Read"}')" "$T1")
ck ACQ share-u3 hard "u1 partage PLA en Read avec u3 (204)" "{\"status\":\"$st\"}" test "$st" = 204

# événement à la création d'un partage ? (sinon : scan planifié)
ev_wait '[.[]|select(.kind!="UserDataSaved")]|length>0' 5 || true
SHARE_EV=$(jq -c '[.[]|{kind,playlistId,userId}]' <<<"${EV:-[]}")
ck U4 share-event probe "un événement du plugin est capté à la création d'un partage (sinon : scan planifié)" "$SHARE_EV" \
  jq -e 'length>0' <<<"$SHARE_EV"

st=$(api GET "$SPK/Shares?playlistId=$PLA"); SH=$(jq -c . "$RESP")
ck U4 shares-plugin probe "GetUserItemShares côté plugin : u2=Write et u3=Read, sans contexte utilisateur (HTTP $st)" "$SH" \
  jq -e --arg a "$U2" --arg b "$U3" "$DEFS"'(.shares|map(select((.userId|n)==($a|n)))|.[0].shareLevel)=="Write" and (.shares|map(select((.userId|n)==($b|n)))|.[0].shareLevel)=="Read"' <<<"$SH"
ck U4 owner probe "le propriétaire (u1) est identifié côté plugin" "$(jc '{ownerUserId}' <<<"$SH")" \
  jq -e --arg a "$U1" "$DEFS"'(.ownerUserId|n)==($a|n)' <<<"$SH"

pl_of() { api GET "$SPK/Playlists?userId=$1" >/dev/null; jc . "$RESP"; }
V1=$(pl_of "$U1"); V2=$(pl_of "$U2"); V3=$(pl_of "$U3")
sees() { jq -e --arg p "$2" "$DEFS"'map(select((.playlistId|n)==($p|n)))|length==1' <<<"$1" >/dev/null; }
sees_not() { sees "$1" "$2" && ! sees "$1" "$3"; }
sees_both() { sees "$1" "$2" && sees "$1" "$3"; }
ck U4 visible-u2 probe "Playlists?userId=u2 : voit SPIKE-A, ne voit pas SPIKE-privee" \
  "$(jc -n --argjson v "$V2" '{count:($v|length), names:($v|map(.name))}')" \
  sees_not "$V2" "$PLA" "$PLP"
ck U4 visible-u3 probe "Playlists?userId=u3 : voit SPIKE-A (Read)" "$(jc -n --argjson v "$V3" '{count:($v|length)}')" \
  sees "$V3" "$PLA"
ck U4 visible-u1 probe "Playlists?userId=u1 : voit SPIKE-A et SPIKE-privee" "$(jc -n --argjson v "$V1" '{count:($v|length)}')" \
  sees_both "$V1" "$PLA" "$PLP"
ck U4 by-media probe "requête « playlists contenant le média M3 visibles par u2 » = [SPIKE-A]" \
  "$(jc -n --argjson v "$V2" --arg m "$M3" "$DEFS"'[$v[]|select(.entries|map(.itemId|n)|index($m|n))|.name]')" \
  jq -e --arg m "$M3" --arg p "$PLA" "$DEFS"'[.[]|select(.entries|map(.itemId|n)|index($m|n))|.playlistId|n]==[($p|n)]' <<<"$V2"

# ---------------------------------------------------------------- U1 : événements « lu »
echo "== U1 — UserDataSaved (lecture simulée, marquage manuel, écriture plugin)"
RT=$(runtime "$M1")
SID="spike-$RANDOM$RANDOM"
ev_clear
play() { # ETAT POSITION
  jc -n --arg i "$M1" --arg s "$SID" --argjson p "$2" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}'
}
st1=$(api POST /Sessions/Playing "$(play start 0)" "$T2")
st2=$(api POST /Sessions/Playing/Progress "$(play prog $((RT/2)))" "$T2")
st3=$(api POST /Sessions/Playing/Stopped "$(play stop $((RT*98/100)))" "$T2")
ck U1 sim-calls hard "u2 : Sessions/Playing, Progress, Stopped (98 %) acceptés" "{\"start\":\"$st1\",\"progress\":\"$st2\",\"stopped\":\"$st3\"}" \
  all2xx "$st1" "$st2" "$st3"
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U2\"|n) and (.itemId|n)==(\"$M1\"|n))]|length>0" 12 || true
E=$(jq -c --arg u "$U2" --arg i "$M1" "$DEFS"'[.[]|select(.kind=="UserDataSaved" and (.userId|n)==($u|n) and (.itemId|n)==($i|n))|{saveReason,played,pluginWrite}]' <<<"${EV:-[]}")
ck U1 sim-event probe "lecture simulée : UserDataSaved reçu pour (u2, M1) ; SaveReason relevé, pluginWrite=false" "$E" \
  jq -e 'length>0 and all(.pluginWrite==false)' <<<"$E"
api GET "/Users/$U2/Items/$M1" "" "$T2" >/dev/null; PLAYED2=$(jq -r '.UserData.Played' "$RESP")
api GET "/Users/$U1/Items/$M1" "" "$T1" >/dev/null; PLAYED1=$(jq -r '.UserData.Played' "$RESP")
ck U1 sim-played probe "la lecture simulée à 98 % marque le média lu pour u2 seulement (u1 non lu)" "{\"u2\":\"$PLAYED2\",\"u1\":\"$PLAYED1\"}" \
  test "$PLAYED2/$PLAYED1" = "true/false"

ev_clear
st=$(api POST "/Users/$U3/PlayedItems/$M2" "" "$T3")
ck U1 manual-call hard "u3 marque M2 lu manuellement (POST PlayedItems)" "{\"status\":\"$st\"}" is2xx "$st"
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U3\"|n) and (.itemId|n)==(\"$M2\"|n))]|length>0" 8 || true
E=$(jq -c --arg u "$U3" --arg i "$M2" "$DEFS"'[.[]|select(.kind=="UserDataSaved" and (.userId|n)==($u|n) and (.itemId|n)==($i|n))|{saveReason,played,pluginWrite}]' <<<"${EV:-[]}")
ck U1 manual-event probe "marquage manuel : UserDataSaved (u3, M2) avec played=true, pluginWrite=false" "$E" \
  jq -e 'length>0 and (.[0].played==true) and all(.pluginWrite==false)' <<<"$E"
api GET "/Users/$U1/Items/$M2" "" "$T1" >/dev/null
ck U1 per-user hard "le « lu » de u3 ne change pas celui de u1 (flag propre à chaque utilisateur)" "{\"u1PlayedM2\":$(jq '.UserData.Played' "$RESP")}" \
  jq -e '.UserData.Played==false' "$RESP"

ev_clear
mp() { jc -n --arg u "$1" --arg i "$2" --argjson a "$3" '{userId:$u,itemId:$i,played:true,asPlugin:$a}'; }
st=$(api POST "$SPK/MarkPlayed" "$(mp "$U1" "$M2" true)")
ck U1 plugin-call probe "plugin : MarkPlayed(u1, M2, asPlugin=true) -> saved & playedAfter" "{\"status\":\"$st\"}" \
  saved_ok "$st"
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U1\"|n) and (.itemId|n)==(\"$M2\"|n))]|length>0" 8 || true
E=$(jq -c --arg u "$U1" --arg i "$M2" "$DEFS"'[.[]|select(.kind=="UserDataSaved" and (.userId|n)==($u|n) and (.itemId|n)==($i|n))|{saveReason,played,pluginWrite}]' <<<"${EV:-[]}")
ck U1 plugin-echo probe "écriture plugin reconnue (pluginWrite=true) : anti-écho possible" "$E" jq -e 'length>0 and .[0].pluginWrite==true' <<<"$E"

ev_clear
api DELETE "/Users/$U1/PlayedItems/$M2" "" "$T1" >/dev/null
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U1\"|n) and (.itemId|n)==(\"$M2\"|n))]|length>0" 8 || true
E=$(jq -c --arg u "$U1" --arg i "$M2" "$DEFS"'[.[]|select(.kind=="UserDataSaved" and (.userId|n)==($u|n) and (.itemId|n)==($i|n))|{saveReason,played,pluginWrite}]' <<<"${EV:-[]}")
ck U1 user-after-plugin probe "écriture utilisateur suivante sur le même couple : pluginWrite=false (entrée consommée)" "$E" \
  jq -e 'length>0 and all(.pluginWrite==false)' <<<"$E"

# ---------------------------------------------------------------- U2 : retrait par le plugin
echo "== U2 — RemoveFromPlaylist par le plugin"
st=$(api POST "/Playlists/$PLA/Items?Ids=$M2&UserId=$U2" "" "$T2")
ck U2 dup-add probe "u2 (Write) ajoute M2 en double : doublon accepté par Emby" "{\"status\":\"$st\"}" test "$st" = 200
EN=$(entries "$PLA" "$T1" "$U1")
E_M1=$(jq -r --arg i "$M1" "$DEFS"'[.[]|select((.itemId|n)==($i|n))|.playlistItemId][0]//""' <<<"$EN")
mapfile -t E_M2 < <(jq -r --arg i "$M2" "$DEFS"'.[]|select((.itemId|n)==($i|n))|.playlistItemId' <<<"$EN")
ev_clear
st=$(api POST "$SPK/RemoveItem" "$(jc -n --arg p "$PLA" --arg e "$E_M1" '{playlistId:$p,playlistItemIds:[$e]}')")
RM=$(jc . "$RESP")
ck U2 remove probe "plugin : RemoveItem(entrée de M1) -> removed=true, entriesAfter sans cette entrée" "{\"status\":\"$st\"}" \
  removed_ok "$st" "$E_M1" "$RM"
ok=true
for pair in "$T1:$U1" "$T2:$U2" "$T3:$U3"; do
  x=$(entries "$PLA" "${pair%%:*}" "${pair##*:}")
  jq -e --arg i "$M1" "$DEFS"'map(select((.itemId|n)==($i|n)))|length==0' <<<"$x" >/dev/null || ok=false
done
ck U2 remove-visible hard "après retrait par le plugin, M1 a disparu pour u1, u2 et u3 (même playlist)" "{\"allGone\":$ok}" test "$ok" = true
ev_wait "[.[]|select(.kind==\"PlaylistItemsRemoved\")]|length>0" 8 || true
ck U2 remove-event probe "événement PlaylistItemsRemoved journalisé (contexte d'origine visible)" "$(jq -c '[.[]|select(.kind=="PlaylistItemsRemoved")|{playlistId,userId,itemId}]' <<<"${EV:-[]}")" \
  jq -e '[.[]|select(.kind=="PlaylistItemsRemoved")]|length>0' <<<"${EV:-[]}"
if [[ ${#E_M2[@]} -ge 2 ]]; then
  st=$(api POST "$SPK/RemoveItem" "$(jc -n --arg p "$PLA" --arg e "${E_M2[1]}" '{playlistId:$p,playlistItemIds:[$e]}')")
  x=$(entries "$PLA" "$T1" "$U1")
  ck U2 dup-remove probe "retrait d'UNE entrée d'un doublon : l'autre entrée de M2 reste (mapping item -> PlaylistItemId)" \
    "$(jc -n --arg s "$st" --argjson x "$x" --arg i "$M2" "$DEFS"'{status:$s, m2Left:($x|map(select((.itemId|n)==($i|n)))|length)}')" \
    jq -e --arg i "$M2" "$DEFS"'map(select((.itemId|n)==($i|n)))|length==1' <<<"$x"
else
  rec U2 dup-remove probe KO "doublon non créé : cas non testé" "null"
fi
st=$(api POST "$SPK/RemoveItem" "$(jc -n --arg p "$PLA" '{playlistId:$p,playlistItemIds:["0"]}')")
ck U2 remove-bad probe "entrée inexistante : erreur 4xx propre (pas de 500)" "{\"status\":\"$st\"}" \
  is4xx "$st"

# ---------------------------------------------------------------- U3 : étiquettes
echo "== U3 — étiquettes écrites par le plugin"
ptags() { api GET "$SPK/Tags?playlistId=$PLT" >/dev/null; jq -c '(.tags//[])|sort' "$RESP"; }
owner_tags() { # AJOUTS_JSON RETRAITS_JSON : sauvegarde de l'éditeur natif (DTO complet, comme le client web)
  local st; st=$(api GET "/Users/$U1/Items/$PLT?Fields=Tags,TagItems,Overview" "" "$T1"); [[ $st == 200 ]] || die "lecture DTO playlist -> $st"
  local dto; dto=$(jq -c --argjson add "$1" --argjson del "$2" \
    '(((.TagItems//[])|map(.Name)) + (.Tags//[]) | unique) as $cur
     | (($cur - $del) + $add | unique) as $new
     | .Tags=$new | .TagItems=($new|map({Name:.}))' "$RESP")
  st=$(api POST "/Items/$PLT" "$dto" "$T1"); [[ $st == 204 ]] || die "sauvegarde métadonnées par le propriétaire -> $st"
}
ptag_post() { api POST "$SPK/Tags" "$(jc -n --arg p "$PLT" --argjson a "$1" --argjson r "$2" --argjson o "${3:-null}" '{playlistId:$p,addTags:$a,removeTags:$r,overview:$o}')"; }
itemupdated() { jq -c --arg p "$PLT" "$DEFS"'[.[]|select(.kind=="ItemUpdated" and (.playlistId|n)==($p|n))|.ts]' <<<"$1"; }
ms_between() { python3 - "$1" <<'PY'
import sys, json
from datetime import datetime
ts=[datetime.fromisoformat(t.replace("Z","+00:00")) for t in json.loads(sys.argv[1])]
print(int((ts[-1]-ts[0]).total_seconds()*1000) if len(ts)>=2 else -1)
PY
}
NON='propager-lu=NON'; OUI='propager-lu=OUI'

owner_tags '["mon-etiquette","Autre Tag"]' '[]'
P=$(ptags)
ck U3 read-owner probe "le plugin lit les étiquettes posées par le propriétaire" "$P" jq -e 'index("mon-etiquette")!=null and index("Autre Tag")!=null' <<<"$P"

ev_clear
st=$(ptag_post "[\"$NON\"]" '[]')
P=$(ptags)
ck U3 add-non probe "le plugin ajoute « propager-lu=NON » (caractère =, casse exacte) sans écraser mon-etiquette / Autre Tag (HTTP $st)" "$P" \
  jq -e --arg t "$NON" 'index($t)!=null and index("mon-etiquette")!=null and index("Autre Tag")!=null' <<<"$P"
st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Playlist&Tags=$(qs "$NON")")
ck U3 search-tag probe "recherche serveur par étiquette « propager-lu=NON » retrouve la playlist" "{\"status\":\"$st\"}" \
  jq -e --arg p "$PLT" "$DEFS"'[.Items[]|.Id|n]|index($p|n)!=null' "$RESP"
api GET "/Users/$U1/Items/$PLT?Fields=Tags,TagItems" "" "$T1" >/dev/null
ck U3 owner-sees probe "le propriétaire voit l'étiquette dans son DTO (TagItems)" "$(jc '{TagItems}' "$RESP")" \
  jq -e --arg t "$NON" '(.TagItems//[])|map(.Name)|index($t)!=null' "$RESP"
sleep 4; E=$(ev_get); UP=$(itemupdated "$E")
ck U3 no-loop probe "pas de boucle : <= 2 événements ItemUpdated 4 s après l'écriture plugin" "$UP" jq -e 'length<=2' <<<"$UP"

st=$(ptag_post '[]' '[]' '"Message de test spike"'); P=$(jc '.overview' "$RESP")
st2=$(ptag_post '[]' '[]' 'null')
ck U3 overview probe "description écrite par le plugin ; overview=null la laisse inchangée" "{\"after\":$P}" \
  jq -e '.overview=="Message de test spike"' "$RESP"
st=$(ptag_post '["spike-tmp"]' '[]'); st=$(ptag_post '[]' '["spike-tmp"]'); P=$(ptags)
ck U3 remove-tag probe "removeTags ne retire que l'étiquette nommée (spike-tmp), le reste est préservé" "$P" \
  jq -e --arg t "$NON" 'index("spike-tmp")==null and index($t)!=null and index("mon-etiquette")!=null' <<<"$P"

st=$(ptag_post "[\"$OUI\"]" '[]'); P=$(ptags)
ck U3 both probe "NON et OUI coexistent ; l'étiquette du propriétaire est préservée" "$P" \
  jq -e --arg a "$NON" --arg b "$OUI" 'index($a)!=null and index($b)!=null and index("mon-etiquette")!=null' <<<"$P"
D=$(engine_of "$P")
ck R both hard "règle produit : NON et OUI ensemble => NON l'emporte, pas de propagation, aucune suppression" "$D" \
  jq -e '.propagate==false and .add==[]' <<<"$D"

seq_case() { # ID DESC STEP1_ADD STEP1_DEL STEP2_ADD STEP2_DEL
  owner_tags '[]' '["propager-lu=OUI"]'; owner_tags "[\"$NON\"]" '[]'   # état initial {NON}
  ev_clear
  owner_tags "$3" "$4"; sleep 1; local mid; mid=$(ptags)
  owner_tags "$5" "$6"; sleep 2; local fin; fin=$(ptags); local e; e=$(ev_get)
  local gap; gap=$(ms_between "$(itemupdated "$e")")
  local dm df; dm=$(engine_of "$mid"); df=$(engine_of "$fin")
  rec U3 "$1" probe OK "$2 : intermédiaire $mid, final $fin, écart ItemUpdated = ${gap} ms (état intermédiaire => plugin poserait ${dm})" \
    "$(jc -n --argjson m "$mid" --argjson f "$fin" --arg g "$gap" --argjson dm "$dm" --argjson df "$df" '{intermediate:$m, final:$f, itemUpdatedGapMs:($g|tonumber), engineAtIntermediate:$dm, engineAtFinal:$df}')"
  ck R "$1-final" hard "règle produit : état final {OUI} sans NON => propagation active" "$df" jq -e '.propagate==true and .add==[]' <<<"$df"
  ck U3 "$1-keep" probe "étiquettes du propriétaire préservées pendant la séquence" "$fin" jq -e 'index("mon-etiquette")!=null and index("Autre Tag")!=null' <<<"$fin"
}
seq_case seq-remove-first "propriétaire : retire NON puis ajoute OUI" '[]' "[\"$NON\"]" "[\"$OUI\"]" '[]'
seq_case seq-add-first "propriétaire : ajoute OUI puis retire NON" "[\"$OUI\"]" '[]' '[]' "[\"$NON\"]"

echo "== U3 — décisions produit (table de référence)"
tab() { # ID DESC TAGS_JSON JQ_EXPECT
  local d; d=$(engine_of "$3"); ck R "$1" hard "$2" "$d" jq -e "$4" <<<"$d"
}
tab r-none  "aucune étiquette => le plugin pose NON, pas de propagation" '[]' '.propagate==false and .add==["propager-lu=NON"]'
tab r-other "étiquettes sans propager-lu* => le plugin pose NON" '["film"]' '.propagate==false and .add==["propager-lu=NON"]'
tab r-non   "NON seule => rien à faire, pas de propagation (legacy)" '["propager-lu=NON"]' '.propagate==false and .add==[]'
tab r-oui   "OUI seule => propagation active" '["propager-lu=OUI","film"]' '.propagate==true and .add==[]'
tab r-case  "casse et espaces tolérés (« Propager-Lu = oui »)" '[" Propager-Lu = oui "]' '.propagate==true'
tab r-both  "NON l'emporte sur OUI" '["propager-lu=OUI","propager-lu=NON"]' '.propagate==false'
tab r-noop  "le plugin ne supprime jamais d'étiquette (aucune action de retrait)" '["propager-lu=OUI","propager-lu=NON"]' 'has("remove")|not'

# ---------------------------------------------------------------- U5 : politique
echo "== U5 — AllowSharingPersonalItems via IUserManager"
is_test_user "$U2" || die "garde : u2 n'est pas un compte de test"
api GET "/Users/$U2" >/dev/null; P0=$(jq -S -c '.Policy' "$RESP")
st=$(api GET "$SPK/Policy?userId=$U2")
ck U5 get probe "GET Policy?userId=u2 = false (défaut)" "$(jc . "$RESP")" jq -e '.allowSharingPersonalItems==false' "$RESP"
st=$(api POST "$SPK/Policy" "$(jc -n --arg u "$U2" '{userId:$u,allowSharingPersonalItems:true}')")
POSTR=$(jc . "$RESP")
api GET "/Users/$U2" >/dev/null; P1=$(jq -S -c '.Policy' "$RESP")
ck U5 set probe "POST Policy(true) : la politique REST de u2 relit AllowSharingPersonalItems=true (HTTP $st)" "$POSTR" jq -e '.AllowSharingPersonalItems==true' <<<"$P1"
ck U5 side-effects probe "aucun autre champ de UserPolicy modifié (P1 avec le champ remis à false == P0)" "null" \
  test "$(jq -S -c '.AllowSharingPersonalItems=false' <<<"$P1")" = "$P0"
st=$(api POST "$SPK/Policy" "$(jc -n --arg u "$U2" '{userId:$u,allowSharingPersonalItems:false}')")
api GET "/Users/$U2" >/dev/null; P2=$(jq -S -c '.Policy' "$RESP")
ck U5 restore probe "retour à false : politique identique à l'état initial" "null" test "$P2" = "$P0"

# ---------------------------------------------------------------- U6 : partage créé par le plugin
echo "== U6 — Setup (playlist + partages créés par le plugin)"
st=$(api POST "$SPK/Setup" "$(jc -n --arg o "$U1" --arg a "$U2" --arg b "$U3" --arg i "$M1" '{ownerUserId:$o,memberUserIds:[$a,$b],itemIds:[$i],name:"SPIKE-setup"}')")
SETUP=$(jc . "$RESP")
SPID=$(jq -r '.playlistId // empty' <<<"$SETUP")
if [[ -n $SPID ]]; then register_playlist "$SPID"; fi
ck U6 setup probe "le plugin crée la playlist ET le partage sans contexte utilisateur (HTTP $st ; non bloquant si KO)" \
  "$(jc -n --arg s "$st" --argjson r "$SETUP" '{status:$s, response:$r}')" \
  jq -e --arg a "$U2" "$DEFS"'(.playlistId//"")!="" and ((.shares//[])|map(select((.userId|n)==($a|n)))|length)==1' <<<"$SETUP"
if [[ -n $SPID ]]; then
  x=$(entries "$SPID" "$T2" "$U2")
  ck U6 setup-visible probe "la playlist créée par le plugin est visible par u2 (même Id)" "$x" jq -e 'length>=1' <<<"$x"
fi

# ---------------------------------------------------------------- U10 : avancement de lecture (issue #44)
echo "== U10 — propagation de la position de lecture (u1 commence, u2 poursuit)"
# Hypothèse de contrat (à aligner sur contracts/http-endpoints.md) : POST Spike/SetPosition
#   {userId, itemId, positionTicks} -> 200. Position lue côté événement : playbackPositionTicks|positionTicks.
is_test_user "$U2" && is_test_user "$U1" || die "garde : u1/u2 ne sont pas des comptes de test"
RT3=$(runtime "$M3")
playi() { # ITEM SESSION POSITION
  jc -n --arg i "$1" --arg s "$2" --argjson p "$3" '{ItemId:$i,MediaSourceId:$i,PlaySessionId:$s,PlayMethod:"DirectPlay",PositionTicks:$p,CanSeek:true}'
}
play_to() { # TOKEN ITEM POSITION : lecture simulée démarrée à 0, arrêtée à POSITION
  local sid="spike-$RANDOM$RANDOM"
  api POST /Sessions/Playing "$(playi "$2" "$sid" 0)" "$1" >/dev/null
  api POST /Sessions/Playing/Progress "$(playi "$2" "$sid" $(($3/2)))" "$1" >/dev/null
  api POST /Sessions/Playing/Stopped "$(playi "$2" "$sid" "$3")" "$1"
}
pos_of() { # UID TOKEN ITEM -> position REST (ticks)
  api GET "/Users/$1/Items/$3" "" "$2" >/dev/null; jq -r '.UserData.PlaybackPositionTicks // 0' "$RESP"
}
played_of() { api GET "/Users/$1/Items/$3" "" "$2" >/dev/null; jq -r '.UserData.Played' "$RESP"; }
in_resume() { # UID TOKEN ITEM
  api GET "/Users/$1/Items/Resume?Recursive=true&MediaTypes=Video&Limit=100" "" "$2" >/dev/null
  jq -e --arg i "$3" "$DEFS"'[.Items[]|.Id|n]|index($i|n)!=null' "$RESP" >/dev/null
}
near() { local d=$(($1-$2)); ((d<0)) && d=$((-d)); ((d<=10000000)); }   # tolérance 1 s
ev_user() { # UID ITEM -> événements UserDataSaved compacts
  jq -c --arg u "$1" --arg i "$2" "$DEFS"'[.[]|select(.kind=="UserDataSaved" and (.userId|n)==($u|n) and (.itemId|n)==($i|n))|{saveReason,played,position:(.playbackPositionTicks // .positionTicks // null),pluginWrite}]' <<<"${EV:-[]}"
}

P1=$((RT3*40/100)); P2=$((RT3*70/100))
ev_clear
st=$(play_to "$T1" "$M3" "$P1")
ck U10 a-stop hard "u1 : lecture simulée de M3 arrêtée à 40 % (Playing, Progress, Stopped acceptés)" "{\"stopped\":\"$st\"}" is2xx "$st"
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U1\"|n) and (.itemId|n)==(\"$M3\"|n))]|length>0" 12 || true
E=$(ev_user "$U1" "$M3")
ck U10 a-event probe "UserDataSaved reçu pour (u1, M3) à l'arrêt ; SaveReason et position relevés" "$E" jq -e 'length>0' <<<"$E"
ck U10 a-event-pos probe "l'événement expose une position de lecture non nulle (sinon relire via IUserDataManager)" "$E" \
  jq -e 'any(.position!=null and .position>0)' <<<"$E"
X=$(pos_of "$U1" "$T1" "$M3")
ck U10 a-rest-pos probe "position REST de u1 = position d'arrêt (±1 s), média non lu" "{\"expected\":$P1,\"got\":$X,\"played\":\"$(played_of "$U1" "$T1" "$M3")\"}" \
  near "$X" "$P1"

ev_clear
st=$(api POST "$SPK/SetPosition" "$(jc -n --arg u "$U2" --arg i "$M3" --argjson p "$X" '{userId:$u,itemId:$i,positionTicks:$p}')")
ck U10 b-set probe "plugin : SetPosition(u2, M3, position de u1) accepté (HTTP $st)" "{\"status\":\"$st\"}" test "$st" = 200
Y=$(pos_of "$U2" "$T2" "$M3")
ck U10 b-pos probe "position de u2 relue par REST = position de u1 (±1 s)" "{\"expected\":$X,\"got\":$Y}" near "$Y" "$X"
PL2=$(played_of "$U2" "$T2" "$M3")
ck U10 b-notplayed probe "le média n'est PAS marqué lu chez u2 (Played=false)" "{\"played\":\"$PL2\"}" test "$PL2" = false
if in_resume "$U2" "$T2" "$M3"; then rs=true; else rs=false; fi
ck U10 b-resume probe "M3 apparaît dans /Users/{u2}/Items/Resume (« reprendre » proposé)" "{\"inResume\":$rs}" test "$rs" = true
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U2\"|n) and (.itemId|n)==(\"$M3\"|n))]|length>0" 8 || true
E=$(ev_user "$U2" "$M3")
ck U10 b-echo probe "l'écriture plugin de la position est reconnue (pluginWrite=true) : pas de boucle u1<->u2" "$E" jq -e 'length>0 and .[0].pluginWrite==true' <<<"$E"

ev_clear
st=$(play_to "$T2" "$M3" "$P2")
ck U10 c-stop hard "u2 poursuit : lecture simulée de M3 arrêtée à 70 %" "{\"stopped\":\"$st\"}" is2xx "$st"
Y=$(pos_of "$U2" "$T2" "$M3")
ck U10 c-pos probe "position de u2 = 70 % après sa lecture (±1 s)" "{\"expected\":$P2,\"got\":$Y}" near "$Y" "$P2"
ev_wait "[.[]|select(.kind==\"UserDataSaved\" and (.userId|n)==(\"$U2\"|n) and (.itemId|n)==(\"$M3\"|n))]|length>0" 12 || true
E=$(ev_user "$U2" "$M3")
ck U10 c-event probe "l'arrêt de u2 est vu comme écriture utilisateur (pluginWrite=false)" "$E" jq -e 'length>0 and all(.pluginWrite==false)' <<<"$E"

st=$(api POST "$SPK/SetPosition" "$(jc -n --arg u "$U1" --arg i "$M3" --argjson p "$Y" '{userId:$u,itemId:$i,positionTicks:$p}')")
X2=$(pos_of "$U1" "$T1" "$M3")
ck U10 d-reverse probe "sens inverse : SetPosition(u1, M3, position de u2) => u1 est mis à jour (dernière lecture gagne) (HTTP $st)" \
  "{\"expected\":$Y,\"got\":$X2}" near "$X2" "$Y"
if in_resume "$U1" "$T1" "$M3"; then rs=true; else rs=false; fi
ck U10 d-resume probe "M3 reste dans Resume de u1 avec la nouvelle position" "{\"inResume\":$rs}" test "$rs" = true
PLU3=$(played_of "$U3" "$T3" "$M3")
ck U10 e-isolation hard "u3 (non concerné) n'est pas affecté : M3 sans position ni lu" "{\"u3Played\":\"$PLU3\",\"u3Pos\":$(pos_of "$U3" "$T3" "$M3")}" \
  test "$PLU3/$(pos_of "$U3" "$T3" "$M3")" = "false/0"

# ---------------------------------------------------------------- bilan
echo "== Comptes protégés"
if compare_protected "fin de run" "${TEST_USERS[@]}"; then PROT=true; else PROT=false; HARD_FAIL=1; fi

jq -s --arg ts "$TS" --arg srv "$EXPECTED_SERVER_NAME" --argjson prot "$PROT" '
  {run:$ts, server:$srv, protectedAccountsUnchanged:$prot,
   summary:(group_by(.u)|map({u:.[0].u, ok:(map(select(.status=="OK"))|length), ko:(map(select(.status=="KO"))|length),
                              verdict:(if all(.status=="OK") then "OK" else "KO" end)})),
   results:.}' "$RES" > "$OUT_FILE"

echo
echo "== Synthèse par incertitude (détail : $OUT_FILE)"
jq -r '.summary[]|"  \(.u)\t\(.verdict)\t(\(.ok) OK / \(.ko) KO)"' "$OUT_FILE"
if [[ $HARD_FAIL == 1 ]]; then echo "ECHEC : au moins une assertion hard est KO." >&2; exit 1; fi
echo "Assertions hard : toutes OK (les KO probe sont des résultats du spike, pas des erreurs)."
