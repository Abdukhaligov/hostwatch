#!/bin/sh
# Starts the hostwatch server. First run creates .venv and installs the requirements.
# Usage: ./run.sh          real server
#        ./run.sh demo     made-up office for screenshots
set -e
cd "$(dirname "$0")"
if [ ! -x .venv/bin/uvicorn ]; then
  python3 -m venv .venv
  .venv/bin/pip install -q -r server/requirements.txt
fi
[ "$1" = demo ] && export HOSTWATCH_DEMO=1
cd server
exec ../.venv/bin/uvicorn app:app --host 0.0.0.0 --port "${HOSTWATCH_PORT:-8765}"
