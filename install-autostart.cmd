@echo off
rem install-autostart.cmd - register dsh-panel.exe to start at logon (current user only)
setlocal
set "DIR=%~dp0"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "DshPanel" /t REG_SZ /d "\"%DIR%dsh-panel.exe\"" /f >nul
if errorlevel 1 (
    echo [dsh-panel] Failed to register auto-start.
    exit /b 1
)
echo [dsh-panel] Auto-start enabled: "%DIR%dsh-panel.exe"
endlocal
