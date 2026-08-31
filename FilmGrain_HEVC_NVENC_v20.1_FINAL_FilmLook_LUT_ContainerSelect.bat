@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem  Multi Film Grain Plate + Vulkan GPU Blend + HEVC NVENC batch encoder
rem  Windows drag-and-drop / multi-file version - V20.1 Final + Film Look LUT FIX + Container Select
rem
rem  IMPORTANT:
rem  The grain plate must be a neutral 50%% gray plate intended
rem  for OVERLAY blend mode.
rem ============================================================

rem ---------- USER SETTINGS ----------

rem Fixed project FFmpeg/FFprobe build (NVENC API 13.0 + Vulkan overlay).
set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"

rem GPU selectors. 0 normally selects the RTX 4080.
set "VULKAN_DEVICE=0"
set "CUDA_DEVICE=0"

rem Main-video NVDEC switch:
rem   1 = enable NVIDIA hardware decode for the main input
rem   0 = software decode
rem Default = 1 for RTX 4080 testing.
set "ENABLE_MAIN_NVDEC=1"

rem Or specify full paths, for example:
rem set "FFMPEG=C:\Tools\ffmpeg\bin\ffmpeg.exe"
rem set "FFPROBE=C:\Tools\ffmpeg\bin\ffprobe.exe"

rem Grain plate root folder:
rem The script recursively searches this folder and all subfolders.
rem
rem Set your permanent Grain SSD folder here.
rem Example:
rem set "DEFAULT_GRAIN_ROOT=G:\FilmGrain"
rem
rem Leave empty to use the folder containing this BAT file.
set "DEFAULT_GRAIN_ROOT=D:\Film_Grain"

if defined DEFAULT_GRAIN_ROOT (
    set "GRAIN_ROOT=%DEFAULT_GRAIN_ROOT%"
) else (
    set "GRAIN_ROOT=%~dp0"
)

rem NVENC quality settings are selected at runtime:
rem   Standard = p6 / fullres / lookahead 32
rem   FAST     = p5 / qres    / lookahead 16
set "PRESET="
set "LOOKAHEAD="
set "MULTIPASS="
set "SPEED_LABEL="
rem B-frame switch: 1 = enable, 0 = disable
rem T600 Laptop: use 0
rem RTX 4080: use 1
set "ENABLE_BF=1"
set "BF=4"

rem Temporal AQ switch: 1 = enable, 0 = disable
rem T600 Laptop: use 0
rem RTX 4080: use 1
set "ENABLE_TEMPORAL_AQ=1"

set "AQ_STRENGTH=8"

rem Output container is selected at runtime.
rem MKV preserves original audio/subtitles/attachments.
rem MP4 uses AAC audio and omits incompatible subtitle/attachment streams.
set "EXT=mkv"
set "CONTAINER_MODE=MKV"
set "CONTAINER_LABEL=MKV / preserve original streams"
set "STREAM_MAP_ARGS=-map 0:a? -map 0:s? -map 0:t?"
set "AUDIO_MUX_ARGS=-c:a copy -c:s copy -c:t copy"
set "CONTAINER_EXTRA_ARGS="

rem Film Look LUT folder.
rem The script recursively scans this folder for *.cube files.
set "LUT_ROOT=E:\Adobe Portable\LUTs\BT.709"

rem LUT runtime state. Default = disabled.
set "LUT_ENABLED=0"
set "LUT_PATH="
set "LUT_FILTER_PATH="
set "LUT_COMPAT_FILE="
set "LUT_LABEL=None"
set "LUT_OPACITY=0.75"
set "LUT_STRENGTH_PCT=75"
set "LUT_FILE_SUFFIX="

rem ---------- END USER SETTINGS ----------


"%FFMPEG%" -version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFmpeg was not found.
    echo Edit FFMPEG at the top of this BAT, or add FFmpeg to PATH.
    echo.
    pause
    exit /b 1
)

"%FFPROBE%" -version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFprobe was not found.
    echo Edit FFPROBE at the top of this BAT, or add FFprobe to PATH.
    echo.
    pause
    exit /b 1
)

rem Check Vulkan Overlay blend support.
"%FFMPEG%" -hide_banner -h filter=blend_vulkan 2>nul | findstr /i "overlay" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not expose blend_vulkan overlay mode.
    echo Run:
    echo   ffmpeg -hide_banner -h filter=blend_vulkan
    echo and confirm that "overlay" is listed.
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

rem Count dragged input files.
set "FILE_COUNT=0"
for %%# in (%*) do set /a FILE_COUNT+=1

rem Runtime counters for final summary.
set "SUCCESS_COUNT=0"
set "FAIL_COUNT=0"
set "SKIP_COUNT=0"

cls
echo ============================================================
echo        Film Grain + Vulkan Blend + HEVC NVENC
echo ============================================================
echo.
echo Speed / quality:
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
) else (
    set "PRESET=p5"
    set "LOOKAHEAD=16"
    set "MULTIPASS=qres"
    set "SPEED_LABEL=FAST"
)

