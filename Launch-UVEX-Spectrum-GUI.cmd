@echo off
setlocal
set "ROOT=%~dp0"
set "PYTHONW=%ROOT%reduction\.venv\Scripts\pythonw.exe"
if not exist "%PYTHONW%" (
  echo Python environment not found: %PYTHONW%
  echo Please follow reduction\README.md to install it first.
  pause
  exit /b 1
)
start "" /D "%ROOT%reduction" "%PYTHONW%" -m uvex_reduce.gui
endlocal
