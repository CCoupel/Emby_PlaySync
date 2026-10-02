#!/usr/bin/env python3
"""Faux Emby + faux moteur (hors ligne) pour tester le flux des scripts d'intégration (20 à 25).
Implémente les règles décrites par le plan (étiquettes, première détection, grâce, retrait à la transition non lu -> lu,
journal/état Diagnostics, tâche planifiée). v1.2.0 (#56, #57, D21) : TROIS familles (remove-si-lu, propager-lu,
propager-avancement, sans héritage) ; tableau A (le retrait exige propager-lu=OUI ; propager-lu ne propage que le flag lu) et
tableau B (la position ne dépend que de propager-avancement, AUCUNE garde sur l'état lu) indépendants ; HelpText V1/V2 -> V3.
Ce n'est PAS le plugin : c'est une spécification exécutable minimale qui vérifie que le script de test lit bien le contrat et
enchaîne correctement les scénarios. MODES (détection de défauts, tests hors ligne « moteur défaillant ») :
ok | staleentry (défaut v1.2.1 : retire le média voisin, I14c) | noremove | nopropagate (ni lu ni position) | noautoshare | repose | retraitseul (défaut v0.2.0-v1.1.0 : retrait SANS propager-lu) |
dcguard (défaut : garde D-c « déclencheur déjà lu » de v0.3.1) | legacyavancement (défaut : propager-lu couvre la position) |
nativeplayed (Emby pose le lu chez un membre après une position >= 90 %, origine plugin : bénin) |
nativeleak (idem mais pris pour une action utilisateur : violation de S6) |
v120 (défaut v1.2.0 du bug #58 : aucune propagation sur un Progress en lecture, fin de lecture sans cibles mémorisées).
v1.2.1 (#58) : propagation PÉRIODIQUE sur Progress (intervalle FAKE_MIN_INTERVAL, 10 s par défaut), règle de fin de lecture
(0 si propager-lu=OUI), cibles mémorisées à l'ouverture de session, Progress tardif ignoré, compteurs PositionProgress."""
import sys, os, json, re, threading, http.server, urllib.parse, itertools, datetime

def _load_help_texts():
    """Lit HelpText.cs sous REPO_ROOT (même mécanisme que I25, tests/integration/21-propagation.sh) :
    V1 = la constante contenant encore « fonction à venir » ; V2 = celle contenant « aussi propagé » ; V3 (v1.2.0) = celle
    contenant « Trois étiquettes ». Une constante introuvable vaut None (V3 absent : #56 non livré dans la copie testée)."""
    root = os.environ.get("REPO_ROOT", os.getcwd())
    path = os.path.join(root, "src", "EmbySharedPlaylist", "Reconciliation", "HelpText.cs")
    if not os.path.exists(path):
        return None, None, None
    src = open(path, encoding="utf-8").read()
    found = {"fonction à venir": None, "aussi propagé": None, "Trois étiquettes": None}
    # « ; » possible DANS un littéral (V2) : le corps d'une constante n'est fait que de littéraux, « + » et espaces
    for m in re.finditer(r'public const string \w+\s*=\s*((?:"(?:[^"\\]|\\.)*"|\s|\+)+);', src, re.S):
        segs = re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))
        if not segs: continue
        text = "".join(segs).replace("\\n", "\n")
        for marker in found:
            if found[marker] is None and marker in text: found[marker] = text
    return found["fonction à venir"], found["aussi propagé"], found["Trois étiquettes"]
V1_TEXT, V2_TEXT, V3_TEXT = _load_help_texts()
FAMS = ("remove-si-lu", "propager-lu", "propager-avancement")   # v1.2.0 : trois familles (ordre de pose de DefaultsService)

