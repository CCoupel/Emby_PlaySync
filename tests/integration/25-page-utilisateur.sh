#!/usr/bin/env bash
# 25-page-utilisateur.sh — scénarios d'intégration P1-P23 de la v1.1.0 (issue #39) puis P24-P26 (#56, D21) et P27-P32 (#55, D22, S11 : création de playlist) de la v1.2.0 sur emby2
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
  purge_created
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
# has_both_tags JSON TAG1 TAG2 : vrai si les deux étiquettes sont présentes (appelée directement, JAMAIS via
# `bash -c` : has_tag/int-lib.sh n'est pas exportée avec `export -f`, un sous-shell bash ne la verrait pas —
# qa-20260928-160840.md §4.1 ; même raison pour toute autre assertion basée sur has_tag dans ce script).
has_both_tags() { has_tag "$1" "$2" && has_tag "$1" "$3"; }
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
purge_stale_test_playlists   # #60 : aucune playlist SPIKE résiduelle d'un run précédent (sauf imbriqué : INT_NESTED=1)

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
    has_tag "$t" "$NON_RM"
  ck P11.non_pr "propager-lu=NON posée" "$t" has_tag "$t" "$NON_PR"
  ck P11.non_av "propager-avancement=NON posée (v1.2.0, D21 : 3e famille, jamais héritée)" "$t" has_tag "$t" "$NON_AV"
  api GET "$UP/Playlists" "" "$T1" >/dev/null
  ck P11.options3 "GET User/Playlists : Options = EXACTEMENT trois clés (remove-si-lu, propager-lu, propager-avancement), toutes à Non (v1.2.0)" "$(cat "$RESP")" \
    bash -c "jq -e --arg p \"\$0\" '.[]|select(.playlistId==\$p)|(.options|keys|sort)==[\"propager-avancement\",\"propager-lu\",\"remove-si-lu\"] and ([.options[]]|unique)==[\"Non\"]' '$RESP' >/dev/null" "$PL"
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
    bash -c "jq -e --arg u \"\$0\" '.Items[]|select(.Id==\$u)' '$RESP' >/dev/null" "$U3"
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
  ck P15.other_family_intact "propager-lu=NON toujours présente (jamais touchée)" "$t" has_tag "$t" "$NON_PR"
  local j; j=$(journal MarkerSet)
  n=$(jcount "$j" "$PL" MarkerSet 'family=remove-si-lu value=OUI removed=1')
  ck P15.journal "MarkerSet journalisé (family=remove-si-lu value=OUI removed=1)" "$j" test "$n" -ge 1
}

p16() {
  echo "== P16 (S10.3) — conflit résolu par la bascule : édition manuelle OUI+NON coexistants -> removed=2"
  owner_edit "$PL" '["remove-si-lu=NON"]' '[]'   # ajoute manuellement NON en plus du OUI déjà posé (P15) : conflit
  local t n; t=$(tags_of "$PL")
  ck P16.conflict_setup "conflit posé (OUI et NON coexistent)" "$t" has_both_tags "$t" "$OUI_RM" "$NON_RM"
  jclear
  ck P16 "POST Options {remove-si-lu,true} de nouveau -> 200, conflit résolu" "null" st_ok 200 POST "$UP/Playlists/$PL/Options" "$(body_option remove-si-lu true)" "$T1"
  t=$(tags_of "$PL")
  ck P16.single_tag "une seule étiquette après résolution" "$t" test "$(tag_count "$t" remove-si-lu)" = 1
  local j; j=$(journal MarkerSet)
  n=$(jcount "$j" "$PL" MarkerSet 'removed=2')
  ck P16.removed2 "removed=2 journalisé" "$j" test "$n" -ge 1
}

