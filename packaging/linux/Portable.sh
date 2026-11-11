#!/bin/sh
set -eu
cd -- "$(dirname -- "$0")"
portable=false
explicit=false
value=false
remaining=$#
while [ "$remaining" -gt 0 ]; do
    arg=$1
    shift
    if "$value"; then
        set -- "$@" "$arg"
        value=false
        remaining=$((remaining - 1))
        continue
    fi
    case "$arg" in
        --portable) portable=true ;;
        --data-dir) explicit=true; value=true; set -- "$@" "$arg" ;;
        *) set -- "$@" "$arg" ;;
    esac
    remaining=$((remaining - 1))
done
if "$portable" && "$explicit"; then
    printf '%s\n' 'Choose either --portable or --data-dir.' >&2
    exit 2
fi
if ! "$explicit" && { "$portable" || [ -f data/state.sqlite ] || [ -f data/companion.sqlite ]; }; then
    set -- --data-dir "$PWD/data" "$@"
fi
exec ./app/WarframeCompanion.Desktop "$@"
