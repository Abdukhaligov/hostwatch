"""hostwatch - collects health reports from Windows PCs on the LAN.

Each PC runs agent/hostwatch-agent.cs (built into hostwatch.exe on the machine),
which POSTs a report every second:
identity (hostname, user, MAC, IP), CPU, memory, disks, uptime and how long
the keyboard/mouse have been idle. This server keeps the latest report per PC,
flags problems and serves a dashboard at /.
"""
from __future__ import annotations

import base64
import csv
import io
import ipaddress
import json
import logging
import os
import re
import secrets
import time
from contextlib import asynccontextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Optional

try:
    from zoneinfo import ZoneInfo
except ImportError:
    ZoneInfo = None

import asyncio
from fastapi import FastAPI, Request
from fastapi.responses import FileResponse, PlainTextResponse, Response
from fastapi.staticfiles import StaticFiles

DATA_PATH = Path(os.environ.get("HOSTWATCH_DATA", "data/agents.json"))
CONFIG_PATH = Path(os.environ.get("HOSTWATCH_CONFIG", "config.json"))   # {"password": "..."}
STALE_AFTER = int(os.environ.get("HOSTWATCH_STALE_AFTER", "30"))   # s without a report -> offline
IDLE_AFTER = int(os.environ.get("HOSTWATCH_IDLE_AFTER", "120"))    # s without input -> idle
ALLOW_PUBLIC = os.environ.get("HOSTWATCH_ALLOW_PUBLIC", "") == "1"
DEMO = os.environ.get("HOSTWATCH_DEMO", "") == "1"   # fake office for screenshots, nothing is saved
HERE = Path(__file__).parent
STATIC_DIR = HERE / "static"
AGENT_DIR = HERE / "agent" if (HERE / "agent").exists() else HERE.parent / "agent"
AGENT_FILES = ("hostwatch.exe",)
HISTORY_SECONDS = 900      # CPU/network samples kept (~15 min at one report per second)
SPARK_POINTS = 120         # samples shown on the cards (~2 min)
CPU_HOT_SECONDS = 120      # CPU ≥90% for this long is a problem
TIMELINE_DAYS = 60         # active/idle history kept per PC (~2 months)
MAX_GAP = 10               # s between reports still counted as continuous
DEFAULT_WORK = {"tz": "Asia/Baku", "intervals": [["09:00", "12:00"], ["14:00", "18:00"]]}
COMMANDS = ("clear_cache",)   # remote actions the dashboard can queue for an agent

# Agents bootstrap their report token from this fixed, hard-to-guess path (the same constant is
# baked into hostwatch-agent.cs). It only gates fetching the token, not the reports themselves, so
# a long static value is enough here; the per-server token below is what every report must carry.
ENROLL_KEY = "b9f3c1a7e24d4f08a1c6e5d20f7b8a3c6d9e4f1028b7c5a3e6f90d2c4b8a1e7f3"

agents: dict[str, dict] = {}   # agent id (primary MAC) -> {report, seen, label, ...}


class QuietPolling(logging.Filter):
    """Agents report and the dashboard polls every second - keep those out of the access log."""
    NOISY = ('"POST /api/report ', '"GET /api/agents ', '"GET /api/idle')

    def filter(self, record: logging.LogRecord) -> bool:
        return not any(marker in record.getMessage() for marker in self.NOISY)


logging.getLogger("uvicorn.access").addFilter(QuietPolling())


