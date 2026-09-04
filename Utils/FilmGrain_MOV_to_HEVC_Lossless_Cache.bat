@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem  Film Grain MOV -> HEVC Main10 Lossless Cache
rem
rem  Unified cache builder:
rem    [1] Original-resolution HEVC Main10 Lossless
rem    [2] 1080p HEVC Main10 Lossless (Vulkan bilinear)
rem    [3] Build both (default)
rem
rem  D:\Film_Grain is scanned recursively.
rem
rem  Verification:
rem    Compares the actual meaningful 10-bit YUV 4:2:0 samples.
rem    The reference hash is captured from the SAME filtered frame
rem    stream that is sent to NVENC. This avoids false mismatches
rem    caused by implementation-specific P010 padding bits.
rem ============================================================

rem ---------- USER SETTINGS ----------

set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"

set "ROOT=D:\Film_Grain"
set "VULKAN_DEVICE=0"
set "PRESET=p5"

rem 1 = verify actual 10-bit samples after encoding
rem 0 = skip verification
set "VERIFY=1"

set "EXT=mkv"
set "SUFFIX_FULL=_HEVC_Lossless"
set "SUFFIX_1080=_1080p_HEVC_Lossless"

rem ---------- END USER SETTINGS ----------


:MENU
cls
echo ============================================================
echo       Film Grain MOV to HEVC Main10 Lossless Cache
echo ============================================================
echo.
echo Root   : "%ROOT%"
echo FFmpeg : "%FFMPEG%"
echo Preset : %PRESET%
echo Verify : %VERIFY%
echo.
echo Select cache mode:
echo.
echo   [1] Original resolution HEVC Lossless
echo       Output: *_HEVC_Lossless.%EXT%
echo.
echo   [2] 1080p HEVC Lossless
echo       Vulkan bilinear -> 1920x1080 P010
echo       Output: *_1080p_HEVC_Lossless.%EXT%
echo.
echo   [3] Build BOTH caches  [default]
echo.
echo   [0] Exit
echo.
set "MODE="
set /p "MODE=Choice [3]: "
if not defined MODE set "MODE=3"

if "%MODE%"=="0" exit /b 0
if "%MODE%"=="1" goto :START
if "%MODE%"=="2" goto :START
if "%MODE%"=="3" goto :START

echo.
echo Invalid choice.
echo.
pause
goto :MENU


:START
if not exist "%ROOT%\" (
    echo.
    echo ERROR: Grain library folder not found:
    echo "%ROOT%"
    echo.
    pause
    exit /b 1
)

if not exist "%FFMPEG%" (
    echo.
    echo ERROR: FFmpeg not found:
    echo "%FFMPEG%"
    echo.
    pause
    exit /b 1
)

if not exist "%FFPROBE%" (
    echo.
    echo ERROR: FFprobe not found:
    echo "%FFPROBE%"
    echo.
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -h encoder=hevc_nvenc 2>nul | findstr /i "lossless" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not expose HEVC NVENC lossless mode.
    echo.
    pause
    exit /b 1
)

if "%MODE%"=="2" goto :CHECK_VULKAN
if "%MODE%"=="3" goto :CHECK_VULKAN
goto :CAPS_OK

:CHECK_VULKAN
"%FFMPEG%" -hide_banner -h filter=scale_vulkan 2>nul | findstr /i "bilinear" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not expose scale_vulkan bilinear.
    echo.
    pause
    exit /b 1
)

:CAPS_OK
set "MOV_TOTAL=0"
set "JOB_TOTAL=0"
set "SUCCESS=0"
set "FAILED=0"
set "SKIPPED=0"
set "VERIFIED=0"
set "VERIFY_FAILED=0"

cls
echo ============================================================
echo       Film Grain MOV to HEVC Main10 Lossless Cache
echo ============================================================
echo.
echo Root       : "%ROOT%"
echo Preset     : %PRESET%
echo Verify     : %VERIFY% - actual 10-bit sample exact
if "%MODE%"=="1" echo Mode       : Original resolution
if "%MODE%"=="2" echo Mode       : 1080p Vulkan bilinear
if "%MODE%"=="3" echo Mode       : Original resolution + 1080p
echo.
echo Searching MOV grain plates...
echo.

for /r "%ROOT%" %%F in (*.mov) do call :PROCESS_MOV "%%~fF"

