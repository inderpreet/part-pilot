#!/usr/bin/env bash
# Publish Inventory Tracker as a single self-contained executable.
#
#   ./scripts/publish.sh [--runtime <rid>] [--all] [--smoke] [--configuration Release]
#
# Produces publish/<rid>/InventoryTracker (one file, no .NET install needed, static assets embedded).
# Supported RIDs: win-x64, linux-x64, linux-arm64, osx-arm64. Defaults to the machine you are on.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/InventoryTracker/InventoryTracker.csproj"
all_rids=(win-x64 linux-x64 linux-arm64 osx-arm64)
configuration=Release
runtime=""
do_all=false
do_smoke=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --runtime|-r) runtime="$2"; shift 2 ;;
    --all) do_all=true; shift ;;
    --smoke) do_smoke=true; shift ;;
    --configuration|-c) configuration="$2"; shift 2 ;;
    -h|--help) sed -n '2,8p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null || { echo "The .NET SDK was not found on PATH. Install the .NET 10 SDK." >&2; exit 1; }

host_rid() {
  local os arch
  os="$(uname -s)"; arch="$(uname -m)"
  case "$os/$arch" in
    Linux/x86_64) echo linux-x64 ;;
    Linux/aarch64|Linux/arm64) echo linux-arm64 ;;
    Darwin/arm64) echo osx-arm64 ;;
    *) echo "" ;;
  esac
}

host="$(host_rid)"
if $do_all; then targets=("${all_rids[@]}")
elif [[ -n "$runtime" ]]; then targets=("$runtime")
elif [[ -n "$host" ]]; then targets=("$host")
else echo "Could not detect this platform; pass --runtime." >&2; exit 1
fi

for rid in "${targets[@]}"; do
  [[ " ${all_rids[*]} " == *" $rid "* ]] || { echo "Unsupported runtime: $rid" >&2; exit 2; }
  out="$root/publish/$rid"
  exe_name="InventoryTracker"; [[ "$rid" == win-* ]] && exe_name="InventoryTracker.exe"

  echo "==> Publishing $rid"
  rm -rf "$out"
  dotnet publish "$project" -c "$configuration" -r "$rid" -o "$out" --nologo

  [[ -f "$out/$exe_name" ]] || { echo "Expected $out/$exe_name was not produced." >&2; exit 1; }
  extra="$(find "$out" -type f ! -name "$exe_name")"
  if [[ -n "$extra" ]]; then
    echo "Unexpected extra files next to the executable:" >&2; echo "$extra" >&2
    exit 1
  fi
  size="$(du -h "$out/$exe_name" | cut -f1)"
  echo "    $out/$exe_name ($size)"

  if $do_smoke; then
    if [[ "$rid" == "$host" ]]; then
      "$root/scripts/smoke.sh" "$out/$exe_name"
    else
      echo "    not smoke-tested here (host is ${host:-unknown})"
    fi
  fi
done