def num(value, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def problems_of(agent: dict, now: float) -> list[str]:
    if now - agent.get("seen", 0) > STALE_AFTER:
        return ["offline"]
    report = agent.get("report") or {}
    problems = []
    recent = (agent.get("cpu_hist") or [])[-CPU_HOT_SECONDS:]
    if len(recent) >= CPU_HOT_SECONDS and min(recent) >= 90:
        problems.append("CPU ≥90% for 2+ min")
    mem = report.get("memory") or {}
    if num(mem.get("total")) and num(mem.get("used")) / num(mem.get("total")) >= 0.9:
        problems.append("memory ≥90%")
    for disk in report.get("disks") or []:
        total, free = num(disk.get("total")), num(disk.get("free"))
        if total and free / total < 0.1:
            problems.append(f"disk {disk.get('name', '?')} <10% free")
    return problems


def view_of(agent_id: str, agent: dict, now: float) -> dict:
    report = agent.get("report") or {}
    online = now - agent.get("seen", 0) <= STALE_AFTER
    idle = num(report.get("idle_seconds"))
    return {
        **report,
        "id": agent_id,
        "label": agent.get("label"),
        "seen": agent.get("seen"),
        "first_seen": agent.get("first_seen"),
        "status": ("offline" if not online else "off" if not in_work_hours(now)
                   else "idle" if idle >= IDLE_AFTER else "active"),
        "problems": problems_of(agent, now),
        "cpu_hist": (agent.get("cpu_hist") or [])[-SPARK_POINTS:],
        "rx_hist": (agent.get("rx_hist") or [])[-SPARK_POINTS:],
        "tx_hist": (agent.get("tx_hist") or [])[-SPARK_POINTS:],
    }


# ---------- active / idle history ----------

def day_start(ts: float) -> float:
    t = time.localtime(ts)
    return time.mktime((t.tm_year, t.tm_mon, t.tm_mday, 0, 0, 0, 0, 0, -1))


def record_activity(agent: dict, now: float, idle_seconds: float) -> None:
    """Adds the time since the previous report to an active or idle segment."""
    last = agent.get("last_report_at")
    agent["last_report_at"] = now
    if not last or not 0 < now - last <= MAX_GAP:
        return
    state = "off" if not in_work_hours(now) else "idle" if idle_seconds >= IDLE_AFTER else "active"
    segments = agent.setdefault("segments", [])
    if segments and segments[-1][2] == state and last - segments[-1][1] <= MAX_GAP:
        segments[-1][1] = now
    else:
        segments.append([last, now, state])
    cutoff = day_start(now) - (TIMELINE_DAYS - 1) * 86400
    while segments and segments[0][1] < cutoff:
        segments.pop(0)


def classify_segment(start: float, end: float, state: str, work: dict) -> list:
    """Split an active/idle span at work-hour boundaries, marking off-hours parts as 'off'."""
    tz = _zone(work["tz"])
    points = [start, end]
    day = datetime.fromtimestamp(start, tz).replace(hour=0, minute=0, second=0, microsecond=0)
    while day.timestamp() < end:
        for interval in work["intervals"]:
            for hhmm in interval:
                try:
                    moment = (day + timedelta(minutes=_minutes(hhmm))).timestamp()
                except (ValueError, TypeError):
                    continue
                if start < moment < end:
                    points.append(moment)
        day += timedelta(days=1)
    points = sorted(set(points))
    pieces = []
    for a, b in zip(points, points[1:]):
        kind = state if in_work_hours((a + b) / 2, work) else "off"
        pieces.append([a, b, kind])
    return pieces


def backfill_segments(agent: dict, raw: list) -> int:
    """Merge agent-recorded history (gathered while the server was unreachable)."""
    work = work_config()
    pieces = []
    for item in raw:
        try:
            a, b, state = float(item[0]), float(item[1]), str(item[2])
        except (ValueError, IndexError, TypeError):
            continue
        if b > a and state in ("active", "idle"):
            pieces += classify_segment(a, b, state, work)
    if not pieces:
        return 0
    merged = []
    for a, b, state in sorted((agent.get("segments") or []) + pieces):
        if merged and merged[-1][2] == state and a - merged[-1][1] <= MAX_GAP:
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b, state])
    cutoff = day_start(time.time()) - (TIMELINE_DAYS - 1) * 86400
    agent["segments"] = [seg for seg in merged if seg[1] >= cutoff]
    return len(pieces)


def day_summary(agent: dict, start: float, end: float) -> dict:
    """Active/idle totals and segments of one agent, clipped to [start, end)."""
    clipped = []
    totals = {"active": 0.0, "idle": 0.0, "off": 0.0}
    longest = 0.0
    for a, b, state in agent.get("segments") or []:
        a, b = max(a, start), min(b, end)
        if b <= a:
            continue
        clipped.append([a, b, state])
        totals[state] = totals.get(state, 0.0) + (b - a)
        if state == "idle":
            longest = max(longest, b - a)
    return {"active": totals["active"], "idle": totals["idle"], "longest_idle": longest, "segments": clipped}