import os, time
MIN_INTERVAL = float(os.environ.get("FAKE_MIN_INTERVAL", "10"))   # minuterie de propagation périodique (miroir de PlaybackSyncTracker.MinInterval)
PORT = int(sys.argv[1]); MODE = sys.argv[2] if len(sys.argv) > 2 else "ok"   # ok | noremove | nopropagate | noautoshare (v0.4.0)
GRACE = 2
PLUGIN_ID = "9ebe814e-9438-42b8-aa57-feea1ae92451"
CONFIG = {"GracePasses": GRACE, "EnableDiagnostics": True, "LogToConsole": True, "LogLevel": "Info", "AutoEnableSharing": True}
lock = threading.RLock()
ids = itertools.count(1000)
USERS = {"admin": "a" * 32, "cyril": "c" * 32, "user2": "b" * 32, "test_u1": "1" * 32, "test_u2": "2" * 32, "test_u3": "3" * 32}
PASSWORDS = {}   # userid -> mot de passe courant (créés dynamiquement via /Users/New, comme test_u4/R8 en v0.3.0)
MEDIA = [str(100 + i) for i in range(24)]
RT = 7_000_000_000
TASK_ID = "77"
pass_no = itertools.count(1)

LIB_ID = "7777"
if os.environ.get("FAKE_NO_TESTUSERS") == "1":   # 00-setup-users.sh : les comptes test_* n'existent pas encore
    for _n in ("test_u1", "test_u2", "test_u3"): USERS.pop(_n, None)
SESSIONS = {}   # (user, PlaySessionId) -> item : sessions de lecture ouvertes (#61)
PL, PLAYED, PLAYDATA, POLICY, POSITION = {}, set(), {}, {}, {}   # POSITION[(user,item)] = ticks (donnée Emby, persiste)
TICKS_30S = 300_000_000   # v0.3.1 : seuil minimal (30 s, 100 ns/tick)
DEFAULT_POLICY = {"EnableAllFolders": True, "EnabledFolders": [], "AllowSharingPersonalItems": False}
def has_access(userid, item):
    # D-d (v0.5.0, #31) : un média inexistant (FindItem -> null côté vrai plugin) est traité EXACTEMENT comme un
    # défaut d'accès (R8), aucune distinction — item ajouté à une playlist mais absent de MEDIA (simulation I44).
    if item not in MEDIA: return False
    return {**DEFAULT_POLICY, **POLICY.get(userid, {})}["EnableAllFolders"]
def members(p): return list(dict.fromkeys([p["owner"]] + [u for u, lvl in p["shares"].items() if lvl != "None"]))
def reset():   # redémarrage du PLUGIN uniquement : la mémoire du moteur est remise à zéro (Emby/PLAYED/PLAYDATA/POLICY/POSITION persistent)
    global SEEN, GRACEC, JOURNAL, HANDLER, LASTPASS, WRITING, SKIPPED, PAUSED, SYNC, POSPROG
    SEEN, GRACEC, JOURNAL, SKIPPED, PAUSED, SYNC = set(), {}, [], {}, {}, {}
    POSPROG = {"Propagated": 0, "Throttled": 0, "LockBusy": 0}
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

def member(p, u): return u == p["owner"] or p["shares"].get(u, "None") != "None"

HELP = V3_TEXT or ("Playlist partagée gérée par Emby Shared Playlist.\n- propager-lu=OUI : lu.\n- remove-si-lu=OUI : retrait (seulement si propager-lu=OUI).\n"
                   "- propager-avancement=OUI : position.")

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

def maybe_replace_help(pid):   # #51 puis v1.2.0 : Overview == V1 ou V2 EXACT -> V3 (première détection ET chaque passe)
    if V3_TEXT is None: return
    p = PL[pid]
    if V1_TEXT is not None and p["overview"] == V1_TEXT:
        p["overview"] = V3_TEXT
        jr("DescriptionWritten", pid, detail="cause=v1-to-v3")
    elif V2_TEXT is not None and p["overview"] == V2_TEXT:
        p["overview"] = V3_TEXT
        jr("DescriptionWritten", pid, detail="cause=v2-to-v3")

