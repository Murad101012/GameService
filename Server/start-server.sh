#!/usr/bin/env bash
# PhonesBridge launcher — THE single source of truth for starting the server
# (used by the desktop icon). Idempotent: clicking it when the server is already
# healthy just opens the Web UI; a stale/wedged listener is cleaned up first.
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

LOG="${PHONESBRIDGE_LOG:-/tmp/phonesbridge.log}"
PORT="${PHONESBRIDGE_PORT:-8052}"
DLL="bin/Debug/net10.0/PhonesBridge.dll"
URL="http://127.0.0.1:$PORT"

healthy() {
    curl -s -m 2 -o /dev/null "$URL/api/debug/targets" 2>/dev/null
}

log() { echo "[$(date '+%F %T')] $*" >> "$LOG"; }

# Make sure a background launch can reach the desktop session (tray icon,
# notifications) even if this launcher was started without session env.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
[ -n "$DISPLAY" ] || DISPLAY="$(ls -t /tmp/.X11-unix/X* 2>/dev/null | head -1 | sed 's/.*X/:/')"
export DISPLAY
# The server must run in Production: Development mode (launchSettings) breaks
# the Windows agents' hub connections, and journald-attached stdout can wedge
# the process. Launch the built DLL directly with output to our own log file.
export ASPNETCORE_ENVIRONMENT=Production

if healthy; then
    log "Server already healthy on port $PORT — opening Web UI."
    nohup xdg-open "$URL" >/dev/null 2>&1 &
    exit 0
fi

# Port occupied but not responding → wedged or foreign listener. Kill every
# PhonesBridge process of ours and wait until the port is actually free.
if ss -ltn "( sport = :$PORT )" 2>/dev/null | grep -q LISTEN; then
    log "Port $PORT busy but unhealthy — stopping stale PhonesBridge processes."
    pkill -f 'bin/Debug/net10\.0/PhonesBridge' 2>/dev/null || true
    pkill -f 'dotnet run.*PhonesBridge' 2>/dev/null || true
    for _ in $(seq 1 20); do
        ss -ltn "( sport = :$PORT )" 2>/dev/null | grep -q LISTEN || break
        sleep 0.5
    done
    # A frozen runtime never runs its SIGTERM handlers — escalate to SIGKILL.
    if ss -ltn "( sport = :$PORT )" 2>/dev/null | grep -q LISTEN; then
        log "Still listening after SIGTERM — sending SIGKILL."
        pkill -9 -f 'bin/Debug/net10\.0/PhonesBridge' 2>/dev/null || true
        pkill -9 -f 'dotnet run.*PhonesBridge' 2>/dev/null || true
        sleep 1
    fi
fi

log "Building latest code..."
if ! dotnet build PhonesBridge.csproj --nologo >> "$LOG" 2>&1; then
    log "BUILD FAILED — see $LOG"
    exit 1
fi

log "Starting server (Production, background, log: $LOG)..."
# Run from $ROOT so the server's ContentRootPath resolves tray-qt/tray,
# wwwroot, Pages and appsettings.json correctly.
(
    cd "$ROOT"
    exec nohup setsid /usr/share/dotnet/dotnet "bin/Debug/net10.0/PhonesBridge.dll" >> "$LOG" 2>&1 < /dev/null
) &

# Wait until it is actually serving before opening the browser.
for _ in $(seq 1 40); do
    if healthy; then
        log "Server is up on $PORT."
        nohup xdg-open "$URL" >/dev/null 2>&1 &
        exit 0
    fi
    sleep 0.5
done

log "Server did not become healthy within 20s — see $LOG"
exit 1