def save() -> None:
    if DEMO:
        return
    try:
        DATA_PATH.parent.mkdir(parents=True, exist_ok=True)
        tmp = DATA_PATH.with_suffix(".tmp")
        tmp.write_text(json.dumps(agents))
        tmp.replace(DATA_PATH)
    except OSError:
        pass


def load() -> None:
    try:
        agents.update(json.loads(DATA_PATH.read_text()))
    except (OSError, ValueError):
        pass


async def save_loop() -> None:
    while True:
        await asyncio.sleep(60)
        save()


@asynccontextmanager
async def lifespan(_: FastAPI):
    if DEMO:
        import demo
        task = asyncio.create_task(demo.run())
    else:
        load()
        agent_token()   # generate and persist the report token on first run
        task = asyncio.create_task(save_loop())
    yield
    task.cancel()
    save()


app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None)


_config: dict = {"mtime": None, "data": {}}


def load_config() -> dict:
    """config.json (password + work hours), re-read whenever the file changes."""
    try:
        mtime = CONFIG_PATH.stat().st_mtime
    except OSError:
        _config["mtime"], _config["data"] = None, {}
        return _config["data"]
    if mtime != _config["mtime"]:
        try:
            data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            data = {}
        _config["mtime"] = mtime
        _config["data"] = data if isinstance(data, dict) else {}
    return _config["data"]


def save_config(data: dict) -> None:
    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    _config["mtime"] = None   # reload on next read


def dashboard_password() -> Optional[str]:
    password = load_config().get("password")
    return str(password) if password else None


def agent_token() -> str:
    """The secret every agent must present on /api/report. Generated once into config.json and
    then left untouched, so it survives restarts and the file can be moved to another machine."""
    data = load_config()
    token = data.get("agent_token")
    if not token:
        token = secrets.token_urlsafe(32)
        data["agent_token"] = token
        save_config(data)
    return str(token)


def token_ok(request: Request) -> bool:
    """A report is trusted if it carries the server's token (always true in demo mode)."""
    if DEMO:
        return True
    given = request.headers.get("x-agent-token", "")
    return secrets.compare_digest(given.encode(), agent_token().encode())


def work_config() -> dict:
    work = load_config().get("work") or {}
    intervals = work.get("intervals")
    if not isinstance(intervals, list) or not intervals:
        intervals = DEFAULT_WORK["intervals"]
    return {"tz": work.get("tz") or DEFAULT_WORK["tz"], "intervals": intervals}


def _zone(name: str):
    if ZoneInfo:
        try:
            return ZoneInfo(name)
        except Exception:
            pass
    return timezone(timedelta(hours=4))   # Baku, no DST


def _minutes(hhmm: str) -> int:
    hours, _, minutes = str(hhmm).partition(":")
    return int(hours) * 60 + int(minutes)


def in_work_hours(ts: float, work: Optional[dict] = None) -> bool:
    work = work or work_config()
    local = datetime.fromtimestamp(ts, _zone(work["tz"]))
    minutes = local.hour * 60 + local.minute
    for interval in work["intervals"]:
        try:
            if _minutes(interval[0]) <= minutes < _minutes(interval[1]):
                return True
        except (ValueError, IndexError, TypeError):
            continue
    return False


def password_ok(request: Request, password: str) -> bool:
    scheme, _, encoded = request.headers.get("authorization", "").partition(" ")
    if scheme.lower() != "basic":
        return False
    try:
        _user, _, given = base64.b64decode(encoded).decode("utf-8").partition(":")
    except ValueError:
        return False
    return secrets.compare_digest(given.encode(), password.encode())


