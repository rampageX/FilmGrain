@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem  AV1 NVENC + grav1synth Film Grain - FINAL v7.5 ContainerSelect
rem  Windows drag-and-drop / multi-file production pipeline
rem
rem  Stage 1: AV1 Main 10 encode with RTX NVENC into IVF
rem  Stage 2: grav1synth injects AV1 film-grain synthesis headers in IVF
rem  Stage 3: remux original audio/subtitles/attachments/metadata
rem  Stage 4: verify final AV1 grain headers with grav1synth inspect
rem  Stage 5: optional Bake-to-Pixels H.264 MP4 upload master
rem
rem  Recommended: grav1synth build containing the Sequence Header
rem  Film Grain flag / padded OBU size fix.
rem ============================================================

rem ---------- USER SETTINGS ----------

set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"
set "GRAV1SYNTH=E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe"

set "CUDA_DEVICE=0"
set "ENABLE_MAIN_NVDEC=1"

rem RTX 4080 / Ada
set "ENABLE_BF=1"
set "BF=4"
set "ENABLE_TEMPORAL_AQ=1"
set "AQ_STRENGTH=8"

rem Final AV1 container is selected at runtime.
set "EXT=mkv"
set "CONTAINER_MODE=MKV"
set "CONTAINER_LABEL=MKV / preserve original streams"
set "FINAL_REMUX_MAP=-map 1:a? -map 1:s? -map 1:t? -map 1:d?"
set "FINAL_REMUX_CODEC=-c copy"
set "FINAL_REMUX_EXTRA="

rem Keep useful intermediates automatically when a stage fails.
rem Successful jobs always clean all temporary files.
set "KEEP_FAILED_INTERMEDIATES=1"

rem ---------- END USER SETTINGS ----------


rem ============================================================
rem Tool checks
rem ============================================================

"%FFMPEG%" -version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFmpeg was not found:
    echo "%FFMPEG%"
    echo.
    pause
    exit /b 1
)

"%FFPROBE%" -version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFprobe was not found:
    echo "%FFPROBE%"
    echo.
    pause
    exit /b 1
)

"%GRAV1SYNTH%" --version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: grav1synth was not found or cannot start:
    echo "%GRAV1SYNTH%"
    echo.
    pause
    exit /b 1
)

rem This also confirms this build contains the newer built-in preset command.
"%GRAV1SYNTH%" presets >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: This grav1synth build does not expose the "presets" command.
    echo Run:
    echo   "%GRAV1SYNTH%" --help
    echo.
    pause
    exit /b 1
)

"%FFMPEG%" -hide_banner -encoders 2>nul | findstr /i "av1_nvenc" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain av1_nvenc.
    echo.
    pause
    exit /b 1
)

if "%~1"=="" (
    echo.
    echo Drag one or more video files onto this BAT file.
    echo.
    pause
    exit /b 0
)


rem ============================================================
rem Counters
rem ============================================================

set "FILE_COUNT=0"
for %%# in (%*) do set /a FILE_COUNT+=1

set "SUCCESS_COUNT=0"
set "FAIL_COUNT=0"
set "SKIP_COUNT=0"

rem Last failure details, shown again in the final summary for single-file jobs.
set "LAST_ERROR_STAGE="
set "LAST_ERROR_LOG="


rem ============================================================
rem Speed / quality menu
rem ============================================================

cls
echo ============================================================
echo        AV1 NVENC + grav1synth Film Grain FINAL v7.5 ContainerSelect
echo ============================================================
echo.
echo AV1 speed / quality:
echo.
echo   [1] Standard  - p6 / fullres / lookahead 32
echo   [2] FAST      - p5 / qres    / lookahead 16   ^(default^)
echo.
set "SPEED_SEL=2"
set /p "SPEED_SEL=Select [1-2, default 2]: "

if "%SPEED_SEL%"=="1" (
    set "PRESET=p6"
    set "LOOKAHEAD=32"
    set "MULTIPASS=fullres"
    set "SPEED_LABEL=Standard"
    set "SPEED_SUFFIX=STD"
) else (
    set "PRESET=p5"
    set "LOOKAHEAD=16"
    set "MULTIPASS=qres"
    set "SPEED_LABEL=FAST"
    set "SPEED_SUFFIX=FAST"
)


rem ============================================================
rem 2.39 cinema framing
rem ============================================================

echo.
echo Cinema framing:
echo.
echo   [1] Off
echo   [2] 2.39:1 active-picture crop   ^(default / recommended^)
echo.
echo       Example: 1920x1080 -^> about 1920x804
echo       Player adds pure black bars during fullscreen playback.
echo.
set "CROP_SEL=2"
set /p "CROP_SEL=Select [1-2, default 2]: "

if "%CROP_SEL%"=="1" (
    set "ENABLE_CROP=0"
    set "CROP_LABEL=Off"
    set "CROP_SUFFIX="
) else (
    set "ENABLE_CROP=1"
    set "CROP_LABEL=2.39:1 active crop"
    set "CROP_SUFFIX=_239"
)


rem ============================================================
rem Frame-rate menu
rem ============================================================

echo.
echo Output frame rate:
echo.
echo   [1] Keep source FPS
echo   [2] Auto cinematic FPS   ^(default^)
echo.
echo       NTSC fractional family -^> 23.976
echo       Integer / PAL family    -^> 24.000
echo.
set "FPS_SEL=2"
set /p "FPS_SEL=Select [1-2, default 2]: "

