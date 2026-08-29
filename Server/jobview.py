#!/usr/bin/env python3
"""KDE JobView client: persistent Dolphin-style progress notification.

Reads JSON update lines from stdin and drives org.kde.JobViewV3 on the
session bus so KDE renders a real progress bar (like Dolphin's file copy)
that stays in the notification tray until terminated.

stdin (JSON, one per line):
  {"title": "...", "info": "...", "percent": <0-100>, "speed": <bytes/s>,
   "l1": "...", "v1": "...", "l2": "...", "v2": "..."}
  {"terminate": true, "error": <code:int>, "message": "..."}
EOF ends the job gracefully (success).
"""
import dbus
import json
import sys


def main():
    desktop_entry = sys.argv[1] if len(sys.argv) > 1 else "PhonesBridge"
    display_name = sys.argv[2] if len(sys.argv) > 2 else "PhonesBridge"

    bus = dbus.SessionBus()

    server = bus.get_object("org.kde.JobViewServer", "/JobViewServer")
    req = server.get_dbus_method("requestView", "org.kde.JobViewServerV2")
    hints = {
        "immediate": True,
        "application-display-name": display_name,
        "application-icon-name": "phone",
    }
    jobpath = str(req(desktop_entry, 0, hints))
    jobobj = bus.get_object("org.kde.JobViewServer", jobpath)
    update = jobobj.get_dbus_method("update", "org.kde.JobViewV3")
    terminate = jobobj.get_dbus_method("terminate", "org.kde.JobViewV3")

    print(f"[JobView] registered {jobpath}", flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            m = json.loads(line)
        except Exception:
            continue

        if m.get("terminate"):
            err = dbus.UInt32(int(m.get("error", 0)))
            terminate(err, m.get("message", ""), {"success": err == 0})
            print(f"[JobView] terminated error={err}", flush=True)
            return

        props = {}
        if "title" in m and m["title"]:
            props["title"] = m["title"]
        if "info" in m and m["info"]:
            props["infoMessage"] = m["info"]
        if "percent" in m:
            props["percent"] = dbus.UInt32(max(0, min(100, int(m["percent"]))))
        if "speed" in m:
            props["speed"] = dbus.UInt64(max(0, int(m["speed"])))
        if "l1" in m and "v1" in m:
            props["descriptionLabel1"] = m["l1"]
            props["descriptionValue1"] = m["v1"]
        if "l2" in m and "v2" in m:
            props["descriptionLabel2"] = m["l2"]
            props["descriptionValue2"] = m["v2"]
        # Extra core/queue details (appended to infoMessage since JobViewV3 has only 2 description slots)
        extra = []
        if "l3" in m and "v3" in m:
            extra.append(f"{m['l3']}: {m['v3']}")
        if "l4" in m and "v4" in m:
            extra.append(f"{m['l4']}: {m['v4']}")
        if extra:
            # Append to infoMessage if present
            base_info = props.get("infoMessage", "")
            props["infoMessage"] = (base_info + " · " + " · ".join(extra)) if base_info else " · ".join(extra)
        if props:
            update(props)

    # EOF: gracefully finish as success.
    terminate(dbus.UInt32(0), "", {"success": True})
    print("[JobView] eof -> terminated", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print(f"[JobView] error: {ex}", file=sys.stderr, flush=True)
        sys.exit(1)