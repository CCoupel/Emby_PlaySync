#!/usr/bin/env python3
"""Faux serveur Emby (hors ligne) pour tester le flux de 05-reentrancy-probe.sh : état en mémoire,
réponses Spike/* en PascalCase, sonde simulée (Probe P1..P6) sur transition non lu -> lu."""
import sys, json, re, threading, http.server, urllib.parse, itertools

PORT = int(sys.argv[1]); MODE = sys.argv[2] if len(sys.argv) > 2 else "ok"   # ok | slow | dup
lock = threading.Lock()
ids = itertools.count(1000)
USERS = {"admin": "a" * 32, "cyril": "c" * 32, "user2": "b" * 32, "test_u1": "1" * 32, "test_u2": "2" * 32, "test_u3": "3" * 32}
BYID = {v: k for k, v in USERS.items()}
TOK = {}
PL = {}            # id -> {name, entries:[{pid,item}], tags:[...]}
PLAYED = set()
EV = []
CFG = {"EnableSpikeEndpoints": False, "EnableReentrancyProbe": False, "GracePasses": 2}
MEDIA = [str(100 + i) for i in range(6)]

def ev(kind, **kw):
    e = {"Ts": "2026-09-26T12:00:00.000Z", "Kind": kind, "PluginWrite": False}
    e.update(kw); EV.append(e)

def probe(sc, d=12, echoes=1, outcome="OK"):
    ev("Probe", Detail=f"scenario={sc} durationMs={d} echoes={echoes} outcome={outcome}")

class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def out(self, code, obj=None):
        b = b"" if obj is None else json.dumps(obj).encode()
        self.send_response(code); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}") if n else {}
    def handle_any(self):
        u = urllib.parse.urlparse(self.path); p = u.path.replace("/emby", "", 1); q = urllib.parse.parse_qs(u.query); m = self.command
        body = self.body() if m in ("POST",) else {}
        with lock:
            return self.route(m, p, q, body)
    do_GET = do_POST = do_DELETE = handle_any
    def route(self, m, p, q, body):
        if p == "/System/Info": return self.out(200, {"ServerName": "emby2-Testing"})
        if p == "/Users/AuthenticateByName":
            t = "tok-" + body["Username"]; TOK[t] = body["Username"]
            return self.out(200, {"AccessToken": t, "User": {"Id": USERS.get(body["Username"], "?")}})
        if p == "/Users" and m == "GET": return self.out(200, [{"Name": n, "Id": i} for n, i in USERS.items()])
        r = re.fullmatch(r"/Users/(\w+)", p)
        if r and m == "GET": return self.out(200, {"Policy": {"IsAdministrator": r.group(1) == USERS["admin"], "BlockedTags": ["x", "y"]}})
        if p == "/Items" and m == "GET":
            if "Ids" in q:
                x = PL.get(q["Ids"][0]); return self.out(200, {"Items": [{"Id": q["Ids"][0], "Name": x["name"]}] if x else []})
            return self.out(200, {"Items": [{"Id": i, "Name": "M" + i} for i in MEDIA]})
        if p == "/Plugins/9ebe814e-9438-42b8-aa57-feea1ae92451/Configuration":
            if m == "GET": return self.out(200, CFG)
            CFG.update(body); return self.out(204)
        if p == "/SharedPlaylist/Spike/Events":
            res = list(EV)
            if q.get("clear", ["false"])[0] == "true": EV.clear()
            return self.out(200, res)
        if p == "/SharedPlaylist/Spike/Tags":
            x = PL[q["playlistId"][0]]; return self.out(200, {"PlaylistId": q["playlistId"][0], "Tags": x["tags"], "Overview": None})
        if p == "/Playlists" and m == "POST":
            pid = str(next(ids)); PL[pid] = {"name": q["Name"][0], "entries": [{"pid": str(next(ids)), "item": i} for i in q["Ids"][0].split(",")], "tags": []}
            ev("PlaylistItemsAdded", PlaylistId=pid)
            if CFG["EnableReentrancyProbe"] and "p4" in q["Name"][0]: probe("P4")
            return self.out(200, {"Id": pid})
        if p == "/Items/Access": return self.out(204)
        r = re.fullmatch(r"/Playlists/(\d+)/Items", p)
        if r:
            x = PL[r.group(1)]
            if m == "GET": return self.out(200, {"Items": [{"Id": e["item"], "PlaylistItemId": e["pid"]} for e in x["entries"]]})
            for i in q["Ids"][0].split(","): x["entries"].append({"pid": str(next(ids)), "item": i})
            return self.out(200, {})
        r = re.fullmatch(r"/Users/(\w+)/PlayedItems/(\d+)", p)
        if r:
            user, item = r.group(1), r.group(2)
            if m == "DELETE": PLAYED.discard((user, item)); return self.out(200, {})
            was = (user, item) in PLAYED; PLAYED.add((user, item))
            if CFG["EnableReentrancyProbe"] and not was:
                for pid, x in PL.items():
                    es = [e for e in x["entries"] if e["item"] == item]
                    for e in es:
                        x["entries"].remove(e); ev("PlaylistItemsRemoved", PlaylistId=pid, EntryId=e["pid"], ItemId=item)
                        if MODE == "dup": ev("PlaylistItemsRemoved", PlaylistId=pid, EntryId=e["pid"], ItemId=item)
                    if es:
                        d = 900 if MODE == "slow" else 15
                        if "p5" in x["name"]: probe("P5", d)
                        elif "p6" in x["name"]: probe("P6", d)
                        else:
                            for s in ("P1", "P2", "P3"): probe(s, d)
            return self.out(200, {})
        r = re.fullmatch(r"/Users/(\w+)/Items/(\d+)", p)
        if r and m == "GET":
            x = PL[r.group(2)]; return self.out(200, {"Id": r.group(2), "TagItems": [{"Name": t} for t in x["tags"]]})
        r = re.fullmatch(r"/Items/(\d+)", p)
        if r and m == "POST":
            PL[r.group(1)]["tags"] = [t["Name"] for t in body.get("TagItems", [])]; return self.out(204)
        if r and m == "DELETE": PL.pop(r.group(1), None); return self.out(204)
        return self.out(404, {"error": p})

http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