if "%FPS_SEL%"=="1" (
    set "FPS_MODE=SOURCE"
    set "FPS_LABEL=Keep source FPS"
) else (
    set "FPS_MODE=AUTO"
    set "FPS_LABEL=Auto cinematic FPS"
)


rem ============================================================
rem Final container
rem ============================================================

echo.
echo Final AV1 container:
echo.
echo   [1] MKV - preserve original audio / subtitles / attachments ^(default^)
echo   [2] MP4 - compatibility mode
echo.
echo       MP4 mode:
echo       - AV1 Film Grain video is stream-copied unchanged
echo       - Audio is converted to AAC 320k
echo       - Original channel layout is preserved where supported
echo       - Subtitles / attachments / data streams are omitted
echo       - Chapters / metadata are preserved
echo       - faststart enabled
echo.
set "CONTAINER_SEL=1"
set /p "CONTAINER_SEL=Select [1-2, default 1]: "

if "%CONTAINER_SEL%"=="2" (
    set "EXT=mp4"
    set "CONTAINER_MODE=MP4"
    set "CONTAINER_LABEL=MP4 compatibility / AAC audio"
    set "FINAL_REMUX_MAP=-map 1:a?"
    set "FINAL_REMUX_CODEC=-c:v copy -c:a aac -b:a 320k"
    set "FINAL_REMUX_EXTRA=-movflags +faststart"
) else (
    set "EXT=mkv"
    set "CONTAINER_MODE=MKV"
    set "CONTAINER_LABEL=MKV / preserve original streams"
    set "FINAL_REMUX_MAP=-map 1:a? -map 1:s? -map 1:t? -map 1:d?"
    set "FINAL_REMUX_CODEC=-c copy"
    set "FINAL_REMUX_EXTRA="
)


rem ============================================================
rem grav1synth Grain mode
rem ============================================================

cls
echo ============================================================
echo        grav1synth Film Grain
echo ============================================================
echo.
echo Grain source:
echo.
echo   [1] Film preset   ^(default / recommended^)
echo   [2] Photon ISO    ^(advanced strength control^)
echo.
set "GRAIN_MODE_SEL=1"
set /p "GRAIN_MODE_SEL=Select [1-2, default 1]: "

set "GRAIN_MODE=PRESET"
set "GRAIN_APPLY_ARGS="
set "GRAIN_LABEL="
set "GRAIN_FILE_TAG="

if "%GRAIN_MODE_SEL%"=="2" goto GRAIN_PHOTON_MENU


rem ------------------------------------------------------------
rem Built-in film preset mode
rem ------------------------------------------------------------

echo.
echo Film format:
echo.
echo   [1] Classic35   - Super 35mm style   ^(default / tested^)
echo   [2] Modern35    - 35mm full-frame style
echo   [3] 16mm        - stronger / coarser
echo   [4] Super8      - very strong / coarse
echo   [5] MaxMid      - synthetic heavy midtone grain
echo.
set "GRAIN_SEL=1"
set /p "GRAIN_SEL=Select [1-5, default 1]: "

set "GRAIN_BASE=Classic35"
set "GRAIN_FORMAT_LABEL=Classic35"
set "USE_STOCK=1"

if "%GRAIN_SEL%"=="2" (
    set "GRAIN_BASE=Modern35"
    set "GRAIN_FORMAT_LABEL=Modern35"
    set "USE_STOCK=1"
)
if "%GRAIN_SEL%"=="3" (
    set "GRAIN_BASE=16mm"
    set "GRAIN_FORMAT_LABEL=16mm"
    set "USE_STOCK=1"
)
if "%GRAIN_SEL%"=="4" (
    set "GRAIN_BASE=Super8"
    set "GRAIN_FORMAT_LABEL=Super8"
    set "USE_STOCK=0"
)
if "%GRAIN_SEL%"=="5" (
    set "GRAIN_BASE=MaxMid"
    set "GRAIN_FORMAT_LABEL=MaxMid"
    set "USE_STOCK=0"
)

set "GRAIN_PRESET=%GRAIN_BASE%"
set "STOCK_LABEL=Built-in"

if "%USE_STOCK%"=="0" goto GRAIN_PRESET_READY

echo.
echo Film stock:
echo.
echo   [1] Fujifilm Eterna 250D       ^(default^)
echo   [2] Fujifilm Eterna 500T
echo   [3] Kodak Vision3 250D
echo   [4] Kodak Vision3 200T
echo.
set "STOCK_SEL=1"
set /p "STOCK_SEL=Select [1-4, default 1]: "

if "%STOCK_SEL%"=="1" (
    set "GRAIN_PRESET=%GRAIN_BASE%"
    set "STOCK_LABEL=Fujifilm Eterna 250D"
)
if "%STOCK_SEL%"=="2" (
    set "GRAIN_PRESET=%GRAIN_BASE%-1"
    set "STOCK_LABEL=Fujifilm Eterna 500T"
)
if "%STOCK_SEL%"=="3" (
    set "GRAIN_PRESET=%GRAIN_BASE%-2"
    set "STOCK_LABEL=Kodak Vision3 250D"
)
if "%STOCK_SEL%"=="4" (
    set "GRAIN_PRESET=%GRAIN_BASE%-3"
    set "STOCK_LABEL=Kodak Vision3 200T"
)

