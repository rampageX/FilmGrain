@echo off
setlocal DisableDelayedExpansion

set "RELEASE_CORE=%~dp0.github\release\release_core.bat"
if not exist "%RELEASE_CORE%" (
    echo.
    echo ERROR: Film Grain Studio release core was not found:
    echo "%RELEASE_CORE%"
    echo.
    pause
    exit /b 1
)

call "%RELEASE_CORE%" %*
set "RELEASE_RC=%ERRORLEVEL%"

endlocal & exit /b %RELEASE_RC%
