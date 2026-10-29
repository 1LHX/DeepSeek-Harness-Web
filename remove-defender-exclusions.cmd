@echo off
rem remove-defender-exclusions.cmd - roll back the Defender exclusions added for dsh-web
rem Requires administrator: the script re-launches itself elevated (UAC prompt).
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0defender-exclusions.ps1" -Remove
echo.
pause
endlocal
