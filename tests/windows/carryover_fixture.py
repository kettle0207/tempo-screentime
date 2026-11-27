"""Add (or with --remove, delete) the test-only rules and theme used to check update carryover.

--remove targets the app that is currently first by id; prefer --remove-from <snapshot.json>.
"""
import os
import sqlite3
import sys

path = os.path.expandvars(r"%LOCALAPPDATA%\Tempo\ScreenTime\history.sqlite3")
db = sqlite3.connect(path, timeout=10)
app = db.execute("SELECT id FROM apps ORDER BY id LIMIT 1").fetchone()[0]
if "--remove-from" in sys.argv:
    pass
elif "--remove" in sys.argv:
    db.execute("DELETE FROM rules WHERE kind='app' AND id=? AND category='study'", (app,))
    db.execute("DELETE FROM rules WHERE kind='site' AND id='tempo-carryover-test.example'")
    db.execute("DELETE FROM settings WHERE id='theme' AND value IN ('light','dark')")
    print("test rules and theme removed")
else:
    db.execute("INSERT OR REPLACE INTO rules VALUES('app',?,'study')", (app,))
    db.execute("INSERT OR REPLACE INTO rules VALUES('site','tempo-carryover-test.example','work')")
    db.execute("INSERT OR REPLACE INTO settings VALUES('theme','light')")
    print("test rules and theme written")
db.commit()
db.close()

# --remove-from <snapshot.json>: delete exactly the fixture rules recorded in a snapshot taken right after writing them.
if "--remove-from" in sys.argv:
    import json
    snap = json.load(open(sys.argv[sys.argv.index("--remove-from") + 1], encoding="utf-8"))
    db = sqlite3.connect(path, timeout=10)
    for kind, ident, category in snap["rules"]:
        db.execute("DELETE FROM rules WHERE kind=? AND id=? AND category=?", (kind, ident, category))
    db.commit()
    print("rules left:", db.execute("SELECT COUNT(*) FROM rules").fetchone()[0])
    db.close()
