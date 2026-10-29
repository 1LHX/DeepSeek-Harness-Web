@echo off
rem stop-dsh.cmd - stop the DeepSeek Harness Web service
rem Usage:  stop-dsh.cmd [--dry-run]
rem   --dry-run : preview which processes would be killed, without killing anything
rem
rem v3.3: the service may have moved to another port when the configured one was taken
rem (see run/dsh-web.port). That recorded port is preferred over dsh-web.config here.
setlocal
set "DIR=%~dp0"
set "PIDFILE=%DIR%run\dsh-web.pid"
set "PORTFILE=%DIR%run\dsh-web.port"
set "PORT=3080"
set "DRYRUN="
if /i "%~1"=="--dry-run" set "DRYRUN=1"

rem Compatibility: pre-v1.4 kept the PID file in the project root
if not exist "%PIDFILE%" if exist "%DIR%dsh-web.pid" set "PIDFILE=%DIR%dsh-web.pid"

rem Read port from dsh-web.config (lines: port=NNNN)
if exist "%DIR%dsh-web.config" (
    for /f "usebackq tokens=1,* delims==" %%A in ('findstr /b /v "#" "%DIR%dsh-web.config"') do (
        if /i "%%A"=="port" set "PORT=%%B"
    )
)

rem Recorded actual port (port-conflict fallback) wins when present
if exist "%PORTFILE%" set /p PORT=<"%PORTFILE%"

set "KILLED="
set "PID="

rem 1) Read the PID outside of any parenthesized block so %PID% expands correctly
if exist "%PIDFILE%" set /p PID=<"%PIDFILE%"
if defined PID (
    if defined DRYRUN (
        echo [dsh-web] [dry-run] would kill process tree %PID% - from %PIDFILE%
        set "KILLED=1"
    ) else (
        taskkill /PID %PID% /T /F >nul 2>&1
        if errorlevel 1 (
            echo [dsh-web] PID %PID% not running.
        ) else (
            echo [dsh-web] Stopped process tree %PID%.
            set "KILLED=1"
        )
        del "%PIDFILE%" >nul 2>&1
    )
)

rem 2) Fallback: kill the listener on the active port (exact match ":3080 ")
rem    - ":3080 " with trailing space avoids matching :30800 etc.
if not defined KILLED (
    for /f "tokens=5" %%P in ('netstat -ano ^| findstr /C:":%PORT% " ^| findstr "LISTENING"') do (
        if defined DRYRUN (
            echo [dsh-web] [dry-run] would kill listener PID %%P - port %PORT%
        ) else (
            echo [dsh-web] Killing listener PID %%P ...
            taskkill /PID %%P /T /F >nul 2>&1
        )
        set "KILLED=1"
    )
)

rem 3) Report
if defined DRYRUN (
    if defined KILLED (
        echo [dsh-web] [dry-run] done - nothing was killed.
    ) else (
        echo [dsh-web] [dry-run] nothing to stop; service is not running.
    )
    exit /b 0
)
if not defined DRYRUN del "%PORTFILE%" >nul 2>&1
if defined KILLED (
    echo [dsh-web] Stopped.
) else (
    echo [dsh-web] Service was not running.
    exit /b 0
)
endlocal
