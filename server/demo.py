"""Demo office for README screenshots: run the server with HOSTWATCH_DEMO=1.

Eight made-up PCs report every second through the same code path as real
agents, with a week of active/idle history behind them. Nothing is saved.
"""
from __future__ import annotations

import asyncio
import random
import time

import app as core

G = 1024 ** 3
CPU_7 = "Intel(R) Core(TM) i7-12700 CPU"
CPU_5 = "Intel(R) Core(TM) i5-10400 CPU"
RYZEN = "AMD Ryzen 9 7950X 16-Core Processor"

# mac, hostname, label, user, ip, adapter, link bit/s, cpu, cores, threads,
# memory GB (total, used), disks [(name, label, total GB, free GB)], profile
PCS = [
    ("3c:7c:3f:a1:10:01", "RECEPTION-01", "Reception", "OFFICE\\anna", "192.168.1.21", "Ethernet", 1e9,
     CPU_5, 6, 12, (16, 7.4), [("C:", "System", 476, 211)], "normal"),
    ("3c:7c:3f:a1:10:02", "ACC-02", "Accounting · Lena", "OFFICE\\lena", "192.168.1.22", "Ethernet", 1e9,
     CPU_5, 6, 12, (8, 7.5), [("C:", "System", 238, 61)], "mem_hot"),
    ("d8:bb:c1:5e:22:03", "DESIGN-WS", "Design workstation", "OFFICE\\mark", "192.168.1.23", "Ethernet", 1e9,
     RYZEN, 16, 32, (64, 41), [("C:", "System", 953, 402), ("D:", "Projects", 3726, 880)], "cpu_hot"),
    ("f4:6b:8c:90:44:04", "MGR-LAPTOP", "Manager laptop", "OFFICE\\david", "192.168.1.31", "Wi-Fi", 1.2e9,
     CPU_7, 12, 20, (32, 14), [("C:", "", 953, 512)], "download"),
    ("3c:7c:3f:a1:10:05", "FILESRV", "File server", "OFFICE\\admin", "192.168.1.10", "Ethernet", 1e9,
     CPU_7, 12, 20, (32, 11), [("C:", "System", 238, 120), ("D:", "Shares", 7452, 290)], "disk_full"),
    ("3c:7c:3f:a1:10:06", "WAREHOUSE-PC", "Warehouse", "OFFICE\\oleg", "192.168.1.41", "Ethernet", 1e8,
     CPU_5, 4, 8, (8, 4.1), [("C:", "System", 238, 140)], "idle_long"),
    ("f4:6b:8c:90:44:07", "INTERN-PC", "Intern", "OFFICE\\intern", "192.168.1.42", "Wi-Fi", 433e6,
     CPU_5, 4, 8, (16, 6.2), [("C:", "", 476, 300)], "idle_short"),
    ("3c:7c:3f:a1:10:08", "MEETING-TV", "Meeting room", "OFFICE\\meeting", "192.168.1.50", "Ethernet", 1e9,
     CPU_5, 4, 8, (8, 3.0), [("C:", "", 238, 180)], "offline"),
]
IDLE_SINCE = {"idle_long": 23 * 60, "idle_short": 7 * 60}   # seconds without input right now
OFFLINE_FOR = 3 * 3600


def workday(start: float, end: float, rng: random.Random) -> list[list]:
    """Active blocks broken up by idle spells and a lunch gap."""
    segments, cur = [], start
    lunch_done = False
    while cur < end:
        hour = time.localtime(cur).tm_hour
        if not lunch_done and hour >= 13:
            cur += rng.uniform(35, 55) * 60          # away for lunch: offline gap
            lunch_done = True
            continue
        active = min(end, cur + rng.uniform(15, 80) * 60)
        segments.append([cur, active, "active"])
        if active >= end:
            break
        idle = min(end, active + rng.uniform(3, 35) * 60)
        segments.append([active, idle, "idle"])
        cur = idle
    return segments


