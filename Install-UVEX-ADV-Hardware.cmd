@echo off
setlocal
cd /d "%~dp0"

fltmc >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator permission...
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

tasklist /FI "IMAGENAME eq NINA.exe" 2>NUL | find /I "NINA.exe" >NUL
if not errorlevel 1 (
  echo N.I.N.A. is running. Close it before installation so the services and plugin cannot be left at different versions.
  pause
  exit /b 2
)

echo Building and testing OpenAstroSpec Auto - UVEX4...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\build.ps1"
if errorlevel 1 goto failed

echo Preflighting the complete hardware deployment before changing installed components...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-service.ps1" -EnableHardware -PreflightOnly
if errorlevel 1 goto failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-qhy-service.ps1" -EnableHardware -PreflightOnly
if errorlevel 1 goto failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-phd2-watchdog.ps1" -PreflightOnly
if errorlevel 1 goto failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-nina-plugin.ps1" -PreflightOnly
if errorlevel 1 goto failed

echo Installing the automatic UVEX COM5 service and desktop shortcut...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-service.ps1" -EnableHardware
if errorlevel 1 goto failed

echo Installing the isolated QHYminiCam8M service in exact-ID hardware mode...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-qhy-service.ps1" -EnableHardware
if errorlevel 1 goto failed

echo Installing the independent PHD2 safety watchdog...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-phd2-watchdog.ps1"
if errorlevel 1 goto failed

echo Installing the N.I.N.A. plugin...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\install-nina-plugin.ps1"
if errorlevel 1 goto failed

echo.
echo Installation completed. UVEX, QHY, the PHD2 safety watchdog and the N.I.N.A. plugin are now one matched build.
echo Use the "OpenAstroSpec Auto - UVEX4 Manager" desktop shortcut and the OpenAstroSpec panels inside N.I.N.A. from now on.
pause
exit /b 0

:failed
echo.
echo Installation failed. Review the error above; no hardware motion was requested by this installer.
pause
exit /b 1