p17() {
  echo "== P17 — chaîne complète : remove-si-lu=OUI ET propager-lu=OUI (v1.2.0, D21) + transition non-lu -> lu = retrait effectif"
  ck P17.enable_pr "POST Options {propager-lu,true} -> 200 (le retrait exige propager-lu depuis v1.2.0 ; remove-si-lu=OUI posée en P15)" "null" \
    st_ok 200 POST "$UP/Playlists/$PL/Options" "$(body_option propager-lu true)" "$T1"
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
  ck P21.tags_inert "remove-si-lu=OUI toujours présente (jamais supprimée par le retrait d'un membre)" "$t" has_tag "$t" "$OUI_RM"
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
    bash -c "jq -e --arg u \"\$0\" '.Items[]|select(.Id==\$u and .UserItemShareLevel==\"ManageDelete\")' '$RESP' >/dev/null" "$U1"

  # Retrait d'un membre NON dernier (u2) : reconstruction = [propriétaire, u3].
  ck P23.remove_not_last "DELETE Members/{u2} (u3 reste) -> 200" "null" st_ok 200 DELETE "$UP/Playlists/$pl3/Members/$U2" "" "$T1"
  st=$(api GET "/Users/ItemAccess?ItemId=$pl3" "" "$T1")
  ck P23.owner_after_notlast_http "GET /Users/ItemAccess -> 2xx (après retrait non-dernier)" "null" is2xx "$st"
  ck P23.owner_after_notlast "propriétaire garde ManageDelete APRÈS le retrait d'un membre non-dernier" "$(cat "$RESP")" \
    bash -c "jq -e --arg u \"\$0\" '.Items[]|select(.Id==\$u and .UserItemShareLevel==\"ManageDelete\")' '$RESP' >/dev/null" "$U1"

  # Retrait du DERNIER membre (u3) : reconstruction = [propriétaire] SEUL — cas le plus à risque (security-audit point 7).
  ck P23.remove_last "DELETE Members/{u3} (dernier membre) -> 200" "null" st_ok 200 DELETE "$UP/Playlists/$pl3/Members/$U3" "" "$T1"
  st=$(api GET "/Users/ItemAccess?ItemId=$pl3" "" "$T1")
  ck P23.owner_after_last_http "GET /Users/ItemAccess -> 2xx (après retrait du dernier membre)" "null" is2xx "$st"
  ck P23.owner_after_last "propriétaire garde ManageDelete APRÈS le retrait du DERNIER membre (cas le plus à risque)" "$(cat "$RESP")" \
    bash -c "jq -e --arg u \"\$0\" '.Items[]|select(.Id==\$u and .UserItemShareLevel==\"ManageDelete\")' '$RESP' >/dev/null" "$U1"

  # Preuve applicative complémentaire : le plugin lui-même retrouve encore le propriétaire ensuite (GetOwned),
  # pas seulement le natif /Users/ItemAccess.
  ck P23.still_manageable "POST Members (repartage) -> 200 : GetOwned résout encore le propriétaire après les retraits" "null" \
    st_ok 200 POST "$UP/Playlists/$pl3/Members" "$(body_member "$U2" Write)" "$T1"
}

