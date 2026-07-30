@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Safe World - PC A Creator Check

set "TOOLS=%~dp0"
for %%I in ("%TOOLS%..") do set "PACKAGE=%%~fI"
set "PROBE=%TOOLS%SharedWorlds.PortableWorldProbe.exe"
set "MANIFEST=%PACKAGE%\acceptance-build.json"
set "EVIDENCE=%USERPROFILE%\Desktop\SafeWorld-Two-PC-Evidence"

echo.
echo Safe World portable World test - PC A creator evidence
echo ======================================================
echo.
echo Run this AFTER:
echo   1. the creator marker was saved through a normal Safe World Continue cycle;
echo   2. Share a Copy produced the final .safeworld file.
echo.
echo Right-click the .safeworld file, choose Copy as path, then paste it below.
echo.
set /p "WORLD=Full path to exported .safeworld: "
set "WORLD=%WORLD:"=%"

if not exist "%WORLD%" (
  echo.
  echo [FAIL] The selected .safeworld file does not exist.
  pause
  exit /b 1
)
if not exist "%PROBE%" (
  echo.
  echo [FAIL] The packaged evidence probe is missing.
  pause
  exit /b 1
)
if not exist "%MANIFEST%" (
  echo.
  echo [FAIL] acceptance-build.json is missing beside Safe World.
  pause
  exit /b 1
)

if not exist "%EVIDENCE%" mkdir "%EVIDENCE%"

"%PROBE%" --creator --file "%WORLD%" --build-manifest "%MANIFEST%" --output "%EVIDENCE%\pc-a-evidence.json"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo [OK] PC A evidence was written to:
  echo      %EVIDENCE%\pc-a-evidence.json
  echo.
  echo Transfer BOTH the .safeworld file and pc-a-evidence.json to PC B.
) else (
  echo [FAIL] Creator evidence did not pass. Do not continue to PC B until the message above is resolved.
)
echo.
pause
exit /b %RESULT%
