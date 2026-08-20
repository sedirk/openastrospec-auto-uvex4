@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-reduction-gui-shortcut.ps1"
if errorlevel 1 (
  echo.
  echo Failed to create the OpenAstroSpec Spectral Studio - UVEX4 shortcut.
  pause
  exit /b 1
)
echo.
echo The desktop shortcut has been created or refreshed.
pause
endlocal
