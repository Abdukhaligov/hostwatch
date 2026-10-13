@echo off
rem Builds hostwatch.exe from hostwatch-agent.cs with the C# compiler bundled in Windows.
rem Run on any Windows 10/11 machine (no SDK needed), then commit the new agent\hostwatch.exe.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4 not found - cannot build.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:hostwatch.exe ^
  /r:System.Management.dll /r:Microsoft.VisualBasic.dll hostwatch-agent.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

echo Built hostwatch.exe
pause
