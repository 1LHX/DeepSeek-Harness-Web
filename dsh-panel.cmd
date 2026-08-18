@echo off
setlocal
set "DIR=%~dp0"
rem Rebuild when the exe or any WebView2 runtime DLL is missing
rem (build-dsh-panel.cmd copies the three DLLs from lib/ next to the exe)
if not exist "%DIR%dsh-panel.exe" goto build
if not exist "%DIR%Microsoft.Web.WebView2.Core.dll" goto build
if not exist "%DIR%Microsoft.Web.WebView2.WinForms.dll" goto build
if not exist "%DIR%WebView2Loader.dll" goto build
goto run

:build
call "%DIR%build-dsh-panel.cmd"
if errorlevel 1 exit /b 1

:run
start "" "%DIR%dsh-panel.exe"
endlocal