p24() {
  echo "== P24 (v1.2.0, S10b/D19/D21) — trois familles : POST Options propager-avancement, remplacement atomique, autres familles intactes, journal MarkerSet"
  local pl4 t j n
  pl4=$(new_pl "SPIKE PS-Trois" "$ITEM1,$ITEM2")
  ck P24.share "POST Members {u2,Write} -> 200 (premier partage : trois NON posées immédiatement)" "null" st_ok 200 POST "$UP/Playlists/$pl4/Members" "$(body_member "$U2" Write)" "$T1"
  PL4=$pl4
  local st; st=$(api GET "$UP/Playlists" "" "$T1")
  ck P24.list_http "GET User/Playlists -> 2xx" "null" is2xx "$st"
  ck P24.list "trois clés Options, toutes Non" "$(cat "$RESP")" \
    bash -c "jq -e --arg p \"\$0\" '.[]|select(.playlistId==\$p)|(.options|keys|sort)==[\"propager-avancement\",\"propager-lu\",\"remove-si-lu\"] and ([.options[]]|unique)==[\"Non\"]' '$RESP' >/dev/null" "$pl4"
  ck P24 "POST Options {propager-avancement,true} -> 200" "null" st_ok 200 POST "$UP/Playlists/$pl4/Options" "$(body_option propager-avancement true)" "$T1"
  ck P24.dto "options.propager-avancement==Oui, les deux autres familles inchangées (Non)" "$(cat "$RESP")" \
    bash -c "jq -e '.options[\"propager-avancement\"]==\"Oui\" and .options[\"propager-lu\"]==\"Non\" and .options[\"remove-si-lu\"]==\"Non\"' '$RESP' >/dev/null"
  t=$(tags_of "$pl4")
  ck P24.single_tag "une seule étiquette propager-avancement, canonique OUI ; NON retirée" "$t" test "$(tag_count "$t" propager-avancement)/$(has_tag "$t" "$OUI_AV" && echo y || echo n)" = "1/y"
  ck P24.others_intact "remove-si-lu=NON et propager-lu=NON toujours présentes (D19 : chaque bascule ne touche que sa famille)" "$t" has_both_tags "$t" "$NON_RM" "$NON_PR"
  j=$(journal MarkerSet); n=$(jcount "$j" "$pl4" MarkerSet 'family=propager-avancement value=OUI removed=1')
  ck P24.journal "MarkerSet journalisé (family=propager-avancement value=OUI removed=1)" "$j" test "$n" -ge 1
  ck P24.invalid_variant "Family « Propager-Avancement » (casse) refusée : 400 invalid-family (comparaison exacte)" "null" \
    st_err 400 invalid-family POST "$UP/Playlists/$pl4/Options" "$(body_option Propager-Avancement true)" "$T1"
}

p25() {
  echo "== P25 (v1.2.0, S10b/D21) — désactiver « Propager le lu » conserve remove-si-lu=OUI (inerte) ; l'API accepte remove-si-lu sans propager-lu ; aucun retrait"
  local pl4=${PL4:-} t item
  [[ -n $pl4 ]] || { skip P25 "P24 non exécuté (playlist PS-Trois absente) : jouer P24 d'abord"; return; }
  item=$ITEM2
  ck P25.rm_alone "POST Options {remove-si-lu,true} alors que propager-lu=NON -> 200 (l'API accepte : la dépendance est appliquée par le moteur et signalée par la page)" "null" \
    st_ok 200 POST "$UP/Playlists/$pl4/Options" "$(body_option remove-si-lu true)" "$T1"
  ck P25.rm_alone_dto "options.remove-si-lu==Oui et propager-lu==Non (état, pas effet)" "$(cat "$RESP")" \
    bash -c "jq -e '.options[\"remove-si-lu\"]==\"Oui\" and .options[\"propager-lu\"]==\"Non\"' '$RESP' >/dev/null"
  # S3b : transition non lu -> lu sous remove-si-lu=OUI + propager-lu=NON : AUCUN retrait, AUCUN flag propagé
  jclear
  finish "$U2" "$T2" "$item"
  ck P25.s3b_stays "S3b : média conservé malgré remove-si-lu=OUI (propager-lu=NON)" "null" stays "$pl4" "$item" 1 5
  ck P25.s3b_noflag "S3b : aucun flag lu propagé à u1" "null" test "$(played_of "$U1" "$T1" "$item")" = false
  ck P25.s3b_journal "S3b : Skipped inactive journalisé, aucune entrée Removal" "$(journal "Skipped,Removal")" \
    test "$(jcount "$(journal "Skipped,Removal")" "$pl4" Skipped 'inactive')/$(jcount "$(journal "Skipped,Removal")" "$pl4" Removal)" = "1/0"
  # activer puis désactiver « Propager le lu » : remove-si-lu=OUI conservé
  ck P25.pr_on "POST Options {propager-lu,true} -> 200" "null" st_ok 200 POST "$UP/Playlists/$pl4/Options" "$(body_option propager-lu true)" "$T1"
  ck P25.pr_off "POST Options {propager-lu,false} -> 200" "null" st_ok 200 POST "$UP/Playlists/$pl4/Options" "$(body_option propager-lu false)" "$T1"
  ck P25.rm_kept "après désactivation de propager-lu : remove-si-lu=OUI CONSERVÉ (D19 : pas de bascule automatique), propager-lu=NON" "$(cat "$RESP")" \
    bash -c "jq -e '.options[\"remove-si-lu\"]==\"Oui\" and .options[\"propager-lu\"]==\"Non\" and .options[\"propager-avancement\"]==\"Oui\"' '$RESP' >/dev/null"
  t=$(tags_of "$pl4")
  ck P25.tag_kept "étiquette remove-si-lu=OUI toujours posée" "$t" has_tag "$t" "$OUI_RM"
}

