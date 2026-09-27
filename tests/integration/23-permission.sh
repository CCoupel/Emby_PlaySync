#!/usr/bin/env bash
# 23-permission.sh — scénarios d'intégration I36-I41 de la v0.4.0 (issue #26) sur emby2
# (QUALIF UNIQUEMENT). À exécuter par qa après déploiement du plugin ; jamais depuis un poste sans avoir
# vérifié la cible.
#
# Pose automatiquement `Policy.AllowSharingPersonalItems=true` pour tous les utilisateurs : à la passe de
# réconciliation (démarrage + toutes les 5 min, ou déclenchée à la main ici via run_pass) pour les comptes
# existants, et IMMÉDIATEMENT à la création d'un compte (`IUserManager.UserCreated`), sans attendre la passe
# suivante. Interrupteur `AutoEnableSharing` (config plugin, défaut true) : à faux, aucune écriture, aucune
# entrée de journal, même pour un compte nouvellement créé. Ne modifie JAMAIS que ce seul champ de la Policy.
# Comportement ASSUMÉ (D-e, docs/chronogrammes.md) : le plugin ne mémorise aucun décochage manuel — un
# administrateur qui décoche la permission d'un compte la voit RÉACTIVÉE à la passe suivante ; testé ici comme
# un SUCCÈS attendu (I40), pas une anomalie. Seul l'interrupteur global protège, et il ne révoque jamais un
# accès déjà accordé (I41).
#
# Comptes protégés (admin/cyril/user2) : JAMAIS touchés par ce script (guard_target/compare_protected, comme le
# reste de la suite) — un élargissement de leurs droits réels par LE PLUGIN LUI-MÊME au premier démarrage de
# v0.4.0 est une conséquence assumée de D8, mais ce script ne le provoque ni ne le vérifie lui-même : il
# n'utilise que test_u1/u2/u3 (comptes de test partagés, remis à false en tête de script) et des comptes
# test_* créés et supprimés par LUI-MÊME (create_test_account/cleanup_test_accounts) pour I37/I39.
#
# Prérequis : tests/integration/00-setup-users.sh exécuté (test_u1/u2/u3).
# Usage : tests/integration/23-permission.sh [I36 I40 …]   (sans liste : tous les scénarios, DANS L'ORDRE :
#   I38/I40/I41 dépendent de l'état posé par I36, comme I27/I28 dans 22-avancement.sh)
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/int-lib.sh"

WANT=("$@")
OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/permission-$TS.json"
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
  if [[ -n ${ORIG_CFG:-} ]]; then plugin_cfg_set "$ORIG_CFG" || true; fi   # restaure AutoEnableSharing (et tout le reste) tel que trouvé
  if [[ $DONE == 0 && -s $RES ]]; then write_out true; echo "  (trap) preuves partielles : $OUT_FILE" >&2; fi
  rm -rf "$SCRATCH"; exit $rc
}
trap on_exit EXIT

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV"
[[ -f $USERS_ENV && -f $SNAPSHOT ]] || die "lancer tests/integration/00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)")
T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)")
T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")

st=$(api GET "$DIAG/State"); [[ $st == 200 ]] || die "Diagnostics/State -> HTTP $st : plugin v0.4.0 non déployé ou EnableDiagnostics=false"
ORIG_CFG=$(plugin_cfg)
echo "  [OK] config plugin lue (restaurée en fin de run, y compris AutoEnableSharing)"

# Ordre déterministe : interrupteur éteint PENDANT la mise en place (aucune passe planifiée en tâche de fond ne
# doit modifier test_u1/u2/u3 avant que je le décide moi-même), puis remise à false explicite des 3 comptes de
# test (baseline connue, comme la remise à zéro du pool de médias dans les 3 autres scripts, #47).
set_auto_sharing false
set_sharing "$U1" false; set_sharing "$U2" false; set_sharing "$U3" false
wait_sharing "$U1" false 5 || true; wait_sharing "$U2" false 5 || true; wait_sharing "$U3" false 5 || true
echo "  [OK] test_u1/u2/u3 remis à AllowSharingPersonalItems=false ; interrupteur éteint le temps de la mise en place"
set_auto_sharing true

# ---------------------------------------------------------------- scénarios
i36() {
  echo "== I36 — démarrage/passe : test_u1/u2/u3 (à false) reçoivent la permission à la passe de réconciliation"
  jclear
  run_pass || die "passe de réconciliation non terminée en 60 s"
  wait_sharing "$U1" true 10 || true; wait_sharing "$U2" true 10 || true; wait_sharing "$U3" true 10 || true
  ck I36.u1 "u1 reçoit AllowSharingPersonalItems=true" "null" test "$(sharing_of "$U1")" = true
  ck I36.u2 "u2 reçoit AllowSharingPersonalItems=true" "null" test "$(sharing_of "$U2")" = true
  ck I36.u3 "u3 reçoit AllowSharingPersonalItems=true" "null" test "$(sharing_of "$U3")" = true
  local j n1 n2 n3 pass
  j=$(journal "PermissionPosed,PermissionPass")
  n1=$(jcount_user "$j" PermissionPosed "$U1"); n2=$(jcount_user "$j" PermissionPosed "$U2"); n3=$(jcount_user "$j" PermissionPosed "$U3")
  ck I36.journal1 "PermissionPosed journalisé pour u1" "$j" test "$n1" -ge 1
  ck I36.journal2 "PermissionPosed journalisé pour u2" "$j" test "$n2" -ge 1
  ck I36.journal3 "PermissionPosed journalisé pour u3" "$j" test "$n3" -ge 1
  pass=$(jq -r '[.[]|select(.kind=="PermissionPass")]|last.detail' <<<"$j")
  ck I36.summary "PermissionPass résumé présent (users/enabled/alreadyEnabled/durationMs)" "{\"detail\":\"$pass\"}" \
    bash -c '[[ $0 == users=*enabled=*alreadyEnabled=*durationMs=* ]]' "$pass"
}

