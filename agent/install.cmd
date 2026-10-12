@echo off
setlocal
rem hostwatch agent installer - per user, no admin rights needed.
rem Downloaded from the server, SERVER below already holds its address.
set "SERVER=__SERVER_URL__"
if "%SERVER:~0,2%"=="__" set /p "SERVER=hostwatch server address (e.g. http://192.168.31.6:8765): "
if "%SERVER%"=="" exit /b 1

set "DIR=%LOCALAPPDATA%\Hostwatch"
if not exist "%DIR%" mkdir "%DIR%"

rem Stop a running copy so the new script is picked up
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -like '*hostwatch-agent.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"

if exist "%~dp0hostwatch-agent.ps1" (
  copy /y "%~dp0hostwatch-agent.ps1" "%DIR%\hostwatch-agent.ps1" >nul
) else (
  powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing '%SERVER%/agent/hostwatch-agent.ps1' -OutFile '%DIR%\hostwatch-agent.ps1'"
)
if not exist "%DIR%\hostwatch-agent.ps1" (
  echo Could not get hostwatch-agent.ps1 from %SERVER%
  pause
  exit /b 1
)

rem Hidden launcher, started at every logon
> "%DIR%\run.vbs" echo CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""%DIR%\hostwatch-agent.ps1"" -Server ""%SERVER%""", 0, False
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Hostwatch /t REG_SZ /d "wscript.exe \"%DIR%\run.vbs\"" /f >nul

start "" wscript.exe "%DIR%\run.vbs"
echo.
echo hostwatch agent installed and running - look for the dot icon in the tray.
echo Reports go to %SERVER%
timeout /t 5 >nul
