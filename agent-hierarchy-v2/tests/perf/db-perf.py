#!/usr/bin/env python3
"""ah2 state-DB perf harness: message ops (CLI + daemon), UI read routes, scale, concurrency, startup, file-vs-db.

Runs a private ahd in a /tmp/ahprobe.* sandbox seeded with a read-only copy of the live msg_* rows (paths rewritten into
the sandbox), then grows the data by cloning it. Never touches the real AH_HOME or its daemon. See README.md.
"""
import argparse, http.client, json, multiprocessing as mp, os, re, shutil, signal, socket, sqlite3, subprocess, sys, tempfile
import threading, time, urllib.parse

PLUGIN = os.path.realpath(os.path.join(os.path.dirname(__file__), "..", ".."))
AH = os.path.join(PLUGIN, "bin", "ah")
PROVE = os.path.join(PLUGIN, "tests", "contract-tools", "prove-self.mjs")
G = {}  # sandbox globals: T, HOME, ENV, REPO, TF, POOL


def pct(xs, p):
    if not xs:
        return None
    s = sorted(xs)
    return round(s[min(len(s) - 1, max(0, int(round(p / 100 * len(s) + 0.5)) - 1))], 1)


def stats(xs):
    return {"n": len(xs), "p50": pct(xs, 50), "p95": pct(xs, 95), "p99": pct(xs, 99), "max": round(max(xs), 1) if xs else None}


# ---------------------------------------------------------------- process + http helpers

def prove(name):
    subprocess.run(["node", PROVE, name, "tm", str(os.getpid()), G["ENV"]["AH_HOME"], G["TF"],
                    f'{G["ENV"]["AH_HOME"]}/boxes/{name}@tm'], env=G["ENV"], check=True, capture_output=True)


def ah(*args, env_extra=None, timeout=600):
    """(ms, rc, stdout, stderr) of one `ah` call, with phase spans on stderr; a call past the timeout is rc 124."""
    env = dict(G["ENV"], AH_TRACE_SPANS="1", **(env_extra or {}))
    t0 = time.perf_counter()
    try:
        p = subprocess.run([AH, *args], cwd=G["REPO"], env=env, capture_output=True, text=True, timeout=timeout)
        rc, out, err = p.returncode, p.stdout, p.stderr
    except subprocess.TimeoutExpired:
        rc, out, err = 124, "", "timeout"
    return (time.perf_counter() - t0) * 1000, rc, out, err


SPAN = re.compile(r"^ah-trace (\S+) ([\d.]+)$", re.M)


class UConn(http.client.HTTPConnection):
    def __init__(self, path, timeout):
        super().__init__("ahd", timeout=timeout)
        self._p = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self._p)


def get(path, timeout=120):
    """(ms, status, body); a request that outlives the timeout is (timeout ms, 0, b"")."""
    c = UConn(G["ENV"]["AH_HOME"] + "/run/ahd.sock", timeout)
    t0 = time.perf_counter()
    try:
        c.request("GET", path)
        r = c.getresponse()
        body, st = r.read(), r.status
    except TimeoutError:
        body, st = b"", 0
    ms = (time.perf_counter() - t0) * 1000
    c.close()
    return ms, st, body


def post(path, obj):
    c = UConn(G["ENV"]["AH_HOME"] + "/run/ahd.sock", 120)
    c.request("POST", path, body=json.dumps(obj), headers={"content-type": "application/json"})
    r = c.getresponse()
    b = r.read()
    c.close()
    return r.status, b


def ctx():
    return {"cwd": G["REPO"], "hierarchy_dir": G["POOL"]}


def state_q(domain, op, args):
    q = urllib.parse.urlencode({"args": json.dumps(args), **ctx()})
    return f"/v1/state/{domain}/{op}?{q}"


def daemon_pid():
    try:
        return json.load(open(G["ENV"]["AH_HOME"] + "/run/ahd.json"))["pid"]
    except Exception:
        return None