def seed_history(agent: dict, profile: str, now: float, rng: random.Random) -> None:
    today = core.day_start(now)
    segments: list[list] = []
    for back in range(core.TIMELINE_DAYS - 1, 0, -1):
        day = today - back * 86400
        if time.localtime(day).tm_wday >= 5:      # weekends off
            continue
        segments += workday(day + 9 * 3600 + rng.uniform(0, 40) * 60,
                            day + 18 * 3600 + rng.uniform(0, 90) * 60, rng)
    start = today + 9 * 3600 if now - today > 10 * 3600 else today
    # idle status starts IDLE_AFTER seconds after the last input
    tail = now - IDLE_SINCE[profile] + core.IDLE_AFTER if profile in IDLE_SINCE else now
    if profile == "offline":
        tail = now - OFFLINE_FOR
    if tail > start:
        segments += workday(start, tail, rng)
    if profile in IDLE_SINCE and tail < now:
        segments.append([tail, now, "idle"])
    agent["segments"] = segments
    agent["last_report_at"] = now if profile != "offline" else now - OFFLINE_FOR


def report_for(pc: tuple, now: float, started: float, rng: random.Random, state: dict) -> dict:
    (mac, host, _label, user, ip, nic, speed, cpu_name, cores, threads, (mem_total, mem_used),
     disks, profile) = pc
    walk = state.setdefault(mac, {"cpu": rng.uniform(4, 20), "rx": rng.uniform(2e4, 4e5)})
    if profile == "cpu_hot":
        cpu = rng.uniform(93, 99)
    else:
        walk["cpu"] = min(60, max(1, walk["cpu"] + rng.uniform(-4, 4)))
        cpu = walk["cpu"] / (4 if profile in IDLE_SINCE else 1)
    if profile == "download":
        rx, tx = rng.uniform(1.9e7, 2.6e7), rng.uniform(4e5, 9e5)
    else:
        walk["rx"] = min(3e6, max(2e3, walk["rx"] * rng.uniform(0.7, 1.4)))
        rx, tx = walk["rx"], walk["rx"] * rng.uniform(0.05, 0.3)
    idle = IDLE_SINCE[profile] + (now - started) if profile in IDLE_SINCE else rng.uniform(0, 20)
    return {
        "id": mac, "agent_version": "1.2", "hostname": host, "user": user,
        "os": "Microsoft Windows 11 Pro 10.0.26100", "model": "Dell OptiPlex 7010", "ip": ip, "mac": mac,
        "networks": [{"name": nic, "adapter": f"{nic} adapter", "mac": mac, "ip": ip,
                      "gateway": "192.168.1.1", "speed": speed, "primary": True}],
        "cpu": {"percent": cpu, "name": cpu_name, "cores": cores, "threads": threads},
        "memory": {"total": mem_total * G, "used": (mem_used + rng.uniform(-0.05, 0.05)) * G},
        "disks": [{"name": n, "label": lb, "fs": "NTFS", "total": t * G, "free": f * G} for n, lb, t, f in disks],
        "net_io": {"rx_bps": rx, "tx_bps": tx},
        "uptime_seconds": 3 * 86400 + 5 * 3600 + (now - started),
        "idle_seconds": idle, "session_id": 1,
    }


async def run() -> None:
    rng = random.Random(7)
    now = started = time.time()
    state: dict = {}
    for pc in PCS:
        mac, label, profile = pc[0], pc[2], pc[-1]
        agent = core.agents.setdefault(mac, {"first_seen": now - 30 * 86400})
        agent.update(label=label, label_set_at=now)
        seed_history(agent, profile, now, rng)
        report = report_for(pc, now, started, rng, state)
        agent["report"] = report
        agent["seen"] = now - OFFLINE_FOR if profile == "offline" else now
        hot = profile == "cpu_hot"
        agent["cpu_hist"] = [rng.uniform(93, 99) if hot else rng.uniform(3, 25) for _ in range(core.HISTORY_SECONDS)]
        agent["rx_hist"] = [report["net_io"]["rx_bps"] * rng.uniform(0.6, 1.3) for _ in range(core.SPARK_POINTS)]
        agent["tx_hist"] = [report["net_io"]["tx_bps"] * rng.uniform(0.6, 1.3) for _ in range(core.SPARK_POINTS)]
    while True:
        await asyncio.sleep(1)
        now = time.time()
        for pc in PCS:
            if pc[-1] != "offline":
                core.ingest(report_for(pc, now, started, rng, state), pc[4])
