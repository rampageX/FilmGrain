@echo off
chcp 65001 >nul
setlocal

set "PREVIEW_SCRIPT=%~dp0_LUT_Tools\LUT_Preview_Batch_v2.3_Gallery.ps1"
if not exist "%PREVIEW_SCRIPT%" (
    echo.
    echo ERROR: Preview support script not found:
    echo "%PREVIEW_SCRIPT%"
    echo.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PREVIEW_SCRIPT%"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Script exited with code %RC%.
pause
exit /b %RC%
