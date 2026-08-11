@echo off
setlocal
set /p WORLD_ID=Enter Steward World ID: 
if "%WORLD_ID%"=="" (
  echo World ID is required.
  pause
  exit /b 1
)
set "OUTPUT=%USERPROFILE%\Desktop\steward-peer-evidence-%COMPUTERNAME%-%RANDOM%.json"
"%~dp0acceptance-tools\SharedWorlds.PeerWorldProbe.exe" --world "%WORLD_ID%" --package-root "%~dp0" --output "%OUTPUT%"
if errorlevel 1 (
  echo.
  echo Evidence capture failed.
  pause
  exit /b 1
)
echo.
echo Evidence written to:
echo %OUTPUT%
pause