echo.
echo Cinematic letterbox:
echo.
echo   [1] Off
echo   [2] Fight Club style - approx. 2.39:1   ^(default^)
echo.
set "LETTERBOX_SEL=2"
set /p "LETTERBOX_SEL=Select [1-2, default 2]: "
if "%LETTERBOX_SEL%"=="1" (
    set "ENABLE_LETTERBOX=0"
    set "LETTERBOX_LABEL=Off"
) else (
    set "ENABLE_LETTERBOX=1"
    set "LETTERBOX_LABEL=2.39:1"
)

echo.
echo Output frame rate:
echo.
echo   [1] Keep source FPS
echo   [2] Auto cinematic FPS   ^(default^)
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

echo.
echo Output container:
echo.
echo   [1] MKV - preserve original audio / subtitles / attachments ^(default^)
echo   [2] MP4 - compatibility mode
echo.
echo       MP4 mode:
echo       - HEVC video remains Main10
echo       - Audio is converted to AAC 320k
echo       - Original channel layout is preserved where supported
echo       - Subtitles / attachments are omitted for compatibility
echo       - Chapters / metadata are preserved
echo       - hvc1 + faststart enabled
echo.
set "CONTAINER_SEL=1"
set /p "CONTAINER_SEL=Select [1-2, default 1]: "

if "%CONTAINER_SEL%"=="2" (
    set "EXT=mp4"
    set "CONTAINER_MODE=MP4"
    set "CONTAINER_LABEL=MP4 compatibility / AAC audio"
    set "STREAM_MAP_ARGS=-map 0:a?"
    set "AUDIO_MUX_ARGS=-c:a aac -b:a 320k"
    set "CONTAINER_EXTRA_ARGS=-tag:v hvc1 -movflags +faststart"
) else (
    set "EXT=mkv"
    set "CONTAINER_MODE=MKV"
    set "CONTAINER_LABEL=MKV / preserve original streams"
    set "STREAM_MAP_ARGS=-map 0:a? -map 0:s? -map 0:t?"
    set "AUDIO_MUX_ARGS=-c:a copy -c:s copy -c:t copy"
    set "CONTAINER_EXTRA_ARGS="
)

rem ============================================================
rem Optional Film Look LUT
rem ============================================================

call :SELECT_FILM_LUT

cls
echo ============================================================
echo        Film Grain + Vulkan Blend + HEVC NVENC
echo ============================================================
echo.
echo Current Grain folder:
echo   "%GRAIN_ROOT%"
echo.
echo   [Enter] Use current folder
echo   [C]     Choose another Grain folder for this run
echo.
set "FSEL="
set /p "FSEL=Grain folder option [Enter/C]: "

if /i "%FSEL%"=="C" (
    echo.
    echo Enter the Grain root folder.
    echo Example: G:\FilmGrain
    echo.
    set "NEW_GRAIN_ROOT="
    set /p "NEW_GRAIN_ROOT=Folder: "
    if defined NEW_GRAIN_ROOT set "GRAIN_ROOT=%NEW_GRAIN_ROOT%"
)

rem Remove surrounding quotes if the folder was pasted with quotes.
if defined GRAIN_ROOT set "GRAIN_ROOT=%GRAIN_ROOT:"=%"

rem Remove one trailing backslash except for drive roots like I:\
if not "%GRAIN_ROOT:~-1%"=="\" goto GRAIN_ROOT_NORMALIZED
if "%GRAIN_ROOT:~1,2%"==":\" if "%GRAIN_ROOT:~3%"=="" goto GRAIN_ROOT_NORMALIZED
set "GRAIN_ROOT=%GRAIN_ROOT:~0,-1%"
:GRAIN_ROOT_NORMALIZED

if not exist "%GRAIN_ROOT%" (
    echo.
    echo ERROR: Grain folder does not exist:
    echo "%GRAIN_ROOT%"
    echo.
    pause
    exit /b 1
)

echo.
echo Grain folder in use:
echo   "%GRAIN_ROOT%"
echo.
echo Grain plate:
echo.
echo   [1]  Cinema Tools - 35mm 4K DCI
echo.
echo   TDCAT Light:
echo   [2]  35mm
echo   [3]  Super 35
echo   [4]  16mm
echo   [5]  Super 16
echo   [6]  8mm
echo.
echo   TDCAT Heavy:
echo   [7]  35mm
echo   [8]  Super 35
echo   [9]  16mm
echo   [10] Super 16
echo   [11] 8mm
echo.
set "PSEL=1"
set /p "PSEL=Select [1-11, default 1]: "

set "GRAIN="
set "GRAIN_SOURCE_MOV="
set "GRAIN_CACHE="
set "GRAIN_CACHE_1080="
set "GRAIN_INPUT="
set "GRAIN_PATTERN="
set "GRAIN_LABEL="
set "GRAIN_DECODE_LABEL="
set "GRAIN_HWACCEL_ARGS="
set "SUFFIX="

