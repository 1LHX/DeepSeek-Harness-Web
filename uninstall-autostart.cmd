@echo off
rem uninstall-autostart.cmd - remove dsh-panel.exe from logon auto-start (current user only)
setlocal
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "DshPanel" /f >nul 2>&1
echo [dsh-panel] Auto-start removed.
endlocal