:GRAIN_PRESET_READY
set "GRAIN_MODE=PRESET"
set "GRAIN_APPLY_ARGS=--preset "%GRAIN_PRESET%""
set "GRAIN_LABEL=%GRAIN_PRESET% / %STOCK_LABEL%"
set "GRAIN_FILE_TAG=%GRAIN_PRESET:-=_%"
goto GRAIN_MENU_DONE


rem ------------------------------------------------------------
rem Photon-noise ISO mode
rem This is not the same character as a scanned-film preset.
rem It is here for direct, predictable grain-strength control.
rem ------------------------------------------------------------

:GRAIN_PHOTON_MENU
set "GRAIN_MODE=ISO"

echo.
echo Photon Grain ISO:
echo.
echo   [1] ISO 200    - light
echo   [2] ISO 400    - medium
echo   [3] ISO 800    - strong       ^(default^)
echo   [4] ISO 1600   - very strong
echo   [5] Custom ISO
echo.
set "ISO_SEL=3"
set /p "ISO_SEL=Select [1-5, default 3]: "

set "GRAIN_ISO=800"

if "%ISO_SEL%"=="1" set "GRAIN_ISO=200"
if "%ISO_SEL%"=="2" set "GRAIN_ISO=400"
if "%ISO_SEL%"=="3" set "GRAIN_ISO=800"
if "%ISO_SEL%"=="4" set "GRAIN_ISO=1600"

if "%ISO_SEL%"=="5" (
    set "CUSTOM_ISO="
    set /p "CUSTOM_ISO=Enter ISO value [positive integer]: "
    call :VALIDATE_CUSTOM_ISO
)

echo.
echo Chroma grain:
echo.
echo   [1] Luma only                 ^(default / cleaner^)
echo   [2] Luma + chroma
echo.
set "CHROMA_SEL=1"
set /p "CHROMA_SEL=Select [1-2, default 1]: "

set "CHROMA_ARGS="
set "CHROMA_LABEL=Luma only"
if "%CHROMA_SEL%"=="2" (
    set "CHROMA_ARGS=--chroma"
    set "CHROMA_LABEL=Luma + chroma"
)

set "GRAIN_APPLY_ARGS=--iso %GRAIN_ISO% %CHROMA_ARGS%"
set "GRAIN_LABEL=Photon ISO %GRAIN_ISO% / %CHROMA_LABEL%"
set "GRAIN_FILE_TAG=ISO%GRAIN_ISO%"
goto GRAIN_MENU_DONE


:GRAIN_MENU_DONE


rem ============================================================
rem AV1 bitrate menu
rem ============================================================

echo.
echo Average AV1 video bitrate:
echo.
echo   [1] 1000 kbps
echo   [2] 1500 kbps   ^(default^)
echo   [3] 2500 kbps
echo   [4] 4000 kbps
echo.
echo   Or enter a custom bitrate in kbps, for example:
echo   800, 1200, 1800, 3000, 6000 ...
echo.
set "BSEL=2"
set /p "BSEL=Select [1-4, default 2, or custom kbps]: "

if "%BSEL%"=="1" goto BITRATE_PRESET_1
if "%BSEL%"=="2" goto BITRATE_PRESET_2
if "%BSEL%"=="3" goto BITRATE_PRESET_3
if "%BSEL%"=="4" goto BITRATE_PRESET_4

