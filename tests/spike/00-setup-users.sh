#!/usr/bin/env bash
# 00-setup-users.sh — crée test_u1/test_u2/test_u3 sur emby2 (QUALIF uniquement).
#  - politique par défaut ; AllowSharingPersonalItems=true pour test_u1 seul
#  - échoue si un compte test_* existe déjà
#  - compare avant/après la liste des utilisateurs et les politiques de admin, cyril, user2
#    (user2 = compte de test de l'utilisateur : jamais modifié)
# Mots de passe aléatoires -> private/spike-users.env (gitignoré). Aucune valeur secrète affichée.
# Usage : tests/spike/00-setup-users.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV"
guard_target

echo "== Préconditions"
st=$(api GET /Users); [[ $st == 200 ]] || die "GET /Users -> $st"
for n in "${TEST_USERS[@]}"; do
  if jq -e --arg n "$n" '.[]|select(.Name==$n)' "$RESP" >/dev/null; then
    die "le compte $n existe déjà : lancer 90-cleanup.sh --delete-users d'abord"
  fi
done
[[ ! -f $USERS_ENV ]] || die "$USERS_ENV existe déjà : lancer 90-cleanup.sh --delete-users d'abord"

# Il faut au moins 4 médias avec durée pour les scénarios (m1..m4)
st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video&Fields=RunTimeTicks&SortBy=SortName&Limit=200")
[[ $st == 200 ]] || die "GET /Items -> $st"
MEDIA=$(jq -c '[.Items[]|select((.RunTimeTicks//0) >= 6000000000)|.Id][0:4]' "$RESP")
[[ $(jq 'length' <<<"$MEDIA") -ge 4 ]] || die "moins de 4 médias (>= 10 min) dans la bibliothèque : demander à l'utilisateur"
echo "  [OK] >= 4 médias exploitables"

echo "== Snapshot avant (utilisateurs + politiques ${PROTECTED_USERS[*]})"
snapshot_users > "$SNAPSHOT"
jq -r '"  utilisateurs : " + (.users|join(", "))' "$SNAPSHOT"
for n in "${PROTECTED_USERS[@]}"; do
  jq -e --arg n "$n" '.policies|has($n)' "$SNAPSHOT" >/dev/null || echo "  [WARN] compte protégé '$n' absent (non comparé)"
done

echo "== Création des comptes"
: > "$USERS_ENV"
ids=()
for i in 1 2 3; do
  n="test_u$i"
  st=$(api POST /Users/New "$(jq -nc --arg n "$n" '{Name:$n}')")
  [[ $st == 200 || $st == 204 ]] || die "création de $n -> HTTP $st"
  id=$(jq -r '.Id // empty' "$RESP")
  [[ -n $id ]] || id=$(user_id_by_name "$n")
  [[ -n $id ]] || die "id de $n introuvable"
  pw=$(python3 -c 'import secrets;print(secrets.token_urlsafe(18))')
  printf 'TEST_U%s_NAME=%s\nTEST_U%s_ID=%s\nTEST_U%s_PW=%s\n' "$i" "$n" "$i" "$id" "$i" "$pw" >> "$USERS_ENV"
  apiok 204 POST "/Users/$id/Password" "$(jq -nc --arg p "$pw" '{NewPw:$p}')"
  ids+=("$id")
  echo "  [OK] $n créé (mot de passe dans private/spike-users.env)"
done

echo "== AllowSharingPersonalItems=true pour test_u1 seul"
st=$(api GET "/Users/${ids[0]}"); [[ $st == 200 ]] || die "GET user u1 -> $st"
pol=$(jq -c '.Policy | .AllowSharingPersonalItems=true' "$RESP")
apiok 204 POST "/Users/${ids[0]}/Policy" "$pol"

echo "== Vérification de l'état"
declare -a pols
for i in 0 1 2; do
  st=$(api GET "/Users/${ids[$i]}"); [[ $st == 200 ]] || die "GET user -> $st"
  pols[i]=$(policy_without_share)
  share=$(jq -r '.Policy.AllowSharingPersonalItems' "$RESP")
  adm=$(jq -r '.Policy.IsAdministrator' "$RESP")
  [[ $adm == false ]] || die "${TEST_USERS[$i]} est administrateur"
  want=false; [[ $i == 0 ]] && want=true
  [[ $share == "$want" ]] || die "${TEST_USERS[$i]} : AllowSharingPersonalItems=$share (attendu $want)"
  echo "  [OK] ${TEST_USERS[$i]} : non admin, AllowSharingPersonalItems=$share"
done
[[ ${pols[0]} == "${pols[1]}" && ${pols[1]} == "${pols[2]}" ]] || die "les politiques de test_u1/u2/u3 diffèrent (hors AllowSharingPersonalItems) : pas la politique par défaut"
echo "  [OK] politiques identiques hors AllowSharingPersonalItems (défaut)"

for i in 1 2 3; do
  pw=$(envget "$USERS_ENV" "TEST_U${i}_PW")
  tok=$(login "test_u$i" "$pw")
  id=${ids[$((i-1))]}
  for m in $(jq -r '.[]' <<<"$MEDIA"); do
    st=$(api GET "/Users/$id/Items/$m" "" "$tok"); [[ $st == 200 ]] || die "test_u$i n'accède pas au média $m (HTTP $st)"
  done
  echo "  [OK] test_u$i s'authentifie et accède aux 4 médias de test"
done

echo "== Comparaison après"
compare_protected "après création" "${TEST_USERS[@]}" || die "comptes protégés modifiés : arrêt et investigation"
echo "Setup terminé."
