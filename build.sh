#!/usr/bin/env bash
# Taco Factory build for Linux hosts (e.g. the game server machine).
set -euo pipefail
cd "$(dirname "$0")"
python3 tools/banned_terms.py
dotnet build TacoFactory.sln -c Release
dotnet test TacoFactory.sln -c Release --no-build
