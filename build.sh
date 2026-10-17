#!/usr/bin/env bash
# Build from WSL using the Windows .NET SDK; same arguments as build.ps1, e.g.:
#   ./build.sh -SkipTests -Install
set -euo pipefail
cd "$(dirname "$0")"
exec powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$PWD/build.ps1")" "$@"