@app.middleware("http")
async def access_control(request: Request, call_next):
    """LAN only. The dashboard is open on the server machine itself and needs the
    password from config.json anywhere else; agents report and download without it."""
    try:
        client = ipaddress.ip_address(request.client.host if request.client else "")
    except ValueError:
        client = None
    if not ALLOW_PUBLIC and not (client and (client.is_private or client.is_loopback)):
        return PlainTextResponse("hostwatch is LAN-only", status_code=403)

    path = request.url.path
    agent_traffic = ((request.method == "POST" and path.startswith("/api/report"))
                     or path.startswith("/agent/") or path.startswith("/api/enroll/"))
    if agent_traffic or (client and client.is_loopback):
        return await call_next(request)

    password = dashboard_password()
    if not password:
        return PlainTextResponse(f"Dashboard is only open on the server itself. Set a password in {CONFIG_PATH} "
                                 'to allow other devices: {"password": "..."}', status_code=403)
    if not password_ok(request, password):
        return PlainTextResponse("Password required", status_code=401,
                                 headers={"WWW-Authenticate": 'Basic realm="hostwatch", charset="UTF-8"'})
    return await call_next(request)


@app.get("/api/enroll/{key}")
def enroll(key: str):
    """Hands an agent the report token, if it knows the baked-in enrollment path."""
    if not secrets.compare_digest(key.encode(), ENROLL_KEY.encode()):
        return PlainTextResponse("not found", status_code=404)
    return {"token": agent_token()}


@app.post("/api/report")
async def report(request: Request) -> dict:
    if not token_ok(request):
        return {"ok": False, "error": "unauthorized"}
    return ingest(await request.json(), request.client.host if request.client else None)


def ingest(body: dict, ip: Optional[str]) -> dict:
    """Stores one agent report (also fed by demo.py)."""
    agent_id = str(body.get("id") or "").lower()
    if not re.fullmatch(r"([0-9a-f]{2}:){5}[0-9a-f]{2}", agent_id):
        return {"ok": False, "error": "id must be the primary MAC address"}
    agent = agents.setdefault(agent_id, {"first_seen": time.time()})

    # The label can be changed both in the tray app and on the dashboard: newest wins.
    label_at = num(body.pop("label_set_at", 0))
    label = body.pop("label", None)
    if label_at > num(agent.get("label_set_at")):
        agent["label"] = (str(label).strip()[:60] or None) if label is not None else None
        agent["label_set_at"] = label_at

    body["ip_seen"] = ip
    agent["report"] = body
    agent["seen"] = time.time()
    record_activity(agent, agent["seen"], num(body.get("idle_seconds")))
    io = body.get("net_io") or {}
    for key, value in (("cpu_hist", (body.get("cpu") or {}).get("percent")),
                       ("rx_hist", io.get("rx_bps")), ("tx_hist", io.get("tx_bps"))):
        hist = agent.setdefault(key, [])
        hist.append(num(value))
        del hist[:-HISTORY_SECONDS]
    response = {"ok": True, "label": agent.get("label"), "label_set_at": num(agent.get("label_set_at")),
                "idle_after": IDLE_AFTER}
    command = agent.pop("command", None)
    if command:
        response["command"] = command
    # Centrally retarget every agent: set "server_url" in config.json and agents adopt it.
    server_url = str(load_config().get("server_url") or "")
    if server_url.startswith("http"):
        response["server_url"] = server_url
    # Today's totals, so the agent's tray counter continues from the server instead of resetting.
    if body.get("want_today"):
        today = day_summary(agent, day_start(agent["seen"]), day_start(agent["seen"]) + 86400)
        response["today_active"] = today["active"]
        response["today_idle"] = today["idle"]
    return response


@app.post("/api/report/backfill")
async def report_backfill(request: Request) -> dict:
    """Agent-recorded active/idle history buffered while this server was unreachable."""
    if not token_ok(request):
        return {"ok": False, "error": "unauthorized"}
    body = await request.json()
    agent_id = str(body.get("id") or "").lower()
    agent = agents.get(agent_id)
    if agent is None:
        return {"ok": False, "error": "unknown agent"}
    added = backfill_segments(agent, body.get("segments") or [])
    return {"ok": True, "added": added}


@app.post("/api/agents/{agent_id}/command")
async def queue_command(agent_id: str, request: Request) -> dict:
    agent = agents.get(agent_id.lower())
    if agent is None:
        return {"ok": False, "error": "unknown agent"}
    body = await request.json()
    command = str(body.get("command") or "")
    if command not in COMMANDS:
        return {"ok": False, "error": "unknown command"}
    agent["command"] = command
    agent["command_at"] = time.time()
    return {"ok": True}