def sandbox_ahd_pids():
    out = subprocess.run(["pgrep", "-f", f'--ah-home {G["ENV"]["AH_HOME"]}'], capture_output=True, text=True).stdout
    return [int(x) for x in out.split()]


def stop_daemon():
    ah("msg", "daemon", "stop")
    for _ in range(50):
        if not sandbox_ahd_pids():
            return
        time.sleep(0.2)
    for sig in (signal.SIGTERM, signal.SIGKILL):
        for p in sandbox_ahd_pids():
            try:
                os.kill(p, sig)
            except ProcessLookupError:
                pass
        time.sleep(2)


def start_daemon():
    """Cold start: CLI `msg daemon start` wall time, then time until /v1/health and the first /v1/view answer."""
    t0 = time.perf_counter()
    ms, rc, out, err = ah("msg", "daemon", "start")
    # a daemon that migrates a big database (inject's schema-8 copy) outlasts the CLI's wait; it is still coming up
    assert rc == 0 or ("daemon_unavailable" in err and "refusing to start" not in err), err
    health = None
    while health is None:
        assert time.perf_counter() - t0 < 3600, "ahd did not answer /v1/health within an hour"
        try:
            hms, st, _ = get("/v1/health")
            if st == 200:
                health = (time.perf_counter() - t0) * 1000
        except OSError:
            time.sleep(0.02)
    vms, st, _ = get("/v1/view", timeout=600)
    return {"cli_start_ms": round(ms, 1), "to_health_ms": round(health, 1), "first_view_ms": round(vms, 1), "first_view_status": st,
            "to_first_view_ms": round((time.perf_counter() - t0) * 1000, 1)}


# ---------------------------------------------------------------- sandbox + data

def live_pools(live):
    db = sqlite3.connect(f"file:{live}?mode=ro", uri=True)
    rows = db.execute("SELECT pool_dir, count(*) FROM msg_messages GROUP BY 1 ORDER BY 2 DESC").fetchall()
    db.close()
    return rows


def setup(live_copy, pools):
    # AH_HOME must be /tmp/ahprobe.* (test mode), and a test-mode daemon refuses to start on a path outside it, so the pools go there too.
    # Every message path is resolved part by part (PathGuard.Resolve -> OnDiskCase lists each parent directory): a crowded /tmp adds to each number.
    t = tempfile.mkdtemp(prefix="ahprobe.", dir="/tmp")
    while not re.fullmatch(r"ahprobe\.[A-Za-z0-9]+", os.path.basename(t)):  # the test-mode predicate takes alphanumerics only
        os.rmdir(t)
        t = tempfile.mkdtemp(prefix="ahprobe.", dir="/tmp")
    t = os.path.realpath(t)
    G["T"] = t
    home = os.path.join(t, "h")
    G["PR"] = os.path.join(t, "pool")
    prefix = os.path.commonpath([os.path.dirname(os.path.dirname(os.path.dirname(p))) for p, _ in pools])
    G["LIVE_PREFIX"] = prefix.rstrip("/") + "/"
    G["NEW_PREFIX"] = os.path.join(G["PR"], "r") + "/"
    biggest = pools[0][0]
    G["POOL"] = G["NEW_PREFIX"] + biggest[len(G["LIVE_PREFIX"]):]
    G["REPO"] = os.path.dirname(os.path.dirname(G["POOL"]))
    G["TF"] = os.path.join(G["POOL"], "teams", "tm.json")
    env = {k: v for k, v in os.environ.items() if not k.startswith(("AH_", "AHD_", "CLAUDE", "HERDR", "TMUX", "AGENT_HIERARCHY"))}
    env.update(AH_HOME=home, HOME=os.path.join(t, "home"), AH_TEST="1", AH_BIN=G["AHD"])
    G["ENV"] = env
    os.makedirs(os.path.join(t, "home", ".claude", "ah2"))
    os.makedirs(os.path.join(G["POOL"], "teams"))
    subprocess.run(["git", "-C", G["REPO"], "init", "-q"], check=True)
    subprocess.run(["git", "-C", G["REPO"], "-c", "user.email=t@example.test", "-c", "user.name=t", "commit", "-q",
                    "--allow-empty", "-m", "init"], check=True)
    members = [{"role": "implementor", "name": "tm-implementor", "transport_id": "p-tm-implementor"},
               {"role": "reviewer", "name": "tm-reviewer", "transport_id": "p-tm-reviewer"}]
    members += [{"role": "implementor", "name": f"tm-w{i}", "transport_id": f"p-tm-w{i}"} for i in range(1, 33)]
    json.dump({"team_id": "t-tm", "orchestrator": {"pid": 1, "session_id": None}, "members": members}, open(G["TF"], "w"))
    for p, _ in pools:
        os.makedirs(os.path.join(G["NEW_PREFIX"] + p[len(G["LIVE_PREFIX"]):], "msgs"), exist_ok=True)
    G["BODY"] = os.path.join(t, "body.md")
    open(G["BODY"], "w").write("Bench message body: do the measured thing and report the numbers back.\n" * 20)
    prove("orchestrator")
    start_daemon()
    for p, _ in pools:
        d = G["NEW_PREFIX"] + p[len(G["LIVE_PREFIX"]):]
        post("/v1/state/known-dirs/note", {"args": {"hierarchy_dir": d}, "context": ctx(), "op_id": "kd-" + d})
    # a new home's mode is db; the bench pools get their rows from inject()
    stop_daemon()


