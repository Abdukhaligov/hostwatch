#!/bin/sh
# Starts the hostwatch server. Creates .venv on first run and (re)installs the requirements when they change.
# Usage: ./run.sh          real server
#        ./run.sh demo     made-up office for screenshots
set -e
cd "$(dirname "$0")"
[ -x .venv/bin/python ] || python3 -m venv .venv
# Reinstall whenever requirements.txt changed since the last install (e.g. after a git pull).
if ! cmp -s server/requirements.txt .venv/requirements.installed; then
  .venv/bin/pip install -q -r server/requirements.txt
  cp server/requirements.txt .venv/requirements.installed
fi
[ "$1" = demo ] && export HOSTWATCH_DEMO=1
cd server
# Serve over https when a cert + key are given (HOSTWATCH_TLS_CERT / HOSTWATCH_TLS_KEY), so the agent
# reports encrypted. Generate a self-signed pair with ../tools/make-cert.sh; the setup trusts it on clients.
TLS=""
if [ -n "$HOSTWATCH_TLS_CERT" ] && [ -n "$HOSTWATCH_TLS_KEY" ]; then
  TLS="--ssl-certfile $HOSTWATCH_TLS_CERT --ssl-keyfile $HOSTWATCH_TLS_KEY"
fi
exec ../.venv/bin/uvicorn app:app --host 0.0.0.0 --port "${HOSTWATCH_PORT:-8765}" $TLS
