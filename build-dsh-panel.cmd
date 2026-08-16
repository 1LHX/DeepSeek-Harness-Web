@echo off
setlocal
set "DIR=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [build] csc.exe not found. .NET Framework 4.x is required.
    pause
    exit /b 1
)
if not exist "%DIR%dsh-panel.ico" (
    echo [build] dsh-panel.ico not found. Running generate-icon.ps1 ...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%DIR%generate-icon.ps1"
    if errorlevel 1 exit /b 1
)
echo [build] Compiling dsh-panel.exe ...
"%CSC%" /nologo /optimize+ /target:winexe /codepage:65001 /win32manifest:"%DIR%dsh-panel.manifest" /win32icon:"%DIR%dsh-panel.ico" /out:"%DIR%dsh-panel.exe" "%DIR%dsh-panel.cs"
if errorlevel 1 (
    echo [build] Compilation FAILED.
    pause
    exit /b 1
)
echo [build] Done: %DIR%dsh-panel.exe
endlocal