def cols(db, table, schema="main"):
    return [r[1] for r in db.execute(f"PRAGMA {schema}.table_info({table})")]


def inject(live_copy, clones):
    """Adds the live msg_* rows once per clone number (0 = the rows as they are), pks offset, paths moved into the
    sandbox, ids re-dated for clones > 0 so they stay valid message ids; writes each message's file from its body."""
    db = sqlite3.connect(G["ENV"]["AH_HOME"] + "/state/ah.db")
    db.execute(f"ATTACH 'file:{live_copy}?mode=ro' AS live")
    lp, np = G["LIVE_PREFIX"], G["NEW_PREFIX"]
    files = 0
    for c in clones:
        off = (c + 1) * 10_000_000
        nid = (lambda col: col) if c == 0 else (lambda col: f"printf('%04d', 2025 - {c}) || substr({col}, 5)")
        path = lambda col: f"replace(replace({col}, '{lp}', '{np}'), id, {nid('id')})"
        mc = cols(db, "msg_messages")
        expr = {"pk": f"pk + {off}", "pool_dir": f"replace(pool_dir, '{lp}', '{np}')", "id": nid("id"),
                "parent": f"CASE WHEN parent IS NULL THEN NULL ELSE {nid('parent')} END",
                "draft_path": path("draft_path"), "export_path": path("export_path")}
        q = lambda cs, e: ",".join(e.get(x, f'"{x}"') for x in cs)
        db.execute(f'INSERT INTO msg_messages ({",".join(chr(34)+x+chr(34) for x in mc)}) SELECT {q(mc, expr)} FROM live.msg_messages')
        bc = cols(db, "msg_bodies")
        db.execute(f'INSERT INTO msg_bodies ({",".join(bc)}) SELECT {q(bc, {"msg_pk": f"msg_pk + {off}"})} FROM live.msg_bodies')
        ec = cols(db, "msg_exchanges")
        db.execute(f'INSERT INTO msg_exchanges ({",".join(chr(34)+x+chr(34) for x in ec)}) SELECT '
                   + q(ec, {"pool_dir": f"replace(pool_dir, '{lp}', '{np}')", "id": nid("id"),
                            "request_pk": f"request_pk + {off}", "response_pk": f"response_pk + {off}"}) + " FROM live.msg_exchanges")
        for p, b in db.execute(f"SELECT coalesce(m.export_path, m.draft_path), b.bytes FROM msg_messages m JOIN msg_bodies b "
                               f"ON b.msg_pk = m.pk AND b.rev = m.body_rev WHERE m.pk BETWEEN {off} AND {off + 9_999_999}"):
            if p:
                os.makedirs(os.path.dirname(p), exist_ok=True)
                open(p, "wb").write(b)
                files += 1
    downgraded = "live" not in cols(db, "msg_exchanges", "live")
    if downgraded:
        # the live copy predates schema 9, so the copied rows hold the column names as text in live and answered_ms; drop what
        # version 9 adds and mark the DB version 8, so the ahd under test migrates it as it would the live DB, by its own backfill
        for ix in ("msg_exchanges_hot", "msg_exchanges_answered", "msg_exchanges_team_opened", "msg_messages_request_slug"):
            db.execute(f"DROP INDEX IF EXISTS {ix}")
        db.execute("ALTER TABLE msg_exchanges DROP COLUMN live")
        db.execute("ALTER TABLE msg_exchanges DROP COLUMN answered_ms")
        db.execute("UPDATE meta SET value = '8' WHERE key = 'schema_version'")
    db.execute("UPDATE domain_seq SET seq = seq + 1 WHERE domain = 'msgs'")
    db.commit()
    db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    n = db.execute("SELECT (SELECT count(*) FROM msg_messages), (SELECT count(*) FROM msg_exchanges)").fetchone()
    db.close()
    return {"messages": n[0], "exchanges": n[1], "files_written": files}


