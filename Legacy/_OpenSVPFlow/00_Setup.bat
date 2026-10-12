@echo off
setlocal DisableDelayedExpansion
set "ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Setup_OpenSVPFlow.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo Setup failed. ErrorLevel=%RC%
if "%RC%"=="0" echo Setup completed.
echo.
pause
exit /b %RC%
