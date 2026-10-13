@echo off
rem Starts the hostwatch server. First run creates .venv and installs the requirements.
rem Usage: run.cmd          real server
rem        run.cmd demo     made-up office for screenshots
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\uvicorn.exe" (
  python -m venv .venv || goto :nopython
  ".venv\Scripts\python" -m pip install -q -r server\requirements.txt || goto :failed
)
if /i "%~1"=="demo" set HOSTWATCH_DEMO=1
if "%HOSTWATCH_PORT%"=="" set HOSTWATCH_PORT=8765
cd server
"..\.venv\Scripts\uvicorn" app:app --host 0.0.0.0 --port %HOSTWATCH_PORT%
exit /b

:nopython
echo Python not found. Install Python 3.9+ from python.org and tick "Add python.exe to PATH".
:failed
pause
exit /b 1
