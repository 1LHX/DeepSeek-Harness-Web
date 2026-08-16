@echo off
rem run-tests.cmd - build + GUI smoke test for dsh-panel
rem Note: does NOT touch the running service. Full service test: dsh-panel.exe --selftest
setlocal
set "DIR=%~dp0"

echo [test] Step 1/3: build ...
call "%DIR%build-dsh-panel.cmd"
if errorlevel 1 (
    echo [test] FAIL: build failed.
    exit /b 1
)

echo [test] Step 2/3: check for a running panel instance ...
tasklist /FI "IMAGENAME eq dsh-panel.exe" | findstr /i "dsh-panel.exe" >nul
if not errorlevel 1 (
    echo [test] Panel is already running - skipping launch/kill, build only.
    echo [test] PASS: build OK.
    exit /b 0
)

echo [test] Step 3/3: launch panel for a 4s smoke test ...
start "" "%DIR%dsh-panel.exe"
timeout /t 4 /nobreak >nul
tasklist /FI "IMAGENAME eq dsh-panel.exe" | findstr /i "dsh-panel.exe" >nul
if errorlevel 1 (
    echo [test] FAIL: panel exited during smoke test. See logs\dsh-web.err.log
    exit /b 1
)
echo [test] Stopping smoke-test instance (the service is not affected) ...
taskkill /IM dsh-panel.exe /F >nul 2>&1
echo [test] PASS: build + GUI smoke test OK.
echo [test] Optional full service self-test (stops the service): dsh-panel.exe --selftest
endlocal
