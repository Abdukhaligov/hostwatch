@echo off
rem Starts the hostwatch server. Creates .venv on first run and (re)installs the requirements when they change.
rem Usage: run.cmd          real server
rem        run.cmd demo     made-up office for screenshots
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
  python -m venv .venv || goto :nopython
)
rem Reinstall whenever requirements.txt changed since the last install (e.g. after a git pull).
fc /b server\requirements.txt .venv\requirements.installed >nul 2>&1 || (
  ".venv\Scripts\python" -m pip install -q -r server\requirements.txt || goto :failed
  copy /y server\requirements.txt .venv\requirements.installed >nul
)
if /i "%~1"=="demo" set HOSTWATCH_DEMO=1
if "%HOSTWATCH_PORT%"=="" set HOSTWATCH_PORT=8765
rem Serve over https when a cert + key are given (HOSTWATCH_TLS_CERT / HOSTWATCH_TLS_KEY), so the agent
rem reports encrypted. Generate a self-signed pair with tools\make-cert.ps1; the setup trusts it on clients.
set "TLS="
if defined HOSTWATCH_TLS_CERT if defined HOSTWATCH_TLS_KEY set "TLS=--ssl-certfile "%HOSTWATCH_TLS_CERT%" --ssl-keyfile "%HOSTWATCH_TLS_KEY%""
cd server
"..\.venv\Scripts\uvicorn" app:app --host 0.0.0.0 --port %HOSTWATCH_PORT% %TLS%
exit /b

:nopython
echo Python not found. Install Python 3.9+ from python.org and tick "Add python.exe to PATH".
:failed
pause
exit /b 1
