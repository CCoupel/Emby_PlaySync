#!/usr/bin/env bash
# 20-etiquettes-retrait.sh — scénarios d'intégration I1–I17 de la v0.2.0 (issues #12 #18 #19 #22 #32 #50 #53) sur emby2
# (QUALIF UNIQUEMENT). À exécuter par qa après déploiement du plugin ; jamais depuis un poste sans avoir vérifié la cible.
#
# Étiquettes : `remove-si-lu` (retrait du média à la transition non lu -> lu), `propager-lu` (propagation du flag lu) et,
# depuis v1.2.0, `propager-avancement` (position, hors périmètre de ce script). NON l'emporte ; le plugin ne supprime
# jamais d'étiquette ; famille absente => NON reposée (première détection immédiate, sinon après GracePasses passes
# consécutives, jamais sur ItemUpdated d'une playlist déjà vue) ; message d'aide seulement si la description est vide.
# v1.2.0 (D21, R4a subordonnée, tableau A) : le retrait exige `remove-si-lu=OUI` ET `propager-lu=OUI` (chacune seule) sur une
# playlist PARTAGÉE, sur transition non lu -> lu ; `remove-si-lu=OUI` SEUL ne retire plus rien (I2.solo) — les scénarios
# historiques activent donc les DEUX familles (helper enable_removal / owner_edit OUI_RM+OUI_PR), et le lu est propagé aux
# autres comptes dans ce cas (I3.others, I11.flags mis à jour).
#
# Prérequis : tests/integration/00-setup-users.sh exécuté (test_u1 propriétaire, test_u2 Write, test_u3 Read) ; >= 6 médias.
# Usage : tests/integration/20-etiquettes-retrait.sh [--restart] [I1 I8 …]
#   --restart : exécute aussi I10 (redémarre deployment/emby2 via kubectl ; KUBECONFIG=private/kubeconfig.yml)
#   sans liste : tous les scénarios (I10 seulement avec --restart). Sortie : tableau + JSON (SPIKE_OUT).
# Statuts : OK | KO | SKIP (précondition ou observation impossible, motif indiqué). Code de sortie 1 si un KO.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
source "$(dirname "${BASH_SOURCE[0]}")/int-lib.sh"

DO_RESTART=0; WANT=()
for a in "$@"; do
  case $a in
    --restart) DO_RESTART=1 ;;
    I[0-9]*) WANT+=("$a") ;;
    *) die "usage : $0 [--restart] [I1 I2 …]" ;;
  esac
done
KUBE_DEPLOY="emby2"; KUBE_NS="media"; KUBECONFIG_FILE="$PRIVATE/kubeconfig.yml"   # QUALIF en dur
OUT_DIR=${SPIKE_OUT:-$ROOT/_work/spike-out}; mkdir -p "$OUT_DIR"
TS=$(date +%Y%m%d-%H%M%S); OUT_FILE="$OUT_DIR/integration-$TS.json"
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
skip() { rec "$1" SKIP "$2" "null"; }

write_out() { # PARTIAL
  jq -s --arg ts "$TS" --argjson p "$1" '
    {run:$ts, partial:$p, summary:{ok:(map(select(.status=="OK"))|length), ko:(map(select(.status=="KO"))|length), skip:(map(select(.status=="SKIP"))|length)}, results:.}' "$RES" > "$OUT_FILE" 2>/dev/null || true
}
on_exit() {
  local rc=$?; set +e
  # #60 : SPIKE-I17 (OUI/OUI, dernier scénario) ne doit pas survivre au script. drop_playlists = playlists DU RUN seulement
  # ($SCRATCH/pls.run) ; pas de cleanup_registered_playlists ici : imbriqué (I26 de 21) il supprimerait celles de 21.
  drop_playlists
  end_test_sessions   # #61 : Stopped sur les sessions ouvertes puis Logout des jetons de test
  if [[ $DONE == 0 && -s $RES ]]; then write_out true; echo "  (trap) preuves partielles : $OUT_FILE" >&2; fi
  rm -rf "$SCRATCH"; exit $rc
}
trap on_exit EXIT

