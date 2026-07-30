@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Safe World - PC B Viewer Check

set "TOOLS=%~dp0"
for %%I in ("%TOOLS%..") do set "PACKAGE=%%~fI"
set "PROBE=%TOOLS%SharedWorlds.PortableWorldProbe.exe"
set "MANIFEST=%PACKAGE%\acceptance-build.json"
set "EVIDENCE=%USERPROFILE%\Desktop\SafeWorld-Two-PC-Evidence"

echo.
echo Safe World portable World test - PC B viewer evidence
echo =====================================================
echo.
echo Run this AFTER:
echo   1. Safe World was already running;
echo   2. the transferred .safeworld was opened/imported;
echo   3. exactly one Safe World desktop process remains running.
echo.
echo Right-click each requested file, choose Copy as path, then paste it below.
echo.
set /p "WORLD=Full path to transferred .safeworld: "
set "WORLD=%WORLD:"=%"
set /p "CREATOR=Full path to PC A pc-a-evidence.json: "
set "CREATOR=%CREATOR:"=%"

if not exist "%WORLD%" (
  echo.
  echo [FAIL] The selected .safeworld file does not exist.
  pause
  exit /b 1
)
if not exist "%CREATOR%" (
  echo.
  echo [FAIL] The selected pc-a-evidence.json file does not exist.
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

"%PROBE%" --viewer --file "%WORLD%" --build-manifest "%MANIFEST%" --creator-evidence "%CREATOR%" --output "%EVIDENCE%\pc-b-evidence.json"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  echo [OK] PC B evidence was written to:
  echo      %EVIDENCE%\pc-b-evidence.json
  echo.
  echo Continue with the real Factorio creator-marker and viewer-persistence checks.
) else (
  echo [FAIL] Viewer evidence did not pass. Do not continue until the message above is resolved.
)
echo.
pause
exit /b %RESULT%