# ---------------------------------------------------------------- measurements

HOT_SQL = {  # the reader's statements (native/src/Ah.StateDb/StateDbMsgReader.cs), columns trimmed
    "exchanges_of_pool": "SELECT e.state, q.id, r.pk FROM msg_exchanges e JOIN msg_messages q ON q.pk=e.request_pk "
                         "LEFT JOIN msg_messages r ON r.pk=e.response_pk WHERE e.pool_dir=?1 AND e.state<>'archived' "
                         "AND q.state<>'archived' ORDER BY e.id",
    "messages_of_pool": "SELECT m.id FROM msg_messages m WHERE m.pool_dir=?1 AND m.state<>'archived' ORDER BY m.id,m.type,m.\"to\",m.slug",
    "message_by_id": "SELECT m.id FROM msg_messages m WHERE m.pool_dir=?1 AND m.state<>'archived' AND m.id=?2",
    "by_path": "SELECT m.pk FROM msg_messages m LEFT JOIN msg_bodies b ON b.msg_pk=m.pk AND b.rev=m.body_rev "
               "WHERE m.draft_path=?1 OR m.export_path=?1 LIMIT 1",
    "refresh_exchange": "SELECT pk,type,state FROM msg_messages WHERE pool_dir=?1 AND id=?2 AND type IN('request','response')",
}


def sql_probe(runs):
    db = sqlite3.connect(f'file:{G["ENV"]["AH_HOME"]}/state/ah.db?mode=ro', uri=True)
    some = db.execute("SELECT id, export_path FROM msg_messages WHERE pool_dir=? AND export_path IS NOT NULL LIMIT 1", (G["POOL"],)).fetchone()
    args = {"exchanges_of_pool": (G["POOL"],), "messages_of_pool": (G["POOL"],), "message_by_id": (G["POOL"], some[0]),
            "by_path": (some[1],), "refresh_exchange": (G["POOL"], some[0])}
    out = {}
    for k, s in HOT_SQL.items():
        plan = [r[3] for r in db.execute("EXPLAIN QUERY PLAN " + s, args[k])]
        xs = []
        for _ in range(runs):
            t0 = time.perf_counter()
            rows = db.execute(s, args[k]).fetchall()
            xs.append((time.perf_counter() - t0) * 1000)
        out[k] = {"plan": plan, "rows": len(rows), **stats(xs)}
    db.close()
    return out


def _as_member(name, fn, q):
    prove(name)
    q.put(fn())


def run_as(name, fn):
    """fn runs in a forked process proven as <name>@tm (the CLI checks its caller's ancestry)."""
    q = mp.get_context("fork").Queue()
    p = mp.get_context("fork").Process(target=_as_member, args=(name, fn, q))
    p.start()
    r = q.get()
    p.join()
    return r


