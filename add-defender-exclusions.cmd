@echo off
rem add-defender-exclusions.cmd - add Defender exclusions for dsh-web's cold-start hot paths
rem Requires administrator: the script re-launches itself elevated (UAC prompt).
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0defender-exclusions.ps1" -Add
echo.
pause
endlocal