echo(%BSEL%| findstr /r /x "[0-9][0-9]*" >nul
if errorlevel 1 goto BITRATE_INVALID

set /a BSEL_NUM=%BSEL% >nul 2>&1
if errorlevel 1 goto BITRATE_INVALID
if %BSEL% LEQ 10 goto BITRATE_INVALID

set "BITRATE_NUM=%BSEL%"
set /a CUSTOM_MAX=%BSEL%*2
set /a CUSTOM_BUF=%BSEL%*4
set "BITRATE=%BSEL%k"
set "MAXRATE=%CUSTOM_MAX%k"
set "BUFSIZE=%CUSTOM_BUF%k"
goto BITRATE_DONE

:BITRATE_PRESET_1
set "BITRATE_NUM=1000"
set "BITRATE=1000k"
set "MAXRATE=2000k"
set "BUFSIZE=4000k"
goto BITRATE_DONE

:BITRATE_PRESET_2
set "BITRATE_NUM=1500"
set "BITRATE=1500k"
set "MAXRATE=3000k"
set "BUFSIZE=6000k"
goto BITRATE_DONE

:BITRATE_PRESET_3
set "BITRATE_NUM=2500"
set "BITRATE=2500k"
set "MAXRATE=5000k"
set "BUFSIZE=10000k"
goto BITRATE_DONE

:BITRATE_PRESET_4
set "BITRATE_NUM=4000"
set "BITRATE=4000k"
set "MAXRATE=8000k"
set "BUFSIZE=16000k"
goto BITRATE_DONE

:BITRATE_INVALID
echo.
echo Invalid bitrate selection. Falling back to 1500 kbps.
set "BITRATE_NUM=1500"
set "BITRATE=1500k"
set "MAXRATE=3000k"
set "BUFSIZE=6000k"

:BITRATE_DONE


rem ============================================================
rem Optional social-platform upload master
rem ============================================================

echo.
echo Social / video-sharing upload copy:
echo.
echo   [1] Off   ^(default^)
echo   [2] Bake Film Grain to pixels + H.264 MP4
echo.
echo       Universal upload master for:
echo       YouTube / Bilibili / Douyin / Tencent Video / etc.
echo.
echo       Grain is synthesized by libdav1d BEFORE H.264 encoding,
echo       so the platform receives ordinary pixels, not AV1 Grain metadata.
echo.
set "UPLOAD_SEL=1"
set /p "UPLOAD_SEL=Select [1-2, default 1]: "

if "%UPLOAD_SEL%"=="2" (
    set "ENABLE_UPLOAD_BAKE=1"
    set "UPLOAD_LABEL=H.264 pixel-grain MP4"
    set "TOTAL_STAGES=5"
) else (
    set "ENABLE_UPLOAD_BAKE=0"
    set "UPLOAD_LABEL=Off"
    set "TOTAL_STAGES=4"
)

if "%ENABLE_UPLOAD_BAKE%"=="1" (
    "%FFMPEG%" -hide_banner -h decoder=libdav1d >nul 2>&1
    if errorlevel 1 (
        echo.
        echo ERROR: This FFmpeg build does not contain the libdav1d AV1 decoder.
        echo Upload Bake requires libdav1d so AV1 Film Grain is synthesized
        echo into the decoded pixels before re-encoding.
        echo.
        echo You can verify manually with:
        echo "%FFMPEG%" -hide_banner -h decoder=libdav1d
        echo.
        pause
        exit /b 1
    )
)


rem ============================================================
rem Encoder arguments
rem ============================================================

set "BF_ARGS="
if "%ENABLE_BF%"=="1" set "BF_ARGS=-bf %BF% -b_ref_mode middle"

set "TAQ_ARGS="
if "%ENABLE_TEMPORAL_AQ%"=="1" set "TAQ_ARGS=-temporal-aq 1"

set "MAIN_HWACCEL_ARGS="
if "%ENABLE_MAIN_NVDEC%"=="1" set "MAIN_HWACCEL_ARGS=-hwaccel cuda -hwaccel_device %CUDA_DEVICE%"


rem ============================================================
rem Session summary
rem ============================================================

cls
echo ============================================================
echo        AV1 NVENC + grav1synth Film Grain FINAL v7.5 ContainerSelect
echo ============================================================
echo.
echo Speed mode    : %SPEED_LABEL%
echo Bitrate       : %BITRATE%
echo Max bitrate   : %MAXRATE%
echo Frame rate    : %FPS_LABEL%
echo Cinema frame  : %CROP_LABEL%
echo Grain mode    : %GRAIN_MODE%
echo Grain profile : %GRAIN_LABEL%
echo Container     : %CONTAINER_LABEL%
echo Upload copy   : %UPLOAD_LABEL%
echo Upload copy   : %UPLOAD_LABEL%
echo AV1 bit depth : 10-bit
echo NVENC preset  : %PRESET%
echo Multipass     : %MULTIPASS%
echo Lookahead     : %LOOKAHEAD%
if "%ENABLE_MAIN_NVDEC%"=="1" (
    echo Main decode   : NVDEC CUDA
) else (
    echo Main decode   : Software
)
if "%ENABLE_BF%"=="1" (
    echo B-frames      : Enabled ^(%BF% / middle ref^)
) else (
    echo B-frames      : Disabled
)
if "%ENABLE_TEMPORAL_AQ%"=="1" (
    echo Temporal AQ   : Enabled
) else (
    echo Temporal AQ   : Disabled
)
echo.
echo Pipeline:
echo   AV1 NVENC ^(IVF^) -^> grav1synth ^(IVF^) -^> MKV remux -^> verify
if "%ENABLE_UPLOAD_BAKE%"=="1" echo   Final AV1 -^> libdav1d Film Grain synthesis -^> H.264 MP4
echo Intermediate  : IVF
echo ============================================================
echo.


rem ============================================================
rem Process files
rem ============================================================

:PROCESS_NEXT
if "%~1"=="" goto FINISHED

set "INPUT=%~f1"
set "INDIR=%~dp1"
set "NAME=%~n1"

echo.
echo ============================================================
echo Input : "%INPUT%"
echo ============================================================

set "LAST_ERROR_STAGE="
set "LAST_ERROR_LOG="

set "WIDTH="
set "HEIGHT="
set "FPS="
set "DURATION="
set "DIM="

set "PROBE_DIM=%TEMP%\AV1GS_dim_%RANDOM%_%RANDOM%.txt"
set "PROBE_FPS=%TEMP%\AV1GS_fps_%RANDOM%_%RANDOM%.txt"
set "PROBE_DUR=%TEMP%\AV1GS_dur_%RANDOM%_%RANDOM%.txt"

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x "%INPUT%" > "%PROBE_DIM%" 2>nul
if errorlevel 1 (
    echo ERROR: FFprobe could not open the input video.
    set /a FAIL_COUNT+=1
    del /q "%PROBE_DIM%" "%PROBE_FPS%" "%PROBE_DUR%" >nul 2>&1
    shift
    goto PROCESS_NEXT
)

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=avg_frame_rate -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_FPS%" 2>nul
"%FFPROBE%" -v error -show_entries format=duration -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_DUR%" 2>nul

set /p "DIM="<"%PROBE_DIM%"
set /p "FPS="<"%PROBE_FPS%"
set /p "DURATION="<"%PROBE_DUR%"

del /q "%PROBE_DIM%" "%PROBE_FPS%" "%PROBE_DUR%" >nul 2>&1

for /f "tokens=1,2 delims=x" %%A in ("%DIM%") do (
    set "WIDTH=%%A"
    set "HEIGHT=%%B"
)

if not defined WIDTH (
    echo ERROR: Could not read video width.
    set /a FAIL_COUNT+=1
    shift
    goto PROCESS_NEXT
)

if not defined HEIGHT (
    echo ERROR: Could not read video height.
    set /a FAIL_COUNT+=1
    shift
    goto PROCESS_NEXT
)

if not defined FPS set "FPS=30000/1001"
if "%FPS%"=="0/0" set "FPS=30000/1001"
if /i "%DURATION%"=="N/A" set "DURATION="

rem ---------- resolve FPS ----------
set "OUT_FPS=%FPS%"
set "FPS_FILTER="
set "FPS_DECISION=Source FPS"
set "FPS_SUFFIX="

if "%FPS_MODE%"=="AUTO" call :AUTO_CINEMA_FPS

rem ---------- resolve 2.39 active-picture crop ----------
set "CROP_FILTER="
set "CROP_DECISION=Off"
set "ACTIVE_WIDTH=%WIDTH%"
set "ACTIVE_HEIGHT=%HEIGHT%"
set "FILE_CROP_SUFFIX="

if "%ENABLE_CROP%"=="1" call :PREPARE_CROP

rem ---------- final output ----------
set "PRESET_SAFE=%GRAIN_FILE_TAG%"
set "OUTPUT=%INDIR%%NAME%_AV1GS_%PRESET_SAFE%_%SPEED_SUFFIX%_%BITRATE_NUM%k%CROP_SUFFIX%%FPS_SUFFIX%.%EXT%"
set "UPLOAD_OUTPUT=%INDIR%%NAME%_AV1GS_%PRESET_SAFE%_%SPEED_SUFFIX%_%BITRATE_NUM%k%CROP_SUFFIX%%FPS_SUFFIX%_UPLOAD_H264_GRAIN.mp4"

if exist "%OUTPUT%" (
    echo SKIP: Output already exists:
    echo "%OUTPUT%"
    set /a SKIP_COUNT+=1
    shift
    goto PROCESS_NEXT
)

rem ---------- automatic high-quality upload-master bitrate ----------
set /a UPLOAD_PIXELS=%ACTIVE_WIDTH%*%ACTIVE_HEIGHT%
set "UPLOAD_BITRATE=18000k"
set "UPLOAD_MAXRATE=27000k"
set "UPLOAD_BUFSIZE=36000k"

if %UPLOAD_PIXELS% LEQ 921600 (
    set "UPLOAD_BITRATE=10000k"
    set "UPLOAD_MAXRATE=15000k"
    set "UPLOAD_BUFSIZE=20000k"
) else if %UPLOAD_PIXELS% LEQ 2073600 (
    set "UPLOAD_BITRATE=18000k"
    set "UPLOAD_MAXRATE=27000k"
    set "UPLOAD_BUFSIZE=36000k"
) else if %UPLOAD_PIXELS% LEQ 3686400 (
    set "UPLOAD_BITRATE=30000k"
    set "UPLOAD_MAXRATE=45000k"
    set "UPLOAD_BUFSIZE=60000k"
) else (
    set "UPLOAD_BITRATE=45000k"
    set "UPLOAD_MAXRATE=67500k"
    set "UPLOAD_BUFSIZE=90000k"
)

rem ---------- isolated same-drive temporary workspace ----------
rem All temporary/intermediate files stay inside one directory.
rem Successful jobs remove it automatically.
rem Failed jobs may retain it for troubleshooting.
set "JOBID=%RANDOM%_%RANDOM%"
set "JOBDIR=%INDIR%__AV1GS_TMP_%JOBID%"
set "TMP_BASE=%JOBDIR%\base.ivf"
set "TMP_GRAIN=%JOBDIR%\grain.ivf"
set "VERIFY_TABLE=%JOBDIR%\verify.txt"
set "ENCODE_LOG=%JOBDIR%\encode.log"
set "GRAIN_LOG=%JOBDIR%\grain.log"
set "REMUX_LOG=%JOBDIR%\remux.log"
set "VERIFY_LOG=%JOBDIR%\verify.log"

if exist "%JOBDIR%" rmdir /s /q "%JOBDIR%" >nul 2>&1
mkdir "%JOBDIR%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Could not create temporary workspace:
    echo "%JOBDIR%"
    set /a FAIL_COUNT+=1
    shift
    goto PROCESS_NEXT
)