def cli_ops(runs, tag):
    """The agent message loop, timed per verb: new, send (orchestrator); inbox, next, response (implementor); list."""
    acc = {"t": {}, "spans": {}, "errs": []}

    def timed(acc, k, *a):
        ms, rc, out, err = ah(*a)
        acc["t"].setdefault(k, []).append(ms)
        for ph, v in SPAN.findall(err):
            acc["spans"].setdefault(f"{k} :: {ph}", []).append(float(v))
        if rc:
            acc["errs"].append((k, rc, re.sub(r"^ah-trace .*\n?", "", err, flags=re.M)[:200]))
        return rc, out

    made = []
    for i in range(runs):
        rc, out = timed(acc, "msg new", "msg", "new", "--to", "implementor", "--from", "orchestrator", "--slug", f"{tag}-{i}",
                        "--title", "Bench perf request", "--team", "tm", "--body-file", G["BODY"])
        if rc:
            continue
        path = json.loads(out)["path"]
        timed(acc, "msg send", "msg", "send", "tm-implementor@tm", "--ref", path, "--kind", "brief")
        made.append((path, os.path.basename(path).split("--")[0]))

    def member():
        m = {"t": {}, "spans": {}, "errs": []}
        for path, mid in made:
            timed(m, "msg inbox", "msg", "inbox")
            timed(m, "msg next", "msg", "next")
            timed(m, "msg new --type response", "msg", "new", "--type", "response", "--id", mid, "--from", "implementor",
                  "--req", path, "--body-file", G["BODY"])
        return m
    m = run_as("tm-implementor", member)
    for key in ("t", "spans"):
        acc[key].update(m[key])
    acc["errs"] += m["errs"]
    for _ in range(runs):
        timed(acc, "msg list", "msg", "list")
        timed(acc, "msg list --all", "msg", "list", "--all")
    floor = [ah("--version")[0] for _ in range(runs)]
    return {"ops": {k: stats(v) for k, v in acc["t"].items()}, "process_floor_ah_version": stats(floor),
            "spans": {k: stats(v) for k, v in acc["spans"].items()}, "errors": acc["errs"][:20], "error_count": len(acc["errs"])}


def discover_routes():
    """UI read routes: the view tree (2 levels), its named views and lists, plus the msgs state reads."""
    routes = ["/v1/view", "/v1/read-model", "/v1/actions",
              state_q("msgs", "list", {"dir": G["POOL"]}), state_q("msgs", "index", {"dir": G["POOL"]})]
    seen, lists = set(), set()
    frontier = ["/"]
    for depth in range(3):
        nxt = []
        for p in frontier:
            ms, st, body = get("/v1/view?path=" + urllib.parse.quote(p))
            if st != 200:
                continue
            s = body.decode()
            lists.update(re.findall(r'"list(?:_id)?":"([^"]+)"', s))
            for c in json.loads(s).get("children", [])[:4]:
                if c.get("path") and c["path"] not in seen:
                    seen.add(c["path"]); nxt.append(c["path"])
        frontier = nxt
    routes += ["/v1/view?path=" + urllib.parse.quote(p) for p in sorted(seen)][:12]
    routes += ["/v1/list/" + urllib.parse.quote(l, safe=":/.") for l in sorted(lists)][:12]
    return routes


def http_reads(routes, runs):
    out = {}
    for r in routes:
        xs, st, size = [], None, 0
        for _ in range(runs):
            ms, st, body = get(r)
            xs.append(ms); size = len(body)
        out[r[:110]] = {"status": st, "bytes": size, **stats(xs)}
    return out


def _writer(i, n, q):
    prove(f"tm-w{i}")
    xs, errs = [], []
    for j in range(n):
        for a in (["msg", "new", "--to", "architect", "--from", "implementor", "--slug", f"c{i}-{j}", "--body-file", G["BODY"]],
                  ["msg", "send", "orchestrator@tm", "--text", f"conc {i}-{j}"]):  # member-to-member needs a link
            ms, rc, out, err = ah(*a)
            xs.append(ms)
            if rc or re.search(r"busy|locked|unavailable|using files", err, re.I):
                errs.append(err.strip()[:160])
    q.put((xs, errs))


