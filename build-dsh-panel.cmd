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
rem ---- WebView2 SDK (lib/): managed references + native loader ----
set "WV2_CORE=%DIR%lib\Microsoft.Web.WebView2.Core.dll"
set "WV2_WF=%DIR%lib\Microsoft.Web.WebView2.WinForms.dll"
set "WV2_LOADER=%DIR%lib\WebView2Loader.dll"
if not exist "%WV2_CORE%" (
    echo [build] %WV2_CORE% not found. lib/ is required - see README.
    pause
    exit /b 1
)
echo [build] Compiling dsh-panel.exe ...
"%CSC%" /nologo /optimize+ /target:winexe /platform:x64 /codepage:65001 /win32manifest:"%DIR%dsh-panel.manifest" /win32icon:"%DIR%dsh-panel.ico" /reference:"%WV2_CORE%" /reference:"%WV2_WF%" /out:"%DIR%dsh-panel.exe" "%DIR%dsh-panel.cs"
if errorlevel 1 (
    echo [build] Compilation FAILED.
    pause
    exit /b 1
)
rem ---- WebView2 runtime files must sit next to the exe (CLR only probes the exe dir) ----
copy /y "%WV2_CORE%"   "%DIR%Microsoft.Web.WebView2.Core.dll" >nul
copy /y "%WV2_WF%"     "%DIR%Microsoft.Web.WebView2.WinForms.dll" >nul
copy /y "%WV2_LOADER%" "%DIR%WebView2Loader.dll" >nul
echo [build] Done: %DIR%dsh-panel.exe
endlocal
