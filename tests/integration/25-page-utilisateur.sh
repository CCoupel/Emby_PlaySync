#!/usr/bin/env bash
# 25-page-utilisateur.sh — scénarios d'intégration P1-P23 de la v1.1.0 (issue #39) sur emby2
# (QUALIF UNIQUEMENT). À exécuter par qa après déploiement du plugin ; jamais depuis un poste sans avoir
# vérifié la cible. Écrit AVANT le code (plan `_work/reports/plan-20260928-143007.md`, contrats
# `contracts/http-endpoints.md` « Page utilisateur », spec `docs/chronogrammes.md` D19/D20/S10).
#
# Endpoints testés (auth = token de SESSION de l'utilisateur, jamais la clé admin) :
#   GET    /SharedPlaylist/User/Playlists
#   GET    /SharedPlaylist/User/Users
#   POST   /SharedPlaylist/User/Playlists/{id}/Members   {UserId,Level}
#   DELETE /SharedPlaylist/User/Playlists/{id}/Members/{userId}
#   POST   /SharedPlaylist/User/Playlists/{id}/Options   {Family,Enabled}
# Porte d'entrée : Policy.AllowSharingPersonalItems du DEMANDEUR ; 403 sharing-disabled sur tous ces endpoints
# si absente. Périmètre : uniquement les playlists dont le demandeur est PROPRIÉTAIRE ; 404 not-found identique
# pour "inexistante" et "non possédée" (anti-IDOR, jamais d'énumération). Niveaux Read/Write UNIQUEMENT (jamais
# Manage/ManageDelete). Écritures sous verrou playlist + relecture (busy non testable de façon déterministe par
# REST : couvert par UserPlaylistServiceSpecTests.Locked_ByAnotherThread_ReturnsBusy..., voir P-busy = SKIP ici).
#
# Prérequis : tests/integration/00-setup-users.sh exécuté (test_u1 propriétaire, test_u2, test_u3) ; >= 1 média
# (>= 10 min, comme le reste de la suite). Crée puis nettoie LUI-MÊME un compte sans permission
# (create_and_login_test_account, comme I37/I39 dans 23-permission.sh).
# Usage : tests/integration/25-page-utilisateur.sh [P1 P8 …]   (sans liste : tous les scénarios, DANS L'ORDRE :
#   P11+ dépendent du partage posé par P11, comme I36 dans 23-permission.sh)
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/int-lib.sh"

WANT=("$@")
OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/page-utilisateur-$TS.json"
RES="$SCRATCH/results.jsonl"; : > "$RES"
FAILS=0; DONE=0

rec() { # ID STATUS DESC [EVIDENCE_JSON]
  local ev=${4:-null}
  jq -e . <<<"$ev" >/dev/null 2>&1 || ev=$(jq -Rn --arg s "$ev" '$s')
  jq -nc --arg id "$1" --arg s "$2" --arg d "$3" --argjson e "$ev" '{id:$id,status:$s,desc:$d,evidence:$e}' >> "$RES"
  printf '  [%s] %s — %s\n' "$2" "$1" "$3"
  if [[ $2 == KO ]]; then FAILS=$((FAILS+1)); fi
  return 0
}
ck() { # ID DESC EVIDENCE cmd... : OK si cmd réussit, sinon KO
  local id=$1 d=$2 e=$3; shift 3
  if "$@"; then rec "$id" OK "$d" "$e"; else rec "$id" KO "$d" "$e"; fi
}
skip() { rec "$1" SKIP "$2" "${3:-null}"; }

write_out() { # PARTIAL
  jq -s --arg ts "$TS" --argjson p "$1" '
    {run:$ts, partial:$p, summary:{ok:(map(select(.status=="OK"))|length), ko:(map(select(.status=="KO"))|length), skip:(map(select(.status=="SKIP"))|length)}, results:.}' "$RES" > "$OUT_FILE" 2>/dev/null || true
}
on_exit() {
  local rc=$?; set +e
  cleanup_test_accounts
  cleanup_registered_playlists
  if [[ $DONE == 0 && -s $RES ]]; then write_out true; echo "  (trap) preuves partielles : $OUT_FILE" >&2; fi
  rm -rf "$SCRATCH"; exit $rc
}
trap on_exit EXIT

