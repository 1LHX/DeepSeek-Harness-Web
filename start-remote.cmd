@echo off
rem start-remote.cmd - open a token-gated LAN door in front of the local DSH service
rem
rem Off by default and never started by the panel: whoever holds the token can run
rem commands on this machine. Close this window (or Ctrl+C) to close the door again.
setlocal
title DeepSeek Harness - remote access (token gated)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-remote.ps1" %*
echo.
echo [remote] proxy stopped.
pause
endlocal
