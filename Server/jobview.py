#!/usr/bin/env python3
"""KDE JobView client: persistent Dolphin-style progress notification.

Reads JSON update lines from stdin and drives org.kde.JobViewV3 on the
session bus so KDE renders a real progress bar (like Dolphin's file copy)
that stays in the notification tray until terminated.

The job is registered with Killable|Suspendable capabilities, so the
notification itself shows pause and cancel buttons. Button presses arrive
as D-Bus signals on the job object and are forwarded to stdout as control
lines for the parent process:
  __suspend__   user pressed pause
  __resume__    user pressed resume
  __cancel__    user pressed cancel/stop

stdin (JSON, one per line):
  {"title": "...", "info": "...", "percent": <0-100>, "speed": <bytes/s>,
   "l1": "...", "v1": "...", "l2": "...", "v2": "..."}
  {"suspended": true|false}   reflect paused state in the job view
  {"terminate": true, "error": <code:int>, "message": "..."}
EOF ends the job gracefully (success).
"""
import json
import sys
import threading

import dbus
from dbus.mainloop.glib import DBusGMainLoop

# requestView capabilities (KJob::Capability): Killable=1, Suspendable=2.
CAPABILITIES = 3


def main():
    desktop_entry = sys.argv[1] if len(sys.argv) > 1 else "PhonesBridge"
    display_name = sys.argv[2] if len(sys.argv) > 2 else "PhonesBridge"

    DBusGMainLoop(set_as_default=True)
    bus = dbus.SessionBus()

    server = bus.get_object("org.kde.JobViewServer", "/JobViewServer")
    req = server.get_dbus_method("requestView", "org.kde.JobViewServerV2")
    hints = {
        "immediate": True,
        "application-display-name": display_name,
        "application-icon-name": "phone",
    }
    jobpath = str(req(desktop_entry, dbus.UInt32(CAPABILITIES), hints))
    jobobj = bus.get_object("org.kde.JobViewServer", jobpath)
    update = jobobj.get_dbus_method("update", "org.kde.JobViewV3")
    terminate = jobobj.get_dbus_method("terminate", "org.kde.JobViewV3")
    set_suspended = jobobj.get_dbus_method("setSuspended", "org.kde.JobViewV2")

    print(f"[JobView] registered {jobpath}", flush=True)

    # Button presses -> control lines on stdout for the parent process.
    bus.add_signal_receiver(lambda: emit("__suspend__"),
                            signal_name="suspendRequested",
                            dbus_interface="org.kde.JobViewV2",
                            path=jobpath)
    bus.add_signal_receiver(lambda: emit("__resume__"),
                            signal_name="resumeRequested",
                            dbus_interface="org.kde.JobViewV2",
                            path=jobpath)
    bus.add_signal_receiver(lambda: emit("__cancel__"),
                            signal_name="cancelRequested",
                            dbus_interface="org.kde.JobViewV2",
                            path=jobpath)

    from gi.repository import GLib
    loop = GLib.MainLoop()
    done = threading.Event()

    def emit(line):
        try:
            print(line, flush=True)
        except Exception:
            pass

    def finish(err, message):
        try:
            terminate(dbus.UInt32(int(err)), str(message), {"success": int(err) == 0})
        except Exception:
            pass
        print(f"[JobView] terminated error={err}", flush=True)
        done.set()
        loop.quit()

    def stdin_loop():
        try:
            for line in sys.stdin:
                line = line.strip()
                if not line:
                    continue
                try:
                    m = json.loads(line)
                except Exception:
                    continue

                if m.get("terminate"):
                    err = int(m.get("error", 0))
                    GLib.idle_add(finish, err, m.get("message", ""))
                    return

                if "suspended" in m:
                    try:
                        set_suspended(bool(m["suspended"]))
                    except Exception:
                        pass
                    continue

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
                    try:
                        update(props)
                    except Exception:
                        pass
        finally:
            if not done.is_set():
                # EOF: gracefully finish as success.
                GLib.idle_add(finish, 0, "")
                print("[JobView] eof -> terminated", flush=True)

    t = threading.Thread(target=stdin_loop, daemon=True)
    t.start()
    try:
        loop.run()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print(f"[JobView] error: {ex}", file=sys.stderr, flush=True)
        sys.exit(1)