@app.post("/api/agents/{agent_id}/label")
async def set_label(agent_id: str, request: Request) -> dict:
    agent = agents.get(agent_id.lower())
    if agent is None:
        return {"ok": False, "error": "unknown agent"}
    body = await request.json()
    agent["label"] = str(body.get("label") or "").strip()[:60] or None
    agent["label_set_at"] = time.time()
    save()
    return {"ok": True}


@app.delete("/api/agents/{agent_id}")
def forget(agent_id: str) -> dict:
    agents.pop(agent_id.lower(), None)
    save()
    return {"ok": True}


@app.get("/api/agents")
def agents_list() -> dict:
    now = time.time()
    return {"ts": now, "idle_after": IDLE_AFTER, "stale_after": STALE_AFTER,
            "work": work_config(), "agents": [view_of(k, v, now) for k, v in agents.items()]}


@app.get("/api/idle")
def idle_report(day: Optional[str] = None) -> dict:
    """Per-PC active/idle time for one local day (YYYY-MM-DD, default today)."""
    now = time.time()
    if day:
        try:
            start = time.mktime(time.strptime(day, "%Y-%m-%d"))
        except ValueError:
            return {"error": "day must be YYYY-MM-DD"}
    else:
        start = day_start(now)
    end = start + 86400
    days = [time.strftime("%Y-%m-%d", time.localtime(day_start(now) - i * 86400)) for i in range(TIMELINE_DAYS)]
    rows = []
    for agent_id, agent in agents.items():
        view = view_of(agent_id, agent, now)
        summary = day_summary(agent, start, end)
        viewing_today = start <= now < end
        current = summary["segments"][-1] if viewing_today and summary["segments"] and view["status"] == "idle" else None
        rows.append({
            "id": agent_id, "name": agent.get("label") or view.get("hostname") or agent_id,
            "hostname": view.get("hostname"), "user": view.get("user"), "status": view["status"],
            "idle_now": num(view.get("idle_seconds")) if view["status"] == "idle" else 0,
            "current_idle_since": current[0] if current else None,
            **summary,
        })
    oldest = time.strftime("%Y-%m-%d", time.localtime(day_start(now) - (TIMELINE_DAYS - 1) * 86400))
    return {"ts": now, "day": time.strftime("%Y-%m-%d", time.localtime(start)), "start": start, "end": end,
            "days": days[:7], "oldest": oldest, "idle_after": IDLE_AFTER, "work": work_config(), "rows": rows}


