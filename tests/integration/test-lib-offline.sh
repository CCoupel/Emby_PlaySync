#!/usr/bin/env bash
# test-lib-offline.sh — test hors ligne de lib.sh (aucun accès à emby2, aucun secret) :
# un serveur HTTP local capture les en-têtes reçus ; on vérifie que l'en-tête X-Emby-Authorization
# de login() arrive INTACT (guillemets, DeviceId) et que les valeurs spéciales sont échappées ; répond en Diagnostics/*.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

PORT=${PORT:-18765}
CAP="$SCRATCH/captured.jsonl"; : > "$CAP"
FAKE="$SCRATCH/fake"; mkdir -p "$FAKE"
python3 - "$PORT" "$CAP" "$FAKE" <<'PY' &
import sys, json, http.server, os, re
port, cap, fake = int(sys.argv[1]), sys.argv[2], sys.argv[3]
class H(http.server.BaseHTTPRequestHandler):
    def _h(self):
        n = int(self.headers.get('Content-Length') or 0)
        body = self.rfile.read(n).decode() if n else ''
        with open(cap, 'a') as f:
            f.write(json.dumps({"path": self.path, "method": self.command, "headers": dict(self.headers), "body": body}) + "\n")
        m = re.search(r'/SharedPlaylist/Diagnostics/(\w+)', self.path)
        f = os.path.join(fake, m.group(1) + '.json') if m else None
        if f and os.path.exists(f):
            out = open(f, 'rb').read()
        else:
            out = json.dumps({"AccessToken": "fake-token", "ok": True}).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(out))); self.end_headers(); self.wfile.write(out)
    do_GET = do_POST = do_DELETE = _h
    def log_message(self, *a): pass
http.server.HTTPServer(('127.0.0.1', port), H).serve_forever()
PY
SRV=$!
trap 'kill $SRV 2>/dev/null; rm -rf "$SCRATCH"' EXIT
for _ in $(seq 20); do curl -s -o /dev/null "http://127.0.0.1:$PORT/" && break; sleep 0.2; done

EMBY_URL="http://127.0.0.1:$PORT/emby"; EMBY_API_KEY='fake"key\with"special'
fail=0
ok() { echo "  [OK] $1"; }
ko() { echo "  [KO] $1"; fail=1; }

tok=$(login "u" 'p"w\d')
last() { tail -n1 "$CAP"; }
[[ $tok == fake-token ]] && ok "login() renvoie le token du corps" || ko "token"
auth=$(last | jq -r '.headers["X-Emby-Authorization"] // ""')
want='MediaBrowser Client="spike", Device="spike", DeviceId="spike-u", Version="1"'
[[ $auth == "$want" ]] && ok "X-Emby-Authorization intact (guillemets et DeviceId présents)" || ko "X-Emby-Authorization altéré : $auth"
[[ $auth == *'DeviceId="spike-u"'* ]] && ok "DeviceId dérivé du nom d'utilisateur (spike-u)" || ko "DeviceId absent ou non dérivé"
[[ $(last | jq -r '.headers["X-Emby-Token"] // "absent"') == absent ]] && ok "login : pas d'en-tête X-Emby-Token" || ko "X-Emby-Token présent au login"
[[ $(last | jq -r '.body|fromjson|.Pw') == 'p"w\d' ]] && ok "corps JSON transmis intact (guillemet et antislash)" || ko "corps altéré"

# I28/I33 (#47) : DeviceId partagé entre comptes de test, piste plausible d'une collision de session Emby —
# un DeviceId distinct par utilisateur doit être vérifiable, pas seulement supposé.
login "autre_utilisateur" "pw" >/dev/null
auth2=$(last | jq -r '.headers["X-Emby-Authorization"] // ""')
[[ $auth2 == *'DeviceId="spike-autre_utilisateur"'* ]] && ok "DeviceId distinct pour un autre utilisateur (spike-autre_utilisateur)" || ko "DeviceId non distinct entre utilisateurs : $auth2"
[[ $auth != "$auth2" ]] && ok "les deux DeviceId observés diffèrent réellement" || ko "même DeviceId pour deux utilisateurs différents (régression I28/I33)"

st=$(api GET "/System/Info?q=1")
[[ $st == 200 ]] && ok "GET simple : 200" || ko "GET : $st"
[[ $(last | jq -r '.headers["X-Emby-Token"]') == 'fake"key\with"special' ]] && ok "token avec guillemets/antislash reçu intact" || ko "token altéré"
[[ $(last | jq -r '.path') == "/emby/System/Info?q=1" ]] && ok "URL/chemin corrects" || ko "chemin : $(last | jq -r .path)"

