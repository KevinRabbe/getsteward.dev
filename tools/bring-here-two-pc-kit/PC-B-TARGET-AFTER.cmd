@echo off
setlocal EnableExtensions

set "TOOLS=%~dp0"
set "ROOT=%TOOLS%.."
set "PROBE=%TOOLS%SharedWorlds.BringHereProbe.exe"
set "MANIFEST=%ROOT%\acceptance-build.json"
set "OUTDIR=%USERPROFILE%\Desktop\SafeWorld-Bring-Here-Evidence"

if not exist "%PROBE%" (
  echo [FAIL] Bring Here probe not found: %PROBE%
  pause
  exit /b 1
)

set /p "WORLD_ID=Paste the exact World ID shown for the remote World on PC B: "
set /p "SOURCE_EVIDENCE=Paste the full path to pc-a-source-before.json: "
if "%WORLD_ID%"=="" (
  echo [FAIL] World ID is required.
  pause
  exit /b 1
)
if not exist "%SOURCE_EVIDENCE%" (
  echo [FAIL] Source evidence file not found.
  pause
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
"%PROBE%" --target-after --world-id "%WORLD_ID%" --build-manifest "%MANIFEST%" --source-evidence "%SOURCE_EVIDENCE%" --output "%OUTDIR%\pc-b-target-after.json"
set "RESULT=%ERRORLEVEL%"

if not "%RESULT%"=="0" (
  echo.
  echo [FAIL] Target-after evidence was not collected. Confirm Bring here completed, keep Safe World online, refresh, and retry after target location publication completes.
) else (
  echo.
  echo [OK] Evidence written to:
  echo %OUTDIR%\pc-b-target-after.json
)

pause
exit /b %RESULT%