# --- helpers propres à ce script -------------------------------------------------------------------------------
UP="/SharedPlaylist/User"
upage_err() { jq -r '.error // empty' "$RESP"; } # après normalisation camelCase de api() (chemin /SharedPlaylist/*)
body_member() { jq -nc --arg u "$1" --arg l "$2" '{UserId:$u,Level:$l}'; }
body_option() { jq -nc --arg f "$1" --argjson e "$2" '{Family:$f,Enabled:$e}'; }
# st_err ATTENDU_HTTP ATTENDU_CODE METHODE CHEMIN [CORPS] [TOKEN] : vrai si HTTP + corps Error==CODE
st_err() {
  local wantst=$1 wantcode=$2; shift 2
  local st; st=$(api "$@")
  [[ $st == "$wantst" ]] || { echo "    (HTTP $st, attendu $wantst)" >&2; return 1; }
  [[ $(upage_err) == "$wantcode" ]] || { echo "    (error=$(upage_err), attendu $wantcode)" >&2; return 1; }
}
# st_ok ATTENDU_HTTP METHODE CHEMIN [CORPS] [TOKEN] : vrai si HTTP == ATTENDU (corps -> $RESP)
st_ok() {
  local wantst=$1; shift
  local st; st=$(api "$@")
  [[ $st == "$wantst" ]] || { echo "    (HTTP $st, attendu $wantst)" >&2; return 1; }
}

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV"
[[ -f $USERS_ENV && -f $SNAPSHOT ]] || die "lancer tests/integration/00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)")
T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)")
T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")

st=$(api GET "$DIAG/State"); [[ $st == 200 ]] || die "Diagnostics/State -> HTTP $st : plugin v1.1.0 non déployé ou EnableDiagnostics=false"

# test_u1 (propriétaire de toute la suite) DOIT avoir la permission de partage ; posée explicitement (ne présume
# jamais de AutoEnableSharing, comme 23-permission.sh).
set_sharing "$U1" true; wait_sharing "$U1" true 10 || die "AllowSharingPersonalItems de test_u1 non posé à true"
echo "  [OK] test_u1 a la permission de partage"

# Compte SANS la permission (P2/P3) : créé, connecté, puis FORCÉ à false explicitement (déterministe, quel que
# soit AutoEnableSharing — set_sharing après coup, comme create_and_login_test_account ne pose rien lui-même).
read -r NP_ID TNP < <(create_and_login_test_account "SPIKE-P-noperm")
set_sharing "$NP_ID" false; wait_sharing "$NP_ID" false 5 || die "impossible de poser AllowSharingPersonalItems=false sur le compte sans permission"
echo "  [OK] compte sans permission créé (id $NP_ID)"