p26() {
  echo "== P26 (v1.2.0, D21) — chaîne complète de l'avancement : propager-avancement=OUI (posée en P24), arrêt de u2 => position chez u1, sans lu"
  local pl4=${PL4:-} item p pu1
  [[ -n $pl4 ]] || { skip P26 "P24 non exécuté (playlist PS-Trois absente)"; return; }
  item=$ITEM1
  unmark "$U1" "$T1" "$item"; unmark "$U2" "$T2" "$item"
  p=$(ticks_at "$item" 40)
  jclear
  local sid; sid=$(play_start "$U2" "$T2" "$item")
  play_progress "$U2" "$T2" "$item" "$sid" "$((p/2))" false >/dev/null
  play_stop "$U2" "$T2" "$item" "$sid" "$p" >/dev/null
  wait_position "$U1" "$T1" "$item" "$p" 10 || true
  pu1=$(position_of "$U1" "$T1" "$item")
  ck P26.position "u1 reçoit la position de l'arrêt de u2 (avancement seul, propager-lu=NON)" "{\"u1\":\"$pu1\",\"expected\":$p}" test "$pu1" = "$p"
  ck P26.notplayed "aucun flag lu posé (l'avancement ne touche jamais au lu)" "null" test "$(played_of "$U1" "$T1" "$item")" = false
}

# --- v1.2.0 (#55, D22, S11) : création d'une playlist depuis la page --------------------------------------------------
body_name() { jq -nc --arg n "$1" '{Name:$n}'; }
created_id() { jq -r '.playlistId // empty' "$RESP"; }
# track_created ID : playlist créée PAR CE SCRIPT via la page (noms libres, non préfixés SPIKE) : enregistrée pour le nettoyage ET
# suivie pour purge_created (le garde SPIKE* de cleanup_registered_playlists reste inchangé : il ne supprime jamais un nom libre).
track_created() { [[ -n ${1:-} ]] || return 0; register_playlist "$1"; echo "$1" >> "$SCRATCH/created.ids"; }
# purge_created [KEEP_ID] : supprime (admin) les playlists suivies, sauf KEEP_ID — évite d'épuiser le quota de 10 de test_u1 (M1)
# entre P27-P33 et P34, et ne laisse aucune playlist « À voir », « Été », ../x… après le run.
purge_created() {
  local keep=${1:-} id; [[ -s $SCRATCH/created.ids ]] || return 0
  while IFS= read -r id; do
    [[ -n $id && $id != "$keep" ]] || continue
    api DELETE "/Items/$id" >/dev/null || true
  done < "$SCRATCH/created.ids"
  if [[ -n $keep ]]; then echo "$keep" > "$SCRATCH/created.ids"; else : > "$SCRATCH/created.ids"; fi
}

