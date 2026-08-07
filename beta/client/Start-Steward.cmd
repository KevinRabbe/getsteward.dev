@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Steward.ps1"
if errorlevel 1 (
  echo.
  echo Steward could not start. Review the error above.
  pause
  exit /b 1
)
exit /b 0