def first_detection(pid):
    p = PL[pid]
    if pid in SEEN: jr("Skipped", pid, detail="already-seen"); return
    maybe_replace_help(pid)
    SEEN.add(pid)
    fams = [f for f in FAMS if state_of(p["tags"], f) == "None"]
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
        for f in FAMS:
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
    # AutoSharingService (v0.4.0, #26, D-a) : APRÈS le retrait/la propagation, indépendant des playlists (tourne même
    # s'il n'y en a aucune) ; interrupteur relu à chaque passe (config live) ; jamais de révocation (D-e : un compte
    # déjà à True le reste, un décochage manuel est reposé à la passe suivante, testé comme un succès).
    if CONFIG.get("AutoEnableSharing", True) and MODE != "noautoshare":
        enabled = already = 0
        for uid in list(USERS.values()):
            # {} et non dict(DEFAULT_POLICY) : EnableSharingIfNeeded (D-b) ne modifie QUE ce champ, jamais les autres
            # (matérialiser EnableAllFolders/EnabledFolders ici romprait la comparaison compare_protected des comptes
            # protégés jamais autrement touchés — même écueil que la revue A1 sur #20/#21, ne modifier QUE le nécessaire).
            pol = POLICY.setdefault(uid, {})
            if pol.get("AllowSharingPersonalItems"):
                already += 1
            else:
                pol["AllowSharingPersonalItems"] = True
                enabled += 1
                jr("PermissionPosed", user=uid)
        jr("PermissionPass", detail=f"users={len(USERS)} enabled={enabled} alreadyEnabled={already} durationMs=1")

def mark_played(user, item):   # écriture PLUGIN (propagation) : distincte d'un set_played utilisateur (pas de ré-entrée)
    PLAYED.add((user, item)); PLAYDATA[(user, item)] = {"LastPlayedDate": datetime.datetime.utcnow().isoformat() + "Z", "PlayCount": 1}

def native_played_on_position_write(m, item, ticks):
    """Modes nativeplayed/nativeleak (spike U14b simulé) : après une position brute écrite par le PLUGIN à >= 90 % de la durée,
    Emby poserait lui-même le « lu » chez le membre. nativeplayed : origine plugin (écho consommé, AUCUN effet moteur, S6
    préservé) ; nativeleak : pris pour une action utilisateur (violation de S6 : le tableau A s'applique dans TOUTES les listes)."""
    if MODE not in ("nativeplayed", "nativeleak") or ticks < 0.9 * RT: return
    PLAYED.add((m, item))
    if MODE == "nativeleak": transition(m, item)

