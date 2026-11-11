#!/bin/sh
set -eu
cd -- "$(dirname -- "$0")"
exec ./app/WarframeCompanion.Desktop --data-dir "$PWD/data" "$@"