def concurrency(n_writers, per_writer, readers=2):
    wal = G["ENV"]["AH_HOME"] + "/state/ah.db-wal"
    wal0 = os.path.getsize(wal) if os.path.exists(wal) else 0
    stop, rx, rerr = threading.Event(), [], []
    reads = [state_q("msgs", "list", {"dir": G["POOL"]}), "/v1/view"]

    def reader(k):
        while not stop.is_set():
            try:
                ms, st, _ = get(reads[k % 2])
                rx.append(ms)
                if st != 200:
                    rerr.append(st)
            except OSError as e:
                rerr.append(str(e))
    ctx_ = mp.get_context("fork")
    q = ctx_.Queue()
    t0 = time.perf_counter()
    ps = [ctx_.Process(target=_writer, args=(i, per_writer, q)) for i in range(1, n_writers + 1)]
    for p in ps:
        p.start()
    th = [threading.Thread(target=reader, args=(k,)) for k in range(readers)]  # after the forks: never fork a threaded process
    for x in th:
        x.start()
    res = [q.get() for _ in ps]
    wall = time.perf_counter() - t0
    for p in ps:
        p.join()
    stop.set()
    for x in th:
        x.join()
    wx = [m for xs, _ in res for m in xs]
    errs = [e for _, es in res for e in es]
    return {"writers": n_writers, "ops": len(wx), "ops_per_s": round(len(wx) / wall, 1), "write": stats(wx),
            "read": stats(rx), "read_errors": len(rerr), "write_errors": len(errs), "error_samples": errs[:5],
            "wal_before": wal0, "wal_after": os.path.getsize(wal) if os.path.exists(wal) else 0}


def daemon_log_hits():
    hits = {}
    for root, _, fs in os.walk(G["ENV"]["AH_HOME"] + "/logs"):
        for f in fs:
            for line in open(os.path.join(root, f), errors="replace"):
                for k in ("SQLITE_BUSY", "database is locked", "busy", "retry", "lock timeout"):
                    if k.lower() in line.lower():
                        hits[k] = hits.get(k, 0) + 1
    return hits


def db_size():
    h = G["ENV"]["AH_HOME"] + "/state/ah.db"
    return {s: os.path.getsize(h + s) if os.path.exists(h + s) else 0 for s in ("", "-wal")}


