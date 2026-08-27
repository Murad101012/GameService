#!/usr/bin/env bash
# Extract the embedded GameServiceCoreAgent / GameServiceUpdaterService /
# GameServiceMonitorService / GameServiceSnapshot / GameServiceShell C# sources
# from WindowsServiceManager.cs and compile them into standalone .exe files.
#
# These are the same programs the Unity game would compile at runtime on the PC via
# csc.exe — this lets you build them without a Unity build, then push them with the
# web UI Update tab.
#
# Requirements: mono (provides `csc` + `mono`). On Linux/macOS: sudo apt install mono-devel
#               On Windows you can use the bundled .NET Framework csc if preferred.
#
# Usage: ./build-agents.sh [output-dir]
#   default output: ./AgentExe/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Support both old (Build/build-agents.sh) and new (Build/WindowsBuild/AgentExe/build-agents.sh) locations
if [[ -f "$SCRIPT_DIR/../../../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs" ]]; then
    MANAGER="$SCRIPT_DIR/../../../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs"
elif [[ -f "$SCRIPT_DIR/../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs" ]]; then
    MANAGER="$SCRIPT_DIR/../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs"
elif [[ -f "$SCRIPT_DIR/../../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs" ]]; then
    MANAGER="$SCRIPT_DIR/../../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs"
else
    MANAGER="$SCRIPT_DIR/../../../DancingLineClone/Assets/Scripts/GameService/WindowsServiceManager.cs"
fi
OUT_DIR="${1:-$SCRIPT_DIR}"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

if [[ ! -f "$MANAGER" ]]; then
    echo "ERROR: cannot find WindowsServiceManager.cs at: $MANAGER" >&2
    exit 1
fi

if ! command -v csc >/dev/null 2>&1; then
    echo "ERROR: mono's 'csc' not found. Install mono-devel first." >&2
    exit 1
fi

mkdir -p "$OUT_DIR"

# Extract the N-th verbatim string source from the manager file.
# Inside `return @"...";` a `""` means an escaped double-quote.
extract() {
    python3 - "$MANAGER" "$2" <<'PYEOF'
import sys
src = open(sys.argv[1], encoding="utf-8-sig").read()
which = int(sys.argv[2])
i = 0
start = -1
for _ in range(which):
    start = src.index('return @"', i)
    i = start + len('return @"')
out = []
while i < len(src):
    c = src[i]
    if c == '"':
        if i + 1 < len(src) and src[i + 1] == '"':
            out.append('"')
            i += 2
        else:
            break
    else:
        out.append(c)
        i += 1
sys.stdout.write(''.join(out))
PYEOF
}

echo "Extracting embedded sources from: $MANAGER"
extract "$MANAGER" 1 > "$TMP_DIR/GameServiceCoreAgentSource.cs"
extract "$MANAGER" 2 > "$TMP_DIR/GameServiceUpdaterServiceSource.cs"
extract "$MANAGER" 3 > "$TMP_DIR/GameServiceMonitorServiceSource.cs"
extract "$MANAGER" 4 > "$TMP_DIR/GameServiceSnapshotSource.cs"
extract "$MANAGER" 5 > "$TMP_DIR/GameServiceShellSource.cs"
echo "  CoreAgent   $(wc -l < "$TMP_DIR/GameServiceCoreAgentSource.cs") lines"
echo "  Updater     $(wc -l < "$TMP_DIR/GameServiceUpdaterServiceSource.cs") lines"
echo "  Monitor     $(wc -l < "$TMP_DIR/GameServiceMonitorServiceSource.cs") lines"
echo "  Snapshot    $(wc -l < "$TMP_DIR/GameServiceSnapshotSource.cs") lines"
echo "  Shell       $(wc -l < "$TMP_DIR/GameServiceShellSource.cs") lines"

REFS="/r:System.dll,System.Core.dll,System.Security.dll"

echo "Compiling with mono csc..."
csc /nologo /target:winexe /out:"$OUT_DIR/GameServiceCoreAgent.exe" $REFS "$TMP_DIR/GameServiceCoreAgentSource.cs"
csc /nologo /target:winexe /out:"$OUT_DIR/GameServiceUpdaterService.exe" $REFS "$TMP_DIR/GameServiceUpdaterServiceSource.cs"
csc /nologo /target:winexe /out:"$OUT_DIR/GameServiceMonitorService.exe" $REFS "$TMP_DIR/GameServiceMonitorServiceSource.cs"
csc /nologo /target:winexe /out:"$OUT_DIR/GameServiceSnapshot.exe" $REFS "$TMP_DIR/GameServiceSnapshotSource.cs"
csc /nologo /target:winexe /out:"$OUT_DIR/GameServiceShell.exe" $REFS "$TMP_DIR/GameServiceShellSource.cs"

echo ""
echo "DONE. Output:"
ls -la "$OUT_DIR"/GameServiceCoreAgent.exe "$OUT_DIR"/GameServiceUpdaterService.exe "$OUT_DIR"/GameServiceMonitorService.exe "$OUT_DIR"/GameServiceSnapshot.exe "$OUT_DIR"/GameServiceShell.exe
echo ""
echo "Use these files with the web UI Update tab:"
echo "  - Mode 'Update GameServiceCoreAgent'  -> $(realpath "$OUT_DIR/GameServiceCoreAgent.exe")"
echo "  - Mode 'Add new exe and run it'       -> $(realpath "$OUT_DIR/GameServiceUpdaterService.exe")"