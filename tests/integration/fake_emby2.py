#!/usr/bin/env python3
"""Faux Emby + faux moteur v0.2.0 (hors ligne) pour tester le flux de 20-etiquettes-retrait.sh.
Implémente les règles décrites par le plan (étiquettes à deux familles, première détection, grâce, retrait à la
transition non lu -> lu, journal/état Diagnostics, tâche planifiée). Ce n'est PAS le plugin : c'est une spécification
exécutable minimale qui vérifie que le script de test lit bien le contrat et enchaîne correctement les scénarios."""
import sys, json, re, threading, http.server, urllib.parse, itertools, datetime

PORT = int(sys.argv[1]); MODE = sys.argv[2] if len(sys.argv) > 2 else "ok"   # ok | noremove (moteur qui ne retire pas)
GRACE = 2
lock = threading.RLock()
ids = itertools.count(1000)
USERS = {"admin": "a" * 32, "cyril": "c" * 32, "user2": "b" * 32, "test_u1": "1" * 32, "test_u2": "2" * 32, "test_u3": "3" * 32}
MEDIA = [str(100 + i) for i in range(8)]
RT = 7_000_000_000
TASK_ID = "77"
pass_no = itertools.count(1)

PL, PLAYED = {}, set()
def reset():   # redémarrage du plugin : la MÉMOIRE du moteur est remise à zéro, pas les données d'Emby
    global SEEN, GRACEC, JOURNAL, HANDLER, LASTPASS, WRITING
    SEEN, GRACEC, JOURNAL = set(), {}, []
    HANDLER = {"Count": 0, "LastMs": 0, "MaxMs": 0}; LASTPASS = {"Ts": None, "DurationMs": 0, "PlaylistsSeen": 0, "SharedManaged": 0}; WRITING = False
reset()

def jr(kind, pl=None, user=None, item=None, detail=None):
    JOURNAL.append({"Ts": datetime.datetime.utcnow().isoformat() + "Z", "Kind": kind, "UserId": user, "ItemId": item, "PlaylistId": pl, "Detail": detail})

def state_of(tags, fam):
    pat = re.compile(r"^\s*" + re.escape(fam) + r"\s*=\s*(NON|OUI)\s*$", re.I)
    oui = non = False
    for t in tags:
        m = pat.match(t)
        if not m: continue
        if m.group(1).upper() == "OUI": oui = True
        else: non = True
    return "Both" if oui and non else "Oui" if oui else "Non" if non else "None"

def shared(p): return bool(p["shares"])
def member(p, u): return u == p["owner"] or u in p["shares"]

HELP = "Playlist partagée gérée par Emby Shared Playlist.\n- remove-si-lu=OUI : retrait.\n- propager-lu=OUI : à venir."

def write_defaults(pid, fams, ov, cause):
    global WRITING
    p = PL[pid]; WRITING = True
    try:
        for f in fams:
            if state_of(p["tags"], f) == "None":
                p["tags"].append(f + "=NON"); jr("MarkerPosed", pid, detail=f"family={f} cause={cause}")
                GRACEC.setdefault(pid, {})[f] = 0
        if ov and not p["overview"].strip():
            p["overview"] = HELP; jr("DescriptionWritten", pid, detail=f"cause={cause}"); GRACEC.setdefault(pid, {})["description"] = 0
        jr("Skipped", pid, detail="reentrant")            # écho de notre propre écriture (ItemUpdated)
    finally: WRITING = False

def first_detection(pid):
    p = PL[pid]
    if pid in SEEN: jr("Skipped", pid, detail="already-seen"); return
    SEEN.add(pid)
    fams = [f for f in ("remove-si-lu", "propager-lu") if state_of(p["tags"], f) == "None"]
    ov = not p["overview"].strip()
    if not fams and not ov: jr("Skipped", pid, detail="marker-present"); return
    write_defaults(pid, fams, ov, "first-detection")

