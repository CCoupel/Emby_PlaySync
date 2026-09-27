#!/usr/bin/env python3
"""Faux Emby + faux moteur v0.2.0 (hors ligne) pour tester le flux de 20-etiquettes-retrait.sh.
Implémente les règles décrites par le plan (étiquettes à deux familles, première détection, grâce, retrait à la
transition non lu -> lu, journal/état Diagnostics, tâche planifiée). Ce n'est PAS le plugin : c'est une spécification
exécutable minimale qui vérifie que le script de test lit bien le contrat et enchaîne correctement les scénarios."""
import sys, os, json, re, threading, http.server, urllib.parse, itertools, datetime

def _load_help_texts():
    """Lit HelpText.cs sous REPO_ROOT (même mécanisme que I25, tests/integration/21-propagation.sh) :
    V1 = la constante contenant encore « fonction à venir » ; V2 = la même, cette ligne remplacée."""
    root = os.environ.get("REPO_ROOT", os.getcwd())
    path = os.path.join(root, "src", "EmbySharedPlaylist", "Reconciliation", "HelpText.cs")
    if not os.path.exists(path):
        return None, None
    src = open(path, encoding="utf-8").read()
    v1 = None
    for m in re.finditer(r"public const string \w+\s*=\s*(.*?);", src, re.S):
        segs = re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))
        if not segs: continue
        text = "".join(segs).replace("\\n", "\n")
        if "fonction à venir" in text:
            v1 = text; break
    if v1 is None:
        return None, None
    v2 = re.sub(r"(?m)^- propager-lu=OUI :.*$",
                 "- propager-lu=OUI : quand un média est lu par un membre, le flag « lu » est posé chez les autres.", v1)
    return v1, v2
V1_TEXT, V2_TEXT = _load_help_texts()

PORT = int(sys.argv[1]); MODE = sys.argv[2] if len(sys.argv) > 2 else "ok"   # ok | noremove | nopropagate (v0.3.0)
GRACE = 2
lock = threading.RLock()
ids = itertools.count(1000)
USERS = {"admin": "a" * 32, "cyril": "c" * 32, "user2": "b" * 32, "test_u1": "1" * 32, "test_u2": "2" * 32, "test_u3": "3" * 32}
if os.environ.get("FAKE_RESTRICTED_USER") == "1":   # v0.3.0 (I20/R8) : absent par défaut, n'affecte pas les tests v0.2.0 existants
    USERS["test_u_restricted"] = "4" * 32
MEDIA = [str(100 + i) for i in range(24)]
RT = 7_000_000_000
TASK_ID = "77"
pass_no = itertools.count(1)

PL, PLAYED, PLAYDATA, POLICY = {}, set(), {}, {}   # POLICY[userid] = {"EnableAllFolders": bool, "EnabledFolders": [...]}
DEFAULT_POLICY = {"EnableAllFolders": True, "EnabledFolders": []}
def has_access(userid, item): return POLICY.get(userid, DEFAULT_POLICY)["EnableAllFolders"]
def members(p): return list(dict.fromkeys([p["owner"]] + list(p["shares"].keys())))
def reset():   # redémarrage du PLUGIN uniquement : la mémoire du moteur est remise à zéro (Emby/PLAYED/PLAYDATA/POLICY persistent)
    global SEEN, GRACEC, JOURNAL, HANDLER, LASTPASS, WRITING, SKIPPED
    SEEN, GRACEC, JOURNAL, SKIPPED = set(), {}, [], {}
    HANDLER = {"Count": 0, "LastMs": 0, "MaxMs": 0}; LASTPASS = {"Ts": None, "DurationMs": 0, "PlaylistsSeen": 0, "SharedManaged": 0}; WRITING = False
reset()

NOISY = ("already-seen", "reentrant", "not-shared", "unknown-owner")
def jr(kind, pl=None, user=None, item=None, detail=None):
    if kind == "Skipped":
        SKIPPED[detail] = SKIPPED.get(detail, 0) + 1          # tous les Skipped sont comptés (Diagnostics/State.SkippedCounts)
        if detail in NOISY: return                            # ...mais les bruyants ne vont pas au journal
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

def maybe_replace_help(pid):   # #51 : Overview == V1 EXACT -> V2 (à la première détection ET à chaque passe)
    if V1_TEXT is None: return
    p = PL[pid]
    if p["overview"] == V1_TEXT:
        p["overview"] = V2_TEXT
        jr("DescriptionWritten", pid, detail="cause=help-v2")