if "%PSEL%"=="1"  set "GRAIN_PATTERN=CT 35mm Grain 4K DCI.mov"
if "%PSEL%"=="1"  set "GRAIN_LABEL=Cinema Tools 35mm 4K DCI"
if "%PSEL%"=="1"  set "SUFFIX=_FG_CT35_V20_HEVC"

if "%PSEL%"=="2"  set "GRAIN_PATTERN=Filmgrain_4KDCI_35mm_24fps.mov"
if "%PSEL%"=="2"  set "GRAIN_LABEL=TDCAT 35mm Light"
if "%PSEL%"=="2"  set "SUFFIX=_FG_35L_V20_HEVC"

if "%PSEL%"=="3"  set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_35mm_24fps.mov"
if "%PSEL%"=="3"  set "GRAIN_LABEL=TDCAT Super 35 Light"
if "%PSEL%"=="3"  set "SUFFIX=_FG_S35L_V20_HEVC"

if "%PSEL%"=="4"  set "GRAIN_PATTERN=Filmgrain_4KDCI_16mm_24fps.mov"
if "%PSEL%"=="4"  set "GRAIN_LABEL=TDCAT 16mm Light"
if "%PSEL%"=="4"  set "SUFFIX=_FG_16L_V20_HEVC"

if "%PSEL%"=="5"  set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_16mm_24fps.mov"
if "%PSEL%"=="5"  set "GRAIN_LABEL=TDCAT Super 16 Light"
if "%PSEL%"=="5"  set "SUFFIX=_FG_S16L_V20_HEVC"

if "%PSEL%"=="6"  set "GRAIN_PATTERN=Filmgrain_4KDCI_8mm_24fps.mov"
if "%PSEL%"=="6"  set "GRAIN_LABEL=TDCAT 8mm Light"
if "%PSEL%"=="6"  set "SUFFIX=_FG_8L_V20_HEVC"

if "%PSEL%"=="7"  set "GRAIN_PATTERN=Filmgrain_4KDCI_35mm_24fps_Heavy.mov"
if "%PSEL%"=="7"  set "GRAIN_LABEL=TDCAT 35mm Heavy"
if "%PSEL%"=="7"  set "SUFFIX=_FG_35H_V20_HEVC"

if "%PSEL%"=="8"  set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_35mm_24fps_Heavy.mov"
if "%PSEL%"=="8"  set "GRAIN_LABEL=TDCAT Super 35 Heavy"
if "%PSEL%"=="8"  set "SUFFIX=_FG_S35H_V20_HEVC"

if "%PSEL%"=="9"  set "GRAIN_PATTERN=Filmgrain_4KDCI_16mm_24fps_Heavy.mov"
if "%PSEL%"=="9"  set "GRAIN_LABEL=TDCAT 16mm Heavy"
if "%PSEL%"=="9"  set "SUFFIX=_FG_16H_V20_HEVC"

if "%PSEL%"=="10" set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_16mm_24fps_Heavy.mov"
if "%PSEL%"=="10" set "GRAIN_LABEL=TDCAT Super 16 Heavy"
if "%PSEL%"=="10" set "SUFFIX=_FG_S16H_V20_HEVC"

if "%PSEL%"=="11" set "GRAIN_PATTERN=Filmgrain_4KDCI_8mm_24fps_Heavy.mov"
if "%PSEL%"=="11" set "GRAIN_LABEL=TDCAT 8mm Heavy"
if "%PSEL%"=="11" set "SUFFIX=_FG_8H_V20_HEVC"

rem Invalid/empty choice falls back to Cinema Tools.
if not defined GRAIN_PATTERN set "GRAIN_PATTERN=CT 35mm Grain 4K DCI.mov"
if not defined GRAIN_LABEL set "GRAIN_LABEL=Cinema Tools 35mm 4K DCI"
if not defined SUFFIX set "SUFFIX=_FG_CT35_V20_HEVC"

rem Search the selected Grain folder and all subfolders.
rem Use DIR -> temp file instead of FOR /R with a quoted root path.
rem This is safer for custom roots, spaces, Chinese names and paths without a trailing backslash.
set "GRAIN_FIND=%TEMP%\FilmGrain_find_%RANDOM%_%RANDOM%.txt"
dir /b /s /a-d "%GRAIN_ROOT%\%GRAIN_PATTERN%" > "%GRAIN_FIND%" 2>nul
if exist "%GRAIN_FIND%" set /p "GRAIN="<"%GRAIN_FIND%"
del /q "%GRAIN_FIND%" >nul 2>&1

if not defined GRAIN (
    echo.
    echo ERROR: Selected grain plate was not found.
    echo.
    echo Expected original filename:
    echo "%GRAIN_PATTERN%"
    echo.
    echo Search root:
    echo "%GRAIN_ROOT%"
    echo.
    echo Keep the original TDCAT/CT filenames and place the MOV files
    echo anywhere inside the selected Grain folder or its subfolders.
    echo.
    pause
    exit /b 1
)

