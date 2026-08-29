#!/usr/bin/env bash
# Fresh build for Windows Browser agent only - zero Unity dep, no old WindowsServiceManager extraction.
# Compiles DancingLineClone/Assets/Scripts/GameService/Windows/WindowsBrowserAgent.cs -> WindowsBrowser.exe
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BROWSER_AGENT="/home/darkgreen/Desktop/GameService/DancingLineClone/Assets/Scripts/GameService/Windows/WindowsBrowserAgent.cs"
if [[ ! -f "$BROWSER_AGENT" ]]; then
  # fallback relative
  for p in "$SCRIPT_DIR/../../../DancingLineClone/Assets/Scripts/GameService/Windows/WindowsBrowserAgent.cs" "$SCRIPT_DIR/../../DancingLineClone/Assets/Scripts/GameService/Windows/WindowsBrowserAgent.cs"; do
    if [[ -f "$p" ]]; then BROWSER_AGENT="$p"; break; fi
  done
fi
if [[ ! -f "$BROWSER_AGENT" ]]; then
  echo "ERROR: cannot find WindowsBrowserAgent.cs at $BROWSER_AGENT" >&2
  exit 1
fi
if ! command -v csc >/dev/null 2>&1; then
  echo "ERROR: mono csc not found" >&2
  exit 1
fi
OUT_DIR="${1:-$SCRIPT_DIR}"
mkdir -p "$OUT_DIR"
REFS="/r:System.dll,System.Core.dll,System.Security.dll"
echo "Building fresh WindowsBrowserAgent: $BROWSER_AGENT"
csc /nologo /target:winexe /out:"$OUT_DIR/WindowsBrowser.exe" $REFS /r:System.Management.dll "$BROWSER_AGENT"
echo "DONE:"
ls -lh "$OUT_DIR/WindowsBrowser.exe"
echo "Use: scp $OUT_DIR/WindowsBrowser.exe -> Windows VM and schtasks create /tn WindowsBrowser /tr ... /sc ONSTART /ru SYSTEM"
