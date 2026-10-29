@echo off
rem run-tests.cmd - build + GUI smoke test for dsh-panel
rem Note: does NOT touch the running service. Full service test: dsh-panel.exe --selftest
setlocal
set "DIR=%~dp0"

rem ---- Step 0: a running panel holds its own exe open, so csc cannot overwrite it ----
tasklist /FI "IMAGENAME eq dsh-panel.exe" | findstr /i "dsh-panel.exe" >nul
if not errorlevel 1 (
    echo [test] dsh-panel.exe is running - its image file is locked, so it cannot be rebuilt.
    echo [test] Use apply-panel-update.cmd to close the panel, rebuild, and reopen it
    echo        ^(the DSH service keeps running throughout^).
    exit /b 0
)

echo [test] Step 1/4: encoding + syntax gate over every script/config ...
rem Why a separate script: three failure modes all look fine while editing and explode at run
rem time -- (a) PS 5.1 reads BOM-less UTF-8 as ANSI, so one CJK comment is a syntax error;
rem (b) cmd.exe parses .cmd in the OEM codepage, so CJK there breaks the batch file;
rem (c) a broken .mjs makes the fast launcher / remote proxy exit silently.
powershell -NoProfile -ExecutionPolicy Bypass -File "%DIR%check-scripts.ps1" -Quiet
if errorlevel 1 (
    echo [test] FAIL: encoding/syntax gate failed ^(see the FAIL lines above^).
    exit /b 1
)

echo [test] Step 2/4: build ...
call "%DIR%build-dsh-panel.cmd"
if errorlevel 1 (
    echo [test] FAIL: build failed.
    exit /b 1
)

echo [test] Step 3/4: launch panel for a 4s smoke test ...
start "" "%DIR%dsh-panel.exe"
rem use the full path so a PATH override (e.g. Git Bash coreutils) cannot shadow timeout.exe
"%WINDIR%\System32\timeout.exe" /t 4 /nobreak >nul 2>nul
tasklist /FI "IMAGENAME eq dsh-panel.exe" | findstr /i "dsh-panel.exe" >nul
if errorlevel 1 (
    echo [test] FAIL: panel exited during smoke test. See logs\dsh-web.err.log
    exit /b 1
)
echo [test] Stopping smoke-test instance (the service is not affected) ...
taskkill /IM dsh-panel.exe /F >nul 2>&1
echo [test] Step 4/4: PASS - parse + build + GUI smoke test OK.
echo [test] Optional full service self-test (stops the service): dsh-panel.exe --selftest
endlocal