@app.get("/api/idle/export")
def idle_export(start: Optional[str] = None, end: Optional[str] = None, kind: str = "summary"):
    """CSV of active/idle history for local days start..end (YYYY-MM-DD, inclusive; default today).
    kind=summary - one row per PC per day: active, idle and offline (work hours with no reports);
    kind=segments - every active/idle/off-hours span."""
    today = time.strftime("%Y-%m-%d")
    try:
        first = time.mktime(time.strptime(start or today, "%Y-%m-%d"))
        last = time.mktime(time.strptime(end or start or today, "%Y-%m-%d"))
    except ValueError:
        return PlainTextResponse("start/end must be YYYY-MM-DD", status_code=400)
    if kind not in ("summary", "segments"):
        return PlainTextResponse("kind must be summary or segments", status_code=400)
    first, last = min(first, last), max(first, last)

    def export_name(agent_id: str, agent: dict) -> str:
        return str(agent.get("label") or (agent.get("report") or {}).get("hostname") or agent_id)

    def work_seconds(start_ts: float, end_ts: float) -> float:
        if end_ts <= start_ts:
            return 0.0
        return sum(b - a for a, b, state in classify_segment(start_ts, end_ts, "work", work) if state == "work")

    def duration(seconds: float) -> str:
        minutes = round(seconds / 60)
        return f"{minutes // 60}h {minutes % 60:02d}m"

    def clock(ts: float) -> str:
        return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts))

    work = work_config()
    now = time.time()
    states = {"active": "Active", "idle": "Idle", "off": "Off-hours"}
    out = io.StringIO()
    writer = csv.writer(out)
    if kind == "summary":
        writer.writerow(["Date", "Name", "Hostname", "User", "MAC", "Active", "Idle", "Offline", "Idle %", "Longest idle"])
    else:
        writer.writerow(["Date", "Name", "Hostname", "User", "MAC", "State", "Start", "End", "Duration"])
    day = first
    while day <= last:
        next_day = day_start(day + 86400 + 3600)   # +1h survives DST-shortened days
        date = time.strftime("%Y-%m-%d", time.localtime(day))
        for agent_id, agent in sorted(agents.items(), key=lambda item: export_name(*item).lower()):
            report = agent.get("report") or {}
            who = [export_name(agent_id, agent), report.get("hostname") or "", report.get("user") or "", agent_id]
            summary = day_summary(agent, day, next_day)
            if kind == "summary":
                # Offline counts only work hours since the PC was first seen and not past now.
                window_start = max(day, num(agent.get("first_seen"), day))
                window_end = min(next_day, now)
                if window_end <= window_start:
                    continue
                tracked = summary["active"] + summary["idle"]
                offline = max(0.0, work_seconds(window_start, window_end) - tracked)
                writer.writerow([date, *who, duration(summary["active"]), duration(summary["idle"]),
                                 duration(offline), round(100 * summary["idle"] / tracked, 1) if tracked else 0,
                                 duration(summary["longest_idle"])])
            else:
                for a, b, state in summary["segments"]:
                    writer.writerow([date, *who, states.get(state, state), clock(a), clock(b), duration(b - a)])
        day = next_day
    writer.writerows([[], [], [], []])
    writer.writerow(["Exported", clock(now)])
    writer.writerow(["Timezone", work["tz"]])
    writer.writerow(["Work hours", ", ".join(f"{a}\u2013{b}" for a, b in work["intervals"])])
    writer.writerow(["Idle after", f"{IDLE_AFTER // 60} min without input"])
    name = f"hostwatch-{kind}-{time.strftime('%Y-%m-%d', time.localtime(first))}_{time.strftime('%Y-%m-%d', time.localtime(last))}.csv"
    # BOM so Excel opens non-ASCII names/users as UTF-8
    return Response("\ufeff" + out.getvalue(), media_type="text/csv; charset=utf-8",
                    headers={"Content-Disposition": f'attachment; filename="{name}"'})


@app.get("/api/settings")
def get_settings() -> dict:
    return {"work": work_config(), "idle_after": IDLE_AFTER}


@app.post("/api/settings/work")
async def set_work(request: Request) -> dict:
    body = await request.json()
    tz = str(body.get("tz") or "").strip() or DEFAULT_WORK["tz"]
    if ZoneInfo:
        try:
            ZoneInfo(tz)
        except Exception:
            return {"ok": False, "error": "unknown timezone"}
    intervals = []
    for interval in body.get("intervals") or []:
        try:
            start_min, end_min = _minutes(interval[0]), _minutes(interval[1])
        except (ValueError, IndexError, TypeError):
            return {"ok": False, "error": "times must be HH:MM"}
        if not (0 <= start_min < end_min <= 24 * 60):
            return {"ok": False, "error": "each interval must be start < end within a day"}
        intervals.append([f"{start_min // 60:02d}:{start_min % 60:02d}",
                          f"{end_min // 60:02d}:{end_min % 60:02d}"])
    if not intervals:
        return {"ok": False, "error": "at least one interval is required"}
    data = load_config()
    data["work"] = {"tz": tz, "intervals": intervals[:6]}
    save_config(data)
    return {"ok": True, "work": work_config()}


@app.get("/agent/{name}")
def agent_download(name: str, request: Request):
    """Serves the agent files; install.cmd gets this server's address baked in."""
    if name not in AGENT_FILES:
        return PlainTextResponse("not found", status_code=404)
    path = AGENT_DIR / name
    if name == "install.cmd":
        server = f"{request.url.scheme}://{request.url.netloc}"
        text = path.read_text(encoding="utf-8").replace("__SERVER_URL__", server)
        text = text.replace("\r\n", "\n").replace("\n", "\r\n")   # cmd.exe wants CRLF
        return PlainTextResponse(text, media_type="application/octet-stream",
                                 headers={"Content-Disposition": f'attachment; filename="{name}"'})
    return FileResponse(path, filename=name)


@app.get("/")
def index() -> FileResponse:
    return FileResponse(STATIC_DIR / "index.html")


app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
