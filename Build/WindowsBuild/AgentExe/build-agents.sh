#!/usr/bin/env bash
# Fresh build for Windows agents - zero Unity dep
# Builds GameServiceWindows.exe (legacy) + Actions.exe + Database.exe
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WIN_DIR="$SCRIPT_DIR/../../../DancingLineClone/Assets/Scripts/GameService/Windows"
if [[ ! -d "$WIN_DIR" ]]; then
  WIN_DIR="/home/darkgreen/Desktop/GameService/DancingLineClone/Assets/Scripts/GameService/Windows"
fi
OUT_DIR="${1:-$SCRIPT_DIR}"
mkdir -p "$OUT_DIR"

build_one() {
  local src="$1"
  local out="$2"
  local name="$3"
  echo "Building $name: $src -> $out"
  if command -v mcs >/dev/null 2>&1; then
    # Prefer mcs for Mono.Data.Sqlite support
    mcs -target:exe -out:"$out" -r:System -r:System.Core -r:System.Management -r:System.Drawing -r:Mono.Data.Sqlite -r:System.Data "$src" 2>&1 || {
      echo "mcs failed for $name, trying csc..."
      if command -v csc >/dev/null 2>&1; then
        csc /nologo /target:exe /out:"$out" /r:System.dll,System.Core.dll /r:System.Management.dll /r:System.Drawing.dll /r:Mono.Data.Sqlite.dll /r:System.Data.dll "$src"
      else
        return 1
      fi
    }
  elif command -v csc >/dev/null 2>&1; then
    csc /nologo /target:exe /out:"$out" /r:System.dll,System.Core.dll /r:System.Management.dll /r:System.Drawing.dll "$src"
  else
    echo "ERROR: csc/mcs not found" >&2
    return 1
  fi
  ls -lh "$out"
}

# Legacy
build_one "$WIN_DIR/GameServiceWindows.cs" "$OUT_DIR/GameServiceWindows.exe" "GameServiceWindows"
# New split exes
build_one "$WIN_DIR/GameServiceActions.cs" "$OUT_DIR/Actions.exe" "Actions"
build_one "$WIN_DIR/GameServiceDatabase.cs" "$OUT_DIR/Database.exe" "Database"

echo "DONE all:"
ls -lh "$OUT_DIR"/GameServiceWindows.exe "$OUT_DIR"/Actions.exe "$OUT_DIR"/Database.exe 2>&1
echo "Deploy: scp -> VM C:/ProgramData/GameServiceWindows/ and schtasks /create /tn GameServiceActions /tr ... /sc ONSTART /ru SYSTEM /rl HIGHEST"
echo "        schtasks /create /tn GameServiceDatabase /tr ... /sc ONSTART /ru SYSTEM /rl HIGHEST"