rem Keep original MOV path for reference.
rem Effective cache selection is finalized per input file after its
rem resolution has been probed.
set "GRAIN_SOURCE_MOV=%GRAIN%"
set "GRAIN_CACHE=%GRAIN:~0,-4%_HEVC_Lossless.mkv"
set "GRAIN_CACHE_1080=%GRAIN:~0,-4%_1080p_HEVC_Lossless.mkv"

echo.
echo Selected plate:
echo   %GRAIN_LABEL%
echo.
echo Grain source:
echo   "%GRAIN_SOURCE_MOV%"
echo.
if exist "%GRAIN_CACHE_1080%" (
    echo 1080p cache:
    echo   "%GRAIN_CACHE_1080%"
) else (
    echo 1080p cache:
    echo   Not found
)
if exist "%GRAIN_CACHE%" (
    echo 4K cache:
    echo   "%GRAIN_CACHE%"
) else (
    echo 4K cache:
    echo   Not found
)
echo.
echo.
echo Grain strength:
echo.
echo   [1] Light    65%%
echo   [2] Natural  75%%
echo   [3] Strong   85%%   ^(recommended^)
echo   [4] Full     100%%
echo.
set "GSEL=3"
set /p "GSEL=Select [1-4, default 3]: "

if "%GSEL%"=="1" set "GRAIN_OPACITY=0.65"
if "%GSEL%"=="2" set "GRAIN_OPACITY=0.75"
if "%GSEL%"=="3" set "GRAIN_OPACITY=0.85"
if "%GSEL%"=="4" set "GRAIN_OPACITY=1.00"
if not defined GRAIN_OPACITY set "GRAIN_OPACITY=0.85"

echo.
echo Average video bitrate:
echo.
echo   [1] 6000 kbps
echo   [2] 7500 kbps   ^(default^)
echo   [3] 9000 kbps
echo   [4] 12000 kbps
echo.
echo   Or enter a custom bitrate in kbps, for example:
echo   1000, 1500, 2500, 5000 ...
echo.
set "BSEL=2"
set /p "BSEL=Select [1-4, default 2, or custom kbps]: "

if "%BSEL%"=="1" goto BITRATE_PRESET_1
if "%BSEL%"=="2" goto BITRATE_PRESET_2
if "%BSEL%"=="3" goto BITRATE_PRESET_3
if "%BSEL%"=="4" goto BITRATE_PRESET_4