p27() {
  echo "== P27 (smoke, critical) — S11 : POST User/Playlists {Name} -> 200, playlist vide, NON partagée, NON gérée (aucune étiquette, aucun message d'aide)"
  local id t o j
  jclear
  ck P27 "POST User/Playlists {\"Films du dimanche\"} -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "  Films du dimanche ")" "$T1"
  id=$(created_id); CREATED_PL=$id
  [[ -n $id ]] && track_created "$id"
  ck P27.id "réponse : playlistId renseigné" "$(cat "$RESP")" test -n "$id"
  ck P27.shape "réponse : isShared=false, members=[], itemCount=0, name normalisé (trim), trois options à None" "$(cat "$RESP")" \
    bash -c "jq -e '.isShared==false and (.members|length)==0 and .itemCount==0 and .name==\"Films du dimanche\" and (.options|keys|sort)==[\"propager-avancement\",\"propager-lu\",\"remove-si-lu\"] and ([.options[]]|unique)==[\"None\"]' '$RESP' >/dev/null"
  t=$(tags_of "$id"); o=$(overview_of "$id")
  ck P27.native "la playlist existe côté Emby, propriétaire test_u1 (visible via GET /Items/{id} par u1)" "null" test "$(api GET "/Users/$U1/Items/$id" "" "$T1")" = 200
  ck P27.notags "aucune étiquette de gestion (playlist non partagée, D6)" "$t" test "$(tag_count "$t" remove-si-lu)/$(tag_count "$t" propager-lu)/$(tag_count "$t" propager-avancement)" = "0/0/0"
  ck P27.nohelp "aucun message d'aide écrit" "\"$o\"" test -z "$o"
  j=$(journal PlaylistCreated)
  local jn; jn=$(jq -r --arg p "$id" --arg u "$U1" '[.[]|select((.playlistId|tostring)==$p and .userId==$u and ((.detail//"")|length)==0)]|length' <<<"$j")
  ck P27.journal "PlaylistCreated journalisé (ids seulement, UserId=test_u1, Detail vide : jamais le nom)" "$j" test "$jn" = 1
  ck P27.nomarkerposed "aucun MarkerPosed (pas de première détection à la création)" "$(journal MarkerPosed)" test "$(jcount "$(journal MarkerPosed)" "$id" MarkerPosed)" = 0
  api GET "$UP/Playlists" "" "$T1" >/dev/null
  ck P27.list "présente dans GET User/Playlists avec isShared=false" "$(cat "$RESP")" \
    bash -c "jq -e --arg p \"\$0\" '.[]|select(.playlistId==\$p)|.isShared==false' '$RESP' >/dev/null" "$id"
}

p28() {
  echo "== P28 — S11 lignes 2-3 : doublon (trim, casse, NFC) -> 409 name-exists ; nom invalide -> 400 invalid-name"
  ck P28.dup_exact "même nom -> 409 name-exists" "null" st_err 409 name-exists POST "$UP/Playlists" "$(body_name "Films du dimanche")" "$T1"
  ck P28.dup_variant "« films du DIMANCHE » avec espaces de bord (trim + casse) -> 409 name-exists" "null" st_err 409 name-exists POST "$UP/Playlists" "$(body_name $'  films du DIMANCHE ')" "$T1"
  ck P28.empty "nom vide -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(body_name "")" "$T1"
  ck P28.blank "nom blanc -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(body_name "   ")" "$T1"
  ck P28.long "nom de 101 caractères -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(body_name "$(printf 'x%.0s' {1..101})")" "$T1"
  ck P28.control "nom avec caractère de contrôle -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(jq -nc '{Name:"a\u0001b"}')" "$T1"
  ck P28.null "corps sans Name -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" '{}' "$T1"
  ck P28.nojournal "aucun PlaylistCreated pour les refus (un seul depuis P27)" "null" test "$(journal PlaylistCreated | jq 'length')" = 1
}

