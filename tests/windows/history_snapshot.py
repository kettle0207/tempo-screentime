"""Summarise a Tempo history database without touching it: copies DB/WAL/SHM to a temp folder first.

    python history_snapshot.py <folder-with-history.sqlite3> <out.json>
Prints per-day totals, manual category rules, settings and an integrity check. No app names are printed.
"""
import json
import shutil
import sqlite3
import sys
import tempfile
from pathlib import Path

src, out = Path(sys.argv[1]), Path(sys.argv[2])
with tempfile.TemporaryDirectory() as tmp:
    for suffix in ("", "-wal", "-shm"):
        f = src / ("history.sqlite3" + suffix)
        if f.exists():
            shutil.copyfile(f, Path(tmp) / f.name)
    db = sqlite3.connect(Path(tmp) / "history.sqlite3")
    days = dict(db.execute("SELECT day, SUM(ms) FROM all_usage GROUP BY day ORDER BY day").fetchall())
    snap = {
        "integrity": db.execute("PRAGMA integrity_check").fetchone()[0],
        "days": days,
        "total_ms": sum(days.values()),
        "rules": sorted(map(list, db.execute("SELECT kind, id, category FROM rules").fetchall())),
        "settings": dict(db.execute("SELECT id, value FROM settings WHERE id NOT IN ('updatePending','updateFrom')").fetchall()),
        "apps": db.execute("SELECT COUNT(*) FROM apps").fetchone()[0],
        "legacy_rows": db.execute("SELECT COUNT(*) FROM usage").fetchone()[0],
        "compact_rows": db.execute("SELECT COUNT(*) FROM buckets").fetchone()[0],
    }
    db.close()
out.write_text(json.dumps(snap, ensure_ascii=False, indent=1), "utf-8")
print(f"integrity={snap['integrity']} days={len(days)} total_h={snap['total_ms']/3.6e6:.2f} "
      f"rules={len(snap['rules'])} settings={sorted(snap['settings'])} apps={snap['apps']} "
      f"legacy={snap['legacy_rows']} compact={snap['compact_rows']}")
if len(sys.argv) > 3:
    before = json.loads(Path(sys.argv[3]).read_text("utf-8"))
    today = max(snap["days"]) if snap["days"] else ""
    lost = {d: (ms, snap["days"].get(d, 0)) for d, ms in before["days"].items() if snap["days"].get(d, 0) < ms}
    print("compare: past days preserved =", not lost, lost if lost else "",
          "| rules equal =", before["rules"] == snap["rules"],
          "| settings equal =", {k: v for k, v in before["settings"].items()} == {k: snap["settings"].get(k) for k in before["settings"]},
          "| growth_s =", round((snap["total_ms"] - before["total_ms"]) / 1000, 1))
