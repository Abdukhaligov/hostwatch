@echo off
rem Builds hostwatch-agent.exe (resident monitor) and hostwatch-setup.exe (installer) from the C#
rem sources with the compiler bundled in Windows. Run on any Windows 10/11 machine (no SDK needed),
rem then commit the two new exe files. hostwatch-common.cs is compiled into both.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4 not found - cannot build.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:hostwatch-agent.exe ^
  /r:System.Management.dll /r:Microsoft.VisualBasic.dll hostwatch-common.cs hostwatch-agent.cs
if errorlevel 1 (
  echo Agent build failed.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:hostwatch-setup.exe ^
  /r:Microsoft.VisualBasic.dll hostwatch-common.cs hostwatch-setup.cs
if errorlevel 1 (
  echo Setup build failed.
  pause
  exit /b 1
)

echo Built hostwatch-agent.exe and hostwatch-setup.exe
pause