def transition(user, item):
    ms = 3
    for pid, p in list(PL.items()):
        es = [e for e in p["entries"] if e["item"] == item]
        if not es: continue
        if not member(p, user): jr("Skipped", pid, user, item, "not-member"); continue
        if not shared(p): jr("Skipped", pid, user, item, "not-shared"); continue
        rm_st = state_of(p["tags"], "remove-si-lu"); jr("MarkerSeen", pid, user, item, f"family=remove-si-lu state={rm_st}")
        pr_st = state_of(p["tags"], "propager-lu"); jr("MarkerSeen", pid, user, item, f"family=propager-lu state={pr_st}")
        # Tableau A (v1.2.0, D21, R4a subordonnée) : retrait seulement si remove-si-lu ET propager-lu sont OUI (mode retraitseul :
        # comportement défaillant v0.2.0-v1.1.0, remove-si-lu suffit).
        removal_on = rm_st == "Oui" and (pr_st == "Oui" or MODE == "retraitseul") and MODE != "noremove"
        if removal_on:
            n = 0
            while n < 50:                         # toutes les entrées du média, une à la fois
                e = next((x for x in p["entries"] if x["item"] == item), None)
                if e is None: break
                p["entries"].remove(e); n += 1
            if MODE == "staleentry" and p["entries"]:
                # défaut v1.2.1 (rafraîchissement d'Emby : identifiants d'entrée renumérotés entre lecture et écriture) : l'identifiant périmé
                # désigne le média voisin, supprimé À LA PLACE (entries=2 pour un média présent une fois ; I14c / I14.integrity KO)
                other = next((x for x in reversed(p["entries"]) if x["item"] != item), None)   # voisin décalé : ici la DERNIÈRE entrée d'un autre média (non lue dans I14c)
                if other is not None: p["entries"].remove(other); n += 1
            for x in p["entries"]: x["pid"] = str(next(ids))   # le rafraîchissement d'Emby réattribue les identifiants d'entrée (I14c)
            jr("Removal", pid, user, item, f"entries={n} durationMs={ms}"); jr("Skipped", pid, detail="already-seen")   # écho PlaylistItemsRemoved
        elif pr_st != "Oui":
            jr("Skipped", pid, user, item, "inactive")   # « inactive » seulement si la propagation du lu n'agit pas non plus

        if pr_st == "Oui" and MODE != "nopropagate":   # propagation du FLAG lu seul, indépendante de remove-si-lu (jamais de position)
            propagated = already = noaccess = 0
            for m in members(p):
                if m == user: continue
                if not has_access(m, item):
                    jr("Skipped", pid, m, item, "no-access"); noaccess += 1; continue
                if (m, item) in PLAYED:
                    jr("Skipped", pid, m, item, "already-played"); already += 1; continue
                mark_played(m, item); propagated += 1
                jr("Skipped", detail="echo-consumed")   # anti-écho : l'écho de CETTE écriture (UserDataSaved du membre) est consommé
            total = len(members(p)) - 1
            jr("Propagation", pid, user, item, f"members={total} propagated={propagated} alreadyPlayed={already} noAccess={noaccess}")

        HANDLER["Count"] += 1; HANDLER["LastMs"] = ms; HANDLER["MaxMs"] = max(HANDLER["MaxMs"], ms)

def open_session(user, item, sid):
    """PlaybackStart / 1er Progress d'un nouveau PlaySessionId : cibles = playlists gérées contenant le média (mémorisées, v1.2.1)."""
    targets = [pid for pid, p in PL.items() if user in members(p) and shared(p) and item in [e["item"] for e in p["entries"]]]
    SYNC[(user, item)] = {"sid": sid, "last": None, "closed": False, "targets": targets}
    return SYNC[(user, item)]