def on_event(pid):
    if WRITING: jr("Skipped", pid, detail="reentrant"); return
    p = PL.get(pid)
    if not p: return
    if pid in SEEN and MODE != "repose": jr("Skipped", pid, detail="already-seen"); return
    if not shared(p): jr("Skipped", pid, detail="not-shared"); return
    if MODE == "repose": SEEN.discard(pid)      # défaut simulé : repose sur ItemUpdated d'une playlist déjà vue
    first_detection(pid)

def do_pass():
    t0 = datetime.datetime.utcnow(); n = 0; managed = 0
    for pid, p in list(PL.items()):
        n += 1
        if not shared(p): continue
        managed += 1
        if pid not in SEEN: first_detection(pid); continue
        toposed = []; c = GRACEC.setdefault(pid, {})
        for f in ("remove-si-lu", "propager-lu"):
            if state_of(p["tags"], f) != "None": c[f] = 0; continue
            c[f] = c.get(f, 0) + 1
            if c[f] >= GRACE: toposed.append(f)
        ov = None
        if not p["overview"].strip():
            c["description"] = c.get("description", 0) + 1
            if c["description"] >= GRACE: ov = True
        else: c["description"] = 0
        if toposed or ov: write_defaults(pid, toposed, bool(ov), "grace-elapsed")
    ms = int((datetime.datetime.utcnow() - t0).total_seconds() * 1000)
    LASTPASS.update({"Ts": datetime.datetime.utcnow().isoformat() + "Z#" + str(next(pass_no)), "DurationMs": ms, "PlaylistsSeen": n, "SharedManaged": managed})
    jr("ScanPass", detail=f"playlists={n} shared={managed} posed=0 pending=0 durationMs={ms}")

def transition(user, item):
    t0 = datetime.datetime.utcnow()
    for pid, p in list(PL.items()):
        es = [e for e in p["entries"] if e["item"] == item]
        if not es: continue
        if not member(p, user): jr("Skipped", pid, user, item, "not-member"); continue
        if not shared(p): jr("Skipped", pid, user, item, "not-shared"); continue
        st = state_of(p["tags"], "remove-si-lu"); jr("MarkerSeen", pid, user, item, f"family=remove-si-lu state={st}")
        if st != "Oui" or MODE == "noremove": jr("Skipped", pid, user, item, "inactive"); continue
        n = 0
        while n < 50:                         # toutes les entrées du média, une à la fois
            e = next((x for x in p["entries"] if x["item"] == item), None)
            if e is None: break
            p["entries"].remove(e); n += 1
        ms = 3
        jr("Removal", pid, user, item, f"entries={n} durationMs={ms}"); jr("Skipped", pid, detail="already-seen")   # écho PlaylistItemsRemoved
        HANDLER["Count"] += 1; HANDLER["LastMs"] = ms; HANDLER["MaxMs"] = max(HANDLER["MaxMs"], ms)

