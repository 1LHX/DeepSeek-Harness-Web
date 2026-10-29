@echo off
rem apply-panel-update.cmd - rebuild dsh-panel.exe from dsh-panel.cs and restart the panel.
rem
rem Why this exists: a running dsh-panel.exe holds its own image file open, so csc cannot
rem overwrite it while the panel is up -- and closing the window only minimizes to the
rem tray. This script ends the panel process, rebuilds, and starts it again.
rem
rem The DSH service is NOT stopped: the panel's Job Object is created without
rem KILL_ON_JOB_CLOSE, so the node service keeps running when the panel process ends.
rem After the restart the panel finds the port already open and shows the page right
rem away -- the conversation lives in the service, not in the window.
setlocal
echo ============================================================
echo  This closes the panel window and rebuilds dsh-panel.exe.
echo  The DSH service keeps running (the port stays up);
echo  the page reloads as soon as the panel comes back.
echo ============================================================
choice /C YN /M "Continue"
if errorlevel 2 exit /b 1

rem Gate FIRST, while the panel is still alive:
rem rebuilding over a broken-script tree just re-ships the same silent failure (a BOM-less
rem .ps1 with CJK text makes start-service.ps1 unparseable, and the panel then only says
rem "start failed" with nothing in the logs). Checking before the kill means a failed gate
rem leaves the user with a working panel instead of no panel at all.
echo [apply] encoding/syntax gate ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-scripts.ps1" -Quiet
if errorlevel 1 (
    echo [apply] GATE FAILED - fix the FAIL lines above first. Nothing was rebuilt or closed.
    pause
    exit /b 1
)

echo [apply] stopping the panel ...
taskkill /F /IM dsh-panel.exe >nul 2>&1
rem give the WebView2 child processes a moment to release the files
ping -n 3 127.0.0.1 >nul

call "%~dp0build-dsh-panel.cmd"
if errorlevel 1 (
    echo [apply] build failed - dsh-panel.exe was NOT replaced.
    pause
    exit /b 1
)

echo [apply] starting the panel ...
start "" "%~dp0dsh-panel.exe"
echo [apply] done.
ping -n 3 127.0.0.1 >nul
endlocal