def first_detection(pid):
    p = PL[pid]
    if pid in SEEN: jr("Skipped", pid, detail="already-seen"); return
    maybe_replace_help(pid)
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
        maybe_replace_help(pid)
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

def mark_played(user, item):   # écriture PLUGIN (propagation) : distincte d'un set_played utilisateur (pas de ré-entrée)
    PLAYED.add((user, item)); PLAYDATA[(user, item)] = {"LastPlayedDate": datetime.datetime.utcnow().isoformat() + "Z", "PlayCount": 1}

def transition(user, item):
    ms = 3
    for pid, p in list(PL.items()):
        es = [e for e in p["entries"] if e["item"] == item]
        if not es: continue
        if not member(p, user): jr("Skipped", pid, user, item, "not-member"); continue
        if not shared(p): jr("Skipped", pid, user, item, "not-shared"); continue
        rm_st = state_of(p["tags"], "remove-si-lu"); jr("MarkerSeen", pid, user, item, f"family=remove-si-lu state={rm_st}")
        if rm_st == "Oui" and MODE != "noremove":
            n = 0
            while n < 50:                         # toutes les entrées du média, une à la fois
                e = next((x for x in p["entries"] if x["item"] == item), None)
                if e is None: break
                p["entries"].remove(e); n += 1
            jr("Removal", pid, user, item, f"entries={n} durationMs={ms}"); jr("Skipped", pid, detail="already-seen")   # écho PlaylistItemsRemoved
        else:
            jr("Skipped", pid, user, item, "inactive")

        pr_st = state_of(p["tags"], "propager-lu")   # familles indépendantes : évaluée QUELLE QUE SOIT la décision remove-si-lu
        if pr_st == "Oui" and MODE != "nopropagate":
            propagated = already = noaccess = 0
            for m in members(p):
                if m == user: continue
                if not has_access(m, item):
                    jr("Skipped", pid, m, item, "no-access"); noaccess += 1; continue
                if (m, item) in PLAYED:
                    jr("Skipped", pid, m, item, "already-played"); already += 1; continue
                mark_played(m, item); propagated += 1
            total = len(members(p)) - 1
            jr("Propagation", pid, user, item, f"members={total} propagated={propagated} alreadyPlayed={already} noAccess={noaccess}")

        HANDLER["Count"] += 1; HANDLER["LastMs"] = ms; HANDLER["MaxMs"] = max(HANDLER["MaxMs"], ms)

def set_played(user, item, val):
    if val:
        if (user, item) in PLAYED: return
        mark_played(user, item); transition(user, item)
    else: PLAYED.discard((user, item)); PLAYDATA.pop((user, item), None)

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
        r = re.fullmatch(r"/Users/(\w+)/Policy", p)
        if r and m == "POST":
            pol = POLICY.setdefault(r.group(1), dict(DEFAULT_POLICY)); pol.update(body); return self.out(204)
        r = re.fullmatch(r"/Users/(\w+)", p)
        if r and m == "GET":
            pol = {"IsAdministrator": r.group(1) == USERS["admin"], "BlockedTags": ["x"]}
            if r.group(1) in POLICY: pol.update(POLICY[r.group(1)])   # champs EnableAllFolders/EnabledFolders : seulement si réglés (POST Policy),
            return self.out(200, {"Policy": pol})                     # pour ne pas changer la forme des réponses des comptes jamais touchés
        if p == "/Items" and m == "GET":
            if "Ids" in q: return self.out(200, {"Items": [self.dto(q["Ids"][0])] if q["Ids"][0] in PL else []})
            return self.out(200, {"Items": [{"Id": i, "Name": "M" + i, "RunTimeTicks": RT} for i in MEDIA]})
        if p == "/SharedPlaylist/Diagnostics/State":
            return self.out(200, {"SeenPlaylistIds": sorted(SEEN), "GraceCounters": GRACEC, "LastPass": LASTPASS, "Handler": HANDLER, "SkippedCounts": SKIPPED, "GracePasses": GRACE})
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
            if not has_access(r.group(1), r.group(2)):
                return self.out(403, {"error": "no library access (fake R8)"})
            key = (r.group(1), r.group(2)); pd = PLAYDATA.get(key, {"LastPlayedDate": None, "PlayCount": 0})
            return self.out(200, {"Id": r.group(2), "RunTimeTicks": RT,
                                   "UserData": {"Played": key in PLAYED, "LastPlayedDate": pd["LastPlayedDate"], "PlayCount": pd["PlayCount"]}})
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