rem Anything else must be a positive integer greater than 10.
echo(%BSEL%| findstr /r /x "[0-9][0-9]*" >nul
if errorlevel 1 goto BITRATE_INVALID

set /a BSEL_NUM=%BSEL% >nul 2>&1
if errorlevel 1 goto BITRATE_INVALID
if %BSEL% LEQ 10 goto BITRATE_INVALID

set /a CUSTOM_MAX=%BSEL%*2
set /a CUSTOM_BUF=%BSEL%*4
set "BITRATE=%BSEL%k"
set "MAXRATE=%CUSTOM_MAX%k"
set "BUFSIZE=%CUSTOM_BUF%k"
goto BITRATE_DONE

:BITRATE_PRESET_1
set "BITRATE=6000k"
set "MAXRATE=12000k"
set "BUFSIZE=24000k"
goto BITRATE_DONE

:BITRATE_PRESET_2
set "BITRATE=7500k"
set "MAXRATE=15000k"
set "BUFSIZE=30000k"
goto BITRATE_DONE

:BITRATE_PRESET_3
set "BITRATE=9000k"
set "MAXRATE=18000k"
set "BUFSIZE=36000k"
goto BITRATE_DONE

:BITRATE_PRESET_4
set "BITRATE=12000k"
set "MAXRATE=24000k"
set "BUFSIZE=48000k"
goto BITRATE_DONE

:BITRATE_INVALID
echo.
echo Invalid bitrate selection. Falling back to 7500 kbps.
set "BITRATE=7500k"
set "MAXRATE=15000k"
set "BUFSIZE=30000k"

:BITRATE_DONE

rem Add selected speed mode to the output suffix.
if /i "%SPEED_LABEL%"=="FAST" (
    set "SUFFIX=%SUFFIX:_V20_HEVC=%_V20FAST_HEVC%"
) else (
    set "SUFFIX=%SUFFIX:_V20_HEVC=%_V20_HEVC%"
)

rem Distinguish letterboxed output from normal output.
if "%ENABLE_LETTERBOX%"=="1" set "SUFFIX=%SUFFIX%_239LB"

rem Build optional B-frame arguments according to ENABLE_BF
set "BF_ARGS="
if "%ENABLE_BF%"=="1" set "BF_ARGS=-bf %BF% -b_ref_mode middle"

rem Build optional Temporal AQ arguments
set "TAQ_ARGS="
if "%ENABLE_TEMPORAL_AQ%"=="1" set "TAQ_ARGS=-temporal-aq 1"

rem Build optional main-input NVDEC arguments.
rem Without -hwaccel_output_format cuda, FFmpeg will transfer decoded
rem frames back to system memory automatically for the existing Vulkan path.
set "MAIN_HWACCEL_ARGS="
if "%ENABLE_MAIN_NVDEC%"=="1" set "MAIN_HWACCEL_ARGS=-hwaccel cuda -hwaccel_device %CUDA_DEVICE%"

echo.
echo ------------------------------------------------------------
echo Grain folder  : %GRAIN_ROOT%
echo Grain plate   : %GRAIN_LABEL%
echo Grain opacity : %GRAIN_OPACITY%
echo Bitrate       : %BITRATE%
echo Max bitrate   : %MAXRATE%
echo Speed mode    : %SPEED_LABEL%
echo Letterbox     : %LETTERBOX_LABEL%
echo Frame rate    : %FPS_LABEL%
echo Blend engine  : Vulkan GPU
echo Vulkan device : %VULKAN_DEVICE%
echo CUDA device   : %CUDA_DEVICE%
if "%ENABLE_MAIN_NVDEC%"=="1" (
    echo Main decode   : NVDEC CUDA
) else (
    echo Main decode   : Software
)
echo NVENC preset  : %PRESET%
echo Multipass     : %MULTIPASS%
echo Lookahead     : %LOOKAHEAD%
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
echo FPS mode      : CFR ^(%FPS_LABEL%^)
echo Duration cap  : source duration
echo Container     : %CONTAINER_LABEL%
echo Film Look     : %LUT_LABEL%
if "%LUT_ENABLED%"=="1" echo LUT strength   : %LUT_STRENGTH_PCT%%% ^(corrected direction^)
echo Output        : HEVC Main10 / %CONTAINER_MODE%
echo ------------------------------------------------------------
echo.

:PROCESS_NEXT
if "%~1"=="" goto FINISHED

set "INPUT=%~f1"
set "INDIR=%~dp1"
set "NAME=%~n1"
set "OUTPUT_BASE=%~dp1%~n1%SUFFIX%%LUT_FILE_SUFFIX%"
set "OUTPUT="

echo.
echo ============================================================
echo Input : "%INPUT%"
echo ============================================================

set "WIDTH="
set "HEIGHT="
set "FPS="
set "DURATION="
set "DIM="

rem Do NOT run ffprobe inside FOR /F command substitution here.
rem CMD re-parses that command line and special characters in filenames
rem can be damaged. Write probe results to temporary files first.
set "PROBE_DIM=%TEMP%\FilmGrain_dim_%RANDOM%_%RANDOM%.txt"
set "PROBE_FPS=%TEMP%\FilmGrain_fps_%RANDOM%_%RANDOM%.txt"
set "PROBE_DUR=%TEMP%\FilmGrain_dur_%RANDOM%_%RANDOM%.txt"

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

rem Resolve per-file output frame rate.
set "OUT_FPS=%FPS%"
set "MAIN_FPS_FILTER="
set "FPS_DECISION=Source FPS"
set "FPS_SUFFIX="

if "%FPS_MODE%"=="AUTO" call :AUTO_CINEMA_FPS

set "DURATION_ARGS="
set "GRAIN_TIME_ARGS="
if defined DURATION (
    set "DURATION_ARGS=-t %DURATION%"
    set "GRAIN_TIME_ARGS=-t %DURATION%"
)

echo Video    : %WIDTH%x%HEIGHT% @ %FPS%
echo OutputFPS: %OUT_FPS%
if "%FPS_MODE%"=="AUTO" echo Auto mode: %FPS_DECISION%

set "OUTPUT=%OUTPUT_BASE%%FPS_SUFFIX%.%EXT%"

if exist "%OUTPUT%" (
    echo SKIP: Output already exists:
    echo "%OUTPUT%"
    set /a SKIP_COUNT+=1
    shift
    goto PROCESS_NEXT
)

if defined DURATION (
    echo Duration : %DURATION% sec
) else (
    echo Duration : unknown
)

rem ------------------------------------------------------------
rem Optional 2.39:1 cinematic letterbox.
rem Output resolution stays unchanged; black bars cover top/bottom.
rem Calculation is done in a subroutine to avoid CMD block expansion.
rem ------------------------------------------------------------
call :PREPARE_LETTERBOX
echo.

rem Choose the fastest verified Grain source for this input resolution.
set "GRAIN_INPUT=%GRAIN_SOURCE_MOV%"
set "GRAIN_DECODE_LABEL=Original MOV / software decode"
set "GRAIN_HWACCEL_ARGS="
set "GRAIN_SCALE_REQUIRED=1"

rem Use 1080p cache for any source at or below 1920x1080.
rem Anything above 1080p (1440p/1600p/4K/etc.) prefers the 4K cache.
set "USE_1080_CACHE=0"
if %WIDTH% LEQ 1920 if %HEIGHT% LEQ 1080 set "USE_1080_CACHE=1"

if "%USE_1080_CACHE%"=="1" if exist "%GRAIN_CACHE_1080%" (
    set "GRAIN_INPUT=%GRAIN_CACHE_1080%"
    set "GRAIN_DECODE_LABEL=1080p HEVC Lossless cache / NVDEC CUDA"
    set "GRAIN_HWACCEL_ARGS=-hwaccel cuda -hwaccel_device %CUDA_DEVICE%"
    rem Skip scaling only for exact 1920x1080. Lower resolutions still scale down on Vulkan.
    if "%WIDTH%x%HEIGHT%"=="1920x1080" (
        set "GRAIN_SCALE_REQUIRED=0"
    ) else (
        set "GRAIN_SCALE_REQUIRED=1"
    )
)

if "%GRAIN_INPUT%"=="%GRAIN_SOURCE_MOV%" if exist "%GRAIN_CACHE%" (
    set "GRAIN_INPUT=%GRAIN_CACHE%"
    set "GRAIN_DECODE_LABEL=4K HEVC Lossless cache / NVDEC CUDA"
    set "GRAIN_HWACCEL_ARGS=-hwaccel cuda -hwaccel_device %CUDA_DEVICE%"
    set "GRAIN_SCALE_REQUIRED=1"
)

set "GRAIN=%GRAIN_INPUT%"

echo Grain    : %GRAIN_DECODE_LABEL%
echo           "%GRAIN%"
echo.

rem Build Grain filter branch.
set "GRAIN_FILTER=[1:v:0]fps=%OUT_FPS%,format=p010le,setpts=PTS-STARTPTS,hwupload"
if "%GRAIN_SCALE_REQUIRED%"=="1" set "GRAIN_FILTER=%GRAIN_FILTER%,scale_vulkan=w=%WIDTH%:h=%HEIGHT%:scaler=bilinear"
set "GRAIN_FILTER=%GRAIN_FILTER%[grainvk]"

rem Build the main-video branch.
rem No-LUT mode intentionally keeps the original V20 filter path unchanged.
set "BASE_FILTER=[0:v:0]%MAIN_FPS_FILTER%format=p010le,setpts=PTS-STARTPTS,hwupload[basevk]"

if "%LUT_ENABLED%"=="1" (
    rem lut3d works in RGB. Use 16-bit planar RGB for the LUT and strength blend,
    rem then return to P010 before Vulkan upload / real-grain overlay.
    set "BASE_FILTER=[0:v:0]%MAIN_FPS_FILTER%format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='%LUT_FILTER_PATH%':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=%LUT_OPACITY%,format=p010le,hwupload[basevk]"
)

rem
rem V20 adaptive decode/cache design:
rem   - Main input optionally uses NVIDIA NVDEC (ENABLE_MAIN_NVDEC).
rem   - Inputs <=1920x1080 prefer the verified 1080p Grain cache.
rem   - Inputs above 1080p prefer the verified 4K Grain cache.
rem   - Exact 1920x1080 needs no Grain scaling.
rem   - Lower resolutions use 1080p cache + Vulkan downscale.
rem   - Higher resolutions use 4K cache + Vulkan scale.
rem   - Overlay blend remains on Vulkan.
rem   - Stable CFR + source-duration hard cap is retained.
rem
set "CMDLINE="%FFMPEG%" -hide_banner -stats -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk %MAIN_HWACCEL_ARGS% -i "%INPUT%" -stream_loop -1 %GRAIN_TIME_ARGS% %GRAIN_HWACCEL_ARGS% -i "%GRAIN%" -filter_complex "%BASE_FILTER%;%GRAIN_FILTER%;[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=%GRAIN_OPACITY%,hwdownload,format=p010le%LETTERBOX_FILTER%[vout]" -map "[vout]" %STREAM_MAP_ARGS% -map_metadata 0 -map_chapters 0 -c:v hevc_nvenc -profile:v main10 -preset %PRESET% -tune hq -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% -multipass %MULTIPASS% -rc-lookahead %LOOKAHEAD% -spatial-aq 1 %TAQ_ARGS% -aq-strength %AQ_STRENGTH% %BF_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% %AUDIO_MUX_ARGS% %CONTAINER_EXTRA_ARGS% "%OUTPUT%""
%CMDLINE%

if errorlevel 1 (
    echo.
    echo ERROR while encoding:
    echo "%INPUT%"
    set /a FAIL_COUNT+=1
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
) else (
    echo.
    echo DONE:
    echo "%OUTPUT%"
    set /a SUCCESS_COUNT+=1
)

rem For a single dragged file, keep the detailed command display.
rem For multiple files, do not pause between jobs.
if "%FILE_COUNT%"=="1" (
    echo.
    echo ============================================================
    echo Actual FFmpeg command:
    echo ============================================================
    echo %CMDLINE%
    echo ============================================================
    echo.
    pause
)

shift
goto PROCESS_NEXT


:AUTO_CINEMA_FPS
rem ============================================================
rem Auto cinematic FPS
rem NTSC fractional family -> 24000/1001 (23.976)
rem Integer / PAL family    -> 24.000
rem Unknown rates           -> keep source FPS
rem ============================================================

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
set "MAIN_FPS_FILTER="
set "FPS_DECISION=Unknown rate - keep source"
set "FPS_SUFFIX="
exit /b 0

:AUTO_23976
set "OUT_FPS=24000/1001"
set "MAIN_FPS_FILTER=fps=24000/1001,"
set "FPS_DECISION=23.976 fps"
set "FPS_SUFFIX=_23976p"
exit /b 0

:AUTO_23976_SAME
set "OUT_FPS=24000/1001"
set "MAIN_FPS_FILTER="
set "FPS_DECISION=Already 23.976 fps"
set "FPS_SUFFIX=_23976p"
exit /b 0

:AUTO_24
set "OUT_FPS=24"
set "MAIN_FPS_FILTER=fps=24,"
set "FPS_DECISION=24.000 fps"
set "FPS_SUFFIX=_24p"
exit /b 0

:AUTO_24_SAME
set "OUT_FPS=24"
set "MAIN_FPS_FILTER="
set "FPS_DECISION=Already 24.000 fps"
set "FPS_SUFFIX=_24p"
exit /b 0


:PREPARE_LETTERBOX
set "BAR_H=0"
set "LETTERBOX_FILTER="

if not "%ENABLE_LETTERBOX%"=="1" exit /b 0

rem If source aspect is already 2.39:1 or wider, leave it untouched.
set /a ASPECT_LEFT=%WIDTH%*100
set /a ASPECT_RIGHT=%HEIGHT%*239
if %ASPECT_LEFT% GEQ %ASPECT_RIGHT% (
    echo Letterbox: source is already 2.39:1 or wider - no bars added
    exit /b 0
)

rem Per-side bar:
rem   (height - width/2.39) / 2
rem Integer form uses 2.39 = 239/100.
set /a BAR_RAW=(%HEIGHT%*239-%WIDTH%*100)/478

rem Force an even number of pixels for P010 / 4:2:0.
set /a BAR_H=(%BAR_RAW%/2)*2

if %BAR_H% LEQ 0 (
    set "BAR_H=0"
    echo Letterbox: bars would be too small - no bars added
    exit /b 0
)

set "LETTERBOX_FILTER=,drawbox=x=0:y=0:w=iw:h=%BAR_H%:color=black:t=fill,drawbox=x=0:y=ih-%BAR_H%:w=iw:h=%BAR_H%:color=black:t=fill"
echo Letterbox: 2.39:1 - top %BAR_H% px / bottom %BAR_H% px
exit /b 0



rem ============================================================
rem Film Look LUT selector
rem ============================================================

:SELECT_FILM_LUT
set "LUT_ENABLED=0"
set "LUT_PATH="
set "LUT_FILTER_PATH="
set "LUT_COMPAT_FILE="
set "LUT_LABEL=None"
set "LUT_OPACITY=0.75"
set "LUT_STRENGTH_PCT=75"
set "LUT_FILE_SUFFIX="

cls
echo ============================================================
echo                    Film Look / LUT
echo ============================================================
echo.
echo LUT folder:
echo   "%LUT_ROOT%"
echo.
echo   [0] None ^(default^)
echo.

if not exist "%LUT_ROOT%" (
    echo WARNING: LUT folder does not exist.
    echo LUT processing will remain disabled.
    echo.
    pause
    exit /b 0
)

set "LUT_LIST=%TEMP%\FilmLUT_list_%RANDOM%_%RANDOM%.txt"
set "LUT_PICK=%TEMP%\FilmLUT_pick_%RANDOM%_%RANDOM%.txt"
set "LUT_ESC=%TEMP%\FilmLUT_esc_%RANDOM%_%RANDOM%.txt"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath($env:LUT_ROOT); $i=1; Get-ChildItem -LiteralPath $root -Filter '*.cube' -File -Recurse | Sort-Object FullName | ForEach-Object { $rel=$_.FullName.Substring($root.Length).TrimStart([char]92); '{0}|{1}|{2}|{3}' -f $i,$rel,$_.BaseName,$_.FullName; $i++ }" > "%LUT_LIST%" 2>nul

if not exist "%LUT_LIST%" (
    echo No .cube LUT files were found.
    echo.
    pause
    exit /b 0
)

for %%Z in ("%LUT_LIST%") do if %%~zZ EQU 0 (
    echo No .cube LUT files were found.
    del /q "%LUT_LIST%" >nul 2>&1
    echo.
    pause
    exit /b 0
)

for /f "usebackq tokens=1,2,* delims=|" %%A in ("%LUT_LIST%") do echo   [%%A] %%B

:LUT_SELECT_RETRY
echo.
set "LUT_SEL=0"
set /p "LUT_SEL=Select LUT [0=None, default 0]: "

if "%LUT_SEL%"=="" set "LUT_SEL=0"
if "%LUT_SEL%"=="0" (
    if defined LUT_COMPAT_FILE del /q "%LUT_COMPAT_FILE%" >nul 2>&1
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" >nul 2>&1
    exit /b 0
)

findstr /b /l /c:"%LUT_SEL%|" "%LUT_LIST%" > "%LUT_PICK%"
if errorlevel 1 (
    echo Invalid LUT selection.
    goto LUT_SELECT_RETRY
)

set "LUT_PATH="
set "LUT_NAME="
set "LUT_RELATIVE="
for /f "usebackq tokens=1,2,3,* delims=|" %%A in ("%LUT_PICK%") do (
    set "LUT_RELATIVE=%%B"
    set "LUT_NAME=%%C"
    set "LUT_PATH=%%D"
)

if not defined LUT_PATH (
    echo ERROR: Could not resolve selected LUT.
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" >nul 2>&1
    pause
    exit /b 0
)

"%FFMPEG%" -hide_banner -h filter=lut3d >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain the lut3d filter.
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" >nul 2>&1
    pause
    exit /b 0
)

rem ------------------------------------------------------------
rem Prepare an FFmpeg-compatible temporary CUBE.
rem DaVinci Resolve accepts:
rem   LUT_3D_INPUT_RANGE a b
rem FFmpeg lut3d does not. Convert it to:
rem   DOMAIN_MIN a a a
rem   DOMAIN_MAX b b b
rem All other LUT content remains unchanged.
rem ------------------------------------------------------------
set "LUT_COMPAT_FILE=%TEMP%\FilmLUT_compat_%RANDOM%_%RANDOM%.cube"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$src=$env:LUT_PATH; $dst=$env:LUT_COMPAT_FILE; $lines=Get-Content -LiteralPath $src; $out=foreach($line in $lines){ if($line -match '^\s*LUT_3D_INPUT_RANGE\s+([^\s]+)\s+([^\s]+)\s*$'){ 'DOMAIN_MIN {0} {0} {0}' -f $matches[1]; 'DOMAIN_MAX {0} {0} {0}' -f $matches[2] } else { $line } }; $out | Set-Content -LiteralPath $dst -Encoding ascii"
if errorlevel 1 (
    echo.
    echo ERROR: Could not prepare FFmpeg-compatible LUT.
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" "%LUT_COMPAT_FILE%" >nul 2>&1
    pause
    exit /b 0
)

if not exist "%LUT_COMPAT_FILE%" (
    echo.
    echo ERROR: FFmpeg-compatible LUT file was not created.
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" >nul 2>&1
    pause
    exit /b 0
)

rem Convert temporary Windows path to FFmpeg filter syntax.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=$env:LUT_COMPAT_FILE; $p=$p.Replace('\','/').Replace(':','\:'); [Console]::Out.Write($p)" > "%LUT_ESC%" 2>nul
set "LUT_FILTER_PATH="
set /p "LUT_FILTER_PATH="<"%LUT_ESC%"

if not defined LUT_FILTER_PATH (
    echo ERROR: Could not prepare LUT path for FFmpeg.
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" "%LUT_COMPAT_FILE%" >nul 2>&1
    pause
    exit /b 0
)

rem Validate the converted LUT before starting the video encode.
"%FFMPEG%" -hide_banner -v error -f lavfi -i "color=c=gray:s=32x32:d=0.04" -vf "format=gbrp16le,lut3d=file='%LUT_FILTER_PATH%':interp=tetrahedral" -frames:v 1 -f null NUL >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFmpeg cannot initialize the selected LUT.
    echo "%LUT_PATH%"
    del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" "%LUT_COMPAT_FILE%" >nul 2>&1
    pause
    exit /b 0
)

echo.
echo LUT strength:
echo.
echo   [1] 25%%
echo   [2] 50%%
echo   [3] 75%%   ^(default^)
echo   [4] 100%%
echo.
set "LUT_STRENGTH_SEL=3"
set /p "LUT_STRENGTH_SEL=Select [1-4, default 3]: "

set "LUT_OPACITY=0.75"
set "LUT_STRENGTH_PCT=75"

if "%LUT_STRENGTH_SEL%"=="1" (
    set "LUT_OPACITY=0.25"
    set "LUT_STRENGTH_PCT=25"
)
if "%LUT_STRENGTH_SEL%"=="2" (
    set "LUT_OPACITY=0.50"
    set "LUT_STRENGTH_PCT=50"
)
if "%LUT_STRENGTH_SEL%"=="4" (
    set "LUT_OPACITY=1.00"
    set "LUT_STRENGTH_PCT=100"
)

set "LUT_ENABLED=1"
set "LUT_LABEL=%LUT_RELATIVE% @ %LUT_STRENGTH_PCT%%%"
set "LUT_FILE_SUFFIX=_LUT%LUT_SEL%_%LUT_STRENGTH_PCT%"

del /q "%LUT_LIST%" "%LUT_PICK%" "%LUT_ESC%" >nul 2>&1
exit /b 0

:FINISHED
if defined LUT_COMPAT_FILE del /q "%LUT_COMPAT_FILE%" >nul 2>&1
cls
echo ============================================================
echo                    Batch Summary
echo ============================================================
echo.
echo Speed mode    : %SPEED_LABEL%
echo Letterbox     : %LETTERBOX_LABEL%
echo Frame rate    : %FPS_LABEL%
echo Film Look     : %LUT_LABEL%
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
echo.
echo ============================================================
echo.
pause
endlocal