st=$(api GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Video&Fields=RunTimeTicks&SortBy=SortName&Limit=200")
[[ $st == 200 ]] || die "GET /Items -> $st"
mapfile -t M < <(jq -r '[.Items[]|select((.RunTimeTicks//0)>=6000000000)|.Id][0:2][]' "$RESP")
[[ ${#M[@]} -ge 2 ]] || die "moins de 2 médias (>= 10 min) : demander à l'utilisateur"
reset_pool_full "${M[@]}"
ITEM1=${M[0]}; ITEM2=${M[1]}
echo "  [OK] 2 médias remis à zéro (lu=false, position=0)"

PL=$(new_pl "SPIKE PS-Main" "$ITEM1,$ITEM2")   # propriétaire test_u1, PAS encore partagée
echo "  [OK] playlist principale créée ($PL), non partagée"

# ---------------------------------------------------------------- scénarios
p1() {
  echo "== P1 (smoke) — GET User/Playlists sur un compte autorisé : 200, tableau JSON"
  ck P1 "GET User/Playlists renvoie 200 et un tableau" "null" st_ok 200 GET "$UP/Playlists" "" "$T1"
  ck P1.array "le corps est un tableau JSON" "null" bash -c "jq -e 'type==\"array\"' '$RESP' >/dev/null"
}

p2() {
  echo "== P2 (critical) — GET User/Playlists SANS permission : 403 sharing-disabled"
  ck P2 "403 sharing-disabled pour un compte sans AllowSharingPersonalItems" "null" \
    st_err 403 sharing-disabled GET "$UP/Playlists" "" "$TNP"
}

p3() {
  echo "== P3 (critical) — GET User/Users et POST Members SANS permission : 403 sur TOUS les endpoints User/*"
  ck P3.users "GET User/Users -> 403 sharing-disabled" "null" st_err 403 sharing-disabled GET "$UP/Users" "" "$TNP"
  ck P3.members "POST Members -> 403 sharing-disabled (même compte non-propriétaire, non-existant : le 403 prime)" "null" \
    st_err 403 sharing-disabled POST "$UP/Playlists/$PL/Members" "$(body_member "$U2" Write)" "$TNP"
}

p4() {
  echo "== P4 — GET User/Users : exclut le demandeur, contient les comptes actifs sélectionnables"
  ck P4 "GET User/Users -> 200" "null" st_ok 200 GET "$UP/Users" "" "$T1"
  ck P4.excludes_self "n'inclut pas test_u1 lui-même" "$(cat "$RESP")" \
    bash -c "! jq -e --arg u \"\$0\" '.[]|select(.userId==\$u)' '$RESP' >/dev/null" "$U1"
  ck P4.has_u2 "contient test_u2" "$(cat "$RESP")" bash -c "jq -e --arg u \"\$0\" '.[]|select(.userId==\$u)' '$RESP' >/dev/null" "$U2"
  ck P4.no_extra_field "aucun champ superflu (UserId/Name seulement)" "$(cat "$RESP")" \
    bash -c "jq -e 'all(.[]; (keys|sort)==[\"name\",\"userId\"])' '$RESP' >/dev/null"
}

p6() {
  echo "== P6 (critical, IDOR) — POST Members sur la playlist de test_u1, par test_u2 (non propriétaire) : 404 not-found"
  ck P6 "404 not-found (test_u2 n'est PAS propriétaire de \$PL)" "null" \
    st_err 404 not-found POST "$UP/Playlists/$PL/Members" "$(body_member "$U3" Write)" "$T2"
}

p7() {
  echo "== P7 (critical) — POST Members sur une playlist INEXISTANTE (par le vrai propriétaire) : même 404 not-found (anti-énumération)"
  ck P7 "404 not-found, code IDENTIQUE à P6 (indiscernable)" "null" \
    st_err 404 not-found POST "$UP/Playlists/does-not-exist-$RANDOM/Members" "$(body_member "$U2" Write)" "$T1"
}

p8() {
  echo "== P8 (critical) — niveaux interdits : Manage/ManageDelete/None/valeur inconnue -> 400 invalid-level"
  local lvl
  for lvl in Manage ManageDelete None banana; do
    ck "P8.$lvl" "Level=$lvl refusé (400 invalid-level)" "null" \
      st_err 400 invalid-level POST "$UP/Playlists/$PL/Members" "$(body_member "$U2" "$lvl")" "$T1"
  done
}

p9() {
  echo "== P9 — cible = soi-même : 400 self"
  ck P9 "400 self (le propriétaire ne peut pas s'ajouter lui-même)" "null" \
    st_err 400 self POST "$UP/Playlists/$PL/Members" "$(body_member "$U1" Write)" "$T1"
}

p10() {
  echo "== P10 — utilisateur cible inconnu : 400 invalid-user"
  ck P10 "400 invalid-user (id inexistant)" "null" \
    st_err 400 invalid-user POST "$UP/Playlists/$PL/Members" "$(body_member "ghost-$RANDOM" Write)" "$T1"
}

p11() {
  echo "== P11 (S10.1) — premier partage (test_u2, Write) : 200, playlist gérée immédiatement (première détection)"
  ck P11 "POST Members {u2,Write} -> 200" "null" st_ok 200 POST "$UP/Playlists/$PL/Members" "$(body_member "$U2" Write)" "$T1"
  ck P11.shared "isShared=true dans la réponse" "$(cat "$RESP")" bash -c "jq -e '.isShared==true' '$RESP' >/dev/null"
  local t o
  t=$(tags_of "$PL"); o=$(overview_of "$PL")
  ck P11.non_rm "remove-si-lu=NON posée (1re détection IMMÉDIATE, CA4, sans attendre la passe)" "$t" \
    bash -c "has_tag \"\$0\" \"$NON_RM\"" "$t"
  ck P11.non_pr "propager-lu=NON posée" "$t" bash -c "has_tag \"\$0\" \"$NON_PR\"" "$t"
  ck P11.help "message d'aide écrit (description non vide)" "\"$o\"" test -n "$o"
  local j n
  j=$(journal ShareChanged)
  n=$(jcount "$j" "$PL" ShareChanged 'action=add.*level=Write')
  ck P11.journal "ShareChanged journalisé (u2, action=add, level=Write)" "$j" test "$n" -ge 1
}

p12() {
  echo "== P12 — effet natif : test_u2 (Write) peut ajouter un média à la playlist"
  local st
  st=$(api POST "/Playlists/$PL/Items?Ids=$ITEM1&UserId=$U2" "" "$T2")
  ck P12 "POST /Playlists/{id}/Items par test_u2 (Write) -> 2xx (HTTP $st)" "null" is2xx "$st"
}

p13() {
  echo "== P13 — ajout test_u3 (Read) : effet natif 403 à l'ajout, visible dans « Gérer la collaboration » natif"
  ck P13.add "POST Members {u3,Read} -> 200" "null" st_ok 200 POST "$UP/Playlists/$PL/Members" "$(body_member "$U3" Read)" "$T1"
  local st
  st=$(api POST "/Playlists/$PL/Items?Ids=$ITEM2&UserId=$U3" "" "$T3")
  ck P13.native_forbidden "test_u3 (Read) reçoit 403 à l'ajout natif (Emby, pas le plugin) : HTTP $st" "null" test "$st" = 403
  st=$(api GET "/Users/ItemAccess?ItemId=$PL" "" "$T1")
  ck P13.item_access_http "GET /Users/ItemAccess (côté propriétaire) -> 2xx" "null" is2xx "$st"
  ck P13.item_access "partage de test_u3 visible dans la réponse native" "$(cat "$RESP")" \
    bash -c "jq -e --arg u \"\$0\" '.[]|select(.UserId==\$u)' '$RESP' >/dev/null" "$U3"
}

p14() {
  echo "== P14 — DELETE Members ciblant la ligne du propriétaire lui-même : 400 self"
  ck P14 "DELETE Members/{u1} par u1 -> 400 self" "null" \
    st_err 400 self DELETE "$UP/Playlists/$PL/Members/$U1" "" "$T1"
}

p15() {
  echo "== P15 (critical) — D19 : bascule remove-si-lu -> OUI, remplacement ATOMIQUE, journal MarkerSet"
  jclear
  ck P15 "POST Options {remove-si-lu,true} -> 200" "null" st_ok 200 POST "$UP/Playlists/$PL/Options" "$(body_option remove-si-lu true)" "$T1"
  ck P15.options_dto "options.remove-si-lu==Oui dans la réponse" "$(cat "$RESP")" bash -c "jq -e '.options[\"remove-si-lu\"]==\"Oui\"' '$RESP' >/dev/null"
  local t n
  t=$(tags_of "$PL")
  ck P15.single_tag "une seule étiquette remove-si-lu, canonique OUI" "$t" test "$(tag_count "$t" remove-si-lu)" = 1
  ck P15.other_family_intact "propager-lu=NON toujours présente (jamais touchée)" "$t" bash -c "has_tag \"\$0\" \"$NON_PR\"" "$t"
  local j; j=$(journal MarkerSet)
  n=$(jcount "$j" "$PL" MarkerSet 'family=remove-si-lu value=OUI removed=1')
  ck P15.journal "MarkerSet journalisé (family=remove-si-lu value=OUI removed=1)" "$j" test "$n" -ge 1
}

p16() {
  echo "== P16 (S10.3) — conflit résolu par la bascule : édition manuelle OUI+NON coexistants -> removed=2"
  owner_edit "$PL" '["remove-si-lu=NON"]' '[]'   # ajoute manuellement NON en plus du OUI déjà posé (P15) : conflit
  local t n; t=$(tags_of "$PL")
  ck P16.conflict_setup "conflit posé (OUI et NON coexistent)" "$t" bash -c "has_tag \"\$0\" \"$OUI_RM\" && has_tag \"\$0\" \"$NON_RM\"" "$t"
  jclear
  ck P16 "POST Options {remove-si-lu,true} de nouveau -> 200, conflit résolu" "null" st_ok 200 POST "$UP/Playlists/$PL/Options" "$(body_option remove-si-lu true)" "$T1"
  t=$(tags_of "$PL")
  ck P16.single_tag "une seule étiquette après résolution" "$t" test "$(tag_count "$t" remove-si-lu)" = 1
  local j; j=$(journal MarkerSet)
  n=$(jcount "$j" "$PL" MarkerSet 'removed=2')
  ck P16.removed2 "removed=2 journalisé" "$j" test "$n" -ge 1
}

p17() {
  echo "== P17 — chaîne complète : remove-si-lu=OUI actif + transition non-lu -> lu = retrait effectif"
  ck P17.baseline "\$ITEM1 présent avant la transition" "null" test "$(count_item "$PL" "$ITEM1")" -ge 1
  finish "$U2" "$T2" "$ITEM1"   # test_u2 (membre) marque ITEM1 lu -> transition non lu -> lu
  ck P17 "\$ITEM1 retiré de la playlist (moteur, chaîne réelle)" "null" wait_count "$PL" "$ITEM1" 0 15
}

p18() {
  echo "== P18 — écho contrôlé : l'écriture de la page ne provoque AUCUNE repose parasite du moteur (MarkerPosed)"
  local j n; j=$(journal MarkerPosed); n=$(jcount "$j" "$PL" MarkerPosed)
  ck P18 "aucun MarkerPosed pour \$PL après les écritures de la page (P15/P16 passent par MarkerSet, pas le moteur)" "$j" \
    test "$n" = 0
}

p19() {
  echo "== P19 — options réservées aux playlists PARTAGÉES : 409 not-shared sur une playlist jamais partagée"
  local pl2; pl2=$(new_pl "SPIKE PS-NotShared" "$ITEM1")
  ck P19 "409 not-shared" "null" st_err 409 not-shared POST "$UP/Playlists/$pl2/Options" "$(body_option remove-si-lu true)" "$T1"
}

p20() {
  echo "== P20 — famille inconnue : 400 invalid-family"
  ck P20 "400 invalid-family" "null" \
    st_err 400 invalid-family POST "$UP/Playlists/$PL/Options" "$(body_option bogus-family true)" "$T1"
}

p21() {
  echo "== P21 (S10.6) — retrait du dernier membre : playlist redevient non partagée, étiquettes INERTES conservées"
  ck P21.remove_u3 "DELETE Members/{u3} -> 200 (u3 retiré, u2 reste : encore partagée)" "null" \
    st_ok 200 DELETE "$UP/Playlists/$PL/Members/$U3" "" "$T1"
  local st; st=$(api GET "$UP/Playlists" "" "$T1")
  ck P21.still_shared_http "GET User/Playlists -> 2xx" "null" is2xx "$st"
  ck P21.still_shared "toujours partagée après le retrait de u3 (u2 reste)" "$(cat "$RESP")" \
    bash -c "jq -e --arg p \"\$0\" '.[]|select(.playlistId==\$p)|.isShared==true' '$RESP' >/dev/null" "$PL"
  ck P21 "DELETE Members/{u2} (dernier membre) -> 200, isShared=false" "null" st_ok 200 DELETE "$UP/Playlists/$PL/Members/$U2" "" "$T1"
  ck P21.unshared "isShared=false dans la réponse" "$(cat "$RESP")" bash -c "jq -e '.isShared==false' '$RESP' >/dev/null"
  local t; t=$(tags_of "$PL")
  ck P21.tags_inert "remove-si-lu=OUI toujours présente (jamais supprimée par le retrait d'un membre)" "$t" bash -c "has_tag \"\$0\" \"$OUI_RM\"" "$t"
}

p22() {
  echo "== P22 — DELETE Members sur un utilisateur qui n'est PAS/PLUS membre : 404 not-found"
  ck P22 "404 not-found (u3 déjà retiré en P21)" "null" \
    st_err 404 not-found DELETE "$UP/Playlists/$PL/Members/$U3" "" "$T1"
}

p23() {
  echo "== P23 (critical, sécurité) — retirer un membre ne fait JAMAIS perdre la ligne ManageDelete du propriétaire"
  # _work/reports/security-audit-20260928-154256.md point 7 (MOYENNE) + _work/reports/code-review-20260928-154519.md
  # ("retour bool jamais vérifié") : EmbyShareGateway.DeleteShare purge TOUTES les lignes de partage PUIS reconstruit
  # explicitement (propriétaire compris), sans transaction SDK. Test dédié demandé par le teamleader : vérifier
  # IMMÉDIATEMENT après le retrait que le propriétaire garde sa ligne ManageDelete — sur les DEUX formes de
  # reconstruction (toKeep = [propriétaire, autre membre] ET toKeep = [propriétaire] seul, cas le plus à risque).
  local pl3
  pl3=$(new_pl "SPIKE PS-OwnerIntegrity" "$ITEM1")
  ck P23.share1 "POST Members {u2,Write} -> 200 (premier partage)" "null" st_ok 200 POST "$UP/Playlists/$pl3/Members" "$(body_member "$U2" Write)" "$T1"
  ck P23.share2 "POST Members {u3,Read} -> 200 (deuxième membre)" "null" st_ok 200 POST "$UP/Playlists/$pl3/Members" "$(body_member "$U3" Read)" "$T1"

  local st
  st=$(api GET "/Users/ItemAccess?ItemId=$pl3" "" "$T1")
  ck P23.owner_before_http "GET /Users/ItemAccess -> 2xx (avant tout retrait)" "null" is2xx "$st"
  ck P23.owner_before "propriétaire (u1) a ManageDelete AVANT tout retrait" "$(cat "$RESP")" \
    bash -c "jq -e --arg u \"\$0\" '.[]|select(.UserId==\$u and .ItemAccess==\"ManageDelete\")' '$RESP' >/dev/null" "$U1"

  # Retrait d'un membre NON dernier (u2) : reconstruction = [propriétaire, u3].
  ck P23.remove_not_last "DELETE Members/{u2} (u3 reste) -> 200" "null" st_ok 200 DELETE "$UP/Playlists/$pl3/Members/$U2" "" "$T1"
  st=$(api GET "/Users/ItemAccess?ItemId=$pl3" "" "$T1")
  ck P23.owner_after_notlast_http "GET /Users/ItemAccess -> 2xx (après retrait non-dernier)" "null" is2xx "$st"
  ck P23.owner_after_notlast "propriétaire garde ManageDelete APRÈS le retrait d'un membre non-dernier" "$(cat "$RESP")" \
    bash -c "jq -e --arg u \"\$0\" '.[]|select(.UserId==\$u and .ItemAccess==\"ManageDelete\")' '$RESP' >/dev/null" "$U1"

  # Retrait du DERNIER membre (u3) : reconstruction = [propriétaire] SEUL — cas le plus à risque (security-audit point 7).
  ck P23.remove_last "DELETE Members/{u3} (dernier membre) -> 200" "null" st_ok 200 DELETE "$UP/Playlists/$pl3/Members/$U3" "" "$T1"
  st=$(api GET "/Users/ItemAccess?ItemId=$pl3" "" "$T1")
  ck P23.owner_after_last_http "GET /Users/ItemAccess -> 2xx (après retrait du dernier membre)" "null" is2xx "$st"
  ck P23.owner_after_last "propriétaire garde ManageDelete APRÈS le retrait du DERNIER membre (cas le plus à risque)" "$(cat "$RESP")" \
    bash -c "jq -e --arg u \"\$0\" '.[]|select(.UserId==\$u and .ItemAccess==\"ManageDelete\")' '$RESP' >/dev/null" "$U1"

  # Preuve applicative complémentaire : le plugin lui-même retrouve encore le propriétaire ensuite (GetOwned),
  # pas seulement le natif /Users/ItemAccess.
  ck P23.still_manageable "POST Members (repartage) -> 200 : GetOwned résout encore le propriétaire après les retraits" "null" \
    st_ok 200 POST "$UP/Playlists/$pl3/Members" "$(body_member "$U2" Write)" "$T1"
}

p_busy() {
  echo "== P-busy — 409 busy : SKIP (aucun moyen déterministe de forcer la contention du verrou par REST)"
  skip P-busy "couvert par UserPlaylistServiceSpecTests.Locked_ByAnotherThread_ReturnsBusy_WithinFiveSecondBudget (unitaire)"
}

ALL=(P1 P2 P3 P4 P6 P7 P8 P9 P10 P11 P12 P13 P14 P15 P16 P17 P18 P19 P20 P21 P22 P23 P-busy)
if [[ ${#WANT[@]} -eq 0 ]]; then WANT=("${ALL[@]}"); fi
for s in "${WANT[@]}"; do
  fn=$(tr 'A-Z-' 'a-z_' <<<"$s")
  declare -F "$fn" >/dev/null || die "scénario inconnu : $s"
  "$fn"
done

echo "== Comptes protégés"
compare_protected "fin des scénarios" "${TEST_USERS[@]}" && rec PROTECTED OK "admin, cyril, user2 inchangés" || rec PROTECTED KO "comptes protégés modifiés" "null"

write_out false; DONE=1
echo
echo "== Bilan (détail : $OUT_FILE)"
jq -r '.summary|"  OK=\(.ok) KO=\(.ko) SKIP=\(.skip)"' "$OUT_FILE"
[[ $FAILS == 0 ]] || { echo "ECHEC : $FAILS assertion(s) KO" >&2; exit 1; }
echo "Toutes les assertions exécutées sont OK."