set "DURATION_ARGS="
if defined DURATION set "DURATION_ARGS=-t %DURATION%"

set "VIDEO_FILTER=%FPS_FILTER%%CROP_FILTER%format=p010le"

echo Source      : %WIDTH%x%HEIGHT% @ %FPS%
echo Output      : %ACTIVE_WIDTH%x%ACTIVE_HEIGHT% @ %OUT_FPS%
if "%FPS_MODE%"=="AUTO" echo FPS choice  : %FPS_DECISION%
if "%ENABLE_CROP%"=="1" echo Framing     : %CROP_DECISION%
echo Grain       : %GRAIN_LABEL%
echo Bitrate     : %BITRATE%
echo Container   : %CONTAINER_LABEL%
echo Final file  : "%OUTPUT%"
if "%ENABLE_UPLOAD_BAKE%"=="1" (
    echo Upload MP4 : "%UPLOAD_OUTPUT%"
    echo Upload rate: %UPLOAD_BITRATE%
)
if defined DURATION echo Duration    : %DURATION% sec
echo.


rem ============================================================
rem Stage 1 - AV1 NVENC video-only encode
rem ============================================================

echo [1/%TOTAL_STAGES%] Encoding clean AV1 Main 10 with NVENC...

set "CMD_ENCODE="%FFMPEG%" -hide_banner -stats -y %MAIN_HWACCEL_ARGS% -i "%INPUT%" -vf "%VIDEO_FILTER%" -map 0:v:0 -an -sn -dn -c:v av1_nvenc -pix_fmt p010le -highbitdepth 1 -preset %PRESET% -tune hq -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% -multipass %MULTIPASS% -rc-lookahead %LOOKAHEAD% -spatial-aq 1 %TAQ_ARGS% -aq-strength %AQ_STRENGTH% %BF_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -f ivf "%TMP_BASE%""
%CMD_ENCODE%

