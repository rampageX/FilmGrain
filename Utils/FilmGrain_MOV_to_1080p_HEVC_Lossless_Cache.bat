@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem  Film Grain MOV -> 1080p HEVC Main10 Lossless Cache
rem  Utility location is independent; D:\Film_Grain is scanned recursively.
rem
rem  Output example:
rem    foo.mov -> foo_1080p_HEVC_Lossless.mkv
rem
rem  The 4K Grain is scaled ONCE to 1920x1080 using Vulkan bilinear,
rem  then stored as HEVC Main10 NVENC Lossless.
rem ============================================================

set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"
set "ROOT=D:\Film_Grain"
set "VULKAN_DEVICE=0"
set "PRESET=p5"
set "VERIFY=1"
set "SUFFIX=_1080p_HEVC_Lossless"
set "EXT=mkv"

cls
echo ============================================================
echo    Film Grain MOV to 1080p HEVC Main10 Lossless Cache
echo ============================================================
echo.
echo Root      : "%ROOT%"
echo FFmpeg    : "%FFMPEG%"
echo Target    : 1920x1080 P010
echo Scale     : Vulkan bilinear
echo Preset    : %PRESET%
echo Verify    : %VERIFY%
echo.

if not exist "%ROOT%\" (
    echo ERROR: Grain library folder not found:
    echo "%ROOT%"
    echo.
    pause
    exit /b 1
)

if not exist "%FFMPEG%" (
    echo ERROR: FFmpeg not found:
    echo "%FFMPEG%"
    echo.
    pause
    exit /b 1
)

if not exist "%FFPROBE%" (
    echo ERROR: FFprobe not found:
    echo "%FFPROBE%"
    echo.
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -h filter=scale_vulkan 2>nul | findstr /i "bilinear" >nul
if errorlevel 1 (
    echo ERROR: This FFmpeg build does not expose scale_vulkan bilinear.
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
echo Original MOV and existing 4K cache files were NOT deleted.
echo.
pause
exit /b


:PROCESS_ONE
set "INPUT=%~1"
set "OUTPUT=%~dp1%~n1%SUFFIX%.%EXT%"
set /a TOTAL+=1

echo ============================================================
echo [%TOTAL%] "%INPUT%"
echo ============================================================

if exist "%OUTPUT%" (
    echo SKIP: 1080p cache already exists:
    echo "%OUTPUT%"
    set /a SKIPPED+=1
    echo.
    goto :eof
)

"%FFMPEG%" -hide_banner -stats -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk -i "%INPUT%" -map 0:v:0 -an -sn -dn -vf "format=p010le,hwupload,scale_vulkan=w=1920:h=1080:scaler=bilinear,hwdownload,format=p010le" -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le -preset %PRESET% -tune lossless -fps_mode passthrough "%OUTPUT%"

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
echo Verifying exact 1080p P010 pixel stream...

set "HASH_SRC=%TEMP%\FilmGrain1080_src_%RANDOM%_%RANDOM%.txt"
set "HASH_DST=%TEMP%\FilmGrain1080_dst_%RANDOM%_%RANDOM%.txt"
set "SRC_HASH="
set "DST_HASH="

"%FFMPEG%" -v error -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk -i "%INPUT%" -map 0:v:0 -an -sn -dn -vf "format=p010le,hwupload,scale_vulkan=w=1920:h=1080:scaler=bilinear,hwdownload,format=p010le" -f hash -hash sha256 "%HASH_SRC%"
if errorlevel 1 (
    echo VERIFY ERROR: Could not hash source 1080p P010 stream.
    set /a VERIFY_FAILED+=1
    del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1
    echo.
    goto :eof
)

"%FFMPEG%" -v error -i "%OUTPUT%" -map 0:v:0 -an -sn -dn -pix_fmt p010le -f hash -hash sha256 "%HASH_DST%"
if errorlevel 1 (
    echo VERIFY ERROR: Could not hash decoded 1080p cache.
    set /a VERIFY_FAILED+=1
    del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1
    echo.
    goto :eof
)

set /p "SRC_HASH="<"%HASH_SRC%"
set /p "DST_HASH="<"%HASH_DST%"
del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1

if "%SRC_HASH%"=="%DST_HASH%" (
    echo VERIFY OK: 1080p P010 pixels are bit-exact.
    set /a VERIFIED+=1
) else (
    echo.
    echo VERIFY FAILED:
    echo Source: %SRC_HASH%
    echo Cache : %DST_HASH%
    echo.
    set /a VERIFY_FAILED+=1
)

echo.
goto :eof
