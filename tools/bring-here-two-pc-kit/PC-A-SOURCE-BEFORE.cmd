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

set /p "WORLD_ID=Paste the exact World ID shown by Safe World on PC A: "
if "%WORLD_ID%"=="" (
  echo [FAIL] World ID is required.
  pause
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
"%PROBE%" --source-before --world-id "%WORLD_ID%" --build-manifest "%MANIFEST%" --output "%OUTDIR%\pc-a-source-before.json"
set "RESULT=%ERRORLEVEL%"

if not "%RESULT%"=="0" (
  echo.
  echo [FAIL] Source-before evidence was not collected. Keep Safe World online, refresh, and retry after location publication completes.
) else (
  echo.
  echo [OK] Evidence written to:
  echo %OUTDIR%\pc-a-source-before.json
)

pause
exit /b %RESULT%
