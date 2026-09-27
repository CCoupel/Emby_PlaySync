#!/usr/bin/env bash
# 90-cleanup.sh — nettoyage des données de test (playlists SPIKE*, comptes test_*) sur emby2 (QUALIF uniquement).
# Reprise de tests/spike/90-cleanup.sh (v0.1.0, supprimé avec #15).
#  - supprime les playlists SPIKE* (ids de private/test-state.json + balayage par nom, dans les
#    bibliothèques des comptes test_*) ;
#  - comptes test_* : GARDÉS par défaut ; --delete-users pour les supprimer (+ fichiers private/test-*) ;
#  - vérifie que admin, cyril et user2 sont inchangés (utilisateurs + politiques) vs le snapshot.
# Usage : tests/integration/90-cleanup.sh [--delete-users]
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

DELETE_USERS=0
case "${1:-}" in
  "") ;;
  --delete-users) DELETE_USERS=1 ;;
  *) die "usage : $0 [--delete-users]" ;;
esac

check_ignored "$USERS_ENV" "$SNAPSHOT" "$STATE" "$QUALIF_ENV"
guard_target

echo "== Playlists SPIKE"
ids=$([[ -f $STATE ]] && jq -r '.[]' "$STATE" || true)
# balayage par nom dans la vue de chaque compte test_* existant (aucun compte réel consulté)
st=$(api GET /Users); [[ $st == 200 ]] || die "GET /Users -> $st"
USERS_JSON=$(jq -c . "$RESP")
declare -a test_ids=()
for n in "${TEST_USERS[@]}"; do
  uid=$(jq -r --arg n "$n" '.[]|select(.Name==$n)|.Id' <<<"$USERS_JSON")
  if [[ -n $uid ]]; then test_ids+=("$uid"); fi
done
for uid in "${test_ids[@]}"; do
  st=$(api GET "/Users/$uid/Items?Recursive=true&IncludeItemTypes=Playlist")
  if [[ $st == 200 ]]; then
    ids+=$'\n'$(jq -r '.Items[]|select(.Name|startswith("SPIKE"))|.Id' "$RESP")
  fi
done
count=0
while IFS= read -r id; do
  if [[ -z $id ]]; then continue; fi
  # garde : ne supprimer que ce qui s'appelle SPIKE* (relu côté serveur)
  st=$(api GET "/Items?Ids=$id")
  nm=$(jq -r '.Items[0].Name // empty' "$RESP" 2>/dev/null || true)
  if [[ -z $nm ]]; then echo "  [--] $id introuvable (déjà supprimée)"; continue; fi
  if [[ $nm != SPIKE* ]]; then echo "  [SKIP] $id (« $nm ») ne commence pas par SPIKE : non supprimée"; continue; fi
  st=$(api DELETE "/Items/$id")
  case $st in
    2*) count=$((count+1)); echo "  [OK] playlist $id supprimée" ;;
    404) ;;   # déjà supprimée (id vu plusieurs fois)
    *) echo "  [KO] suppression de $id -> HTTP $st" ;;
  esac
done < <(printf '%s\n' "$ids" | sort -u)
echo "  $count playlist(s) supprimée(s)"
rm -f "$STATE"

echo "== Comptes test_*"
if [[ $DELETE_USERS == 1 ]]; then
  for uid in "${test_ids[@]}"; do
    # garde : jamais un compte protégé
    name=$(jq -r --arg i "$uid" '.[]|select(.Id==$i)|.Name' <<<"$USERS_JSON")
    [[ $name == test_u[123] ]] || die "garde : $name n'est pas un compte test_u*"
    apiok '2*' DELETE "/Users/$uid"
    echo "  [OK] $name supprimé"
  done
  extra=()
else
  echo "  comptes conservés (--delete-users pour les supprimer)"
  # attendus en plus du snapshot : les comptes test_* qui existent réellement
  extra=(); for uid in "${test_ids[@]}"; do extra+=("$(jq -r --arg i "$uid" '.[]|select(.Id==$i)|.Name' <<<"$USERS_JSON")"); done
fi

echo "== Vérification des comptes protégés"
rc=0
compare_protected "après nettoyage" "${extra[@]}" || rc=1
if [[ $DELETE_USERS == 1 && $rc == 0 ]]; then
  rm -f "$USERS_ENV" "$SNAPSHOT"
  echo "  fichiers private/test-users.env et test-snapshot.json supprimés"
fi
[[ $rc == 0 ]] || { echo "ECHEC : comptes protégés différents du snapshot — ne rien corriger sans validation de l'utilisateur" >&2; exit 1; }
echo "Nettoyage terminé."
