#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"
if [[ "${1:-}" == "--detach" || "${1:-}" == "-d" ]]; then
  shift
  log="${XDG_CACHE_HOME:-$HOME/.cache}/WarframeCompanion"
  mkdir -p "$log"
  setsid nohup "$0" "$@" >>"$log/app.log" 2>&1 </dev/null &
  echo "Warframe Companion started. Log: $log/app.log"
  exit 0
fi
case "${1:-}" in
  quit|trigger|riven)
    action="$1"
    case "$action" in trigger) action="rewards/scan";; riven) action="riven/scan";; esac
    exec curl --fail --silent --show-error -X POST "http://127.0.0.1:${WFC_PORT:-8642}/api/$action"
    ;;
esac
if command -v dotnet >/dev/null && command -v cmake >/dev/null && pkg-config --exists webkit2gtk-4.1 gtk-layer-shell-0; then
  cmake -S companion/Native -B companion/Native/build -DCMAKE_BUILD_TYPE=Release
  cmake --build companion/Native/build --parallel
  dotnet build companion/Desktop -c Release -p:RestoreLockedMode=true
  exec dotnet companion/Desktop/bin/Release/net10.0/WarframeCompanion.Desktop.dll "$@"
fi
if [[ -e /etc/NIXOS ]] && command -v nix-shell >/dev/null && [[ -z "${IN_NIX_SHELL:-}" ]]; then
  printf -v arguments '%q ' "$@"
  exec nix-shell shell.nix --run "./run.sh $arguments"
fi
echo "Install .NET SDK 10, CMake, C++, GTK3, WebKitGTK 4.1 and gtk-layer-shell. See companion/README.md." >&2
exit 1