# ---------------------------------------------------------------- préconditions
echo "== Préconditions"
guard_target
check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV" "$KUBECONFIG_FILE"
[[ -f $USERS_ENV && -f $SNAPSHOT ]] || die "lancer tests/integration/00-setup-users.sh d'abord"
U1=$(envget "$USERS_ENV" TEST_U1_ID); U2=$(envget "$USERS_ENV" TEST_U2_ID); U3=$(envget "$USERS_ENV" TEST_U3_ID)
relogin() {
  T1=$(login test_u1 "$(envget "$USERS_ENV" TEST_U1_PW)"); T2=$(login test_u2 "$(envget "$USERS_ENV" TEST_U2_PW)"); T3=$(login test_u3 "$(envget "$USERS_ENV" TEST_U3_PW)")
}
relogin
purge_stale_test_playlists   # #60 : aucune playlist SPIKE d'un run précédent (sauf si imbriqué : INT_NESTED=1)
select_test_media 10   # (I14c parallèle : jusqu'à 10 médias par playlist ; les autres scénarios n'en utilisent que 6)  # #61 : bibliothèque locale PlaySync-Tests uniquement ; refus de tout média /config/virtual/
[[ ${#M[@]} -ge 6 ]] || die "moins de 6 médias (>= 10 min) : I14/I15 exigent 6 médias (demander à l'utilisateur)"
reset_pool_full "${M[@]}"   # remise à zéro complète (lu+position) : un run précédent (même script, même invocation séparée) ne doit rien laisser
echo "  [OK] bassin de ${#M[@]} médias remis à zéro (lu=false, position=0)"
st=$(api GET "$DIAG/State"); [[ $st == 200 ]] || die "Diagnostics/State -> HTTP $st : plugin v0.2.0 non déployé ou EnableDiagnostics=false"
GRACE=$(jq -r '.gracePasses // 2' "$RESP")
task_id >/dev/null
LOGS_OK=0; T_START=$(date -u +%Y-%m-%dT%H:%M:%SZ)
if command -v kubectl >/dev/null && [[ -f $KUBECONFIG_FILE ]]; then
  KUBECONFIG="$KUBECONFIG_FILE" kubectl logs "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" --tail=1 >/dev/null 2>&1 && LOGS_OK=1
fi
echo "  GracePasses=$GRACE ; logs kubectl : $([[ $LOGS_OK == 1 ]] && echo lisibles || echo indisponibles)"
REMOVAL_MS="$SCRATCH/removal.ms"; REMOVAL_BURST="$SCRATCH/removal.burst"; : > "$REMOVAL_MS"; : > "$REMOVAL_BURST"; : > "$SCRATCH/removal.acc"
CUR_SCEN=""
# harvest_removals : durées des Removal du journal, dédoublonnées par ts|playlist|item, classées par scénario (#59/F3) :
#  - « burst » = retraits des scénarios de concurrence (I14, I14c, I14p, I15) : la sérialisation par playlist s'ajoute à la durée ;
#  - « iso »   = tous les autres scénarios ; seul le PREMIER Removal de chaque playlist compte (retrait isolé, cas courant).
harvest_removals() {
  local cls=iso
  case "$CUR_SCEN" in I14|I14C|I14P|I15) cls=burst ;; esac
  journal Removal | jq -r '.[]|"\(.ts)|\(.playlistId)|\(.itemId)|\(.detail//"")"' | while IFS='|' read -r t p i d; do
    echo "$t|$p|$i|$(jkv "$d" durationMs)|$cls"
  done >> "$SCRATCH/removal.acc"
  sort "$SCRATCH/removal.acc" | awk -F'|' '!s[$1"|"$2"|"$3]++' > "$SCRATCH/removal.acc.tmp"; mv "$SCRATCH/removal.acc.tmp" "$SCRATCH/removal.acc"
  awk -F'|' '$5=="iso" && !f[$2]++ {print $4}' "$SCRATCH/removal.acc" | grep -E '^[0-9]+$' > "$REMOVAL_MS" || true
  awk -F'|' '$5=="burst" {print $4}' "$SCRATCH/removal.acc" | grep -E '^[0-9]+$' > "$REMOVAL_BURST" || true
}
assert_baseline() { # un scénario ne doit pas dépendre du précédent : journal vidé, état « lu » des médias de test remis à zéro
  local m
  drop_playlists
  jclear
  for m in "${M[@]}"; do
    unmark "$U1" "$T1" "$m"; unmark "$U2" "$T2" "$m"; unmark "$U3" "$T3" "$m"
  done
  # attente active (#47, réserve qa sur I3.others : un effet différé d'un sous-scénario immédiatement précédent) :
  # confirme que le retour à « non lu » est bien retombé côté serveur AVANT de rendre la main au scénario suivant,
  # au lieu de faire confiance à la réponse HTTP synchrone des unmark seule.
  for m in "${M[@]}"; do
    wait_played "$U1" "$T1" "$m" false 5 || true
    wait_played "$U2" "$T2" "$m" false 5 || true
    wait_played "$U3" "$T3" "$m" false 5 || true
  done
}

# ---------------------------------------------------------------- scénarios
i0() {
  echo "== I0 — smoke : plugin chargé, Diagnostics/State, tâche planifiée"
  ck I0.state "Diagnostics/State répond (200)" "null" test "$(api GET "$DIAG/State")" = 200
  ck I0.task "tâche planifiée « réconciliation » listée" "{\"id\":\"$(task_id)\"}" test -n "$(task_id)"
  ck I0.grace "GracePasses >= 1" "{\"gracePasses\":$GRACE}" test "$GRACE" -ge 1
}

i1() {
  echo "== I1 — playlist partagée sans étiquette : passe => trois NON + message ; F1 finie par u2 => reste"
  assert_baseline
  local pl t o e; pl=$(shared_pl "SPIKE-I1" "${M[0]},${M[1]}")
  prime "$pl" || true
  t=$(tags_of "$pl"); o=$(overview_of "$pl")
  ck I1.tags "les TROIS étiquettes NON sont posées (casse exacte, v1.2.0 : + propager-avancement=NON, sans héritage)" "$t" bash -c 'jq -e --arg a "$1" --arg b "$2" --arg c "$3" "index(\$a)!=null and index(\$b)!=null and index(\$c)!=null" <<<"$0" >/dev/null' "$t" "$NON_RM" "$NON_PR" "$NON_AV"
  ck I1.help "message d'aide écrit (description vide au départ)" "{\"len\":${#o}}" bash -c '[[ $0 == *remove-si-lu=OUI* ]]' "$o"
  e=$(journal MarkerPosed)
  ck I1.journal "MarkerPosed x3 (une par famille) pour cette playlist" "$e" test "$(jcount "$e" "$pl" MarkerPosed 'first-detection')" = 3
  finish "$U2" "$T2" "${M[0]}"
  ck I1.f1 "F1 finie par u2 (sans remove-si-lu=OUI) : reste dans la playlist" "null" stays "$pl" "${M[0]}" 1 5
}

i2_case() { # ID DESC AJOUTS RETRAITS ATTENDU(stays|removed) [ajouts multi-lignes]
  local id=$1 desc=$2 add=$3 del=$4 want=$5 pl
  pl=$(shared_pl "SPIKE-$id" "${M[0]},${M[1]}"); LAST_PL=$pl
  prime "$pl" || true
  owner_edit "$pl" "$add" "$del"
  nap 1
  finish "$U2" "$T2" "${M[0]}"
  if [[ $want == removed ]]; then
    ck "$id" "$desc => F1 retirée" "$(tags_of "$pl")" wait_count "$pl" "${M[0]}" 0 10
  else
    ck "$id" "$desc => F1 reste" "$(tags_of "$pl")" stays "$pl" "${M[0]}" 1 5
  fi
}
i2() {
  # Règle décidée par l'utilisateur : casse ignorée, espaces tolérés autour de « = » => « Remove-Si-Lu = oui » est ACTIF.
  # (La mention « casse oui : F1 reste » du plan est une erreur du plan ; REMOVE-SI-LU=non, elle, reste inactive.)
  echo "== I2 — états de remove-si-lu (et dépendance à propager-lu, v1.2.0)"
  assert_baseline
  i2_case I2.non   "remove-si-lu=NON (défaut)" '[]' '[]' stays
  i2_case I2.both  "remove-si-lu=OUI + =NON ensemble (NON l'emporte)" "[\"$OUI_RM\"]" '[]' stays
  i2_case I2.case  "REMOVE-SI-LU=non (casse)" '["REMOVE-SI-LU=non"]' "[\"$NON_RM\"]" stays
  i2_case I2.prop  "propager-lu=OUI seul (remove-si-lu=NON)" "[\"$OUI_PR\"]" "[\"$NON_PR\"]" stays
  # v1.2.0 (D21, S3b) : le retrait exige AUSSI propager-lu=OUI ; remove-si-lu=OUI SEUL (propager-lu=NON) => RIEN.
  i2_case I2.solo  "remove-si-lu=OUI SEUL (propager-lu=NON) : plus aucun retrait (S3b, v1.2.0)" "[\"$OUI_RM\"]" "[\"$NON_RM\"]" stays
  i2_case I2.prboth "remove-si-lu=OUI + propager-lu=OUI+NON ensemble (propager-lu inactive, NON l'emporte)" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\"]" stays
  i2_case I2.space "« Remove-Si-Lu = oui » + « PROPAGER-LU = Oui » (casse et espaces tolérés)" '["Remove-Si-Lu = oui","PROPAGER-LU = Oui"]' "[\"$NON_RM\",\"$NON_PR\"]" removed
  local pl x
  i2_case I2.oui "remove-si-lu=OUI ET propager-lu=OUI (chacune seule)" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]" removed; pl=$LAST_PL
  x=$(for t in "$T1:$U1" "$T2:$U2" "$T3:$U3"; do api GET "/Playlists/$pl/Items?UserId=${t##*:}" "" "${t%%:*}" >/dev/null; jq -c --arg i "${M[0]}" '[.Items[]|select(.Id==$i)]|length' "$RESP"; done | jq -sc .)
  ck I2.oui.views "F1 absente pour u1, u2 et u3" "$x" test "$x" = "[0,0,0]"
}

i3() {
  echo "== I3 — TogglePlayed par le propriétaire"
  assert_baseline
  local pl p2 p3; pl=$(shared_pl "SPIKE-I3" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  finish "$U1" "$T1" "${M[0]}"
  ck I3.removed "u1 (propriétaire) passe F1 à lu : retirée" "null" wait_count "$pl" "${M[0]}" 0 10
  # évidence enrichie (KO isolé, non reproduit le 2026-09-27, cf. rapport qa) : valeurs réelles + ids pour diagnostic
  p2=$(played_of "$U2" "$T2" "${M[0]}"); p3=$(played_of "$U3" "$T3" "${M[0]}")
  ck I3.others "v1.2.0 : le retrait exige propager-lu=OUI, donc le lu EST propagé aux autres comptes (R4b, tableau A) — avant : inchangé" \
    "{\"u2\":\"$p2\",\"u3\":\"$p3\",\"media\":\"${M[0]}\",\"playlist\":\"$pl\"}" \
    test "$p2/$p3" = "true/true"
}

i4() {
  echo "== I4 — played=true sur PlaybackProgress"
  assert_baseline
  local pl; pl=$(shared_pl "SPIKE-I4" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  play_to "$U2" "$T2" "${M[0]}" 99 progress-only
  nap 3
  local pl2; pl2=$(played_of "$U2" "$T2" "${M[0]}")
  if [[ $pl2 != true ]]; then
    skip I4 "Emby n'a pas marqué lu sur Playing/Progress à 99 % (played=$pl2) : cas non observable en simulation, à voir en lecture réelle"
  else
    ck I4 "Progress marquant lu (played=true) => F1 retirée" "{\"played\":\"$pl2\"}" wait_count "$pl" "${M[0]}" 0 10
  fi
  api POST /Sessions/Playing/Stopped "$(jq -nc --arg i "${M[0]}" '{ItemId:$i,MediaSourceId:$i,PositionTicks:0}')" "$T2" >/dev/null
}

i5() {
  echo "== I5 — doublons : toutes les entrées du média sont retirées (une à la fois, relecture entre chaque)"
  assert_baseline
  local pl j; pl=$(shared_pl "SPIKE-I5" "${M[0]},${M[1]}"); prime "$pl" || true
  add_item "$pl" "${M[0]}"
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  ck I5.dup "M0 présent 2 fois au départ" "null" test "$(count_item "$pl" "${M[0]}")" = 2
  jclear
  finish "$U2" "$T2" "${M[0]}"
  ck I5.all "une seule transition : les DEUX entrées de M0 sont retirées" "null" wait_count "$pl" "${M[0]}" 0 10
  j=$(journal Removal)
  ck I5.journal "un seul Removal pour cette playlist, entries=2" "$j" test "$(jcount "$j" "$pl" Removal 'entries=2')" = 1
  ck I5.other "l'autre média (M1) est intact" "null" test "$(count_item "$pl" "${M[1]}")" = 1
  add_item "$pl" "${M[0]}"; unmark "$U2" "$T2" "${M[0]}"; nap 1; finish "$U2" "$T2" "${M[0]}"
  ck I5.again "une nouvelle transition retire de nouveau l'entrée ré-ajoutée" "null" wait_count "$pl" "${M[0]}" 0 10
}

i6() {
  echo "== I6 — playlist privée / publique sans partage : rien"
  assert_baseline
  local pl st tg
  pl=$(new_pl "SPIKE-I6-privee" "${M[0]},${M[1]}")
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" '[]'
  run_pass || true
  finish "$U1" "$T1" "${M[0]}"
  ck I6.private "playlist privée avec remove-si-lu=OUI + propager-lu=OUI : F1 reste" "null" stays "$pl" "${M[0]}" 1 5
  tg=$(tags_of "$pl")
  ck I6.notag "aucune étiquette par défaut n'est posée sur une playlist non partagée (ni propager-avancement)" "$tg" test "$(tag_count "$tg" remove-si-lu)/$(tag_count "$tg" propager-lu)/$(tag_count "$tg" propager-avancement)" = "1/1/0"
  pl=$(new_pl "SPIKE-I6-publique" "${M[2]},${M[3]}")
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" '[]'
  st=$(api POST "/Items/$pl/MakePublic" "" "$T1")
  if [[ $st != 2* ]]; then skip I6.public "MakePublic refusé (HTTP $st) : permission de partage absente"; return; fi
  run_pass || true
  finish "$U1" "$T1" "${M[2]}"
  ck I6.public "playlist publique sans partage explicite : F1 reste" "null" stays "$pl" "${M[2]}" 1 5
}

i7() {
  echo "== I7 — arrêt à 50 % : aucune transition"
  assert_baseline
  local pl j; pl=$(shared_pl "SPIKE-I7" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  jclear
  play_to "$U2" "$T2" "${M[0]}" 50
  ck I7.stays "arrêt à 50 % avec retrait effectif : F1 reste" "null" stays "$pl" "${M[0]}" 1 5
  j=$(journal Removal)
  ck I7.nojournal "aucune entrée Removal" "$j" test "$(jcount "$j" "$pl" Removal)" = 0
}

i8() {
  echo "== I8 — remplacement de NON par OUI (deux ordres) et grâce par famille (GracePasses=$GRACE)"
  assert_baseline
  local pl t gs i
  # (a) retirer NON puis ajouter OUI
  pl=$(shared_pl "SPIKE-I8a" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" '[]' "[\"$NON_RM\"]"
  nap 4
  t=$(tags_of "$pl")
  ck I8a.noimmediate "après « retirer NON » seul : pas de repose immédiate (playlist déjà vue)" "$t" test "$(tag_count "$t" remove-si-lu)" = 0
  if [[ $GRACE -gt 1 ]]; then
    run_pass || true
    wait_grace_counter "$pl" remove-si-lu 1 10 || true   # attente active (#47) : la passe a démarré, pas garanti que CETTE playlist soit déjà traitée
    gs=$(state | jq -r --arg p "$pl" "$DEFS"'.graceCounters|to_entries|map(select((.key|n)==($p|n)))|.[0].value["remove-si-lu"] // 0')
    ck I8a.grace1 "1re passe sans étiquette : compteur de grâce = 1, pas de pose" "{\"counter\":$gs}" test "$gs" = 1
  fi
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_PR\"]"     # retrait EFFECTIF (v1.2.0) : les deux familles
  run_pass || true
  wait_grace_counter "$pl" remove-si-lu 0 10 || true
  t=$(tags_of "$pl"); gs=$(state | jq -r --arg p "$pl" "$DEFS"'.graceCounters|to_entries|map(select((.key|n)==($p|n)))|.[0].value["remove-si-lu"] // 0')
  ck I8a.final "OUI ajoutée avant la fin de la grâce : état {OUI}, aucun NON reposé, compteur remis à 0" "$t" \
    bash -c 'jq -e --arg a "$1" --arg b "$2" "index(\$a)!=null and index(\$b)==null" <<<"$0" >/dev/null && [[ $3 == 0 ]]' "$t" "$OUI_RM" "$NON_RM" "$gs"
  finish "$U2" "$T2" "${M[0]}"
  ck I8a.active "remove-si-lu=OUI actif : F1 retirée" "null" wait_count "$pl" "${M[0]}" 0 10
  # (b) ajouter OUI puis retirer NON
  pl=$(shared_pl "SPIKE-I8b" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_PR\"]"; nap 2      # propager-lu=OUI seule ; remove-si-lu=OUI + NON (conflit)
  finish "$U2" "$T2" "${M[1]}"
  ck I8b.both "OUI + NON ensemble : NON l'emporte, M1 reste" "null" stays "$pl" "${M[1]}" 1 4
  owner_edit "$pl" '[]' "[\"$NON_RM\"]"; run_pass || true
  t=$(tags_of "$pl")
  ck I8b.final "après retrait de NON : {OUI}" "$t" bash -c 'jq -e --arg a "$1" --arg b "$2" "index(\$a)!=null and index(\$b)==null" <<<"$0" >/dev/null' "$t" "$OUI_RM" "$NON_RM"
  unmark "$U2" "$T2" "${M[1]}"; nap 1; finish "$U2" "$T2" "${M[1]}"
  ck I8b.active "puis actif : M1 retiré" "null" wait_count "$pl" "${M[1]}" 0 10
  # (c) grâce PAR FAMILLE : on retire seulement propager-lu=NON
  pl=$(shared_pl "SPIKE-I8c" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" '[]' "[\"$NON_PR\"]"; jclear
  for ((i=1; i<=GRACE; i++)); do
    run_pass || true
    t=$(tags_of "$pl")
    gs=$(state | jq -c --arg p "$pl" "$DEFS"'.graceCounters|to_entries|map(select((.key|n)==($p|n)))|.[0].value // {}')
    if [[ $i -lt $GRACE ]]; then
      ck "I8c.pass$i" "passe $i/$GRACE : propager-lu absente => compteur $i, pas encore reposée ; remove-si-lu intacte" "$gs" \
        bash -c '[[ $(jq -r ".[\"propager-lu\"]//0" <<<"$0") == "$2" && $(jq -r ".[\"remove-si-lu\"]//0" <<<"$0") == 0 && $(jq "index(\"propager-lu=NON\")" <<<"$1") == null ]]' "$gs" "$t" "$i"
    else
      ck "I8c.pass$i" "passe $i/$GRACE : propager-lu=NON reposée (grâce écoulée), remove-si-lu=NON inchangée" "$t" \
        bash -c 'jq -e --arg a "$1" --arg b "$2" "index(\$a)!=null and index(\$b)!=null" <<<"$0" >/dev/null' "$t" "$NON_PR" "$NON_RM"
    fi
  done
  local j; j=$(journal MarkerPosed)
  ck I8c.journal "MarkerPosed family=propager-lu cause=grace-elapsed" "$j" test "$(jcount "$j" "$pl" MarkerPosed 'family=propager-lu cause=grace-elapsed')" = 1
}

i9() {
  echo "== I9 — message d'aide : jamais par-dessus un texte, réécrit après grâce si vidé"
  assert_baseline
  local pl o i j; pl=$(shared_pl "SPIKE-I9" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" '[]' '[]' '"Ma description personnelle"'
  for ((i=1; i<=GRACE+1; i++)); do run_pass || true; done
  o=$(overview_of "$pl")
  ck I9.kept "description non vide préservée après $((GRACE+1)) passes" "{\"len\":${#o}}" test "$o" = "Ma description personnelle"
  owner_edit "$pl" '[]' '[]' '""'
  jclear
  for ((i=1; i<=GRACE; i++)); do
    run_pass || true; o=$(overview_of "$pl")
    if [[ $i -lt $GRACE ]]; then ck "I9.pass$i" "passe $i/$GRACE : description vide, pas encore réécrite" "null" test -z "$o"; fi
  done
  ck I9.rewritten "après $GRACE passes : message d'aide réécrit" "{\"len\":${#o}}" bash -c '[[ $0 == *remove-si-lu=OUI* ]]' "$o"
  j=$(journal DescriptionWritten)
  ck I9.journal "DescriptionWritten cause=grace-elapsed" "$j" test "$(jcount "$j" "$pl" DescriptionWritten 'grace-elapsed')" = 1
}

i10() {
  echo "== I10 — redémarrage : première détection immédiate, pas de doublon d'étiquette"
  if [[ $DO_RESTART != 1 ]]; then skip I10 "option --restart absente"; return; fi
  if ! command -v kubectl >/dev/null || [[ ! -f $KUBECONFIG_FILE ]]; then skip I10 "kubectl ou private/kubeconfig.yml absent"; return; fi
  local a b ta tb i ok=0 j
  a=$(shared_pl "SPIKE-I10a" "${M[0]},${M[1]}"); prime "$a" || true
  b=$(shared_pl "SPIKE-I10b" "${M[0]},${M[1]}"); prime "$b" || true
  owner_edit "$b" '[]' "[\"$NON_PR\"]"; owner_edit "$a" "[\"mon-etiquette\"]" '[]'
  ta=$(tags_of "$a")
  KUBECONFIG="$KUBECONFIG_FILE" kubectl rollout restart "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" >/dev/null
  KUBECONFIG="$KUBECONFIG_FILE" kubectl rollout status "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" --timeout=300s >/dev/null || { rec I10 KO "redémarrage non terminé en 300 s"; return; }
  for ((i=0; i<90; i++)); do api GET /System/Info >/dev/null && [[ $(jq -r '.ServerName//""' "$RESP") == "$EXPECTED_SERVER_NAME" ]] && { ok=1; break; }; nap 2; done
  [[ $ok == 1 ]] || { rec I10 KO "serveur injoignable après redémarrage"; return; }
  relogin; TASK_ID=""
  for ((i=0; i<60; i++)); do [[ $(api GET "$DIAG/State") == 200 ]] && break; nap 2; done
  ck I10.memory "mémoire remise à zéro : SeenPlaylistIds ne contient pas encore les playlists" "null" \
    bash -c '! jq -e --arg a "$1" "(.seenPlaylistIds//[])|index(\$a)!=null" <<<"$0" >/dev/null' "$(state)" "$a"
  run_pass || true; nap 2
  tb=$(tags_of "$b"); j=$(journal MarkerPosed)
  ck I10.immediate "playlist B (propager-lu absente) : NON reposée IMMÉDIATEMENT à la 1re passe (première détection, pas de grâce)" "$tb" \
    bash -c 'jq -e --arg a "$1" "index(\$a)!=null" <<<"$0" >/dev/null' "$tb" "$NON_PR"
  ck I10.cause "MarkerPosed cause=first-detection pour B" "$j" test "$(jcount "$j" "$b" MarkerPosed 'family=propager-lu cause=first-detection')" = 1
  ck I10.nodup "playlist A (étiquettes complètes) : aucune étiquette ajoutée ni dupliquée" "$(tags_of "$a")" test "$(tags_of "$a")" = "$ta"
  ck I10.seen "les deux playlists sont maintenant « vues »" "null" bash -c 'jq -e --arg a "$1" --arg b "$2" "(.seenPlaylistIds//[]) as \$s | (\$s|index(\$a)!=null) and (\$s|index(\$b)!=null)" <<<"$0" >/dev/null' "$(state)" "$a" "$b"
}

i11() {
  echo "== I11 — membre Read finit F1 : retiré, lu propagé aux autres (v1.2.0 : retrait exige propager-lu)"
  assert_baseline
  local pl; pl=$(shared_pl "SPIKE-I11" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  finish "$U3" "$T3" "${M[0]}"
  ck I11.removed "u3 (Read) passe F1 à lu : le plugin la retire" "null" wait_count "$pl" "${M[0]}" 0 10
  ck I11.flags "lu propagé à u1 et u2 (true/true, R4b)" "null" test "$(played_of "$U1" "$T1" "${M[0]}")/$(played_of "$U2" "$T2" "${M[0]}")" = "true/true"
}

i12() {
  echo "== I12 — relecture d'un média déjà lu : rien ; décocher/recocher : retire"
  assert_baseline
  local pl; pl=$(shared_pl "SPIKE-I12" "${M[0]},${M[1]}"); prime "$pl" || true
  finish "$U2" "$T2" "${M[0]}"                      # déjà lu AVANT d'activer le retrait (NON : reste)
  ck I12.pre "lu sous remove-si-lu=NON : reste" "null" stays "$pl" "${M[0]}" 1 3
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  play_to "$U2" "$T2" "${M[0]}" 98
  ck I12.reread "relecture jusqu'au bout d'un média déjà lu (aucune transition) : reste" "{\"played\":\"$(played_of "$U2" "$T2" "${M[0]}")\"}" stays "$pl" "${M[0]}" 1 5
  unmark "$U2" "$T2" "${M[0]}"; nap 1; finish "$U2" "$T2" "${M[0]}"
  ck I12.toggle "décocher puis recocher « lu » : transition => retiré" "null" wait_count "$pl" "${M[0]}" 0 10
}

i13() {
  echo "== I13 — première détection à l'action ; déjà vue : aucune pose"
  assert_baseline
  local pl t before after i j e13a e13b
  before=$(state | jq -r '.lastPass.ts // ""')
  pl=$(shared_pl "SPIKE-I13" "${M[0]}")
  add_item "$pl" "${M[1]}"                       # PlaylistItemsAdded sur une playlist partagée non vue
  for ((i=0; i<10; i++)); do t=$(tags_of "$pl"); has_tag "$t" "$NON_RM" && break; nap 1; done
  after=$(state | jq -r '.lastPass.ts // ""')
  if [[ $before != "$after" ]]; then skip I13.action "une passe planifiée a eu lieu pendant le test : cause de pose ambiguë"; else
    ck I13.action "ajout d'une entrée à une playlist non vue : les trois NON sont posés SANS passe" "$t" bash -c 'jq -e --arg a "$1" --arg b "$2" --arg c "$3" "index(\$a)!=null and index(\$b)!=null and index(\$c)!=null" <<<"$0" >/dev/null' "$t" "$NON_RM" "$NON_PR" "$NON_AV"
  fi
  e13a=$(echo_sum)
  owner_edit "$pl" '[]' "[\"$NON_PR\"]"
  add_item "$pl" "${M[2]}"; nap 6
  e13b=$(echo_sum)
  t=$(tags_of "$pl")
  ck I13.seen "playlist déjà vue : un événement ne repose rien (propager-lu reste absente)" "$t" test "$(tag_count "$t" propager-lu)" = 0
  ck I13.echoes "les événements de la playlist déjà vue sont ignorés et comptés (skippedCounts already-seen/reentrant +$((e13b-e13a)))" "{\"before\":$e13a,\"after\":$e13b}" test "$((e13b-e13a))" -ge 1
}

# integrity_check JOURNAL PLAYLIST N : N Removal entries=1, 0 Skipped already-removed, 0 Error wrong-entry* ET tolérance bornée de stale-entry
# (F3-4) : au plus 1 Skipped stale-entry par retrait (= la borne des 2 tentatives du moteur : 1 NoEffect puis Removed) ; au-delà => KO.
integrity_check() {
  local ja=$1 pl=$2 n=$3 stale
  stale=$(jcount "$ja" "$pl" Skipped 'stale-entry')
  [[ "$(jcount "$ja" "$pl" Removal 'entries=1( |$)')/$(jcount "$ja" "$pl" Skipped 'already-removed')/$(jcount "$ja" "$pl" Error 'wrong-entry')" == "$n/0/0" ]] && [[ $stale -le $n ]]
}
stale_evidence() { # JOURNAL PLAYLIST -> {removals, staleEntry, ratio (stale-entry / Removal, borne 1), skippedCountsStaleEntry (State), journal}
  jq -nc --argjson r "$(jcount "$1" "$2" Removal)" --argjson n "$(jcount "$1" "$2" Skipped 'stale-entry')" --argjson sc "$(state | jq '.skippedCounts["stale-entry"] // 0')" --argjson j "$1" \
    '{removals:$r, staleEntry:$n, ratio:(if $r>0 then ($n/$r) else null end), ratioBound:1, skippedCountsStaleEntry:$sc, journal:$j}'
}

i14() {
  echo "== I14 — événements simultanés"
  assert_baseline
  local pl j k i
  pl=$(shared_pl "SPIKE-I14" "$(IFS=,; echo "${M[*]:0:6}")"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1; jclear
  local -a U=("$U1" "$U2" "$U3") T=("$T1" "$T2" "$T3")
  rm -f "$SCRATCH"/ms.i14 "$SCRATCH"/bg.i14*.st
  for k in 0 1 2 3 4 5; do   # 3 comptes, médias différents, en parallèle
    bg_call "i14a$k" i14 POST "/Users/${U[$((k%3))]}/PlayedItems/${M[$k]}" "" "${T[$((k%3))]}"
  done
  wait
  ck I14.distinct "6 médias différents finis en parallèle par 3 comptes : tous retirés (playlist vide)" "$(entries "$pl")" wait_empty "$pl" 15
  j=$(journal Removal)
  ck I14.once "exactement un Removal par média (6)" "$j" test "$(jcount "$j" "$pl" Removal)" = 6
  # intégrité (F2-b, R4a) : un identifiant d'entrée périmé (renumérotation par le rafraîchissement d'Emby) retirait un AUTRE média
  local ja; ja=$(journal Removal,Skipped,Error)
  # entries = nombre d'entrées du média AVANT retrait (1 ici ; 2 pour les doublons d'I5). Skipped stale-entry (F3 : suppression sans effet, identifiant
  # d'entrée périmé, retentée) est TOLÉRÉ et consigné en evidence (compteur journal + State.skippedCounts).
  ck I14.integrity "lot parallèle : chaque Removal a entries=1, aucun Skipped already-removed (médias distincts), aucun Error wrong-entry ; stale-entry toléré (ratio stale-entry/Removal ≤ 1, consigné)" "$(stale_evidence "$ja" "$pl")" \
    integrity_check "$ja" "$pl" 6
  # même média, 3 comptes en parallèle
  add_item "$pl" "${M[0]}"; nap 1; jclear
  for i in 0 1 2; do unmark "${U[$i]}" "${T[$i]}" "${M[0]}"; done; nap 1
  for i in 0 1 2; do bg_call "i14b$i" i14 POST "/Users/${U[$i]}/PlayedItems/${M[0]}" "" "${T[$i]}"; done
  wait
  wait_count "$pl" "${M[0]}" 0 10 || true; nap 2
  j=$(journal Removal)
  ck I14.same "même média fini par 3 comptes en parallèle : UNE seule entrée retirée (1 Removal)" "$j" test "$(jcount "$j" "$pl" Removal)" = 1
  ck I14.noerror "aucune entrée Error dans le journal" "$(journal Error)" test "$(journal Error | jq length)" = 0
  ck I14.slow "aucun appel > 5 s" "null" bash -c '[[ ! -f $0 ]] || ! awk "\$1>5000{f=1} END{exit !f}" "$0"' "$SCRATCH/ms.i14"
  harvest_removals
}

i14c() {   # critical (R4a, F2-b) : rafale de lectures d'un même membre sur une même playlist — seul le média lu disparaît
  echo "== I14c — rafale (critical) : retraits rapprochés d'un même membre, seul le média lu disparaît (x3)"
  local rep k j pl bad got want ja
  for rep in 1 2 3; do
    assert_baseline
    pl=$(shared_pl "SPIKE-I14c-$rep" "$(IFS=,; echo "${M[*]:0:6}")"); prime "$pl" || true
    owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1; jclear
    bad=""
    for k in 0 1 2 3 4 5; do   # séquence rapide (sélection multiple) : pas d'attente entre deux marquages hormis l'attente active du retrait
      api POST "/Users/$U2/PlayedItems/${M[$k]}" "" "$T2" >/dev/null
      wait_count "$pl" "${M[$k]}" 0 10 || bad+=" M$k:non-retiré"
      for j in 0 1 2 3 4 5; do
        want=1; if [[ $j -le $k ]]; then want=0; fi
        got=$(count_item "$pl" "${M[$j]}")
        if [[ $got != "$want" ]]; then bad+=" après-M$k:M$j=$got(attendu $want)"; fi
      done
    done
    ck "I14c.rep$rep" "répétition $rep : après chaque marquage, seul ce média a disparu ; les autres sont présents une seule fois" \
      "$(jq -nc --arg b "$bad" '{violations:$b}')" test -z "$bad"
    ja=$(journal Removal,Skipped,Error)
    ck "I14c.integrity$rep" "répétition $rep : 6 Removal entries=1, aucun Skipped already-removed, aucun Error wrong-entry ; stale-entry toléré (ratio stale-entry/Removal ≤ 1, consigné)" "$(stale_evidence "$ja" "$pl")" \
      integrity_check "$ja" "$pl" 6
  done
  harvest_removals
}

i14p() {   # critical (R4a, F2-b) : fin de lecture de PLUSIEURS médias EN PARALLÈLE par 3 membres sur une même playlist (déclenche la fenêtre de course)
  local iters=${I14C_ITER:-8} pn=${#M[@]} kk it k j bad want got ja wl jl
  if [[ $pn -gt 10 ]]; then pn=10; fi
  kk=$((pn-4))   # médias lus ; les 4 derniers restent NON LUS : un retrait « voisin » (identifiant d'entrée périmé) les fait disparaître
  echo "== I14c (parallèle) — $iters itérations : $kk médias finis en parallèle par 3 membres sur $pn, les $((pn-kk)) autres doivent rester (une fois)"
  local -a U=("$U1" "$U2" "$U3") T=("$T1" "$T2" "$T3")
  for ((it=1; it<=iters; it++)); do
    assert_baseline
    local pl; pl=$(shared_pl "SPIKE-I14p-$it" "$(IFS=,; echo "${M[*]:0:$pn}")"); prime "$pl" || true
    owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; jclear
    rm -f "$SCRATCH"/bg.i14p*.st
    for ((k=0; k<kk; k++)); do   # lancement simultané (comme une sélection multiple / plusieurs membres) : marquage lu via l'API, comme I14
      bg_call "i14p$k" i14p POST "/Users/${U[$((k%3))]}/PlayedItems/${M[$k]}" "" "${T[$((k%3))]}"
    done
    wait
    for ((k=0; k<kk; k++)); do wait_count "$pl" "${M[$k]}" 0 15 || true; done
    nap 3   # laisser aboutir un éventuel retrait tardif d'un mauvais média (rafraîchissement d'Emby)
    bad=""
    for ((j=0; j<pn; j++)); do
      want=1; if [[ $j -lt $kk ]]; then want=0; fi
      got=$(count_item "$pl" "${M[$j]}")
      if [[ $got != "$want" ]]; then bad+=" M$j=$got(attendu $want)"; fi
    done
    ja=$(journal Removal,Skipped,Error)
    wl=$(jcount "$ja" "$pl" Removal 'entries=1( |$)'); jl="$wl/$(jcount "$ja" "$pl" Skipped 'already-removed')/$(jcount "$ja" "$pl" Error 'wrong-entry')"
    ck "I14c.par$it" "itération $it : seuls les $kk médias lus ont disparu, les $((pn-kk)) autres sont présents une fois" \
      "$(jq -nc --arg b "$bad" '{violations:$b}')" test -z "$bad"
    ck "I14c.parintegrity$it" "itération $it : $kk Removal entries=1, aucun Skipped already-removed, aucun Error wrong-entry (removal/already-removed/wrong-entry = $jl) ; stale-entry toléré (ratio stale-entry/Removal ≤ 1, consigné)" "$(stale_evidence "$ja" "$pl")" \
      integrity_check "$ja" "$pl" "$kk"
  done
  harvest_removals
}

i15() {
  echo "== I15 — passe planifiée pendant des transitions"
  assert_baseline
  local pl id k t pidbg dup
  pl=$(shared_pl "SPIKE-I15" "$(IFS=,; echo "${M[*]:0:6}")"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  id=$(task_id)
  ( CFG="$SCRATCH/bg.loop.cfg"; RESP="$SCRATCH/bg.loop.resp"; BODYF="$SCRATCH/bg.loop.body"
    while :; do api POST "/ScheduledTasks/Running/$id" >/dev/null; nap 1.5; done ) & pidbg=$!
  for k in 0 1 2 3 4 5; do
    t0=$(now_ms); finish "$U2" "$T2" "${M[$k]}"; t1=$(now_ms); echo $((t1-t0)) >> "$SCRATCH/ms.i15"
    nap 0.4
  done
  wait_count "$pl" "${M[5]}" 0 15 || true
  kill "$pidbg" 2>/dev/null || true; wait "$pidbg" 2>/dev/null || true
  nap 3
  t=$(tags_of "$pl"); dup=$(jq -r '[.[]|ascii_downcase]|group_by(.)|map(select(length>1))|length' <<<"$t")
  ck I15.removed "aucun retrait perdu : playlist vide" "$(entries "$pl")" test "$(entries "$pl" | jq length)" = 0
  ck I15.nodup "aucune étiquette dupliquée" "$t" test "$dup" = 0
  ck I15.families "une seule étiquette par famille" "$t" test "$(tag_count "$t" remove-si-lu)/$(tag_count "$t" propager-lu)/$(tag_count "$t" propager-avancement)" = "1/1/1"
  ck I15.slow "aucun appel de transition > 5 s" "null" bash -c '! awk "\$1>5000{f=1} END{exit !f}" "$0"' "$SCRATCH/ms.i15"
  harvest_removals
}

i16() {
  echo "== I16 — ré-entrance : au plus un écho synchrone et un écho différé par écriture du plugin, aucune repose"
  assert_baseline
  local pl jp j1 j2 e16a e16b e16c e16d
  # (a) écriture de pose : une seule écriture (ApplyDefaults) => un seul écho, trois étiquettes posées une fois
  jclear   # AVANT la création : la fenêtre du journal couvre la première détection, quelle qu'en soit la cause (action, passe planifiée)
  pl=$(shared_pl "SPIKE-I16" "${M[0]},${M[1]}")
  e16a=$(echo_settle)
  prime "$pl" || true
  local w; for ((w=0; w<15; w++)); do   # attente active de la stabilisation : 3 poses (plafond 15 s), puis 6 s sans nouvelle pose
    jp=$(journal); [[ $(jcount "$jp" "$pl" MarkerPosed) == 3 ]] && break; nap 1
  done
  nap 6; jp=$(journal); e16b=$(echo_settle)
  ck I16.posed "MarkerPosed : exactement une pose par famille (3), quelle que soit la cause, pas de repose 6 s plus tard" "$jp" test "$(jcount "$jp" "$pl" MarkerPosed)" = 3
  ck I16.echo.pose "un écho au plus (already-seen/reentrant) pour l'écriture de pose (+$((e16b-e16a)))" "{\"before\":$e16a,\"after\":$e16b}" test "$((e16b-e16a))" -le 1
  # (b) écriture de retrait
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; jclear
  local sc sd rc rd ac ad   # référence stabilisée APRÈS owner_edit : son ItemUpdated différé ne doit pas être compté dans la fenêtre (b)
  sc=$(echo_settle_split); rc=${sc% *}; ac=${sc#* }; e16c=$((rc+ac))
  finish "$U2" "$T2" "${M[0]}"; wait_count "$pl" "${M[0]}" 0 10 || true
  nap 3; j1=$(journal); nap 6; j2=$(journal); sd=$(echo_settle_split); rd=${sd% *}; ad=${sd#* }; e16d=$((rd+ad))
  ck I16.removal "un seul Removal pour un retrait" "$j1" test "$(jcount "$j1" "$pl" Removal)" = 1
  # un seul RemoveFromPlaylist => au plus un écho synchrone (PlaylistItemsRemoved, reentrant) et un écho différé (ItemUpdated du worker de
  # rafraîchissement, already-seen) — spec §S8 l.447. F1 (v1.2.2) : le WriteScope est révoqué à la sortie de l'écriture, donc l'écho différé
  # du worker n'est PLUS compté reentrant (avant F1 : reentrant +2 / already-seen +0) — vrai seulement si ReadRemovalEngine ne pose AUCUN scope externe autour de la boucle (M1, 564f950). Evidence {before,after} par compteur consignée dans le JSON. Les bornes (≤ 1 chacune) restent inchangées.
  ck I16.echo.removal.reentrant "au plus un écho synchrone (PlaylistItemsRemoved, reentrant) pour l'unique écriture de retrait (+$((rd-rc))) ; F1 : le worker différé n'y est plus compté" "{\"before\":$rc,\"after\":$rd}" test "$((rd-rc))" -le 1
  ck I16.echo.removal.deferred "au plus un écho différé (ItemUpdated du worker, already-seen) pour l'unique écriture de retrait (+$((ad-ac)))" "{\"before\":$ac,\"after\":$ad}" test "$((ad-ac))" -le 1
  ck I16.norepose "6 s plus tard : aucune pose ni nouveau retrait (pas de boucle)" "null" \
    test "$(jcount "$j2" "$pl" MarkerPosed)/$(jcount "$j2" "$pl" Removal)" = "0/1"
  ck I16.noerror "aucune entrée Error pour cette playlist" "null" test "$(jcount "$j2" "$pl" Error)" = 0
}

i17() {
  echo "== I17 — latence et chemin rapide"
  local s max p95 before after i pl
  harvest_removals
  s=$(state); max=$(jq -r '.handler.maxMs // 0' <<<"$s")
  ck I17.max "Handler.MaxMs <= 2000 ms" "$(jq -c '.handler' <<<"$s")" test "$max" -le 2000
  # I17.p95 : retraits ISOLÉS (1er Removal de chaque playlist, hors I14/I14c/I14p/I15) ; seuil de la spec inchangé (300 ms)
  p95=$(stats_json "$REMOVAL_MS" | jq -r '.p95 // 0'); n=$(stats_json "$REMOVAL_MS" | jq -r '.n')
  if [[ $n -lt 5 ]]; then skip I17.p95 "trop peu de retraits isolés observés ($n < 5) : lancer I3/I5/I9... dans la même exécution"
  else ck I17.p95 "p95 des Removal.durationMs des retraits ISOLÉS <= 300 ms (n=$n)" "$(stats_json "$REMOVAL_MS")" test "$p95" -le 300; fi
  # I17.burst : retraits de I14/I14c/I14p/I15 (rafales) : durationMs inclut l'attente du verrou de playlist (contrat) => bornes plus larges
  local bp95 bmax bn
  bp95=$(stats_json "$REMOVAL_BURST" | jq -r '.p95 // 0'); bmax=$(stats_json "$REMOVAL_BURST" | jq -r '.max // 0'); bn=$(stats_json "$REMOVAL_BURST" | jq -r '.n')
  if [[ $bn -lt 5 ]]; then skip I17.burst "trop peu de retraits en rafale observés ($bn < 5) : lancer I14/I14c/I14p/I15 dans la même exécution"
  else ck I17.burst "retraits en rafale (I14/I14c/I14p/I15) : p95 <= 1000 ms et max <= 2000 ms (n=$bn)" "$(stats_json "$REMOVAL_BURST")" \
    bash -c '[[ $1 -le 1000 && $2 -le 2000 ]]' _ "$bp95" "$bmax"; fi
  pl=$(shared_pl "SPIKE-I17" "${M[0]},${M[1]}"); prime "$pl" || true
  owner_edit "$pl" "[\"$OUI_RM\",\"$OUI_PR\"]" "[\"$NON_RM\",\"$NON_PR\"]"; nap 1
  before=$(state | jq -r '.handler.count // 0')
  for ((i=0; i<20; i++)); do play_to "$U2" "$T2" "${M[1]}" 30 progress-only; done
  nap 2; after=$(state | jq -r '.handler.count // 0')
  ck I17.fast "20 PlaybackProgress sans transition : aucun traitement de transition (Handler.Count inchangé)" "{\"before\":$before,\"after\":$after}" test "$before" = "$after"
  close_open_sessions   # #61 : les 20 sessions progress-only (et celles des autres scénarios) sont fermées par Stopped
}

ALL=(I0 I1 I2 I3 I4 I5 I6 I7 I8 I9 I10 I11 I12 I13 I14 I14c I14p I15 I16 I17)
if [[ ${#WANT[@]} -eq 0 ]]; then WANT=("${ALL[@]}"); fi
for s in "${WANT[@]}"; do
  fn=$(tr 'A-Z' 'a-z' <<<"$s")
  declare -F "$fn" >/dev/null || die "scénario inconnu : $s"
  CUR_SCEN=$(tr 'a-z' 'A-Z' <<<"$s")
  "$fn"
  harvest_removals
done
drop_playlists   # #60 : retire aussi les playlists du dernier scénario (I17 : SPIKE-I17 OUI/OUI)

# ---------------------------------------------------------------- logs et comptes protégés
echo "== Logs d'Emby pendant l'exécution"
if [[ $LOGS_OK == 1 ]]; then
  KUBECONFIG="$KUBECONFIG_FILE" kubectl logs "deployment/$KUBE_DEPLOY" -n "$KUBE_NS" --since-time="$T_START" > "$SCRATCH/emby.log" 2>/dev/null || true
  LS=$(scan_log "$SCRATCH/emby.log")
  ck LOGS "zéro « database is locked »/SQLITE_BUSY/exception/erreur plugin dans les logs d'Emby" "$LS" \
    test "$(jq -r '.locked+.exceptions+.pluginErrors' <<<"$LS")" = 0
else
  skip LOGS "logs kubectl indisponibles (kubectl ou private/kubeconfig.yml)"
fi
check_no_test_sessions   # #61 : aucune session test_u* en lecture
echo "== Comptes protégés"
compare_protected "fin des scénarios" "${TEST_USERS[@]}" && rec PROTECTED OK "admin, cyril, user2 inchangés" || rec PROTECTED KO "comptes protégés modifiés" "null"

write_out false; DONE=1
echo
echo "== Bilan (détail : $OUT_FILE)"
jq -r '.summary|"  OK=\(.ok) KO=\(.ko) SKIP=\(.skip)"' "$OUT_FILE"
[[ $FAILS == 0 ]] || { echo "ECHEC : $FAILS assertion(s) KO" >&2; exit 1; }
echo "Toutes les assertions exécutées sont OK."