p29() {
  echo "== P29 — accents et espaces internes SIGNIFICATIFS ; 100 caractères acceptés"
  local a b c
  ck P29.accent1 "« Été » -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "Été")" "$T1"; a=$(created_id); [[ -n $a ]] && track_created "$a"
  ck P29.accent2 "« Ete » (sans accents) est un AUTRE nom -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "Ete")" "$T1"; b=$(created_id); [[ -n $b ]] && track_created "$b"
  ck P29.nfc "« Été » décomposé (NFC) -> 409 name-exists" "null" st_err 409 name-exists POST "$UP/Playlists" "$(jq -nc '{Name:"E\u0301te\u0301"}')" "$T1"
  ck P29.space1 "« À  voir » (deux espaces) -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "À  voir")" "$T1"; c=$(created_id); [[ -n $c ]] && track_created "$c"
  ck P29.space2 "« À voir » (un espace) est un AUTRE nom -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "À voir")" "$T1"; c=$(created_id); [[ -n $c ]] && track_created "$c"
  ck P29.len100 "un nom de 100 caractères -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "$(printf 'y%.0s' {1..100})")" "$T1"; c=$(created_id); [[ -n $c ]] && track_created "$c"
  purge_created "${CREATED_PL:-}"   # quota (M1) : ne garder que la playlist de P27 (nécessaire à P31)
}

p30() {
  echo "== P30 — S11 ligne 4 : l'unicité est PAR PROPRIÉTAIRE ; ligne 5 : sans permission -> 403 sharing-disabled ; propriétaire = session"
  local id2 tok2 st
  read -r id2 tok2 < <(create_and_login_test_account "SPIKE-P-owner2")
  set_sharing "$id2" true; wait_sharing "$id2" true 10 || die "AllowSharingPersonalItems non posé sur le second propriétaire"
  ck P30.other_owner "un autre compte (avec permission) crée « Films du dimanche » alors que test_u1 le possède : 200 (unicité par propriétaire)" "null" \
    st_ok 200 POST "$UP/Playlists" "$(body_name "Films du dimanche")" "$tok2"
  st=$(created_id); [[ -n $st ]] && track_created "$st"
  ck P30.not_listed_for_u1 "GET User/Playlists de test_u1 ne contient PAS la playlist du second compte" "$(cat "$RESP")" \
    bash -c "! jq -e --arg p \"\$0\" '.[]|select(.playlistId==\$p)' '$RESP' >/dev/null" "$st"
  ck P30.nopermission "compte SANS permission de partage -> 403 sharing-disabled (avant toute validation du nom)" "null" \
    st_err 403 sharing-disabled POST "$UP/Playlists" "$(body_name "")" "$TNP"
}

p31() {
  echo "== P31 (critical) — S11 -> S10 : ajouter un premier membre à la playlist créée = premier partage, première détection, TROIS étiquettes NON"
  local id t
  id=${CREATED_PL:-}
  [[ -n $id ]] || { skip P31 "P27 non exécuté"; return; }
  ck P31.share "POST Members {u2,Write} sur la playlist créée -> 200" "null" st_ok 200 POST "$UP/Playlists/$id/Members" "$(body_member "$U2" Write)" "$T1"
  ck P31.shared "isShared=true" "$(cat "$RESP")" bash -c "jq -e '.isShared==true' '$RESP' >/dev/null"
  t=$(tags_of "$id")
  has_tag "$t" "$NON_RM" && has_tag "$t" "$NON_PR" && has_tag "$t" "$NON_AV" && rec P31.tags OK "trois NON posées" "$t" || rec P31.tags KO "trois NON attendues" "$t"
  ck P31.help "message d'aide écrit" "null" test -n "$(overview_of "$id")"
  purge_created   # quota (M1) : P32-P34 repartent de zéro playlist créée par la page
}

p32() {
  echo "== P32 — course : deux créations simultanées du même nom -> une seule réussit (verrou de création par propriétaire)"
  local i ok=0 ko=0 st
  rm -f "$SCRATCH"/race.*
  for i in 1 2; do
    ( CFG="$SCRATCH/race.$i.cfg"; RESP="$SCRATCH/race.$i.resp"; BODYF="$SCRATCH/race.$i.body"
      st=$(api POST "$UP/Playlists" "$(body_name "SPIKE-course-$$")" "$T1"); echo "$st" > "$SCRATCH/race.$i.st"
      jq -r '.playlistId // empty' "$RESP" > "$SCRATCH/race.$i.id" ) &
  done
  wait
  for i in 1 2; do
    st=$(cat "$SCRATCH/race.$i.st"); [[ $st == 200 ]] && ok=$((ok+1)); [[ $st == 409 ]] && ko=$((ko+1))
    [[ -s "$SCRATCH/race.$i.id" ]] && track_created "$(cat "$SCRATCH/race.$i.id")"
  done
  ck P32 "exactement une création 200 et un refus 409 (name-exists)" "{\"ok\":$ok,\"conflict\":$ko}" test "$ok/$ko" = "1/1"
  api GET "$UP/Playlists" "" "$T1" >/dev/null
  ck P32.single "une seule playlist portant ce nom dans la liste" "$(cat "$RESP")" \
    bash -c "[[ \$(jq -r --arg n 'SPIKE-course-$$' '[.[]|select(.name==\$n)]|length' '$RESP') == 1 ]]"
  purge_created
}


