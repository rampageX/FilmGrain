@echo off
rem Shared Film Grain path configuration loader.
rem This file is CALLed by other BAT files and intentionally has no SETLOCAL.
rem FilmGrain_Config.ini is UTF-8 without BOM. Delayed expansion must stay disabled
rem in callers while loading path values so literal ! characters are preserved.

set "FILMGRAIN_CONFIG_FILE=%~dp0..\FilmGrain_Config.ini"

if not exist "%FILMGRAIN_CONFIG_FILE%" (
    >"%FILMGRAIN_CONFIG_FILE%" echo [Paths]
    >>"%FILMGRAIN_CONFIG_FILE%" echo FFMPEG_DIR=E:\EnCoder\FFMpeg\x64\bin
    >>"%FILMGRAIN_CONFIG_FILE%" echo GRAV1SYNTH=E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
    >>"%FILMGRAIN_CONFIG_FILE%" echo GRAIN_ROOT=D:\Film_Grain
    >>"%FILMGRAIN_CONFIG_FILE%" echo LUT_ROOT=E:\Adobe Portable\LUTs
)

set "FFMPEG_DIR=E:\EnCoder\FFMpeg\x64\bin"
set "GRAV1SYNTH=E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe"
set "GRAIN_ROOT=D:\Film_Grain"
set "LUT_ROOT=E:\Adobe Portable\LUTs"
set "_FG_HAVE_DIR=0"
set "_FG_LEGACY_FFMPEG="
set "_FG_LEGACY_FFPROBE="

set "_FG_OLD_CP="
for /f "tokens=2 delims=:" %%C in ('chcp') do set "_FG_OLD_CP=%%C"
set "_FG_OLD_CP=%_FG_OLD_CP: =%"
chcp 65001 >nul 2>&1

for /f "usebackq tokens=1,* delims== eol=;" %%A in ("%FILMGRAIN_CONFIG_FILE%") do (
    if /i "%%A"=="FFMPEG_DIR" set "FFMPEG_DIR=%%B"
    if /i "%%A"=="FFMPEG_DIR" set "_FG_HAVE_DIR=1"
    if /i "%%A"=="FFMPEG" set "_FG_LEGACY_FFMPEG=%%B"
    if /i "%%A"=="FFPROBE" set "_FG_LEGACY_FFPROBE=%%B"
    if /i "%%A"=="GRAV1SYNTH" set "GRAV1SYNTH=%%B"
    if /i "%%A"=="GRAIN_ROOT" set "GRAIN_ROOT=%%B"
    if /i "%%A"=="LUT_ROOT" set "LUT_ROOT=%%B"
)

if not "%_FG_HAVE_DIR%"=="1" if defined _FG_LEGACY_FFMPEG for %%P in ("%_FG_LEGACY_FFMPEG%") do set "FFMPEG_DIR=%%~dpP"
if not "%_FG_HAVE_DIR%"=="1" if not defined _FG_LEGACY_FFMPEG if defined _FG_LEGACY_FFPROBE for %%P in ("%_FG_LEGACY_FFPROBE%") do set "FFMPEG_DIR=%%~dpP"
set "FFMPEG=%FFMPEG_DIR%\ffmpeg.exe"
set "FFPROBE=%FFMPEG_DIR%\ffprobe.exe"

if defined _FG_OLD_CP chcp %_FG_OLD_CP% >nul 2>&1
set "_FG_OLD_CP="
set "_FG_HAVE_DIR="
set "_FG_LEGACY_FFMPEG="
set "_FG_LEGACY_FFPROBE="
exit /b 0
