@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem AV1 / HEVC Grain Video -> x264 Grain Upload Master
rem Drag one or more AV1 / HEVC video files onto this BAT.
rem
rem AV1 input: libdav1d synthesizes AV1 Film Grain into pixels first.
rem HEVC / other input: FFmpeg uses the normal decoder automatically.
rem H.264 upload encoding uses libx264 / preset slow / tune grain / 2-pass.
rem Bitrate follows Film Grain Studio x264 Grain FPS/resolution linkage.
rem ============================================================

call "%~dp0FilmGrain_Config_Load.bat"
if errorlevel 1 exit /b 1

"%FFMPEG%" -version >nul 2>&1
if errorlevel 1 (
    echo ERROR: FFmpeg not found:
    echo "%FFMPEG%"
    pause
    exit /b 1
)

"%FFPROBE%" -version >nul 2>&1
if errorlevel 1 (
    echo ERROR: FFprobe not found:
    echo "%FFPROBE%"
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -h decoder=libdav1d >nul 2>&1
if errorlevel 1 (
    echo ERROR: This FFmpeg build does not contain libdav1d.
    echo AV1 Film Grain bake requires the libdav1d AV1 decoder.
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -h encoder=libx264 >nul 2>&1
if errorlevel 1 (
    echo ERROR: This FFmpeg build does not contain libx264.
    echo x264 Grain upload encoding requires libx264.
    pause
    exit /b 1
)

if "%~1"=="" (
    echo Drag one or more AV1 / HEVC video files onto this BAT.
    pause
    exit /b 0
)

echo.
echo ============================================================
echo x264 Grain quality:
echo.
echo   [1] Recommended  - 1080p60 high-dynamic base 15 Mbps  ^(default^)
echo   [2] High Quality - 1080p60 high-dynamic base 20 Mbps
echo   [3] Extreme      - 1080p60 high-dynamic base 25 Mbps
echo.
set "QUALITY_SEL=1"
set /p "QUALITY_SEL=Select [1-3, default 1]: "

set "BASE60=15000"
set "QUALITY_LABEL=Recommended"
if "%QUALITY_SEL%"=="2" (
    set "BASE60=20000"
    set "QUALITY_LABEL=High Quality"
)
if "%QUALITY_SEL%"=="3" (
    set "BASE60=25000"
    set "QUALITY_LABEL=Extreme"
)

echo.
echo Dynamic level:
echo.
echo   [1] Normal dynamic  - 0.5x bitrate budget  ^(default^)
echo   [2] High dynamic    - 1.0x bitrate budget
echo.
set "DYNAMIC_SEL=1"
set /p "DYNAMIC_SEL=Select [1-2, default 1]: "

set "DYNAMIC_PCT=50"
set "DYNAMIC_LABEL=Normal"
if "%DYNAMIC_SEL%"=="2" (
    set "DYNAMIC_PCT=100"
    set "DYNAMIC_LABEL=High"
)

:LOOP
if "%~1"=="" goto DONE

set "INPUT=%~f1"
set "INDIR=%~dp1"
set "NAME=%~n1"
set "PROBE_DIM=%~dp1AV1BAKE_dim_%RANDOM%_%RANDOM%.txt"
set "PROBE_FPS=%~dp1AV1BAKE_fps_%RANDOM%_%RANDOM%.txt"
set "PROBE_CODEC=%~dp1AV1BAKE_codec_%RANDOM%_%RANDOM%.txt"

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x "%INPUT%" > "%PROBE_DIM%" 2>nul
if errorlevel 1 goto PROBE_FAILED

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=avg_frame_rate -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_FPS%" 2>nul
if errorlevel 1 goto PROBE_FAILED

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=codec_name -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_CODEC%" 2>nul
if errorlevel 1 goto PROBE_FAILED

set "DIM="
set "FPS="
set "VIDEO_CODEC="
set /p "DIM="<"%PROBE_DIM%"
set /p "FPS="<"%PROBE_FPS%"
set /p "VIDEO_CODEC="<"%PROBE_CODEC%"
del /q "%PROBE_DIM%" "%PROBE_FPS%" "%PROBE_CODEC%" >nul 2>&1

set "DECODE_OPT="
set "DECODER_LABEL=FFmpeg auto"
if /i "%VIDEO_CODEC%"=="av1" set "DECODE_OPT=-c:v libdav1d"
if /i "%VIDEO_CODEC%"=="av1" set "DECODER_LABEL=libdav1d Film Grain bake"

set "W=1920"
set "H=1080"
for /f "tokens=1,2 delims=x" %%A in ("%DIM%") do (
    set "W=%%A"
    set "H=%%B"
)

if not defined FPS set "FPS=30000/1001"
if "%FPS%"=="0/0" set "FPS=30000/1001"

set "FPS_NUM=30000"
set "FPS_DEN=1001"
for /f "tokens=1,2 delims=/" %%A in ("%FPS%") do (
    set "FPS_NUM=%%A"
    set "FPS_DEN=%%B"
)
if not defined FPS_DEN set "FPS_DEN=1001"
if "%FPS_DEN%"=="0" set "FPS_DEN=1001"

set /a PIXELS=%W%*%H%
set /a RATIO10000=(%PIXELS%/20736)*100
if %RATIO10000% LSS 1 set "RATIO10000=1"
call :ISQRT %RATIO10000% RES_FACTOR_PCT
if %RES_FACTOR_PCT% LSS 1 set "RES_FACTOR_PCT=1"

set /a FPS100=%FPS_NUM%*100/%FPS_DEN%
set /a BRNUM=%BASE60%*%FPS100%/6000
set /a BRNUM=%BRNUM%*%RES_FACTOR_PCT%/100
set /a BRNUM=%BRNUM%*%DYNAMIC_PCT%/100
set /a BRNUM=(%BRNUM%+250)/500*500
if %BRNUM% LSS 500 set "BRNUM=500"

set /a MRNUM=%BRNUM%*3
set /a BSNUM=%BRNUM%*6

set "BR=%BRNUM%k"
set "MR=%MRNUM%k"
set "BS=%BSNUM%k"
set "OUTPUT=%INDIR%%NAME%_UPLOAD_H264_X264GRAIN_%BRNUM%k.mp4"
set "PASSLOG=%INDIR%AV1BAKE_x264pass_%RANDOM%_%RANDOM%"

echo.
echo ============================================================
echo Input       : "%INPUT%"
echo Resolution  : %W%x%H%
echo Source FPS  : %FPS%
echo Input codec : %VIDEO_CODEC%
echo Decoder     : %DECODER_LABEL%
echo x264 mode   : %QUALITY_LABEL% / %DYNAMIC_LABEL% dynamic
echo Video rate  : %BR%
echo Maxrate     : %MR%
echo Bufsize     : %BS%
echo Encoder     : libx264 / slow / tune grain / 2-pass
echo Audio       : AAC 256k / stereo / 48 kHz
echo Output      : "%OUTPUT%"
echo ============================================================
echo.

if exist "%OUTPUT%" (
    echo SKIP: Output already exists.
    shift
    goto LOOP
)

echo [1/2] x264 analysis pass...
"%FFMPEG%" -hide_banner -stats -y %DECODE_OPT% -i "%INPUT%" -map 0:v:0 -an -sn -dn -c:v libx264 -profile:v high -pix_fmt yuv420p -preset slow -tune grain -b:v %BR% -maxrate:v %MR% -bufsize:v %BS% -pass 1 -passlogfile "%PASSLOG%" -f null NUL
if errorlevel 1 goto ENCODE_FAILED

echo.
echo [2/2] x264 final pass...
"%FFMPEG%" -hide_banner -stats -y %DECODE_OPT% -i "%INPUT%" -map 0:v:0 -map 0:a:0? -map_metadata 0 -c:v libx264 -profile:v high -pix_fmt yuv420p -preset slow -tune grain -b:v %BR% -maxrate:v %MR% -bufsize:v %BS% -pass 2 -passlogfile "%PASSLOG%" -c:a aac -b:a 256k -ac 2 -ar 48000 -movflags +faststart "%OUTPUT%"
if errorlevel 1 goto ENCODE_FAILED

call :CLEAN_PASSLOG
echo.
echo DONE:
echo "%OUTPUT%"
shift
goto LOOP

:PROBE_FAILED
del /q "%PROBE_DIM%" "%PROBE_FPS%" "%PROBE_CODEC%" >nul 2>&1
echo.
echo ERROR: FFprobe could not read the input video.
echo "%INPUT%"
shift
goto LOOP

:ENCODE_FAILED
call :CLEAN_PASSLOG
echo.
echo ERROR: x264 Grain bake failed.
if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
shift
goto LOOP

:CLEAN_PASSLOG
if defined PASSLOG if exist "%PASSLOG%-0.log" del /q "%PASSLOG%-0.log" >nul 2>&1
if defined PASSLOG if exist "%PASSLOG%-0.log.mbtree" del /q "%PASSLOG%-0.log.mbtree" >nul 2>&1
exit /b 0

:ISQRT
set "SQRT_N=%~1"
set "SQRT_X=0"
:ISQRT_LOOP
set /a SQRT_NEXT=%SQRT_X%+1
set /a SQRT_SQ=%SQRT_NEXT%*%SQRT_NEXT%
if %SQRT_SQ% GTR %SQRT_N% goto ISQRT_DONE
set "SQRT_X=%SQRT_NEXT%"
goto ISQRT_LOOP
:ISQRT_DONE
set "%~2=%SQRT_X%"
exit /b 0

:DONE
echo.
echo All files processed.
pause
endlocal
