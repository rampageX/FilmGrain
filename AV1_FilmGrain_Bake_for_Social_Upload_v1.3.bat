@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem AV1 Film Grain -> Pixel Grain Upload Master v1.3
rem Drag one or more AV1 Film Grain files onto this BAT.
rem
rem libdav1d synthesizes AV1 Film Grain into pixels first.
rem Current FFmpeg marks the old explicit film-grain decoder switch deprecated.
rem dav1d applies AV1 Film Grain by default, so no explicit switch is needed.
rem The result is then encoded as high-quality H.264/AAC MP4
rem for broad video-sharing / social-platform compatibility.
rem ============================================================

set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "AQ_STRENGTH=8"

"%FFMPEG%" -version >nul 2>&1
if errorlevel 1 (
    echo ERROR: FFmpeg not found:
    echo "%FFMPEG%"
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -h decoder=libdav1d >nul 2>&1
if errorlevel 1 (
    echo ERROR: This FFmpeg build does not contain libdav1d.
    echo Film Grain Bake requires the libdav1d AV1 decoder.
    echo.
    echo You can verify manually with:
    echo "%FFMPEG%" -hide_banner -h decoder=libdav1d
    pause
    exit /b 1
)

if "%~1"=="" (
    echo Drag one or more AV1 Film Grain files onto this BAT.
    pause
    exit /b 0
)

:LOOP
if "%~1"=="" goto DONE

set "INPUT=%~f1"
set "INDIR=%~dp1"
set "NAME=%~n1"
set "OUTPUT=%INDIR%%NAME%_UPLOAD_H264_GRAIN.mp4"

set "DIMFILE=%TEMP%\AV1BAKE_dim_%RANDOM%_%RANDOM%.txt"
"E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe" -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x "%INPUT%" > "%DIMFILE%" 2>nul
set "DIM="
set /p "DIM="<"%DIMFILE%"
del /q "%DIMFILE%" >nul 2>&1

set "W=1920"
set "H=1080"
for /f "tokens=1,2 delims=x" %%A in ("%DIM%") do (
    set "W=%%A"
    set "H=%%B"
)

set /a PIXELS=%W%*%H%
set "BR=18000k"
set "MR=27000k"
set "BS=36000k"

if %PIXELS% LEQ 921600 (
    set "BR=10000k"
    set "MR=15000k"
    set "BS=20000k"
) else if %PIXELS% LEQ 2073600 (
    set "BR=18000k"
    set "MR=27000k"
    set "BS=36000k"
) else if %PIXELS% LEQ 3686400 (
    set "BR=30000k"
    set "MR=45000k"
    set "BS=60000k"
) else (
    set "BR=45000k"
    set "MR=67500k"
    set "BS=90000k"
)

echo.
echo ============================================================
echo Input       : "%INPUT%"
echo Resolution  : %W%x%H%
echo Video rate  : %BR%
echo Output      : "%OUTPUT%"
echo ============================================================
echo.

if exist "%OUTPUT%" (
    echo SKIP: Output already exists.
    shift
    goto LOOP
)

set "CMD="%FFMPEG%" -hide_banner -stats -y -c:v libdav1d -i "%INPUT%" -map 0:v:0 -map 0:a:0? -map_metadata 0 -c:v h264_nvenc -profile:v high -pix_fmt yuv420p -preset p6 -tune hq -rc vbr -b:v %BR% -maxrate:v %MR% -bufsize:v %BS% -multipass fullres -rc-lookahead 32 -spatial-aq 1 -aq-strength %AQ_STRENGTH% -temporal-aq 1 -bf 4 -b_ref_mode middle -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%OUTPUT%""
%CMD%

if errorlevel 1 (
    echo.
    echo ERROR: Bake failed.
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
) else (
    echo.
    echo DONE:
    echo "%OUTPUT%"
)

shift
goto LOOP

:DONE
echo.
echo All files processed.
pause
endlocal
