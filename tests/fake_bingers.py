"""A small in-memory Bingers API used to exercise the Emby plugin end to end.

Follows the documented behaviour: bearer auth, title refs (imdb:/tvdb:/tmdb:show:/tmdb:movie:),
season+episode or tvdb_episode addressing, per-item results with status ok/already/unresolved,
12-hour repeat suppression vs rewatches, and an /entries change feed with cursors.
Test hooks live under /__ (log, unwatch, reset).
"""
import json
import sys
import threading
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

TOKEN = "bng_pat_test"
LOCK = threading.Lock()
RATE_LIMIT_NEXT = 0


def now():
    return datetime.now(timezone.utc)


def iso(d):
    return d.strftime("%Y-%m-%dT%H:%M:%SZ")


def parse(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


class Store:
    def __init__(self):
        self.titles = {}       # id -> {id, kind, title, ids}
        self.episodes = {}     # show id -> [ {id, season, number, title, tvdb_episode} ]
        self.state = {}        # entity id -> {watched, plays, first, last, title_id, kind}
        self.feed = []         # [(seq, entity_id)]
        self.seq = 0
        self.log = []          # every write the plugin sent: dicts
        self.rewatches = 0
        self.cursorless_calls = 0

    def add_show(self, name, ids, eps):
        tid = str(uuid.uuid4())
        self.titles[tid] = {"id": tid, "kind": "show", "title": name, "ids": ids}
        self.episodes[tid] = [
            {"id": str(uuid.uuid4()), "season": s, "number": n, "title": f"{name} {s}x{n}", "tvdb_episode": tvdb}
            for (s, n, tvdb) in eps
        ]
        return tid

    def add_movie(self, name, ids):
        tid = str(uuid.uuid4())
        self.titles[tid] = {"id": tid, "kind": "movie", "title": name, "ids": ids}
        return tid

    def resolve_title(self, ref):
        if ref in self.titles:
            return self.titles[ref]
        parts = ref.split(":")
        if len(parts) == 3 and parts[0] == "tmdb":
            kind, val = parts[1], parts[2]
            for t in self.titles.values():
                if t["kind"] == kind and t["ids"].get("tmdb") == val:
                    return t
            return None
        if len(parts) == 2:
            prov, val = parts
            for t in self.titles.values():
                if t["ids"].get(prov) == val:
                    return t
        return None

    def touch(self, eid):
        self.seq += 1
        self.feed.append((self.seq, eid))

    def mark(self, eid, title_id, kind, at):
        st = self.state.get(eid)
        if st and st["watched"]:
            if abs((parse(st["last"]) - at).total_seconds()) < 12 * 3600:
                return "already", "unchanged"
            st["plays"] += 1
            st["last"] = iso(at)
            self.rewatches += 1
            self.touch(eid)
            return "ok", "rewatched"
        self.state[eid] = {"watched": True, "plays": 1, "first": iso(at), "last": iso(at),
                           "title_id": title_id, "kind": kind, "deleted": False}
        self.touch(eid)
        return "ok", "watched"

    def unmark(self, eid):
        st = self.state.get(eid)
        if not st or not st["watched"]:
            return "already"
        st["watched"] = False
        st["deleted"] = True
        self.touch(eid)
        return "ok"

    def targets(self, item):
        """-> list of (entity_id, title_id, kind) or None when unresolved."""
        if "tvdb_episode" in item:
            for tid, eps in self.episodes.items():
                for e in eps:
                    if e["tvdb_episode"] and e["tvdb_episode"] == item["tvdb_episode"]:
                        return [(e["id"], tid, "episode")]
            return None
        t = self.resolve_title(item.get("title", ""))
        if not t:
            return None
        if t["kind"] == "movie":
            return [(t["id"], t["id"], "movie")]
        eps = self.episodes[t["id"]]
        if "season" in item and "episode" in item:
            m = [e for e in eps if e["season"] == item["season"] and e["number"] == item["episode"]]
            return [(m[0]["id"], t["id"], "episode")] if m else None
        if "season" in item:
            return [(e["id"], t["id"], "episode") for e in eps if e["season"] == item["season"]] or None
        return [(e["id"], t["id"], "episode") for e in eps if e["season"] > 0] or None


S = Store()


def seed():
    global S
    S = Store()
    bb = S.add_show("Breaking Bad", {"tvdb": "81189", "tmdb": "1396", "imdb": "tt0903747"},
                    [(1, 1, "349232"), (1, 2, "349235"), (1, 3, "349236"), (2, 1, None)])
    S.add_show("Dark", {"tvdb": "334824", "tmdb": "70523"}, [(1, 1, "5555501"), (1, 2, "5555502")])
    inception = S.add_movie("Inception", {"imdb": "tt1375666", "tmdb": "27205"})
    S.add_movie("Heat", {"tmdb": "949", "imdb": "tt0113277"})
    # Already on Bingers before the plugin is installed: Breaking Bad 1x1 and Inception.
    S.mark(S.episodes[bb][0]["id"], bb, "episode", now() - timedelta(days=30))
    S.mark(inception, inception, "movie", now() - timedelta(days=60))


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def authed(self):
        global RATE_LIMIT_NEXT
        if RATE_LIMIT_NEXT > 0:
            RATE_LIMIT_NEXT -= 1
            body = json.dumps({"error": {"code": "rate_limited", "message": "slow down", "requestId": "x", "retryAfterSeconds": 1}}).encode()
            self.send_response(429)
            self.send_header("Retry-After", "1")
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return False
        if self.headers.get("Authorization") != "Bearer " + TOKEN:
            self.send(401, {"error": {"code": "unauthorized", "message": "bad token", "requestId": "x"}})
            return False
        return True

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        with LOCK:
            if u.path == "/__log":
                return self.send(200, {"log": S.log, "rewatches": S.rewatches, "cursorless": S.cursorless_calls})
            if u.path == "/__reset":
                seed()
                return self.send(200, {"ok": True})
            if u.path == "/__ratelimit":
                global RATE_LIMIT_NEXT
                RATE_LIMIT_NEXT = int(q.get("n", ["1"])[0])
                return self.send(200, {"ok": True})
            if not self.authed():
                return
            if u.path == "/v1/me":
                return self.send(200, {"user": {"handle": "tester", "name": "Test"}, "scopes": ["library:read", "library:write"]})
            if u.path == "/v1/entries":
                cursor = int(q.get("cursor", ["0"])[0])
                if "cursor" not in q:
                    S.cursorless_calls += 1
                limit = int(q.get("limit", ["1000"])[0])
                latest = {}
                for seq, eid in S.feed:
                    if seq > cursor:
                        latest[eid] = seq
                rows = sorted(latest.items(), key=lambda kv: kv[1])
                page, more = rows[:limit], len(rows) > limit
                items = []
                for eid, seq in page:
                    st = S.state[eid]
                    items.append({"entity_kind": st["kind"], "entity_id": eid, "title_id": st["title_id"],
                                  "watched": st["watched"], "plays": st["plays"],
                                  "first_watched_at": st["first"], "last_watched_at": st["last"],
                                  "rating": None, "updated_at": iso(now()), "deleted": st["deleted"]})
                nxt = str(page[-1][1]) if page else str(cursor if cursor else S.seq)
                return self.send(200, {"items": items, "next_cursor": nxt, "has_more": more})
            if u.path == "/v1/titles":
                ids = q.get("ids", [""])[0].split(",")
                return self.send(200, {"items": [S.titles[i] for i in ids if i in S.titles]})
            if u.path.startswith("/v1/titles/") and u.path.endswith("/episodes"):
                tid = u.path.split("/")[3]
                t = S.resolve_title(tid)
                if not t or t["kind"] != "show":
                    return self.send(404, {"error": {"code": "not_found", "message": "Unknown title", "requestId": "x"}})
                return self.send(200, {"title": t, "items": [{k: v for k, v in e.items() if k != "tvdb_episode"} for e in S.episodes[t["id"]]]})
            return self.send(404, {"error": {"code": "not_found", "message": u.path, "requestId": "x"}})

    def do_POST(self):
        u = urlparse(self.path)
        with LOCK:
            if u.path == "/__unwatch":
                b = self.body()
                t = S.resolve_title(b["title"])
                eid = t["id"] if t["kind"] == "movie" else [e["id"] for e in S.episodes[t["id"]] if e["season"] == b["season"] and e["number"] == b["episode"]][0]
                return self.send(200, {"status": S.unmark(eid)})
            if u.path == "/__watch":
                b = self.body()
                t = S.resolve_title(b["title"])
                if t["kind"] == "movie":
                    tg = [(t["id"], t["id"], "movie")]
                else:
                    tg = [(e["id"], t["id"], "episode") for e in S.episodes[t["id"]] if e["season"] == b["season"] and e["number"] == b["episode"]]
                return self.send(200, {"status": S.mark(tg[0][0], tg[0][1], tg[0][2], now())})
            if not self.authed():
                return
            b = self.body()
            if u.path == "/v1/scrobble":
                items = b.get("items", [])
                if not 1 <= len(items) <= 100:
                    return self.send(400, {"error": {"code": "bad_request", "message": "1-100 items", "requestId": "x"}})
                results = []
                for i, it in enumerate(items):
                    allowed = {"title", "season", "episode", "tvdb_episode", "watched_at", "rating"}
                    if set(it) - allowed or any(v is None for v in it.values()):
                        return self.send(400, {"error": {"code": "bad_request", "message": f"bad item {it}", "requestId": "x"}})
                    S.log.append({"op": "scrobble", **it})
                    tg = S.targets(it)
                    if not tg:
                        results.append({"index": i, "action": "watch", "status": "unresolved", "message": "No title matches"})
                        continue
                    at = parse(it["watched_at"]) if "watched_at" in it else now()
                    statuses = [S.mark(e, t, k, at) for (e, t, k) in tg]
                    st = "already" if all(s == "already" for s, _ in statuses) else "ok"
                    res = {"index": i, "action": "watch", "status": st, "outcome": statuses[0][1],
                           "title": S.titles[tg[0][1]]}
                    if tg[0][2] == "episode" and len(tg) == 1:
                        e = next(x for x in S.episodes[tg[0][1]] if x["id"] == tg[0][0])
                        res["episode"] = {"id": e["id"], "season": e["season"], "number": e["number"], "title": e["title"]}
                    results.append(res)
                return self.send(200, {"results": results})
            if u.path == "/v1/library/actions":
                results = []
                for i, a in enumerate(b.get("actions", [])):
                    S.log.append({"op": "action", **a})
                    tg = S.targets(a)
                    if not tg:
                        results.append({"index": i, "action": a["action"], "status": "unresolved"})
                        continue
                    if a["action"] == "unwatch":
                        st = [S.unmark(e) for (e, _, _) in tg]
                        results.append({"index": i, "action": "unwatch", "status": "ok" if "ok" in st else "already"})
                    else:
                        results.append({"index": i, "action": a["action"], "status": "error", "message": "not faked"})
                return self.send(200, {"results": results})
            return self.send(404, {"error": {"code": "not_found", "message": u.path, "requestId": "x"}})


if __name__ == "__main__":
    seed()
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
