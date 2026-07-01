@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "DEPLOY_SCRIPT=%SCRIPT_DIR%deploy-agent.ps1"

if not exist "%DEPLOY_SCRIPT%" (
    echo deploy-agent.ps1 was not found next to run.bat
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_SCRIPT%" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Deploy finished with exit code %EXIT_CODE%.
pause

endlocal & exit /b %EXIT_CODE%
