#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
runtime="${1:-linux-x64}"
"${DOTNET:-dotnet}" publish hakchi_cli.csproj -c Release -r "$runtime" \
  --self-contained true -p:PublishSingleFile=true -o "bin/publish/$runtime"
printf '\nRun: %s/bin/publish/%s/hakchi --help\n' "$PWD" "$runtime"
