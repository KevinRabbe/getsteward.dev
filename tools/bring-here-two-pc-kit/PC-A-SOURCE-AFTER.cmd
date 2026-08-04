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

set /p "WORLD_ID=Paste the same exact World ID on PC A: "
set /p "SOURCE_EVIDENCE=Paste the full path to pc-a-source-before.json: "
set /p "TARGET_EVIDENCE=Paste the full path to pc-b-target-after.json: "
if "%WORLD_ID%"=="" (
  echo [FAIL] World ID is required.
  pause
  exit /b 1
)
if not exist "%SOURCE_EVIDENCE%" (
  echo [FAIL] Source-before evidence file not found.
  pause
  exit /b 1
)
if not exist "%TARGET_EVIDENCE%" (
  echo [FAIL] Target-after evidence file not found.
  pause
  exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
"%PROBE%" --source-after --world-id "%WORLD_ID%" --build-manifest "%MANIFEST%" --source-evidence "%SOURCE_EVIDENCE%" --target-evidence "%TARGET_EVIDENCE%" --output "%OUTDIR%\pc-a-source-after.json"
set "RESULT=%ERRORLEVEL%"

if not "%RESULT%"=="0" (
  echo.
  echo [FAIL] Source preservation was not proven. Do not mark the physical test complete.
) else (
  echo.
  echo [OK] Linked source-after evidence written to:
  echo %OUTDIR%\pc-a-source-after.json
)

pause
exit /b %RESULT%