if errorlevel 1 (
    echo.
    echo ERROR: AV1 NVENC encode failed.
    set "LAST_ERROR_STAGE=Stage 1 - AV1 NVENC encode"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)

if not exist "%TMP_BASE%" (
    echo.
    echo ERROR: AV1 intermediate file was not created.
    set "LAST_ERROR_STAGE=Stage 1 - AV1 NVENC output missing"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)

rem ============================================================
rem Stage 2 - inject AV1 Film Grain metadata
rem ============================================================

echo.
echo [2/%TOTAL_STAGES%] Injecting AV1 Film Grain with grav1synth ^(IVF path^)...

set "CMD_GRAIN="%GRAV1SYNTH%" apply "%TMP_BASE%" -o "%TMP_GRAIN%" %GRAIN_APPLY_ARGS% --replace -y"
pushd "%JOBDIR%"
%CMD_GRAIN% > "%GRAIN_LOG%" 2>&1
set "GRAIN_RC=%ERRORLEVEL%"
popd

if not "%GRAIN_RC%"=="0" (
    echo.
    echo ============================================================
    echo grav1synth ERROR OUTPUT
    echo ============================================================
    if exist "%GRAIN_LOG%" (
        type "%GRAIN_LOG%"
    ) else (
        echo No grav1synth log file was created.
    )
    echo ============================================================
    echo.
    echo ERROR: grav1synth failed with exit code %GRAIN_RC%.
    set "LAST_ERROR_STAGE=Stage 2 - grav1synth apply"
    set "LAST_ERROR_LOG=%GRAIN_LOG%"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)

if not exist "%TMP_GRAIN%" (
    echo.
    echo ERROR: grav1synth did not create the grain AV1 intermediate.
    set "LAST_ERROR_STAGE=Stage 2 - grav1synth output missing"
    set "LAST_ERROR_LOG=%GRAIN_LOG%"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)


rem ============================================================
rem Stage 3 - restore original audio/subtitles/attachments
rem ============================================================

echo.
echo [3/%TOTAL_STAGES%] Building final %CONTAINER_MODE% container...

set "CMD_REMUX="%FFMPEG%" -hide_banner -stats -y -i "%TMP_GRAIN%" -i "%INPUT%" -map 0:v:0 %FINAL_REMUX_MAP% -map_metadata 1 -map_chapters 1 %FINAL_REMUX_CODEC% %FINAL_REMUX_EXTRA% "%OUTPUT%""
%CMD_REMUX%

