@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Film Grain Studio Benchmark v1.0 - v4 StudioBridge settings preserved.
set "FGB_HOME=%~dp0"
set "FGB_SOURCE=%~f1"
if "%~1"=="" goto NO_INPUT
if not exist "%~dp0FGS_Benchmark.ps1" goto NO_HELPER
set "FGB_ROOT=%~dp1FGS_Benchmark_%RANDOM%_%RANDOM%"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0FGS_Benchmark.ps1" -Action Init
if errorlevel 1 goto FAILED
call :RUN B01 HEVC PROCEDURAL VBR
call :RUN B02 HEVC FGSIM VBR
call :RUN B03 HEVC NATIVE VBR
call :RUN B04 AV1 PROCEDURAL VBR
call :RUN B05 AV1 FGSIM VBR
call :RUN B06 AV1 NATIVE VBR
call :RUN B07 X264 PROCEDURAL VBR
call :RUN B08 X264 FGSIM VBR
call :RUN B09 X264 NATIVE VBR
call :RUN Q01 HEVC FGSIM VBR
call :RUN Q02 HEVC FGSIM STANDARD
call :RUN Q03 HEVC FGSIM HIGH
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0FGS_Benchmark.ps1" -Action Finish
if errorlevel 1 goto FAILED
echo.
echo Benchmark completed. Review Benchmark.md for per-test status.
echo "%FGB_ROOT%"
pause
exit /b 0
:RUN
echo.
echo [%~1] %~2 / %~3 / %~4
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%FGB_HOME%FGS_Benchmark.ps1" -Action Run -Test %~1 -Encoder %~2 -Grain %~3 -Quality %~4
if errorlevel 1 echo [%~1] FAILED - see Logs and Benchmark.md.
exit /b 0
:NO_INPUT
echo Drag one source video onto this CMD.
pause
exit /b 1
:NO_HELPER
echo FGS_Benchmark.ps1 must be beside this CMD.
pause
exit /b 1
:FAILED
echo Benchmark could not complete. Read the error above.
echo "%FGB_ROOT%"
pause
exit /b 1
