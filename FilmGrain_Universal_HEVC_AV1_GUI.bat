@echo off
setlocal DisableDelayedExpansion

set "STUDIO_PS1=%~dp0Utils\FilmGrain_Studio.ps1"
set "STUDIO_LAUNCHER=%~dp0Utils\FilmGrain_Studio_Launcher.vbs"
if not exist "%STUDIO_PS1%" (
    echo.
    echo ERROR: Film Grain Studio PowerShell file was not found:
    echo "%STUDIO_PS1%"
    echo.
    pause
    exit /b 1
)
if not exist "%STUDIO_LAUNCHER%" (
    echo.
    echo ERROR: Film Grain Studio hidden launcher was not found:
    echo "%STUDIO_LAUNCHER%"
    echo.
    pause
    exit /b 1
)

"%SystemRoot%\System32\wscript.exe" "%STUDIO_LAUNCHER%" "%STUDIO_PS1%" %*
set "STUDIO_RC=%ERRORLEVEL%"

endlocal & exit /b %STUDIO_RC%
