@echo off
setlocal
rem hostwatch agent installer - per user, no admin rights needed.
rem Copies the ready-made hostwatch.exe to this user and runs it at every logon.
rem No script launcher, no hidden PowerShell. (If only the source is available,
rem it falls back to building hostwatch.exe with the C# compiler Windows ships with.)
rem Downloaded from the server, SERVER below already holds its address.
set "SERVER=__SERVER_URL__"
if "%SERVER:~0,2%"=="__" set /p "SERVER=hostwatch server address (e.g. http://192.168.31.6:8765): "
if "%SERVER%"=="" exit /b 1

set "DIR=%LOCALAPPDATA%\Hostwatch"
if not exist "%DIR%" mkdir "%DIR%"

rem Stop a running agent (this .exe, and the old PowerShell-based one if present)
taskkill /f /im hostwatch.exe >nul 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -like '*hostwatch-agent.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }" >nul 2>&1
if exist "%DIR%\run.vbs" del /q "%DIR%\run.vbs" >nul 2>&1
if exist "%DIR%\hostwatch-agent.ps1" del /q "%DIR%\hostwatch-agent.ps1" >nul 2>&1
if exist "%DIR%\hostwatch.exe" del /q "%DIR%\hostwatch.exe" >nul 2>&1

rem Get the ready-made exe: copy it if it sits next to this installer, otherwise fetch it
if exist "%~dp0hostwatch.exe" (
  copy /y "%~dp0hostwatch.exe" "%DIR%\hostwatch.exe" >nul
) else (
  powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing '%SERVER%/agent/hostwatch.exe' -OutFile '%DIR%\hostwatch.exe'" >nul 2>&1
)
if exist "%DIR%\hostwatch.exe" goto :haveexe

rem Fallback: no exe available, build it from source with the compiler bundled in Windows
if exist "%~dp0hostwatch-agent.cs" (
  copy /y "%~dp0hostwatch-agent.cs" "%DIR%\hostwatch-agent.cs" >nul
) else (
  powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing '%SERVER%/agent/hostwatch-agent.cs' -OutFile '%DIR%\hostwatch-agent.cs'"
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto :haveexe
if exist "%DIR%\hostwatch-agent.cs" "%CSC%" /nologo /target:winexe /optimize+ /out:"%DIR%\hostwatch.exe" /r:System.Management.dll /r:Microsoft.VisualBasic.dll "%DIR%\hostwatch-agent.cs"

:haveexe
if not exist "%DIR%\hostwatch.exe" (
  echo Could not get hostwatch.exe from %SERVER%
  pause
  exit /b 1
)

rem Start at every logon, straight to the exe
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Hostwatch /t REG_SZ /d "\"%DIR%\hostwatch.exe\" -Server \"%SERVER%\"" /f >nul

start "" "%DIR%\hostwatch.exe" -Server "%SERVER%"
echo.
echo hostwatch agent installed and running - look for the dot icon in the tray.
echo Reports go to %SERVER%
timeout /t 5 >nul