# --- compare_protected : le JSON des politiques contient des [ ] (pas de glob dans [[ ]])
echo "== compare_protected"
SNAPSHOT="$SCRATCH/snap.json"
PROTECTED_USERS=(admin user2)
mk_state() { # POLITIQUE_USER2_JSON NOMS_JSON
  jq -nSc --argjson p "$1" --argjson u "$2" '{users:$u, policies:{admin:{IsAdministrator:true,BlockedTags:["a","b"]}, user2:$p}}'
}
POL='{"EnabledFolders":["x","y"],"BlockedTags":[],"AllowSharingPersonalItems":false}'
mk_state "$POL" '["admin","user2"]' > "$SNAPSHOT"
snapshot_users() { CUR; }
CUR() { mk_state "$POL_NOW" "$USERS_NOW"; }
POL_NOW=$POL; USERS_NOW='["admin","user2"]'
if compare_protected "identique avec [ ]" >/dev/null; then ok "politiques identiques (avec [ ]) => égales"; else ko "faux positif : politiques identiques jugées différentes"; fi
POL_NOW='{"EnabledFolders":["x","y","z"],"BlockedTags":[],"AllowSharingPersonalItems":false}'
if compare_protected "politique modifiée" >/dev/null; then ko "politique modifiée non détectée"; else ok "politique modifiée (EnabledFolders) => détectée"; fi

# v0.4.0 (#26, D8) : AllowSharingPersonalItems false->true est la SEULE transition tolérée (le plugin peut légitimement
# l'accorder à un compte protégé dès la première passe après déploiement) ; l'inverse (révocation, contraire à D-e)
# reste détecté comme n'importe quelle autre différence. Snapshot inchangé (POL, user2 à false) pour ces deux essais.
POL_NOW='{"EnabledFolders":["x","y"],"BlockedTags":[],"AllowSharingPersonalItems":true}'
if compare_protected "AllowSharingPersonalItems false->true" >/dev/null; then ok "AllowSharingPersonalItems false->true toléré (#26)"; else ko "faux positif : false->true jugé différent"; fi

mk_state '{"EnabledFolders":["x","y"],"BlockedTags":[],"AllowSharingPersonalItems":true}' '["admin","user2"]' > "$SNAPSHOT"   # snapshot où user2 est DÉJÀ à true
POL_NOW='{"EnabledFolders":["x","y"],"BlockedTags":[],"AllowSharingPersonalItems":false}'
if compare_protected "AllowSharingPersonalItems true->false" >/dev/null; then ko "révocation AllowSharingPersonalItems non détectée (contraire à D-e)"; else ok "révocation AllowSharingPersonalItems (true->false) toujours détectée"; fi
mk_state "$POL" '["admin","user2"]' > "$SNAPSHOT"   # remet le snapshot d'origine pour la suite
POL_NOW=$POL; USERS_NOW='["admin","test_u1","user2"]'
if compare_protected "extra non déclaré" >/dev/null; then ko "utilisateur en trop non détecté"; else ok "utilisateur en trop => détecté"; fi
if compare_protected "extra déclaré" test_u1 >/dev/null; then ok "utilisateur attendu (test_u1) accepté"; else ko "utilisateur attendu rejeté"; fi


# --- need_int
echo "== need_int"
( need_int "x" "123" ) 2>/dev/null && ok "need_int accepte un entier" || ko "need_int rejette un entier"
( need_int "x" "null" ) 2>/dev/null && ko "need_int accepte 'null'" || ok "need_int refuse 'null'"
( need_int "x" "" ) 2>/dev/null && ko "need_int accepte vide" || ok "need_int refuse vide"

# --- Diagnostics/* (v0.2.0) : PascalCase normalisé, clés d'identifiants et de familles préservées
echo "== Diagnostics PascalCase"
cat > "$FAKE/State.json" <<'J'
{"SeenPlaylistIds":["1001"],"GraceCounters":{"1001":{"remove-si-lu":1,"propager-lu":0,"propager-avancement":0,"description":0}},"LastPass":{"Ts":"2026-09-26T12:00:00Z","DurationMs":5,"PlaylistsSeen":2,"SharedManaged":1},"Handler":{"Count":3,"LastMs":4,"MaxMs":9},"GracePasses":2}
J
cat > "$FAKE/Journal.json" <<'J'
[{"Ts":"t","Kind":"Removal","UserId":"u","ItemId":"7","PlaylistId":"1001","Detail":"entries=1 durationMs=4"}]
J
api GET "/SharedPlaylist/Diagnostics/State" >/dev/null
jq -e '.seenPlaylistIds[0]=="1001" and .graceCounters["1001"]["remove-si-lu"]==1 and .graceCounters["1001"]["propager-avancement"]==0 and .lastPass.durationMs==5 and .handler.maxMs==9 and .gracePasses==2' "$RESP" >/dev/null && ok "State : clés camelCase, ids et noms de familles préservés" || ko "State : normalisation"
api GET "/SharedPlaylist/Diagnostics/Journal?clear=false&kind=Removal" >/dev/null
jq -e '.[0].kind=="Removal" and .[0].playlistId=="1001" and (.[0].detail|test("durationMs=4"))' "$RESP" >/dev/null && ok "Journal : ts/kind/playlistId/detail extraits" || ko "Journal : normalisation"
echo "  (aucune valeur réelle utilisée : serveur local, clé factice)"

[[ $fail == 0 ]] || { echo "ECHEC test-lib-offline" >&2; exit 1; }
echo "test-lib-offline : OK"