if errorlevel 1 (
    echo.
    echo ERROR: Final remux failed.
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=Stage 3 - FFmpeg remux"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)


rem ============================================================
rem Stage 4 - end-to-end verification
rem ============================================================

echo.
echo [4/%TOTAL_STAGES%] Verifying Film Grain headers in FINAL file...

del /q "%VERIFY_TABLE%" "%VERIFY_LOG%" >nul 2>&1
"%GRAV1SYNTH%" inspect "%OUTPUT%" -o "%VERIFY_TABLE%" -y > "%VERIFY_LOG%" 2>&1
set "VERIFY_RC=%ERRORLEVEL%"

if not "%VERIFY_RC%"=="0" (
    echo.
    echo ============================================================
    echo grav1synth VERIFY ERROR OUTPUT
    echo ============================================================
    if exist "%VERIFY_LOG%" type "%VERIFY_LOG%"
    echo ============================================================
    echo.
    echo ERROR: grav1synth verification command failed.
    echo Final file has been kept for inspection:
    echo "%OUTPUT%"
    set "LAST_ERROR_STAGE=Stage 4 - grav1synth inspect"
    set "LAST_ERROR_LOG=%VERIFY_LOG%"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)

if not exist "%VERIFY_TABLE%" (
    echo.
    echo ERROR: No AV1 Film Grain headers were found in the final file.
    echo Final file has been kept for inspection:
    echo "%OUTPUT%"
    set "LAST_ERROR_STAGE=Stage 4 - no Film Grain table produced"
    set "LAST_ERROR_LOG=%VERIFY_LOG%"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)

for %%Z in ("%VERIFY_TABLE%") do if %%~zZ LEQ 16 (
    echo.
    echo ERROR: Grain verification table is unexpectedly empty.
    echo Final file has been kept for inspection:
    echo "%OUTPUT%"
    set "LAST_ERROR_STAGE=Stage 4 - empty Film Grain table"
    set "LAST_ERROR_LOG=%VERIFY_LOG%"
    set /a FAIL_COUNT+=1
    call :HANDLE_FAILED_JOB
    goto FILE_DONE
)

if exist "%VERIFY_LOG%" type "%VERIFY_LOG%"
echo.
echo VERIFIED: AV1 Film Grain headers are present.

rem ============================================================
rem Stage 5 - optional Film Grain Bake-to-Pixels upload master
rem ============================================================

if "%ENABLE_UPLOAD_BAKE%"=="1" (
    echo.
    echo [5/5] Baking AV1 Film Grain to pixels for upload...
    echo.
    echo Decoder      : libdav1d / Film Grain default ON
    echo Upload codec : H.264 NVENC / High / yuv420p
    echo Video rate   : %UPLOAD_BITRATE%
    echo Audio        : AAC 320k stereo / 48 kHz
    echo.

    rem IMPORTANT: invoke FFmpeg directly inside this parenthesized block.
    rem Do not assign a command variable and expand it here.
    if exist "%UPLOAD_OUTPUT%" (
        echo SKIP Upload MP4 already exists:
        echo "%UPLOAD_OUTPUT%"
    ) else (
        "%FFMPEG%" -hide_banner -stats -y -c:v libdav1d -i "%OUTPUT%" -map 0:v:0 -map 0:a:0? -map_metadata 0 -c:v h264_nvenc -profile:v high -pix_fmt yuv420p -preset p6 -tune hq -rc vbr -b:v %UPLOAD_BITRATE% -maxrate:v %UPLOAD_MAXRATE% -bufsize:v %UPLOAD_BUFSIZE% -multipass fullres -rc-lookahead 32 -spatial-aq 1 -aq-strength %AQ_STRENGTH% -temporal-aq 1 -bf 4 -b_ref_mode middle -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%UPLOAD_OUTPUT%"

        if errorlevel 1 (
            echo.
            echo ERROR: Upload Bake encode failed.
            if exist "%UPLOAD_OUTPUT%" del /q "%UPLOAD_OUTPUT%" >nul 2>&1
            set "LAST_ERROR_STAGE=Stage 5 - Film Grain Bake upload encode"
            set /a FAIL_COUNT+=1
            call :HANDLE_FAILED_JOB
            goto FILE_DONE
        )

        if not exist "%UPLOAD_OUTPUT%" (
            echo.
            echo ERROR: Upload Bake MP4 was not created.
            set "LAST_ERROR_STAGE=Stage 5 - upload MP4 missing"
            set /a FAIL_COUNT+=1
            call :HANDLE_FAILED_JOB
            goto FILE_DONE
        )

        echo.
        echo UPLOAD MASTER DONE:
        echo "%UPLOAD_OUTPUT%"
    )
)

echo.
echo DONE:
echo "%OUTPUT%"
set /a SUCCESS_COUNT+=1

rmdir /s /q "%JOBDIR%" >nul 2>&1
goto FILE_DONE



:HANDLE_FAILED_JOB
if "%KEEP_FAILED_INTERMEDIATES%"=="1" (
    echo.
    echo Temporary workspace retained for troubleshooting:
    echo "%JOBDIR%"
    echo.
    echo Files in this directory are intermediate/debug files, not final output.
) else (
    if defined JOBDIR if exist "%JOBDIR%" rmdir /s /q "%JOBDIR%" >nul 2>&1
)
exit /b 0


:FILE_DONE

shift
goto PROCESS_NEXT


rem ============================================================
rem Custom ISO validator
rem ============================================================

:VALIDATE_CUSTOM_ISO
if not defined CUSTOM_ISO (
    echo Invalid ISO. Falling back to ISO 800.
    set "GRAIN_ISO=800"
    exit /b 0
)

echo(%CUSTOM_ISO%| findstr /r /x "[0-9][0-9]*" >nul
if errorlevel 1 (
    echo Invalid ISO. Falling back to ISO 800.
    set "GRAIN_ISO=800"
    exit /b 0
)

set /a ISO_TEST=%CUSTOM_ISO% >nul 2>&1
if errorlevel 1 (
    echo Invalid ISO. Falling back to ISO 800.
    set "GRAIN_ISO=800"
    exit /b 0
)

if %CUSTOM_ISO% LEQ 0 (
    echo Invalid ISO. Falling back to ISO 800.
    set "GRAIN_ISO=800"
    exit /b 0
)

set "GRAIN_ISO=%CUSTOM_ISO%"
exit /b 0


rem ============================================================
rem AUTO FPS helper
rem ============================================================

:AUTO_CINEMA_FPS

if "%FPS%"=="24000/1001" goto AUTO_23976_SAME
if "%FPS%"=="30000/1001" goto AUTO_23976
if "%FPS%"=="48000/1001" goto AUTO_23976
if "%FPS%"=="60000/1001" goto AUTO_23976
if "%FPS%"=="120000/1001" goto AUTO_23976

if "%FPS%"=="24/1" goto AUTO_24_SAME
if "%FPS%"=="24" goto AUTO_24_SAME
if "%FPS%"=="25/1" goto AUTO_24
if "%FPS%"=="25" goto AUTO_24
if "%FPS%"=="30/1" goto AUTO_24
if "%FPS%"=="30" goto AUTO_24
if "%FPS%"=="48/1" goto AUTO_24
if "%FPS%"=="48" goto AUTO_24
if "%FPS%"=="50/1" goto AUTO_24
if "%FPS%"=="50" goto AUTO_24
if "%FPS%"=="60/1" goto AUTO_24
if "%FPS%"=="60" goto AUTO_24
if "%FPS%"=="100/1" goto AUTO_24
if "%FPS%"=="100" goto AUTO_24
if "%FPS%"=="120/1" goto AUTO_24
if "%FPS%"=="120" goto AUTO_24

set "OUT_FPS=%FPS%"
set "FPS_FILTER="
set "FPS_DECISION=Unknown rate - keep source"
set "FPS_SUFFIX="
exit /b 0

:AUTO_23976
set "OUT_FPS=24000/1001"
set "FPS_FILTER=fps=24000/1001,"
set "FPS_DECISION=23.976 fps"
set "FPS_SUFFIX=_23976p"
exit /b 0

:AUTO_23976_SAME
set "OUT_FPS=24000/1001"
set "FPS_FILTER="
set "FPS_DECISION=Already 23.976 fps"
set "FPS_SUFFIX=_23976p"
exit /b 0

:AUTO_24
set "OUT_FPS=24"
set "FPS_FILTER=fps=24,"
set "FPS_DECISION=24.000 fps"
set "FPS_SUFFIX=_24p"
exit /b 0

:AUTO_24_SAME
set "OUT_FPS=24"
set "FPS_FILTER="
set "FPS_DECISION=Already 24.000 fps"
set "FPS_SUFFIX=_24p"
exit /b 0


rem ============================================================
rem 2.39 active-picture crop helper
rem ============================================================

:PREPARE_CROP

set "CROP_FILTER="
set "CROP_DECISION=No crop needed"
set "ACTIVE_WIDTH=%WIDTH%"
set "ACTIVE_HEIGHT=%HEIGHT%"

rem If source is already 2.39:1 or wider, do not crop further.
set /a ASPECT_LEFT=%WIDTH%*100
set /a ASPECT_RIGHT=%HEIGHT%*239
if %ASPECT_LEFT% GEQ %ASPECT_RIGHT% exit /b 0

rem Nearest even active height to WIDTH / 2.39.
rem Formula: round(W*100/239 to nearest multiple of 2)
set /a TARGET_H=((%WIDTH%*100+239)/478)*2

if %TARGET_H% GEQ %HEIGHT% exit /b 0
if %TARGET_H% LEQ 0 exit /b 0

set /a CROP_Y=(%HEIGHT%-%TARGET_H%)/2
rem Prefer even chroma-aligned vertical origin.
set /a CROP_Y=(CROP_Y/2)*2

set "ACTIVE_HEIGHT=%TARGET_H%"
set "CROP_FILTER=crop=w=iw:h=%TARGET_H%:x=0:y=%CROP_Y%,"
set "CROP_DECISION=%WIDTH%x%HEIGHT% to %WIDTH%x%TARGET_H% centered"
exit /b 0


rem ============================================================
rem Finished
rem ============================================================

:FINISHED
if "%FAIL_COUNT%"=="0" cls
echo.
echo ============================================================
echo                    Batch Summary
echo ============================================================
echo.
echo Encoder       : AV1 NVENC Main 10
echo Intermediate  : IVF
echo Grain engine  : grav1synth AV1 Film Grain
echo Grain profile : %GRAIN_LABEL%
echo Container     : %CONTAINER_LABEL%
echo Speed mode    : %SPEED_LABEL%
echo Bitrate       : %BITRATE%
echo Frame rate    : %FPS_LABEL%
echo Cinema frame  : %CROP_LABEL%
echo Total dragged : %FILE_COUNT%
echo Successful    : %SUCCESS_COUNT%
echo Failed        : %FAIL_COUNT%
echo Skipped       : %SKIP_COUNT%
echo.
if "%FAIL_COUNT%"=="0" (
    echo RESULT: ALL COMPLETED SUCCESSFULLY.
) else (
    echo RESULT: COMPLETED WITH ERRORS.
)

if "%FILE_COUNT%"=="1" if not "%LAST_ERROR_STAGE%"=="" (
    echo.
    echo ============================================================
    echo Last failure
    echo ============================================================
    echo Stage : %LAST_ERROR_STAGE%
    if not "%LAST_ERROR_LOG%"=="" (
        echo Log   : "%LAST_ERROR_LOG%"
        if exist "%LAST_ERROR_LOG%" (
            echo.
            type "%LAST_ERROR_LOG%"
        )
    )
)

if "%FILE_COUNT%"=="1" (
    echo.
    echo ============================================================
    echo Actual commands
    echo ============================================================
    if defined CMD_ENCODE echo [Encode] %CMD_ENCODE%
    if defined CMD_GRAIN  echo [Grain ] %CMD_GRAIN%
    if defined CMD_REMUX  echo [Remux ] %CMD_REMUX%
)

echo.
echo ============================================================
echo.
pause
endlocal
