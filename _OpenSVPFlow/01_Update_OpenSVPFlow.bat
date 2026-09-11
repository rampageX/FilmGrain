@echo off
setlocal DisableDelayedExpansion
set "ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Update_OpenSVPFlow.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Update failed. ErrorLevel=%RC%
if "%RC%"=="0" echo Update completed.
echo.
pause
exit /b %RC%
