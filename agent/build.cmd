@echo off
rem Builds hostwatch-agent.exe (resident monitor) and hostwatch-setup.exe (installer) from the C#
rem sources with the compiler bundled in Windows. Run on any Windows 10/11 machine (no SDK needed).
rem
rem It bakes the server address into both exes, so the agent installs without typing -Server - handy
rem for mass rollout. Usage:
rem   build.cmd                     auto-detect this machine's LAN IP, port 8765  -> http://<ip>:8765
rem   build.cmd 192.168.31.6        that IP, port 8765                            -> http://192.168.31.6:8765
rem   build.cmd 192.168.31.6 9000   that IP and port                              -> http://192.168.31.6:9000
rem   build.cmd https://hostwatch:8765   a full URL, used as-is (use this for https)
rem
rem The baked address is only a default: -Server, the tray's Change server and the server_url push still
rem override it. hostwatch-common.cs is compiled into both exes; the baked value goes in a generated
rem hostwatch-build.cs (git-ignored, regenerated every build).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4 not found - cannot build.
  pause
  exit /b 1
)

set "SERVER=%~1"
set "PORT=%~2"
if "%PORT%"=="" set "PORT=8765"

if "%SERVER%"=="" (
  rem No argument: auto-detect the IPv4 of the up adapter that has a default gateway (the LAN IP).
  for /f "delims=" %%A in ('powershell -NoProfile -Command "(Get-NetIPConfiguration ^| Where-Object { $_.IPv4DefaultGateway -ne $null -and $_.NetAdapter.Status -eq 'Up' } ^| Select-Object -First 1).IPv4Address.IPAddress"') do set "IP=%%A"
  if "%IP%"=="" (
    echo Could not auto-detect the LAN IP. Pass it: build.cmd ^<ip^> [port]
    pause
    exit /b 1
  )
  set "SERVER=http://%IP%:%PORT%"
) else (
  rem An argument that doesn't start with http is a bare IP/host: wrap it as http://<arg>:<port>.
  echo %SERVER% | findstr /b /i "http" >nul
  if errorlevel 1 set "SERVER=http://%SERVER%:%PORT%"
)

echo Baking server address: %SERVER%
> hostwatch-build.cs echo namespace Hostwatch { static class BuildConfig { public const string DefaultServer = "%SERVER%"; } }

"%CSC%" /nologo /target:winexe /optimize+ /out:hostwatch-agent.exe ^
  /r:System.Management.dll /r:Microsoft.VisualBasic.dll hostwatch-common.cs hostwatch-build.cs hostwatch-agent.cs
if errorlevel 1 (
  echo Agent build failed.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:hostwatch-setup.exe ^
  /r:Microsoft.VisualBasic.dll hostwatch-common.cs hostwatch-build.cs hostwatch-setup.cs
if errorlevel 1 (
  echo Setup build failed.
  pause
  exit /b 1
)

echo Built hostwatch-agent.exe and hostwatch-setup.exe for %SERVER%
pause