echo.
echo ============================================================
echo                     Batch Summary
echo ============================================================
echo.
echo MOV files      : %MOV_TOTAL%
echo Cache jobs     : %JOB_TOTAL%
echo Converted      : %SUCCESS%
echo Failed         : %FAILED%
echo Skipped        : %SKIPPED%
if "%VERIFY%"=="1" (
    echo Verified       : %VERIFIED%
    echo Verify failed  : %VERIFY_FAILED%
)
echo.
if "%FAILED%"=="0" if "%VERIFY_FAILED%"=="0" (
    echo RESULT: ALL COMPLETED SUCCESSFULLY.
) else (
    echo RESULT: COMPLETED WITH ERRORS.
)
echo.
echo Source MOV files were NOT deleted.
echo Existing cache files were NOT overwritten.
echo.
pause
exit /b


:PROCESS_MOV
set "INPUT=%~1"
set /a MOV_TOTAL+=1

if "%MODE%"=="1" call :PROCESS_VARIANT "%INPUT%" FULL
if "%MODE%"=="2" call :PROCESS_VARIANT "%INPUT%" 1080
if "%MODE%"=="3" call :PROCESS_VARIANT "%INPUT%" FULL
if "%MODE%"=="3" call :PROCESS_VARIANT "%INPUT%" 1080

goto :eof


:PROCESS_VARIANT
set "INPUT=%~1"
set "VARIANT=%~2"
set /a JOB_TOTAL+=1

if /i "%VARIANT%"=="FULL" goto :SET_FULL
if /i "%VARIANT%"=="1080" goto :SET_1080

echo ERROR: Internal variant selection failed.
set /a FAILED+=1
goto :eof


:SET_FULL
set "OUTPUT=%~dp1%~n1%SUFFIX_FULL%.%EXT%"
set "VARIANT_LABEL=Original resolution"
set "SKIP_LABEL=Original-resolution cache already exists"
goto :RUN_VARIANT


:SET_1080
set "OUTPUT=%~dp1%~n1%SUFFIX_1080%.%EXT%"
set "VARIANT_LABEL=1080p / Vulkan bilinear"
set "SKIP_LABEL=1080p cache already exists"
goto :RUN_VARIANT


:RUN_VARIANT
echo ============================================================
echo [%JOB_TOTAL%] %VARIANT_LABEL%
echo Input : "%INPUT%"
echo Output: "%OUTPUT%"
echo ============================================================

if exist "%OUTPUT%" (
    echo SKIP: %SKIP_LABEL%:
    echo "%OUTPUT%"
    set /a SKIPPED+=1
    echo.
    goto :eof
)

set "HASH_SRC=%TEMP%\FilmGrain_src10_%RANDOM%_%RANDOM%.txt"
set "HASH_DST=%TEMP%\FilmGrain_dst10_%RANDOM%_%RANDOM%.txt"
set "SRC_HASH="
set "DST_HASH="

del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1

if not "%VERIFY%"=="1" goto :ENCODE_NO_VERIFY

echo Encoding and capturing SAME-PASS 10-bit reference hash...
echo.

if /i "%VARIANT%"=="FULL" goto :ENCODE_FULL_VERIFY
if /i "%VARIANT%"=="1080" goto :ENCODE_1080_VERIFY
goto :ENCODE_FAILED


:ENCODE_FULL_VERIFY
rem
rem format=p010le is the exact working format used by the Film Grain pipeline.
rem The same P010 frame stream is split:
rem   [venc]   -> HEVC NVENC Main10 Lossless
rem   [vhash]  -> planar yuv420p10le -> SHA-256
rem Only the meaningful 10-bit sample values are verified.
rem
"%FFMPEG%" -hide_banner -stats -y ^
 -i "%INPUT%" ^
 -filter_complex "[0:v:0]format=p010le,split=2[venc][vhash];[vhash]format=yuv420p10le[vhash10]" ^
 -map "[venc]" -an -sn -dn ^
 -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le ^
 -preset %PRESET% -tune lossless -fps_mode passthrough ^
 "%OUTPUT%" ^
 -map "[vhash10]" -an -sn -dn ^
 -c:v rawvideo -pix_fmt yuv420p10le -fps_mode passthrough ^
 -f hash -hash sha256 "%HASH_SRC%"

if errorlevel 1 goto :ENCODE_FAILED
goto :ENCODE_VERIFY_DONE


:ENCODE_1080_VERIFY
rem
rem Vulkan scaling is executed ONCE.
rem After hwdownload, the SAME 1920x1080 P010 frame stream is split:
rem   [venc]   -> HEVC NVENC Main10 Lossless
rem   [vhash]  -> planar yuv420p10le -> SHA-256
rem This avoids false T600 mismatches caused only by P010 padding bits.
rem
"%FFMPEG%" -hide_banner -stats -y ^
 -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk ^
 -i "%INPUT%" ^
 -filter_complex "[0:v:0]format=p010le,hwupload,scale_vulkan=w=1920:h=1080:scaler=bilinear,hwdownload,format=p010le,split=2[venc][vhash];[vhash]format=yuv420p10le[vhash10]" ^
 -map "[venc]" -an -sn -dn ^
 -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le ^
 -preset %PRESET% -tune lossless -fps_mode passthrough ^
 "%OUTPUT%" ^
 -map "[vhash10]" -an -sn -dn ^
 -c:v rawvideo -pix_fmt yuv420p10le -fps_mode passthrough ^
 -f hash -hash sha256 "%HASH_SRC%"

