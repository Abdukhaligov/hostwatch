@echo off
rem Removes the hostwatch agent for the current user.
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Hostwatch /f >nul 2>&1
taskkill /f /im hostwatch.exe >nul 2>&1
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -like '*hostwatch-agent.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }" >nul 2>&1
rmdir /s /q "%LOCALAPPDATA%\Hostwatch" 2>nul
echo hostwatch agent removed.
timeout /t 3 >nul
