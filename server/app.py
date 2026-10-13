"""hostwatch - collects health reports from Windows PCs on the LAN.

Each PC runs agent/hostwatch-agent.cs (built into hostwatch.exe on the machine),
which POSTs a report every second:
identity (hostname, user, MAC, IP), CPU, memory, disks, uptime and how long
the keyboard/mouse have been idle. This server keeps the latest report per PC,
flags problems and serves a dashboard at /.
"""
from __future__ import annotations

import base64
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
from fastapi.responses import FileResponse, PlainTextResponse
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
TIMELINE_DAYS = 7          # active/idle history kept per PC
MAX_GAP = 10               # s between reports still counted as continuous
DEFAULT_WORK = {"tz": "Asia/Baku", "intervals": [["09:00", "12:00"], ["14:00", "18:00"]]}

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
    agent_traffic = (request.method == "POST" and path == "/api/report") or path.startswith("/agent/")
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


@app.post("/api/report")
async def report(request: Request) -> dict:
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
    return {"ok": True, "label": agent.get("label"), "label_set_at": num(agent.get("label_set_at"))}


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
    return {"ts": now, "day": time.strftime("%Y-%m-%d", time.localtime(start)), "start": start, "end": end,
            "days": days, "idle_after": IDLE_AFTER, "work": work_config(), "rows": rows}


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
