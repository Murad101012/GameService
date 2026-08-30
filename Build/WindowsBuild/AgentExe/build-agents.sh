#!/usr/bin/env bash
# Fresh build for Windows agent - zero Unity dep
# Compiles DancingLineClone/Assets/Scripts/GameService/Windows/GameServiceWindows.cs -> GameServiceWindows.exe
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AGENT_SRC="$SCRIPT_DIR/../../../DancingLineClone/Assets/Scripts/GameService/Windows/GameServiceWindows.cs"
if [[ ! -f "$AGENT_SRC" ]]; then
  AGENT_SRC="/home/darkgreen/Desktop/GameService/DancingLineClone/Assets/Scripts/GameService/Windows/GameServiceWindows.cs"
fi
if [[ ! -f "$AGENT_SRC" ]]; then
  echo "ERROR: cannot find GameServiceWindows.cs at $AGENT_SRC" >&2
  exit 1
fi
if ! command -v csc >/dev/null 2>&1 && ! command -v mcs >/dev/null 2>&1; then
  echo "ERROR: csc/mcs not found" >&2
  exit 1
fi
OUT_DIR="${1:-$SCRIPT_DIR}"
mkdir -p "$OUT_DIR"
echo "Building fresh GameServiceWindows: $AGENT_SRC -> $OUT_DIR/GameServiceWindows.exe"
# Try csc (Mono) first, fallback to mcs
if command -v csc >/dev/null 2>&1; then
  csc /nologo /target:exe /out:"$OUT_DIR/GameServiceWindows.exe" /r:System.dll,System.Core.dll /r:System.Management.dll /r:System.Drawing.dll "$AGENT_SRC" || {
    echo "csc failed, trying mcs..."
    mcs -target:exe -out:"$OUT_DIR/GameServiceWindows.exe" -r:System -r:System.Core -r:System.Management -r:System.Drawing "$AGENT_SRC"
  }
else
  mcs -target:exe -out:"$OUT_DIR/GameServiceWindows.exe" -r:System -r:System.Core -r:System.Management -r:System.Drawing "$AGENT_SRC"
fi
echo "DONE:"
ls -lh "$OUT_DIR/GameServiceWindows.exe"
echo "Deploy: scp $OUT_DIR/GameServiceWindows.exe -> VM C:/ProgramData/GameServiceWindows/ and schtasks /create /tn GameServiceWindows /tr ... /sc ONSTART /ru SYSTEM /rl HIGHEST"