i37() {
  echo "== I37 — UserCreated : nouveau compte de test reçoit la permission IMMÉDIATEMENT, sans attendre la passe"
  jclear
  local id
  id=$(create_test_account "SPIKE-I37")
  wait_sharing "$id" true 5 || true   # doit être quasi instantané (événement, pas la tâche planifiée de 5 min)
  ck I37.immediate "permission posée SANS avoir déclenché de passe (run_pass jamais appelé ici)" "null" test "$(sharing_of "$id")" = true
  local j n
  j=$(journal PermissionPosed); n=$(jcount_user "$j" PermissionPosed "$id")
  ck I37.journal "PermissionPosed journalisé pour ce nouveau compte" "$j" test "$n" -ge 1
  api DELETE "/Users/$id" >/dev/null || true   # nettoyé ICI (comme test_u4/I20/I36 v0.3.0) : "fin des scénarios" ne doit voir que test_u1/u2/u3
}

i38() {
  echo "== I38 — compte déjà actif : aucune écriture (Policy strictement inchangée, aucun nouveau PermissionPosed)"
  ck I38.setup "précondition : u1 déjà à true (I36)" "null" test "$(sharing_of "$U1")" = true
  local before after j n
  before=$(policy_of "$U1")
  jclear
  run_pass || die "passe de réconciliation non terminée en 60 s"
  after=$(policy_of "$U1")
  ck I38.unchanged "Policy de u1 strictement inchangée (avant/après la passe)" "{\"before\":$before,\"after\":$after}" test "$before" = "$after"
  j=$(journal PermissionPosed); n=$(jcount_user "$j" PermissionPosed "$U1")
  ck I38.noposed "aucune entrée PermissionPosed pour u1 (déjà actif : compté alreadyEnabled, jamais reposé)" "$j" test "$n" = 0
}

i39() {
  echo "== I39 — AutoEnableSharing=false : aucune écriture, y compris pour un nouveau compte créé pendant ce temps"
  set_auto_sharing false
  jclear
  local id
  id=$(create_test_account "SPIKE-I39")
  nap 3   # négatif (rien ne doit se produire) : fenêtre fixe volontaire, pas d'attente active
  ck I39.noimmediate "le nouveau compte reste à false (UserCreated n'a rien posé, interrupteur éteint)" "null" test "$(sharing_of "$id")" = false
  run_pass || die "passe de réconciliation non terminée en 60 s"
  ck I39.nopass "toujours à false après une passe complète (interrupteur toujours éteint)" "null" test "$(sharing_of "$id")" = false
  local j
  j=$(journal "PermissionPosed,PermissionPass")
  ck I39.nojournal "aucune entrée PermissionPosed/PermissionPass (rien n'est même tenté)" "$j" test "$(jq 'length' <<<"$j")" = 0
  set_auto_sharing true   # remis avant les scénarios suivants
  api DELETE "/Users/$id" >/dev/null || true   # nettoyé ICI : "fin des scénarios" ne doit voir que test_u1/u2/u3
}

i40() {
  echo "== I40 — décochage manuel (admin) puis passe suivante : permission RÉACTIVÉE (D-e, succès attendu, pas une anomalie)"
  ck I40.setup "précondition : u2 déjà à true (I36)" "null" test "$(sharing_of "$U2")" = true
  set_sharing "$U2" false
  wait_sharing "$U2" false 5 || true
  ck I40.unchecked "u2 repassé à false (action admin simulée, POST /Users/{id}/Policy)" "null" test "$(sharing_of "$U2")" = false
  jclear
  run_pass || die "passe de réconciliation non terminée en 60 s"
  wait_sharing "$U2" true 10 || true
  ck I40.reactivated "u2 REPASSE à true à la passe suivante (D-e vérifié explicitement comme un succès)" "null" test "$(sharing_of "$U2")" = true
  local j n
  j=$(journal PermissionPosed); n=$(jcount_user "$j" PermissionPosed "$U2")
  ck I40.journal "PermissionPosed journalisé pour u2 (nouvelle pose après le décochage)" "$j" test "$n" -ge 1
}

i41() {
  echo "== I41 — désactiver l'interrupteur NE révoque PAS un accès déjà accordé"
  ck I41.setup "précondition : u3 déjà à true (I36)" "null" test "$(sharing_of "$U3")" = true
  set_auto_sharing false
  jclear
  run_pass || die "passe de réconciliation non terminée en 60 s"
  ck I41.untouched "u3 reste à true (l'interrupteur éteint n'écrit rien, ne révoque rien)" "null" test "$(sharing_of "$U3")" = true
  local j
  j=$(journal "PermissionPosed,PermissionPass")
  ck I41.nojournal "aucune entrée PermissionPosed/PermissionPass pendant que l'interrupteur est éteint" "$j" test "$(jq 'length' <<<"$j")" = 0
  set_auto_sharing true
}

ALL=(I36 I37 I38 I39 I40 I41)
if [[ ${#WANT[@]} -eq 0 ]]; then WANT=("${ALL[@]}"); fi
for s in "${WANT[@]}"; do
  fn=$(tr 'A-Z' 'a-z' <<<"$s")
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