if errorlevel 1 goto :ENCODE_FAILED
goto :ENCODE_VERIFY_DONE


:ENCODE_VERIFY_DONE
if not exist "%OUTPUT%" goto :ENCODE_FAILED
if not exist "%HASH_SRC%" goto :REFERENCE_HASH_FAILED

set /a SUCCESS+=1

echo.
echo DONE:
echo "%OUTPUT%"
echo.
echo Verifying decoded cache against SAME-PASS 10-bit samples...

"%FFMPEG%" -v error ^
 -i "%OUTPUT%" ^
 -map 0:v:0 -an -sn -dn ^
 -vf "format=yuv420p10le" ^
 -c:v rawvideo -pix_fmt yuv420p10le -fps_mode passthrough ^
 -f hash -hash sha256 "%HASH_DST%"

if errorlevel 1 goto :DECODE_HASH_FAILED
if not exist "%HASH_DST%" goto :DECODE_HASH_FAILED

set /p "SRC_HASH="<"%HASH_SRC%"
set /p "DST_HASH="<"%HASH_DST%"

if "%SRC_HASH%"=="%DST_HASH%" goto :VERIFY_OK
goto :VERIFY_BAD


:VERIFY_OK
echo VERIFY OK: Actual 10-bit YUV samples are lossless.
set /a VERIFIED+=1
echo.
goto :CLEAN_HASH


:VERIFY_BAD
echo.
echo VERIFY FAILED:
echo Encode input 10-bit : %SRC_HASH%
echo Decoded cache 10-bit: %DST_HASH%
echo.
echo The meaningful 10-bit samples differ.
echo Keep the original MOV and do NOT use this cache yet.
set /a VERIFY_FAILED+=1
echo.
goto :CLEAN_HASH


:REFERENCE_HASH_FAILED
echo.
echo VERIFY ERROR: SAME-PASS source reference hash was not created.
echo The output cache is not considered verified.
set /a FAILED+=1
set /a VERIFY_FAILED+=1
echo.
goto :CLEAN_HASH


:DECODE_HASH_FAILED
echo.
echo VERIFY ERROR: Could not hash decoded HEVC cache.
set /a VERIFY_FAILED+=1
echo.
goto :CLEAN_HASH


:ENCODE_NO_VERIFY
if /i "%VARIANT%"=="FULL" goto :ENCODE_FULL_NO_VERIFY
if /i "%VARIANT%"=="1080" goto :ENCODE_1080_NO_VERIFY
goto :ENCODE_FAILED


:ENCODE_FULL_NO_VERIFY
"%FFMPEG%" -hide_banner -stats -y ^
 -i "%INPUT%" ^
 -map 0:v:0 -an -sn -dn ^
 -vf "format=p010le" ^
 -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le ^
 -preset %PRESET% -tune lossless -fps_mode passthrough ^
 "%OUTPUT%"

if errorlevel 1 goto :ENCODE_FAILED
goto :ENCODE_NO_VERIFY_DONE


:ENCODE_1080_NO_VERIFY
"%FFMPEG%" -hide_banner -stats -y ^
 -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk ^
 -i "%INPUT%" ^
 -map 0:v:0 -an -sn -dn ^
 -vf "format=p010le,hwupload,scale_vulkan=w=1920:h=1080:scaler=bilinear,hwdownload,format=p010le" ^
 -c:v hevc_nvenc -profile:v main10 -pix_fmt p010le ^
 -preset %PRESET% -tune lossless -fps_mode passthrough ^
 "%OUTPUT%"

if errorlevel 1 goto :ENCODE_FAILED
goto :ENCODE_NO_VERIFY_DONE


:ENCODE_NO_VERIFY_DONE
set /a SUCCESS+=1
echo.
echo DONE:
echo "%OUTPUT%"
echo.
goto :CLEAN_HASH


:ENCODE_FAILED
echo.
echo ERROR: Encoding failed.
if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
set /a FAILED+=1
echo.
goto :CLEAN_HASH


:CLEAN_HASH
del /q "%HASH_SRC%" "%HASH_DST%" >nul 2>&1
goto :eof