def position_transition(user, item, ticks, trigger="stop", targets=None):
    # Tableau B (v1.2.0, D21) : seuil minimal 30 s = POSITION ABSOLUE ; famille propager-avancement seule ; AUCUNE garde sur
    # l'état lu (#57). Mode dcguard : garde D-c de v0.3.1 (déclencheur déjà lu => rien) ; legacyavancement : propager-lu suffit.
    # v1.2.1 (#58) : trigger = periodic | pause | stop | completion ; periodic : silencieux (compteurs, aucun journal) ;
    # completion : 0 si propager-lu=OUI (seuil non appliqué), sinon position brute (seuil appliqué) ; `targets` = playlists
    # mémorisées (réunies aux playlists contenant encore le média) — mode v120 : playlists contenant encore le média SEULES.
    if trigger != "completion" and ticks < TICKS_30S: return 0
    if MODE == "dcguard" and (user, item) in PLAYED: return 0
    fam = "propager-lu" if MODE == "legacyavancement" else "propager-avancement"
    quiet = trigger == "periodic"
    pids = [pid for pid, p in PL.items() if user in members(p) and item in [e["item"] for e in p["entries"]]]
    if trigger == "completion" and MODE != "v120":
        pids += [t for t in (targets or []) if t in PL and t not in pids]
    done = 0
    for pid in pids:
        p = PL[pid]
        if not shared(p): continue
        if state_of(p["tags"], fam) != "Oui" or MODE == "nopropagate":
            if not quiet: jr("Skipped", pid, user, item, "inactive")
            continue
        write = ticks
        if trigger == "completion":
            if state_of(p["tags"], "propager-lu") == "Oui": write = 0
            elif ticks < TICKS_30S:
                jr("Skipped", pid, user, item, "too-short"); continue
        propagated = already = noaccess = 0
        for m in members(p):
            if m == user: continue
            if not has_access(m, item):
                if not quiet: jr("Skipped", pid, m, item, "no-access")
                noaccess += 1; continue
            if POSITION.get((m, item), 0) == write:
                if not quiet: jr("Skipped", pid, m, item, "same-position")
                already += 1; continue
            POSITION[(m, item)] = write; propagated += 1        # position BRUTE (ou 0 en fin de lecture) : ni Played ni PlayCount
            native_played_on_position_write(m, item, write)
        done += propagated
        total = len(members(p)) - 1
        if not quiet:
            jr("PositionPropagation", pid, user, item, f"members={total} propagated={propagated} samePosition={already} noAccess={noaccess} durationMs=3 trigger={trigger}")
    return done

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
        if p == "/Users/New" and m == "POST":
            name = body["Name"]
            uid = ("u" + str(next(ids))).ljust(32, "0")[:32]   # id factice unique, longueur 32 comme un vrai GUID sans tirets
            USERS[name] = uid
            # UserCreated (v0.4.0, #26) : pose immédiate, SANS attendre la passe suivante — simule Emby/UserPolicyListener.
            if CONFIG.get("AutoEnableSharing", True) and MODE != "noautoshare":
                pol = POLICY.setdefault(uid, {}); pol["AllowSharingPersonalItems"] = True
                jr("PermissionPosed", user=uid)
            return self.out(200, {"Id": uid, "Name": name})
        if p == f"/Plugins/{PLUGIN_ID}/Configuration" and m == "GET": return self.out(200, dict(CONFIG))
        if p == f"/Plugins/{PLUGIN_ID}/Configuration" and m == "POST": CONFIG.update(body); return self.out(204)
        r = re.fullmatch(r"/Users/(\w+)/Password", p)
        if r and m == "POST": PASSWORDS[r.group(1)] = body.get("NewPw"); return self.out(204)
        r = re.fullmatch(r"/Users/(\w+)", p)
        if r and m == "DELETE":
            name = next((n for n, i in USERS.items() if i == r.group(1)), None)
            if name: del USERS[name]
            POLICY.pop(r.group(1), None); PASSWORDS.pop(r.group(1), None)
            return self.out(204)
        r = re.fullmatch(r"/Users/(\w+)/Policy", p)
        if r and m == "POST":
            pol = POLICY.setdefault(r.group(1), {} if os.environ.get("FAKE_NO_TESTUSERS") == "1" else dict(DEFAULT_POLICY)); pol.update(body); return self.out(204)
        r = re.fullmatch(r"/Users/(\w+)", p)
        if r and m == "GET":
            # AllowSharingPersonalItems (v0.4.0, #26) : présent par défaut à False pour TOUT compte (comme un vrai
            # Emby fraîchement créé), contrairement à EnableAllFolders/EnabledFolders qui restent ABSENTS tant que
            # /Policy n'a jamais été posté (forme volontairement différente, cf. commentaire ci-dessous, R8).
            pol = {"IsAdministrator": r.group(1) == USERS["admin"], "BlockedTags": ["x"], "AllowSharingPersonalItems": False}
            if r.group(1) in POLICY: pol.update(POLICY[r.group(1)])   # champs EnableAllFolders/EnabledFolders : seulement si réglés (POST Policy),
            return self.out(200, {"Policy": pol})                     # pour ne pas changer la forme des réponses des comptes jamais touchés
        if p == "/Items" and m == "GET":
            if "Ids" in q: return self.out(200, {"Items": [self.dto(q["Ids"][0])] if q["Ids"][0] in PL else []})
            root = "/config/virtual/Alphi/" if os.environ.get("FAKE_VIRTUAL") == "1" else "/config/test-media/"   # #61 : FAKE_VIRTUAL=1 => médias VirtualLib (.strm)
            ext = ".strm" if os.environ.get("FAKE_VIRTUAL") == "1" else ".mp4"
            if "ParentId" in q and q["ParentId"][0] != LIB_ID: return self.out(200, {"Items": []})
            return self.out(200, {"Items": [{"Id": i, "Name": "M" + i, "RunTimeTicks": RT, "Path": root + "PlaySync-Test-" + i + ext} for i in MEDIA]})
        if p == "/Library/VirtualFolders" and m == "GET":
            return self.out(200, [{"Name": "Listes de lecture", "ItemId": "9"}, {"Name": "PlaySync-Tests", "ItemId": LIB_ID, "Locations": ["/config/test-media"]}])
        if p == "/Sessions" and m == "GET":
            return self.out(200, [{"UserName": n, "UserId": u, "NowPlayingItem": {"Id": it}} for (u, sid), it in SESSIONS.items() for n, uu in USERS.items() if uu == u])
        if p == "/Sessions/Logout" and m == "POST":
            user = {"tok-" + n: i for n, i in USERS.items()}.get(self.headers.get("X-Emby-Token"))
            for k in [k for k in SESSIONS if k[0] == user]: SESSIONS.pop(k)
            return self.out(204)
        if p == "/SharedPlaylist/Diagnostics/State":
            return self.out(200, {"SeenPlaylistIds": sorted(SEEN), "GraceCounters": GRACEC, "LastPass": LASTPASS, "Handler": HANDLER, "SkippedCounts": SKIPPED, "GracePasses": GRACE, "PositionProgress": POSPROG})
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
        r = re.fullmatch(r"/Playlists/(\d+)/Items/Delete", p)
        if r and m == "POST":
            x = PL[r.group(1)]; eids = set(q.get("EntryIds", [""])[0].split(","))
            x["entries"] = [e for e in x["entries"] if e["pid"] not in eids]
            on_event(r.group(1)); return self.out(204)
        r = re.fullmatch(r"/Playlists/(\d+)/Items", p)
        if r:
            x = PL[r.group(1)]
            if m == "GET": return self.out(200, {"Items": [{"Id": e["item"], "PlaylistItemId": e["pid"]} for e in x["entries"]]})
            for i in q["Ids"][0].split(","): x["entries"].append({"pid": str(next(ids)), "item": i})
            on_event(r.group(1)); return self.out(200, {})
        r = re.fullmatch(r"/Users/(\w+)/PlayedItems/(\d+)", p)
        if r:
            set_played(r.group(1), r.group(2), m == "POST"); return self.out(200, {})
        r = re.fullmatch(r"/Users/(\w+)/Items", p)
        if r and m == "GET":   # liste des playlists visibles d'un compte (purge SPIKE résiduelles, #60)
            if "Playlist" not in q.get("IncludeItemTypes", [""])[0]: return self.out(200, {"Items": []})
            return self.out(200, {"Items": [{"Id": pid, "Name": x["name"]} for pid, x in PL.items() if member(x, r.group(1))]})
        r = re.fullmatch(r"/Users/(\w+)/Items/(\d+)", p)
        if r and m == "GET":
            if r.group(2) in PL: return self.out(200, self.dto(r.group(2)))
            if not has_access(r.group(1), r.group(2)):
                return self.out(403, {"error": "no library access (fake R8)"})
            key = (r.group(1), r.group(2)); pd = PLAYDATA.get(key, {"LastPlayedDate": None, "PlayCount": 0})
            return self.out(200, {"Id": r.group(2), "RunTimeTicks": RT,
                                   "UserData": {"Played": key in PLAYED, "LastPlayedDate": pd["LastPlayedDate"], "PlayCount": pd["PlayCount"],
                                                "PlaybackPositionTicks": POSITION.get(key, 0)}})
        if p == "/Sessions/Playing":
            user = {"tok-" + n: i for n, i in USERS.items()}.get(self.headers.get("X-Emby-Token"))
            SESSIONS[(user, body.get("PlaySessionId", ""))] = body["ItemId"]
            open_session(user, body.get("ItemId"), body.get("PlaySessionId"))
            PAUSED.pop((user, body.get("ItemId")), None)
            return self.out(204)
        if p == "/Sessions/Playing/Progress":
            user = {"tok-" + n: i for n, i in USERS.items()}.get(self.headers.get("X-Emby-Token"))
            item = body["ItemId"]; paused = bool(body.get("IsPaused", False)); sid = body.get("PlaySessionId"); ticks = body.get("PositionTicks", 0)
            SESSIONS[(user, sid)] = item   # un Progress (même tardif) rouvre une session visible dans /Sessions, comme Emby (#61, I44)
            s = SYNC.get((user, item))
            if s and s["closed"] and s["sid"] == sid: return self.out(204)       # Progress tardif d'une session arrêtée : ignoré (CA6)
            if not s or s["closed"] or s["sid"] != sid: s = open_session(user, item, sid)
            was_paused = PAUSED.get((user, item), False)
            PAUSED[(user, item)] = paused
            if paused:
                if not was_paused:   # transition false -> true : propagation IMMÉDIATE (inchangée)
                    if position_transition(user, item, ticks, "pause", s["targets"]) is not None: s["last"] = time.time()
            elif MODE != "v120":     # v1.2.1 : chaque Progress en lecture est candidat, au plus 1 propagation / MIN_INTERVAL
                if s["last"] is not None and time.time() - s["last"] < MIN_INTERVAL: POSPROG["Throttled"] += 1
                else:
                    if ticks >= TICKS_30S and position_transition(user, item, ticks, "periodic", s["targets"]) > 0: POSPROG["Propagated"] += 1
                    if ticks >= TICKS_30S: s["last"] = time.time()
            return self.out(204)
        if p == "/Sessions/Playing/Stopped":
            user = {"tok-" + n: i for n, i in USERS.items()}.get(self.headers.get("X-Emby-Token"))
            SESSIONS.pop((user, body.get("PlaySessionId", "")), None)
            item = body["ItemId"]; ticks = body.get("PositionTicks", 0)
            s = SYNC.get((user, item)); targets = s["targets"] if s else []
            if s: s["closed"] = True
            if ticks >= 0.9 * RT:
                # Fin de lecture, séquence d'Emby (plan §2) : UserData du déclencheur (lu, position 0) -> flux du lu IMMÉDIAT
                # (propagation du flag, retrait) -> PUIS PlaybackStopped (PlayedToCompletion) vers le flux de l'avancement.
                POSITION[(user, item)] = 0
                set_played(user, item, True)
                position_transition(user, item, ticks, "completion", targets)
            else:
                POSITION[(user, item)] = ticks       # la position du lecteur lui-même (donnée d'Emby, pas du plugin)
                position_transition(user, item, ticks, "stop", targets)   # systématique (D-b)
            PAUSED.pop((user, item), None)
            return self.out(204)
        r = re.fullmatch(r"/Items/(\d+)", p)
        if r and m == "POST":
            x = PL[r.group(1)]; x["tags"] = [t["Name"] for t in body.get("TagItems", [])]; x["overview"] = body.get("Overview") or ""
            on_event(r.group(1)); return self.out(204)
        if r and m == "DELETE": PL.pop(r.group(1), None); return self.out(204)
        r = re.fullmatch(r"/Items/(\d+)/MakePublic", p)
        if r: PL[r.group(1)]["public"] = True; return self.out(204)
        return self.out(404, {"error": p})

if os.environ.get("FAKE_STALE") == "1":   # #60 : playlist SPIKE-I17 OUI/OUI résiduelle (non supprimée par un run précédent de 20), membre test_u3
    _pid = str(next(ids))
    PL[_pid] = {"name": "SPIKE-I17", "owner": USERS["test_u1"], "shares": {USERS["test_u2"]: "Write", USERS["test_u3"]: "Read"},
                "tags": ["remove-si-lu=OUI", "propager-lu=OUI", "propager-avancement=NON"], "overview": "x", "public": False,
                "entries": [{"pid": str(next(ids)), "item": MEDIA[0]}, {"pid": str(next(ids)), "item": MEDIA[1]}]}
    SEEN.add(_pid)

http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
