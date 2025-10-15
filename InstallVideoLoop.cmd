@echo off
setlocal
set SCRIPT_DIR=%~dp0
set PS_SCRIPT=%SCRIPT_DIR%installer\InstallVideoLoop.ps1

if not exist "%PS_SCRIPT%" (
    echo Cannot find installer script at "%PS_SCRIPT%".
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
if %ERRORLEVEL% neq 0 (
    echo Installation failed. See output above for details.
    pause
    exit /b %ERRORLEVEL%
)

echo Installation complete. You can now close this window.
pause
