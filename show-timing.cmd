@echo off
rem show-timing.cmd - print the last boot's phase timings (cmd-friendly, no PowerShell needed)
rem
rem boot-timing.log holds two kinds of lines: [panel] marks written by dsh-panel.exe and
rem [start-service] phase lines written by start-service.ps1. This script pulls out the
rem three answers you actually want:
rem   1) how long node took to open the port (the cold-start number that matters)
rem   2) the panel's own phases (window / dsh entry / WebView2 / navigate)
rem   3) which entry was used, and whether a port fallback happened
rem Keep this file ASCII-only: cmd.exe parses it in the OEM codepage, so CJK text breaks it.
setlocal enabledelayedexpansion
set "LOG=%~dp0run\boot-timing.log"
if not exist "%LOG%" (
    echo [timing] %LOG% not found - the service has not been started yet.
    exit /b 0
)

echo ============================================================
echo  LAST "PORT READY"  (node spawn -^> port listening)
echo ============================================================
set "LAST="
for /f "usebackq delims=" %%L in (`findstr /C:"PORT READY" "%LOG%"`) do set "LAST=%%L"
if defined LAST (echo   !LAST!) else (echo   none)

echo.
echo ============================================================
echo  PANEL / LAUNCHER MARKS  (last launch)
echo ============================================================
for /f "usebackq delims=" %%L in (`findstr /C:"[panel]" /C:"[launcher]" "%LOG%"`) do echo   %%L

echo.
echo ============================================================
echo  ENTRY / FALLBACKS
echo ============================================================
set "ENTRY="
for /f "usebackq delims=" %%L in (`findstr /C:"port free:" "%LOG%"`) do set "ENTRY=%%L"
if defined ENTRY (echo   !ENTRY!) else (echo   none)
findstr /C:"fallback" /C:"already running" /C:"already served" "%LOG%"

echo.
echo ============================================================
echo  DEFENDER EXCLUSIONS  (only [ok] lines were really applied)
echo ============================================================
if exist "%~dp0run\defender-exclusions.log" (
    findstr /C:"[ok]" /C:"apply " "%~dp0run\defender-exclusions.log"
) else (
    echo   no run\defender-exclusions.log - exclusions were never applied
)
endlocal