def set_played(user, item, val):
    if val:
        if (user, item) in PLAYED: return
        PLAYED.add((user, item)); transition(user, item)
    else: PLAYED.discard((user, item))

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
        u = urllib.parse.urlparse(self.path); p = u.path.replace("/emby", "", 1); q = urllib.parse.parse_qs(u.query)
        body = self.body() if self.command == "POST" else {}
        with lock: return self.route(self.command, p, q, body)
    do_GET = do_POST = do_DELETE = handle_any
    def dto(self, pid):
        p = PL[pid]; return {"Id": pid, "Name": p["name"], "Overview": p["overview"], "Tags": p["tags"], "TagItems": [{"Name": t} for t in p["tags"]]}
    def route(self, m, p, q, body):
        if p == "/__reset": reset(); return self.out(204)
        if p == "/System/Info": return self.out(200, {"ServerName": "emby2-Testing"})
        if p == "/Users/AuthenticateByName": return self.out(200, {"AccessToken": "tok-" + body["Username"], "User": {"Id": USERS.get(body["Username"], "?")}})
        if p == "/Users" and m == "GET": return self.out(200, [{"Name": n, "Id": i} for n, i in USERS.items()])
        r = re.fullmatch(r"/Users/(\w+)", p)
        if r and m == "GET": return self.out(200, {"Policy": {"IsAdministrator": r.group(1) == USERS["admin"], "BlockedTags": ["x"]}})
        if p == "/Items" and m == "GET":
            if "Ids" in q: return self.out(200, {"Items": [self.dto(q["Ids"][0])] if q["Ids"][0] in PL else []})
            return self.out(200, {"Items": [{"Id": i, "Name": "M" + i, "RunTimeTicks": RT} for i in MEDIA]})
        if p == "/SharedPlaylist/Diagnostics/State":
            return self.out(200, {"SeenPlaylistIds": sorted(SEEN), "GraceCounters": GRACEC, "LastPass": LASTPASS, "Handler": HANDLER, "GracePasses": GRACE})
        if p == "/SharedPlaylist/Diagnostics/Journal":
            res = list(JOURNAL)
            if "kind" in q: ks = q["kind"][0].split(","); res = [e for e in res if e["Kind"] in ks]
            if q.get("clear", ["false"])[0] == "true": JOURNAL.clear()
            return self.out(200, res)
        if p == "/ScheduledTasks" and m == "GET": return self.out(200, [{"Name": "Emby Shared Playlist — réconciliation", "Id": TASK_ID, "State": "Idle"}])
        if p == "/ScheduledTasks/Running/" + TASK_ID and m == "POST": do_pass(); return self.out(204)
        if p == "/Playlists" and m == "POST":
            pid = str(next(ids)); PL[pid] = {"name": q["Name"][0], "owner": q["UserId"][0], "shares": {}, "tags": [], "overview": "", "public": False,
                                            "entries": [{"pid": str(next(ids)), "item": i} for i in q["Ids"][0].split(",")]}
            on_event(pid); return self.out(200, {"Id": pid})
        if p == "/Items/Access":
            for pid in body["ItemIds"]:
                for u in body["UserIds"]: PL[pid]["shares"][u] = body["ItemAccess"]
            return self.out(204)
        r = re.fullmatch(r"/Playlists/(\d+)/Items", p)
        if r:
            x = PL[r.group(1)]
            if m == "GET": return self.out(200, {"Items": [{"Id": e["item"], "PlaylistItemId": e["pid"]} for e in x["entries"]]})
            for i in q["Ids"][0].split(","): x["entries"].append({"pid": str(next(ids)), "item": i})
            on_event(r.group(1)); return self.out(200, {})
        r = re.fullmatch(r"/Users/(\w+)/PlayedItems/(\d+)", p)
        if r:
            set_played(r.group(1), r.group(2), m == "POST"); return self.out(200, {})
        r = re.fullmatch(r"/Users/(\w+)/Items/(\d+)", p)
        if r and m == "GET":
            if r.group(2) in PL: return self.out(200, self.dto(r.group(2)))
            return self.out(200, {"Id": r.group(2), "RunTimeTicks": RT, "UserData": {"Played": (r.group(1), r.group(2)) in PLAYED}})
        if p == "/Sessions/Playing" or p == "/Sessions/Playing/Progress": return self.out(204)
        if p == "/Sessions/Playing/Stopped":
            user = {"tok-" + n: i for n, i in USERS.items()}.get(self.headers.get("X-Emby-Token"))
            if body.get("PositionTicks", 0) >= 0.9 * RT: set_played(user, body["ItemId"], True)
            return self.out(204)
        r = re.fullmatch(r"/Items/(\d+)", p)
        if r and m == "POST":
            x = PL[r.group(1)]; x["tags"] = [t["Name"] for t in body.get("TagItems", [])]; x["overview"] = body.get("Overview") or ""
            on_event(r.group(1)); return self.out(204)
        if r and m == "DELETE": PL.pop(r.group(1), None); return self.out(204)
        r = re.fullmatch(r"/Items/(\d+)/MakePublic", p)
        if r: PL[r.group(1)]["public"] = True; return self.out(204)
        return self.out(404, {"error": p})

http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
