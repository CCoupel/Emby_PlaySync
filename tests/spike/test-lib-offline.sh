#!/usr/bin/env bash
# test-lib-offline.sh — test hors ligne de lib.sh (aucun accès à emby2, aucun secret) :
# un serveur HTTP local capture les en-têtes reçus ; on vérifie que l'en-tête X-Emby-Authorization
# de login() arrive INTACT (guillemets, DeviceId) et que les valeurs spéciales sont échappées.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

PORT=${PORT:-18765}
CAP="$SCRATCH/captured.jsonl"; : > "$CAP"
python3 - "$PORT" "$CAP" <<'PY' &
import sys, json, http.server
port, cap = int(sys.argv[1]), sys.argv[2]
class H(http.server.BaseHTTPRequestHandler):
    def _h(self):
        n = int(self.headers.get('Content-Length') or 0)
        body = self.rfile.read(n).decode() if n else ''
        with open(cap, 'a') as f:
            f.write(json.dumps({"path": self.path, "method": self.command, "headers": dict(self.headers), "body": body}) + "\n")
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
want='MediaBrowser Client="spike", Device="spike", DeviceId="spike-1", Version="1"'
[[ $auth == "$want" ]] && ok "X-Emby-Authorization intact (guillemets et DeviceId présents)" || ko "X-Emby-Authorization altéré : $auth"
[[ $auth == *'DeviceId="spike-1"'* ]] && ok "DeviceId présent" || ko "DeviceId absent"
[[ $(last | jq -r '.headers["X-Emby-Token"] // "absent"') == absent ]] && ok "login : pas d'en-tête X-Emby-Token" || ko "X-Emby-Token présent au login"
[[ $(last | jq -r '.body|fromjson|.Pw') == 'p"w\d' ]] && ok "corps JSON transmis intact (guillemet et antislash)" || ko "corps altéré"

st=$(api GET "/System/Info?q=1")
[[ $st == 200 ]] && ok "GET simple : 200" || ko "GET : $st"
[[ $(last | jq -r '.headers["X-Emby-Token"]') == 'fake"key\with"special' ]] && ok "token avec guillemets/antislash reçu intact" || ko "token altéré"
[[ $(last | jq -r '.path') == "/emby/System/Info?q=1" ]] && ok "URL/chemin corrects" || ko "chemin : $(last | jq -r .path)"
echo "  (aucune valeur réelle utilisée : serveur local, clé factice)"

[[ $fail == 0 ]] || { echo "ECHEC test-lib-offline" >&2; exit 1; }
echo "test-lib-offline : OK"