p33() {
  echo "== P33 (QUALIF, F2) — noms hostiles : ce que le plugin refuse (400 invalid-name) et ce qu'Emby stocke/renvoie tel quel pour les noms acceptés"
  local n id name
  # refusés par le plugin : contrôle, Cf (U+200B, U+202E), séparateur de ligne (U+2028), NUL
  ck P33.zwsp "U+200B seul -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(jq -nc '{Name:"\u200b"}')" "$T1"
  ck P33.rlo "U+202E (RIGHT-TO-LEFT OVERRIDE) -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(jq -nc '{Name:"Films\u202etxt"}')" "$T1"
  ck P33.nul "NUL -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(jq -nc '{Name:"a\u0000b"}')" "$T1"
  ck P33.lf "saut de ligne -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(jq -nc '{Name:"a\nb"}')" "$T1"
  ck P33.ls "U+2028 -> 400 invalid-name" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(jq -nc '{Name:"a\u2028b"}')" "$T1"
  # acceptés (données, jamais interprétées) : Emby doit les stocker et les renvoyer à l'identique, sans erreur 500 ni plantage
  for name in '../x' 'a/b' 'a\b' '<b>x</b> & "q"' "l'apostrophe" 'Films 🎬'; do
    st=$(api POST "$UP/Playlists" "$(body_name "$name")" "$T1")
    if [[ $st == 200 ]]; then
      id=$(created_id); [[ -n $id ]] && track_created "$id"
      n=$(jq -r '.name' "$RESP")
      ck "P33.ok[$name]" "nom accepté et renvoyé à l'identique par le plugin" "$(cat "$RESP")" test "$n" = "$name"
      api GET "/Users/$U1/Items/$id" "" "$T1" >/dev/null
      ck "P33.emby[$name]" "Emby relit le même nom (aucune troncature/assainissement inattendu)" "$(jq -c '{Name:.Name}' "$RESP")" test "$(jq -r '.Name' "$RESP")" = "$name"
    else
      rec "P33.ok[$name]" KO "HTTP $st inattendu pour un nom accepté par le contrat (observation F2 : noter le comportement d'Emby)" "$(cat "$RESP")"
    fi
  done
  ck P33.noerror "aucune entrée Error journalisée par ces créations" "null" test "$(journal Error | jq 'length')" = 0
  purge_created   # quota (M1) : ne pas épuiser les 10 playlists de test_u1 avant P34
}