def read_model_counters():
    ms, st, b = get("/v1/read-model")
    c = json.loads(b).get("counters", {}) if st == 200 else {}
    return {k: c.get(k) for k in ("full_passes", "last_pass_ms", "listings", "coalesce_ms")}


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ahd", required=True, help="the ahd binary under test (tests/publish-bin.sh output)")
    ap.add_argument("--live-db", default=os.path.expanduser("~/.claude/ah2/state/ah.db"), help="read-only source of msg_* rows")
    ap.add_argument("--scales", default="1,11,53", help="data multipliers of the live rows (1 = as is)")
    ap.add_argument("--runs", type=int, default=30)
    ap.add_argument("--conc", default="4,8,16", help="parallel writer counts")
    ap.add_argument("--per-writer", type=int, default=10)
    ap.add_argument("--big-runs", type=int, default=3, help="runs per op at scales above x1")
    ap.add_argument("--conc-scales", default="1", help="scales the concurrency phase runs at")
    ap.add_argument("--out", required=True)
    ap.add_argument("--keep", action="store_true", help="keep the sandbox (still stops the daemon)")
    a = ap.parse_args()
    G["AHD"], G["LIVE"] = os.path.realpath(a.ahd), a.live_db
    os.makedirs(a.out, exist_ok=True)
    res = {"started": time.strftime("%Y-%m-%dT%H:%M:%S"), "ahd": G["AHD"], "scales": {}}
    live_copy = os.path.join(a.out, "live-copy.db")
    subprocess.run(["sqlite3", "-readonly", a.live_db, f".backup {live_copy}"], check=True)  # the only touch of the live DB
    pools = live_pools(live_copy)
    try:
        setup(live_copy, pools)
        res["sandbox"] = G["T"]
        done = 0
        for k in [int(x) for x in a.scales.split(",")]:
            log(f"scale x{k}: inject")
            inj = inject(live_copy, range(done, k))
            done = k
            r = {"inject": inj, "size": db_size()}
            r["start"] = start_daemon()
            time.sleep(3)
            r["read_model"] = read_model_counters()
            log(f"scale x{k}: sql")
            r["sql"] = sql_probe(a.runs)
            # ops cost seconds each at x1 already and grow with the data: bigger scales get --big-runs runs
            runs = a.runs if k == 1 else a.big_runs
            log(f"scale x{k}: cli")
            r["cli"] = cli_ops(runs, f"s{k}")
            log(f"scale x{k}: http")
            r["http"] = http_reads(discover_routes()[: 10 if k == 1 else 6], runs)
            log(f"scale x{k}: concurrency")
            conc = [int(x) for x in a.conc.split(",")] if str(k) in a.conc_scales.split(",") else []
            r["concurrency"] = [concurrency(n, a.per_writer) for n in conc]
            r["read_model_after"] = read_model_counters()
            r["size_after"] = db_size()
            r["log_hits"] = daemon_log_hits()
            res["scales"][f"x{k}"] = r
            stop_daemon()
            json.dump(res, open(os.path.join(a.out, "results.json"), "w"), indent=1)
    finally:
        try:
            stop_daemon()
        finally:
            res["leftover_ahd"] = sandbox_ahd_pids()
            json.dump(res, open(os.path.join(a.out, "results.json"), "w"), indent=1)
            if not a.keep:
                for d in (G.get("T"), G.get("PR")):
                    d and shutil.rmtree(d, ignore_errors=True)
    print(os.path.join(a.out, "results.json"))


def summary(path):
    """`db-perf.py summary <results.json>`: one line per measured thing, p50/p95/p99 ms."""
    res = json.load(open(path))
    f = lambda s: f'{s.get("p50")}/{s.get("p95")}/{s.get("p99")}'
    for name, r in res["scales"].items():
        print(f'== {name}: {r["inject"]} size={r["size"]} start={r["start"]} read_model={r.get("read_model")}')
        for k, s in r["sql"].items():
            print(f'  sql {k:20} {f(s):>22} rows={s["rows"]} plan={" | ".join(s["plan"])}')
        for k, s in r["cli"]["ops"].items():
            print(f'  cli {k:28} {f(s):>22}')
        print(f'  cli process floor           {f(r["cli"]["process_floor_ah_version"]):>22}  errors={r["cli"]["error_count"]} {r["cli"]["errors"][:2]}')
        for k, s in r["cli"]["spans"].items():
            if (s.get("p50") or 0) >= 5:
                print(f'  span {k:60} {f(s):>22}')
        for k, s in r["http"].items():
            print(f'  http {k[:70]:70} {f(s):>22} st={s["status"]} bytes={s["bytes"]}')
        for c in r["concurrency"]:
            print(f'  conc w={c["writers"]} ops={c["ops"]} {c["ops_per_s"]}/s write={f(c["write"])} read={f(c["read"])} '
                  f'werr={c["write_errors"]} rerr={c["read_errors"]} wal {c["wal_before"]}->{c["wal_after"]} {c["error_samples"][:1]}')
        print(f'  read_model_after={r.get("read_model_after")} size_after={r.get("size_after")} log_hits={r.get("log_hits")}')
    print("leftover_ahd", res.get("leftover_ahd"))


def log(m):
    print(time.strftime("%H:%M:%S"), m, file=sys.stderr, flush=True)


if __name__ == "__main__":
    summary(sys.argv[2]) if sys.argv[1:2] == ["summary"] else main()
