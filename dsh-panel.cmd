@echo off
setlocal
set "DIR=%~dp0"
if not exist "%DIR%dsh-panel.exe" (
    call "%DIR%build-dsh-panel.cmd"
    if errorlevel 1 exit /b 1
)
start "" "%DIR%dsh-panel.exe"
endlocal
