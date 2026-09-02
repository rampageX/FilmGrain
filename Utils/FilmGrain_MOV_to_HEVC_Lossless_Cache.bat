@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem  Film Grain MOV -> HEVC Main10 Lossless Cache
rem  Utility location is independent; D:\Film_Grain is scanned recursively.
rem
rem  Purpose:
rem    Convert ProRes/other MOV grain plates into GPU-decodable HEVC
rem    cache files while preserving the exact P010 pixel stream.
rem
rem  Output:
rem    Same folder as source, same basename:
rem      xxx.mov  ->  xxx_HEVC_Lossless.mkv
rem ============================================================

rem ---------- USER SETTINGS ----------

set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"

rem Grain library root. Subfolders are scanned recursively.
set "ROOT=D:\Film_Grain"

rem NVENC lossless preset.
rem NVIDIA recommends p4-p5 as a good speed/compression balance.
set "PRESET=p5"

rem 1 = verify bit-exact P010 pixels after encoding (recommended first run)
rem 0 = skip verification for faster batch conversion
set "VERIFY=1"

rem Output suffix/container
set "SUFFIX=_HEVC_Lossless"
set "EXT=mkv"

rem ---------- END USER SETTINGS ----------

cls
echo ============================================================
echo       Film Grain MOV to HEVC Main10 Lossless Cache
echo ============================================================
echo.
echo Root      : "%ROOT%"
echo FFmpeg    : "%FFMPEG%"
echo Preset    : %PRESET%
echo Verify    : %VERIFY%
echo Output    : *_HEVC_Lossless.%EXT%
echo.

if not exist "%ROOT%\" (
    echo ERROR: Grain library folder not found:
    echo "%ROOT%"
    echo.
    pause
    exit /b 1
)

if not exist "%FFMPEG%" (
    echo ERROR: FFmpeg was not found:
    echo "%FFMPEG%"
    echo.
    pause
    exit /b 1
)

if not exist "%FFPROBE%" (
    echo ERROR: FFprobe was not found:
    echo "%FFPROBE%"
    echo.
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -h encoder=hevc_nvenc 2>nul | findstr /i "lossless" >nul
if errorlevel 1 (
    echo ERROR: This FFmpeg build does not expose HEVC NVENC lossless mode.
    echo.
    pause
    exit /b 1
)

set "TOTAL=0"
set "SUCCESS=0"
set "FAILED=0"
set "SKIPPED=0"
set "VERIFIED=0"
set "VERIFY_FAILED=0"

echo Searching MOV grain plates...
echo.

for /r "%ROOT%" %%F in (*.mov) do call :PROCESS_ONE "%%~fF"

echo.
echo ============================================================
echo                     Batch Summary
echo ============================================================
echo.
echo Total MOV     : %TOTAL%
echo Converted     : %SUCCESS%
echo Failed        : %FAILED%
echo Skipped       : %SKIPPED%
if "%VERIFY%"=="1" (
    echo Verified      : %VERIFIED%
    echo Verify failed : %VERIFY_FAILED%
)
echo.
if "%FAILED%"=="0" if "%VERIFY_FAILED%"=="0" (
    echo RESULT: ALL COMPLETED SUCCESSFULLY.
) else (
    echo RESULT: COMPLETED WITH ERRORS.
)
echo.
echo Source MOV files were NOT deleted.
echo.
pause
exit /b


:PROCESS_ONE
set "INPUT=%~1"
set "OUTDIR=%~dp1"
set "BASENAME=%~n1"
set "OUTPUT=%~dp1%~n1%SUFFIX%.%EXT%"

set /a TOTAL+=1

echo ============================================================
echo [%TOTAL%] "%INPUT%"
echo ============================================================

if exist "%OUTPUT%" (
    echo SKIP: Cache already exists:
    echo "%OUTPUT%"
    set /a SKIPPED+=1
    echo.
    goto :eof
)

rem
rem Important:
rem   The current Film Grain pipeline ultimately uses P010 (10-bit 4:2:0)
rem   before Vulkan blending.
rem
rem   Therefore this cache intentionally performs that same P010 conversion
rem   ONCE, then encodes that P010 stream in HEVC NVENC LOSSLESS mode.
rem   There is no additional lossy HEVC quantization.
rem
"%FFMPEG%" -hide_banner -stats -y -i "%INPUT%" -map 0:v:0 -an -sn -dn -vf "format=p010le" -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le -preset %PRESET% -tune lossless -fps_mode passthrough "%OUTPUT%"

if errorlevel 1 (
    echo.
    echo ERROR: Encoding failed.
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
    set /a FAILED+=1
    echo.
    goto :eof
)

set /a SUCCESS+=1
echo.
echo DONE:
echo "%OUTPUT%"

if not "%VERIFY%"=="1" (
    echo.
    goto :eof
)

echo.
echo Verifying bit-exact P010 pixel stream...
echo This may take some time, but it is only needed once for the cache library.

set "HASH_SRC=%TEMP%\FilmGrain_src_%RANDOM%_%RANDOM%.txt"
set "HASH_DST=%TEMP%\FilmGrain_dst_%RANDOM%_%RANDOM%.txt"
set "SRC_HASH="
set "DST_HASH="

rem Hash the exact P010 stream that the normal pipeline would create from MOV.
"%FFMPEG%" -v error -i "%INPUT%" -map 0:v:0 -an -sn -dn -vf "format=p010le" -f hash -hash sha256 "%HASH_SRC%"
if errorlevel 1 (
    echo VERIFY ERROR: Could not hash source P010 stream.
    set /a VERIFY_FAILED+=1
    del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1
    echo.
    goto :eof
)

rem Hash the P010 stream decoded from the lossless HEVC cache.
"%FFMPEG%" -v error -i "%OUTPUT%" -map 0:v:0 -an -sn -dn -pix_fmt p010le -f hash -hash sha256 "%HASH_DST%"
if errorlevel 1 (
    echo VERIFY ERROR: Could not hash decoded HEVC cache.
    set /a VERIFY_FAILED+=1
    del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1
    echo.
    goto :eof
)

set /p "SRC_HASH="<"%HASH_SRC%"
set /p "DST_HASH="<"%HASH_DST%"
del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1

if "%SRC_HASH%"=="%DST_HASH%" (
    echo VERIFY OK: P010 pixels are bit-exact.
    set /a VERIFIED+=1
) else (
    echo.
    echo VERIFY FAILED:
    echo Source: %SRC_HASH%
    echo Cache : %DST_HASH%
    echo.
    echo Keep the original MOV. Do NOT use this cache file yet.
    set /a VERIFY_FAILED+=1
)

echo.
goto :eof
