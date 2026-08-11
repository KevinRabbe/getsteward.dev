@echo off
setlocal
"%~dp0..\acceptance-tools\SharedWorlds.PeerWorldProbe.exe" --list
if errorlevel 1 (
  echo.
  echo Steward peer World listing failed.
  pause
  exit /b 1
)
echo.
pause