p34() {
  echo "== P34 (QUALIF, audit M1) — quota de 10 playlists possédées : 409 limit-reached, ordre 403 -> 400 -> quota -> name-exists, rien de supprimé"
  local id2 tok2 i st n before
  read -r id2 tok2 < <(create_and_login_test_account "SPIKE-P-quota")
  set_sharing "$id2" true; wait_sharing "$id2" true 10 || die "AllowSharingPersonalItems non posé sur le compte du quota"
  # 9 playlists possédées CRÉÉES NATIVEMENT (hors page) : le quota compte toutes les playlists possédées (S11 ligne 7)
  for ((i=1; i<=9; i++)); do
    st=$(api POST "/Playlists?Name=$(qs "SPIKE-quota-$i")&MediaType=Video&Ids=$ITEM1&UserId=$id2" "" "$tok2")
    [[ $st == 200 ]] && track_created "$(jq -r '.Id' "$RESP")"
  done
  ck P34.nine "9 possédées (natives) : la 10e créée depuis la page -> 200" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "SPIKE-quota-page")" "$tok2"
  n=$(created_id); [[ -n $n ]] && track_created "$n"
  api GET "$UP/Playlists" "" "$tok2" >/dev/null; before=$(jq 'length' "$RESP")
  ck P34.ten "10 possédées : la suivante -> 409 limit-reached" "null" st_err 409 limit-reached POST "$UP/Playlists" "$(body_name "SPIKE-quota-11")" "$tok2"
  ck P34.before_unique "quota vérifié AVANT l'unicité : nom déjà possédé à 10 -> 409 limit-reached (et non name-exists)" "null" st_err 409 limit-reached POST "$UP/Playlists" "$(body_name "SPIKE-quota-page")" "$tok2"
  ck P34.after_400 "400 invalid-name AVANT le quota (nom vide à 10)" "null" st_err 400 invalid-name POST "$UP/Playlists" "$(body_name "")" "$tok2"
  ck P34.after_403 "403 sharing-disabled AVANT le quota (compte sans permission)" "null" st_err 403 sharing-disabled POST "$UP/Playlists" "$(body_name "X")" "$TNP"
  api GET "$UP/Playlists" "" "$tok2" >/dev/null
  ck P34.intact "rien créé ni supprimé : toujours $before playlists" "$(jq 'length' "$RESP")" test "$(jq 'length' "$RESP")" = "$before"
  ck P34.per_owner "le quota est PAR compte : test_u1 (moins de 10 possédées) crée encore" "null" st_ok 200 POST "$UP/Playlists" "$(body_name "SPIKE-quota-u1-$$")" "$T1"
  n=$(created_id); [[ -n $n ]] && track_created "$n"
  ck P34.nonumber "le message d'erreur du serveur ne contient pas la valeur du quota" "null" test "$(jq -r '.error // ""' "$RESP")" != "10"
}


p_busy() {
  echo "== P-busy — 409 busy : SKIP (aucun moyen déterministe de forcer la contention du verrou par REST)"
  skip P-busy "couvert par UserPlaylistServiceSpecTests.Locked_ByAnotherThread_ReturnsBusy_WithinFiveSecondBudget (unitaire)"
}

ALL=(P1 P2 P3 P4 P6 P7 P8 P9 P10 P11 P12 P13 P14 P15 P16 P17 P18 P19 P20 P21 P22 P23 P24 P25 P26 P27 P28 P29 P30 P31 P32 P33 P34 P-busy)
if [[ ${#WANT[@]} -eq 0 ]]; then WANT=("${ALL[@]}"); fi
for s in "${WANT[@]}"; do
  fn=$(tr 'A-Z-' 'a-z_' <<<"$s")
  declare -F "$fn" >/dev/null || die "scénario inconnu : $s"
  "$fn"
done

echo "== Comptes protégés"
# cleanup_test_accounts AVANT compare_protected (qa-20260928-160840.md §4.4) : le compte éphémère
# SPIKE-P-noperm-* (créé pour P2/P3) doit avoir disparu avant la comparaison, sinon compare_protected voit un
# utilisateur en plus par rapport au snapshot de 00-setup-users.sh (idempotent : on_exit le rappelle sans effet).
cleanup_test_accounts
compare_protected "fin des scénarios" "${TEST_USERS[@]}" && rec PROTECTED OK "admin, cyril, user2 inchangés" || rec PROTECTED KO "comptes protégés modifiés" "null"

write_out false; DONE=1
echo
echo "== Bilan (détail : $OUT_FILE)"
jq -r '.summary|"  OK=\(.ok) KO=\(.ko) SKIP=\(.skip)"' "$OUT_FILE"
[[ $FAILS == 0 ]] || { echo "ECHEC : $FAILS assertion(s) KO" >&2; exit 1; }
echo "Toutes les assertions exécutées sont OK."
