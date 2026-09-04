@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem  Universal Film Grain pipeline - Studio bridge
rem
rem  HEVC backend baseline:
rem    HEVC - scanned Grain plate + Vulkan overlay + NVENC
rem
rem  AV1 backend baseline:
rem    AV1 - NVENC Main10 -> IVF -> grav1synth -> remux
rem
rem  Shared core:
rem    tool/input checks, speed, FPS, container, LUT Gallery,
rem    FFprobe, counters, output summary and GPU switches.
rem ============================================================

rem ---------- USER SETTINGS ----------

rem Fixed project FFmpeg/FFprobe build (NVENC API 13.0).
set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"
set "GRAV1SYNTH=E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe"

rem GPU selectors. 0 normally selects the RTX 4080.
set "VULKAN_DEVICE=0"
set "CUDA_DEVICE=0"
set "AQ_STRENGTH=8"
set "HARDWARE_CAPS_SCRIPT=%~dp0FilmGrain_Hardware_Caps.ps1"
set "SUBTITLE_HELPER=%~dp0FilmGrain_Subtitle_Prepare.ps1"

rem HEVC scanned-Grain library. All subfolders are searched.
set "DEFAULT_GRAIN_ROOT=D:\Film_Grain"

rem Shared visual LUT Gallery.
set "LUT_ROOT=E:\Adobe Portable\LUTs"
set "LUT_PREVIEW_ROOT=%LUT_ROOT%\_LUT_PREVIEWS"
set "LUT_GALLERY_SELECTOR=%~dp0..\_LUT_Tools\LUT_Gallery_Selector.ps1"

rem Keep AV1 intermediates when a stage fails.
set "KEEP_FAILED_INTERMEDIATES=1"

rem ---------- END USER SETTINGS ----------


rem ============================================================
rem Runtime defaults
rem ============================================================

set "MODE="
set "MODE_LABEL="
set "EXT=mp4"
set "CONTAINER_MODE=MP4"
set "CONTAINER_LABEL=MP4 compatibility / AAC audio"

set "LUT_ENABLED=0"
set "LUT_PATH="
set "LUT_FILTER_PATH="
set "LUT_COMPAT_FILE="
set "LUT_LABEL=None"
set "LUT_OPACITY=0.75"
set "LUT_STRENGTH_PCT=75"
set "LUT_FILE_SUFFIX="

set "FILE_COUNT=0"
set "SUCCESS_COUNT=0"
set "FAIL_COUNT=0"
set "SKIP_COUNT=0"
set "LAST_ERROR_STAGE="
set "LAST_ERROR_LOG="
set "STUDIO_FFMPEG_PROGRESS_ARGS="

rem Film Grain Studio only supplies validated values through FG_* variables.
rem With FG_STUDIO_MODE unset this BAT keeps the original interactive flow.
if /i "%FG_STUDIO_MODE%"=="1" (
    set "STUDIO_FFMPEG_PROGRESS_ARGS=-progress pipe:2 -stats_period 0.5"
    if defined FG_KEEP_FAILED set "KEEP_FAILED_INTERMEDIATES=%FG_KEEP_FAILED%"
)


rem ============================================================
rem Common startup checks
rem ============================================================

call :CHECK_COMMON_TOOLS
if errorlevel 1 goto FATAL_END

call :LOAD_HARDWARE_CAPS
if errorlevel 1 goto FATAL_END

if "%~1"=="" goto NO_INPUT

for %%# in (%*) do set /a FILE_COUNT+=1

call :SELECT_MODE
if errorlevel 1 goto FATAL_END

if /i "%MODE%"=="HEVC" call :CHECK_HEVC_TOOLS
if /i "%MODE%"=="AV1"  call :CHECK_AV1_TOOLS
if errorlevel 1 goto FATAL_END

call :SELECT_SPEED
if errorlevel 1 goto FATAL_END
call :SELECT_FRAMING
call :SELECT_DEINTERLACE
call :SELECT_FPS
call :SELECT_CONTAINER
call :SELECT_FILM_LUT

if /i "%MODE%"=="HEVC" call :SELECT_HEVC_GRAIN
if errorlevel 1 goto FATAL_END

if /i "%MODE%"=="AV1" call :SELECT_AV1_GRAIN
if errorlevel 1 goto FATAL_END

if /i "%MODE%"=="HEVC" call :SELECT_HEVC_BITRATE
if /i "%MODE%"=="AV1"  call :SELECT_AV1_BITRATE

call :SELECT_UPLOAD
if errorlevel 1 goto FATAL_END
call :SELECT_UPLOAD_SUBTITLE
if errorlevel 1 goto FATAL_END

call :BUILD_ENCODER_ARGS
call :SHOW_SESSION_SUMMARY
goto PROCESS_NEXT


rem ============================================================
rem Tool checks
rem ============================================================

:CHECK_COMMON_TOOLS
"%FFMPEG%" -version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFmpeg was not found or cannot start:
    echo "%FFMPEG%"
    echo.
    exit /b 1
)

"%FFPROBE%" -version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFprobe was not found or cannot start:
    echo "%FFPROBE%"
    echo.
    exit /b 1
)
exit /b 0


:LOAD_HARDWARE_CAPS
if not exist "%HARDWARE_CAPS_SCRIPT%" (
    echo.
    echo ERROR: Hardware capability detector was not found:
    echo "%HARDWARE_CAPS_SCRIPT%"
    echo.
    exit /b 1
)

set "HW_CAPS_ENV=%TEMP%\FilmGrain_HardwareCaps_%RANDOM%_%RANDOM%.cmd"
echo.
echo [Hardware Detection] Loading the GPU capability profile...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%HARDWARE_CAPS_SCRIPT%" -FFmpeg "%FFMPEG%" -GpuIndex %CUDA_DEVICE% -CudaDevice %CUDA_DEVICE% -VulkanDevice %VULKAN_DEVICE% -OutputCmd "%HW_CAPS_ENV%"
if errorlevel 1 (
    if exist "%HW_CAPS_ENV%" del /q "%HW_CAPS_ENV%" >nul 2>&1
    echo.
    echo ERROR: Hardware capability detection failed.
    echo.
    exit /b 1
)
if not exist "%HW_CAPS_ENV%" (
    echo.
    echo ERROR: Hardware capability profile was not created.
    echo.
    exit /b 1
)
call "%HW_CAPS_ENV%"
del /q "%HW_CAPS_ENV%" >nul 2>&1

if not "%FG_CAP_AV1%"=="1" if not "%FG_CAP_HEVC_PIPELINE%"=="1" (
    echo.
    echo ERROR: No supported Film Grain hardware pipeline is available.
    echo AV1 NVENC requires AV1 Main10 hardware encoding.
    echo HEVC mode requires HEVC Main10 NVENC and Vulkan.
    echo.
    exit /b 1
)
exit /b 0


:CHECK_HEVC_TOOLS
if not "%FG_CAP_HEVC_PIPELINE%"=="1" (
    echo.
    echo ERROR: HEVC Main10 NVENC or the required Vulkan path is not supported.
    echo.
    exit /b 1
)
"%FFMPEG%" -hide_banner -encoders 2>nul | findstr /i "hevc_nvenc" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain hevc_nvenc.
    echo.
    exit /b 1
)

"%FFMPEG%" -hide_banner -h filter=blend_vulkan 2>nul | findstr /i "overlay" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not expose blend_vulkan overlay mode.
    echo.
    exit /b 1
)

"%FFMPEG%" -hide_banner -h filter=scale_vulkan >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain scale_vulkan.
    echo.
    exit /b 1
)
exit /b 0


:CHECK_AV1_TOOLS
if not "%FG_CAP_AV1%"=="1" (
    echo.
    echo ERROR: AV1 Main10 NVENC is not supported by this GPU / driver.
    echo.
    exit /b 1
)
"%GRAV1SYNTH%" --version >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: grav1synth was not found or cannot start:
    echo "%GRAV1SYNTH%"
    echo.
    exit /b 1
)

"%GRAV1SYNTH%" presets >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: This grav1synth build does not expose the "presets" command.
    echo.
    exit /b 1
)

"%FFMPEG%" -hide_banner -encoders 2>nul | findstr /i "av1_nvenc" >nul
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain av1_nvenc.
    echo.
    exit /b 1
)
exit /b 0


rem ============================================================
rem Shared setup menus
rem ============================================================

:SELECT_MODE
if not "%FG_STUDIO_MODE%"=="1" cls
echo ============================================================
echo       Universal Film Grain Pipeline - Studio Bridge
echo ============================================================
echo.
echo Output codec / Grain method:
echo.
echo   [1] HEVC Main10 - scanned Grain plate / Vulkan overlay
if "%FG_CAP_AV1%"=="1" (
    echo   [2] AV1 Main10  - NVENC + grav1synth Film Grain metadata ^(default^)
) else (
    echo   [2] AV1 Main10  - unavailable on %FG_CAP_GPU_NAME%
)
echo.
set "MODE_SEL=2"
if "%FG_STUDIO_MODE%"=="1" (
    if /i "%FG_MODE%"=="HEVC" set "MODE_SEL=1"
    if /i "%FG_MODE%"=="AV1"  set "MODE_SEL=2"
) else (
    if "%FG_CAP_AV1%"=="1" (
        set /p "MODE_SEL=Select [1-2, default 2]: "
    ) else (
        set "MODE_SEL=1"
        set /p "MODE_SEL=Select [1-2, default 1]: "
    )
)

if "%MODE_SEL%"=="2" (
    if "%FG_CAP_AV1%"=="1" (
        set "MODE=AV1"
        set "MODE_LABEL=AV1 NVENC + grav1synth Film Grain"
    ) else (
        if "%FG_STUDIO_MODE%"=="1" (
            echo.
            echo ERROR: Film Grain Studio requested AV1, but AV1 NVENC is unavailable.
            echo Reopen Studio so it can select the HEVC fallback automatically.
            echo.
            exit /b 1
        )
        echo AV1 is unavailable; automatically using HEVC.
        set "MODE=HEVC"
        set "MODE_LABEL=HEVC scanned Grain + Vulkan overlay"
    )
) else (
    set "MODE=HEVC"
    set "MODE_LABEL=HEVC scanned Grain + Vulkan overlay"
)
exit /b 0


:SELECT_SPEED
echo.
echo Speed / quality:
echo.
echo   [1] Standard  - p6 / fullres / lookahead 32
echo   [2] FAST      - p5 / qres    / lookahead 16   ^(default^)
if /i "%MODE%"=="AV1" if "%FG_CAP_AV1_UHQ%"=="1" echo   [3] UHQ       - p4 / fullres / automatic temporal analysis
echo.
set "SPEED_SEL=2"
if "%FG_STUDIO_MODE%"=="1" (
    if /i "%FG_SPEED%"=="STANDARD" set "SPEED_SEL=1"
    if /i "%FG_SPEED%"=="FAST"     set "SPEED_SEL=2"
    if /i "%FG_SPEED%"=="UHQ"      set "SPEED_SEL=3"
) else (
    if /i "%MODE%"=="AV1" if "%FG_CAP_AV1_UHQ%"=="1" (
        set /p "SPEED_SEL=Select [1-3, default 2]: "
    ) else (
        set /p "SPEED_SEL=Select [1-2, default 2]: "
    )
)

if "%SPEED_SEL%"=="3" if /i not "%MODE%"=="AV1" (
    echo.
    echo ERROR: UHQ is available only in AV1 mode.
    echo.
    exit /b 1
)
if "%SPEED_SEL%"=="3" if not "%FG_CAP_AV1_UHQ%"=="1" (
    echo.
    echo ERROR: AV1 UHQ is not supported by the current GPU / driver / FFmpeg build.
    echo.
    exit /b 1
)

set "UHQ_SELECTED=0"
if "%SPEED_SEL%"=="3" set "UHQ_SELECTED=1"

if "%SPEED_SEL%"=="1" (
    set "PRESET=p6"
    set "LOOKAHEAD=32"
    set "MULTIPASS=fullres"
    set "ENCODER_TUNE=hq"
    set "UHQ_MODE=0"
    set "SPEED_LABEL=Standard"
    set "SPEED_SUFFIX=STD"
) else if "%UHQ_SELECTED%"=="1" (
    set "PRESET=p4"
    set "LOOKAHEAD=0"
    set "MULTIPASS=fullres"
    set "ENCODER_TUNE=uhq"
    set "UHQ_MODE=1"
    set "SPEED_LABEL=UHQ"
    set "SPEED_SUFFIX=UHQ"
) else (
    set "PRESET=p5"
    set "LOOKAHEAD=16"
    set "MULTIPASS=qres"
    set "ENCODER_TUNE=hq"
    set "UHQ_MODE=0"
    set "SPEED_LABEL=FAST"
    set "SPEED_SUFFIX=FAST"
)
exit /b 0


:SELECT_FRAMING
set "ENABLE_CROP=0"
set "ENABLE_LETTERBOX=0"
set "FRAME_MODE=LETTERBOX"
set "FRAME_LABEL=2.39:1 baked black bars / keep source resolution"
set "FRAME_SUFFIX=_239LB"

if "%FG_STUDIO_MODE%"=="1" (
    if "%FG_CINEMATIC_FRAME%"=="0" set "FRAME_MODE=OFF"
    if "%FG_CINEMATIC_FRAME%"=="1" if /i "%FG_FRAME_MODE%"=="CROP" set "FRAME_MODE=CROP"
    if "%FG_CINEMATIC_FRAME%"=="1" if /i "%FG_FRAME_MODE%"=="LETTERBOX" set "FRAME_MODE=LETTERBOX"
) else (
    echo.
    echo Cinematic framing ^(HEVC / AV1^):
    echo.
    echo   [1] Off
    echo   [2] Baked black bars - keep source resolution   ^(default^)
    echo   [3] Active-picture crop - output approx. 2.39:1
    echo.
    echo       Letterbox is recommended when subtitles may be added later.
    echo       Crop example: 1920x1080 -^> about 1920x804.
    echo.
    set "FRAME_SEL=2"
    set /p "FRAME_SEL=Select [1-3, default 2]: "
    if "%FRAME_SEL%"=="1" set "FRAME_MODE=OFF"
    if "%FRAME_SEL%"=="3" set "FRAME_MODE=CROP"
)

if /i "%FRAME_MODE%"=="OFF" (
    set "FRAME_LABEL=Off"
    set "FRAME_SUFFIX="
    exit /b 0
)

if /i "%FRAME_MODE%"=="CROP" (
    set "ENABLE_CROP=1"
    set "FRAME_LABEL=2.39:1 active-picture crop"
    set "FRAME_SUFFIX=_239"
    exit /b 0
)

set "FRAME_MODE=LETTERBOX"
set "ENABLE_LETTERBOX=1"
set "FRAME_LABEL=2.39:1 baked black bars / keep source resolution"
set "FRAME_SUFFIX=_239LB"
exit /b 0


:SELECT_FPS
echo.
echo Output frame rate:
echo.
echo   [1] Keep source FPS for progressive input
echo   [2] Auto cinematic FPS for progressive input   ^(default^)
echo.
echo       With Auto deinterlace enabled, interlaced input always uses
echo       field-rate output: 29.97i -^> 59.94p / 25i -^> 50p.
echo       Progressive input keeps the normal FPS choice below.
echo.
echo       NTSC fractional family -^> 23.976
echo       Integer / PAL family    -^> 24.000
echo.
set "FPS_SEL=2"
if "%FG_STUDIO_MODE%"=="1" (
    if /i "%FG_FPS_MODE%"=="SOURCE" set "FPS_SEL=1"
    if /i "%FG_FPS_MODE%"=="AUTO"   set "FPS_SEL=2"
) else (
    set /p "FPS_SEL=Select [1-2, default 2]: "
)
if "%FPS_SEL%"=="1" (
    set "FPS_MODE=SOURCE"
    set "FPS_LABEL=Keep source FPS"
) else (
    set "FPS_MODE=AUTO"
    set "FPS_LABEL=Auto cinematic FPS"
)
exit /b 0



rem ============================================================
rem Deinterlace selector
rem ============================================================
:SELECT_DEINTERLACE
set "DEINT_MODE=AUTO"
set "DEINT_METHOD=BWDIF_VULKAN"

if "%FG_STUDIO_MODE%"=="1" (
    if /i "%FG_DEINTERLACE%"=="OFF"  set "DEINT_MODE=OFF"
    if /i "%FG_DEINTERLACE%"=="AUTO" set "DEINT_MODE=AUTO"
    if /i "%FG_DEINT_METHOD%"=="BWDIF_VULKAN" set "DEINT_METHOD=BWDIF_VULKAN"
    if /i "%FG_DEINT_METHOD%"=="BWDIF_CUDA"   set "DEINT_METHOD=BWDIF_CUDA"
    if /i "%FG_DEINT_METHOD%"=="W3FDIF"       set "DEINT_METHOD=W3FDIF"
    goto SELECT_DEINTERLACE_BUILD
)

echo.
echo Deinterlace:
echo.
echo   [1] Auto - BWDIF Vulkan   ^(default^)
echo   [2] Auto - BWDIF CUDA     ^(backup^)
echo   [3] Auto - W3FDIF Complex ^(quality comparison^)
echo   [4] Off
echo.
echo       Auto only activates for input reported as interlaced by FFprobe.
echo       Field-rate output: 29.97i -^> 59.94p / 25i -^> 50p.
echo.
set "DEINT_SEL=1"
set /p "DEINT_SEL=Select [1-4, default 1]: "
if "%DEINT_SEL%"=="2" set "DEINT_METHOD=BWDIF_CUDA"
if "%DEINT_SEL%"=="3" set "DEINT_METHOD=W3FDIF"
if "%DEINT_SEL%"=="4" set "DEINT_MODE=OFF"

:SELECT_DEINTERLACE_BUILD
if /i "%DEINT_MODE%"=="OFF" (
    set "DEINT_METHOD=OFF"
    set "DEINT_LABEL=Off"
    set "DEINT_FILTER="
    set "DEINT_HW_ARGS="
    set "DEINT_SUFFIX="
    exit /b 0
)

set "DEINT_FILTER="
set "DEINT_HW_ARGS="
set "DEINT_SUFFIX="

if /i "%DEINT_METHOD%"=="BWDIF_VULKAN" (
    set "DEINT_LABEL=BWDIF Vulkan / field-rate"
    set "DEINT_FILTER=format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le,"
    set "DEINT_HW_ARGS=-init_hw_device vulkan=deintvk:%VULKAN_DEVICE% -filter_hw_device deintvk"
    set "DEINT_SUFFIX=_DI_BWV"
    exit /b 0
)

if /i "%DEINT_METHOD%"=="BWDIF_CUDA" (
    set "DEINT_LABEL=BWDIF CUDA / field-rate"
    set "DEINT_FILTER=format=p010le,hwupload_cuda=device=%CUDA_DEVICE%,bwdif_cuda=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le,"
    set "DEINT_SUFFIX=_DI_BWC"
    exit /b 0
)

set "DEINT_METHOD=W3FDIF"
set "DEINT_LABEL=W3FDIF Complex / field-rate"
set "DEINT_FILTER=w3fdif=filter=complex:mode=field:parity=auto:deint=all,"
set "DEINT_SUFFIX=_DI_W3F"
exit /b 0
:SELECT_CONTAINER
echo.
echo Output container:
echo.
echo   [1] MKV - preserve original audio / subtitles / attachments
echo   [2] MP4 - compatibility mode ^(default^)
echo.
echo       MP4 mode converts audio to AAC 256k and omits
echo       incompatible subtitle / attachment / data streams.
echo.
set "CONTAINER_SEL=2"
if "%FG_STUDIO_MODE%"=="1" (
    if /i "%FG_CONTAINER%"=="MKV" set "CONTAINER_SEL=1"
    if /i "%FG_CONTAINER%"=="MP4" set "CONTAINER_SEL=2"
) else (
    set /p "CONTAINER_SEL=Select [1-2, default 2]: "
)

if "%CONTAINER_SEL%"=="2" goto SELECT_CONTAINER_MP4
goto SELECT_CONTAINER_MKV

:SELECT_CONTAINER_MP4
set "EXT=mp4"
set "CONTAINER_MODE=MP4"
set "CONTAINER_LABEL=MP4 compatibility / AAC 256k"
if /i "%MODE%"=="HEVC" (
    set "HEVC_STREAM_MAP_ARGS=-map 0:a?"
    set "HEVC_AUDIO_MUX_ARGS=-c:a aac -b:a 256k"
    set "HEVC_CONTAINER_EXTRA_ARGS=-tag:v hvc1 -movflags +faststart"
) else (
    set "AV1_FINAL_REMUX_MAP=-map 1:a?"
    set "AV1_FINAL_REMUX_CODEC=-c:v copy -c:a aac -b:a 256k"
    set "AV1_FINAL_REMUX_EXTRA=-movflags +faststart"
)
exit /b 0

:SELECT_CONTAINER_MKV
set "EXT=mkv"
set "CONTAINER_MODE=MKV"
set "CONTAINER_LABEL=MKV / preserve original streams"
if /i "%MODE%"=="HEVC" (
    set "HEVC_STREAM_MAP_ARGS=-map 0:a? -map 0:s? -map 0:t?"
    set "HEVC_AUDIO_MUX_ARGS=-c:a copy -c:s copy -c:t copy"
    set "HEVC_CONTAINER_EXTRA_ARGS="
) else (
    set "AV1_FINAL_REMUX_MAP=-map 1:a? -map 1:s? -map 1:t? -map 1:d?"
    set "AV1_FINAL_REMUX_CODEC=-c copy"
    set "AV1_FINAL_REMUX_EXTRA="
)
exit /b 0


rem ============================================================
rem Shared Film Look / LUT Gallery
rem ============================================================

:SELECT_FILM_LUT
set "LUT_ENABLED=0"
set "LUT_PATH="
set "LUT_FILTER_PATH="
if defined LUT_COMPAT_FILE del /q "%LUT_COMPAT_FILE%" >nul 2>&1
set "LUT_COMPAT_FILE="
set "LUT_LABEL=None"
set "LUT_OPACITY=0.75"
set "LUT_STRENGTH_PCT=75"
set "LUT_FILE_SUFFIX="

if "%FG_STUDIO_MODE%"=="1" (
    if not defined FG_LUT_PATH exit /b 0
    set "LUT_PATH=%FG_LUT_PATH%"
    goto SELECT_FILM_LUT_VALIDATE
)

cls
echo ============================================================
echo                 Film Look / LUT Gallery
echo ============================================================
echo.
echo LUT folder:
echo   "%LUT_ROOT%"
echo Preview gallery:
echo   "%LUT_PREVIEW_ROOT%"
echo.
echo   [0] None ^(default^)
echo   [1] Open visual thumbnail gallery
echo.
set "LUT_MODE=0"
set /p "LUT_MODE=Select [0-1, default 0]: "
if "%LUT_MODE%"=="" set "LUT_MODE=0"
if "%LUT_MODE%"=="0" exit /b 0
if not "%LUT_MODE%"=="1" exit /b 0

if not exist "%LUT_GALLERY_SELECTOR%" (
    echo.
    echo ERROR: Shared LUT Gallery selector was not found:
    echo "%LUT_GALLERY_SELECTOR%"
    echo.
    echo Keep this BAT beside the _LUT_Tools folder.
    echo LUT processing will remain disabled.
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

if not exist "%LUT_ROOT%" (
    echo.
    echo ERROR: LUT root does not exist:
    echo "%LUT_ROOT%"
    echo LUT processing will remain disabled.
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

set "LUT_PICK=%TEMP%\FilmLUT_gallery_pick_%RANDOM%_%RANDOM%.txt"
del /q "%LUT_PICK%" >nul 2>&1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LUT_GALLERY_SELECTOR%" -LutRoot "%LUT_ROOT%" -PreviewRoot "%LUT_PREVIEW_ROOT%" -OutputFile "%LUT_PICK%" >nul
set "GALLERY_RC=%ERRORLEVEL%"

rem RC 10 = explicit None; RC 11 = close/cancel.
if "%GALLERY_RC%"=="10" (
    del /q "%LUT_PICK%" >nul 2>&1
    exit /b 0
)
if not "%GALLERY_RC%"=="0" (
    del /q "%LUT_PICK%" >nul 2>&1
    echo.
    if "%GALLERY_RC%"=="11" (
        echo LUT Gallery cancelled. LUT disabled.
    ) else (
        echo LUT Gallery could not be opened. Error code: %GALLERY_RC%
    )
    if not "%FG_STUDIO_MODE%"=="1" timeout /t 2 >nul
    exit /b 0
)

set "LUT_PATH="
if exist "%LUT_PICK%" set /p "LUT_PATH="<"%LUT_PICK%"
del /q "%LUT_PICK%" >nul 2>&1
if not defined LUT_PATH exit /b 0

:SELECT_FILM_LUT_VALIDATE
if not exist "%LUT_PATH%" (
    echo.
    echo ERROR: Selected LUT no longer exists:
    echo "%LUT_PATH%"
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

for %%L in ("%LUT_PATH%") do set "LUT_NAME=%%~nL"
set "LUT_RELATIVE=%LUT_NAME%"

set "LUT_REL_FILE=%TEMP%\FilmLUT_relative_%RANDOM%_%RANDOM%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath($env:LUT_ROOT).TrimEnd([char]92); $p=[IO.Path]::GetFullPath($env:LUT_PATH); $prefix=$root+[char]92; if($p.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){[Console]::Out.Write($p.Substring($prefix.Length))}else{[Console]::Out.Write($p)}" > "%LUT_REL_FILE%" 2>nul
if exist "%LUT_REL_FILE%" set /p "LUT_RELATIVE="<"%LUT_REL_FILE%"
del /q "%LUT_REL_FILE%" >nul 2>&1

"%FFMPEG%" -hide_banner -h filter=lut3d >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain the lut3d filter.
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

rem Reject 1D/shaper CUBE files.
findstr /i /b /c:"LUT_3D_SIZE" "%LUT_PATH%" >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: Selected .cube does not contain LUT_3D_SIZE.
    echo It may be a 1D/shaper LUT rather than a supported 3D LUT.
    echo.
    echo LUT:
    echo "%LUT_PATH%"
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

rem DaVinci Resolve accepts LUT_3D_INPUT_RANGE, FFmpeg does not.
rem Convert it to DOMAIN_MIN / DOMAIN_MAX in one shared temp copy.
set "LUT_COMPAT_FILE=%TEMP%\FilmLUT_compat_%RANDOM%_%RANDOM%.cube"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$src=$env:LUT_PATH; $dst=$env:LUT_COMPAT_FILE; " ^
  "$lines=Get-Content -LiteralPath $src; " ^
  "$out=foreach($line in $lines){ " ^
  "  if($line -match '^\s*LUT_3D_INPUT_RANGE\s+([^\s]+)\s+([^\s]+)\s*$'){ " ^
  "    'DOMAIN_MIN {0} {0} {0}' -f $matches[1]; " ^
  "    'DOMAIN_MAX {0} {0} {0}' -f $matches[2] " ^
  "  } else { $line } " ^
  "}; " ^
  "$out | Set-Content -LiteralPath $dst -Encoding ascii"
if errorlevel 1 (
    echo.
    echo ERROR: Could not prepare FFmpeg-compatible LUT.
    if exist "%LUT_COMPAT_FILE%" del /q "%LUT_COMPAT_FILE%" >nul 2>&1
    set "LUT_COMPAT_FILE="
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)
if not exist "%LUT_COMPAT_FILE%" (
    echo.
    echo ERROR: FFmpeg-compatible LUT file was not created.
    set "LUT_COMPAT_FILE="
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

set "LUT_ESC=%TEMP%\FilmLUT_esc_%RANDOM%_%RANDOM%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=$env:LUT_COMPAT_FILE; $p=$p.Replace([char]92,'/').Replace(':','\:'); [Console]::Out.Write($p)" > "%LUT_ESC%" 2>nul
set "LUT_FILTER_PATH="
if exist "%LUT_ESC%" set /p "LUT_FILTER_PATH="<"%LUT_ESC%"
del /q "%LUT_ESC%" >nul 2>&1

if not defined LUT_FILTER_PATH (
    echo.
    echo ERROR: Could not prepare the LUT path for FFmpeg.
    del /q "%LUT_COMPAT_FILE%" >nul 2>&1
    set "LUT_COMPAT_FILE="
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

rem Validate the converted CUBE before touching any source video.
"%FFMPEG%" -hide_banner -v error -f lavfi -i "color=c=gray:s=32x32:d=0.04" -vf "format=gbrp16le,lut3d=file='%LUT_FILTER_PATH%':interp=tetrahedral" -frames:v 1 -f null NUL >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: FFmpeg cannot initialize the selected LUT.
    echo "%LUT_PATH%"
    del /q "%LUT_COMPAT_FILE%" >nul 2>&1
    set "LUT_COMPAT_FILE="
    set "LUT_FILTER_PATH="
    echo.
    if not "%FG_STUDIO_MODE%"=="1" pause
    exit /b 0
)

echo.
echo Selected LUT:
echo   %LUT_RELATIVE%
echo.
echo LUT strength:
echo.
echo   [1] 25%%
echo   [2] 50%%
echo   [3] 75%%   ^(default^)
echo   [4] 100%%
echo.
set "LUT_STRENGTH_SEL=3"
if "%FG_STUDIO_MODE%"=="1" (
    if "%FG_LUT_STRENGTH%"=="25"  set "LUT_STRENGTH_SEL=1"
    if "%FG_LUT_STRENGTH%"=="50"  set "LUT_STRENGTH_SEL=2"
    if "%FG_LUT_STRENGTH%"=="75"  set "LUT_STRENGTH_SEL=3"
    if "%FG_LUT_STRENGTH%"=="100" set "LUT_STRENGTH_SEL=4"
) else (
    set /p "LUT_STRENGTH_SEL=Select [1-4, default 3]: "
)

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
set "LUT_FILE_SUFFIX=_LUT_%LUT_NAME%_%LUT_STRENGTH_PCT%"
exit /b 0


rem ============================================================
rem HEVC scanned-Grain setup
rem ============================================================

:SELECT_HEVC_GRAIN
if defined DEFAULT_GRAIN_ROOT (
    set "GRAIN_ROOT=%DEFAULT_GRAIN_ROOT%"
) else (
    set "GRAIN_ROOT=%~dp0"
)
if "%FG_STUDIO_MODE%"=="1" if defined FG_GRAIN_ROOT set "GRAIN_ROOT=%FG_GRAIN_ROOT%"

if not "%FG_STUDIO_MODE%"=="1" cls
echo ============================================================
echo           HEVC Scanned Film Grain / Vulkan
echo ============================================================
echo.
echo Current Grain folder:
echo   "%GRAIN_ROOT%"
echo.
echo   [Enter] Use current folder
echo   [C]     Choose another Grain folder for this run
echo.
set "FSEL="
if not "%FG_STUDIO_MODE%"=="1" set /p "FSEL=Grain folder option [Enter/C]: "
if /i "%FSEL%"=="C" call :PROMPT_HEVC_GRAIN_ROOT

if defined GRAIN_ROOT set "GRAIN_ROOT=%GRAIN_ROOT:"=%"

rem Remove one trailing backslash, except for a drive root such as D:\
if not "%GRAIN_ROOT:~-1%"=="\" goto HEVC_GRAIN_ROOT_NORMALIZED
if "%GRAIN_ROOT:~1,2%"==":\" if "%GRAIN_ROOT:~3%"=="" goto HEVC_GRAIN_ROOT_NORMALIZED
set "GRAIN_ROOT=%GRAIN_ROOT:~0,-1%"

:HEVC_GRAIN_ROOT_NORMALIZED
if not exist "%GRAIN_ROOT%" (
    echo.
    echo ERROR: Grain folder does not exist:
    echo "%GRAIN_ROOT%"
    echo.
    exit /b 1
)

rem Studio passes the exact file selected from its live directory scan.
if "%FG_STUDIO_MODE%"=="1" if defined FG_HEVC_GRAIN_PATH goto HEVC_STUDIO_GRAIN_DIRECT

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
if "%FG_STUDIO_MODE%"=="1" (
    if defined FG_HEVC_PLATE set "PSEL=%FG_HEVC_PLATE%"
) else (
    set /p "PSEL=Select [1-11, default 1]: "
)

set "GRAIN="
set "GRAIN_PATTERN="
set "GRAIN_LABEL="
set "HEVC_SUFFIX="

if "%PSEL%"=="1"  set "GRAIN_PATTERN=CT 35mm Grain 4K DCI.mov"
if "%PSEL%"=="1"  set "GRAIN_LABEL=Cinema Tools 35mm 4K DCI"
if "%PSEL%"=="1"  set "HEVC_SUFFIX=_FG_CT35_V20_HEVC"
if "%PSEL%"=="2"  set "GRAIN_PATTERN=Filmgrain_4KDCI_35mm_24fps.mov"
if "%PSEL%"=="2"  set "GRAIN_LABEL=TDCAT 35mm Light"
if "%PSEL%"=="2"  set "HEVC_SUFFIX=_FG_35L_V20_HEVC"
if "%PSEL%"=="3"  set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_35mm_24fps.mov"
if "%PSEL%"=="3"  set "GRAIN_LABEL=TDCAT Super 35 Light"
if "%PSEL%"=="3"  set "HEVC_SUFFIX=_FG_S35L_V20_HEVC"
if "%PSEL%"=="4"  set "GRAIN_PATTERN=Filmgrain_4KDCI_16mm_24fps.mov"
if "%PSEL%"=="4"  set "GRAIN_LABEL=TDCAT 16mm Light"
if "%PSEL%"=="4"  set "HEVC_SUFFIX=_FG_16L_V20_HEVC"
if "%PSEL%"=="5"  set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_16mm_24fps.mov"
if "%PSEL%"=="5"  set "GRAIN_LABEL=TDCAT Super 16 Light"
if "%PSEL%"=="5"  set "HEVC_SUFFIX=_FG_S16L_V20_HEVC"
if "%PSEL%"=="6"  set "GRAIN_PATTERN=Filmgrain_4KDCI_8mm_24fps.mov"
if "%PSEL%"=="6"  set "GRAIN_LABEL=TDCAT 8mm Light"
if "%PSEL%"=="6"  set "HEVC_SUFFIX=_FG_8L_V20_HEVC"
if "%PSEL%"=="7"  set "GRAIN_PATTERN=Filmgrain_4KDCI_35mm_24fps_Heavy.mov"
if "%PSEL%"=="7"  set "GRAIN_LABEL=TDCAT 35mm Heavy"
if "%PSEL%"=="7"  set "HEVC_SUFFIX=_FG_35H_V20_HEVC"
if "%PSEL%"=="8"  set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_35mm_24fps_Heavy.mov"
if "%PSEL%"=="8"  set "GRAIN_LABEL=TDCAT Super 35 Heavy"
if "%PSEL%"=="8"  set "HEVC_SUFFIX=_FG_S35H_V20_HEVC"
if "%PSEL%"=="9"  set "GRAIN_PATTERN=Filmgrain_4KDCI_16mm_24fps_Heavy.mov"
if "%PSEL%"=="9"  set "GRAIN_LABEL=TDCAT 16mm Heavy"
if "%PSEL%"=="9"  set "HEVC_SUFFIX=_FG_16H_V20_HEVC"
if "%PSEL%"=="10" set "GRAIN_PATTERN=Filmgrain_4KDCI_Super_16mm_24fps_Heavy.mov"
if "%PSEL%"=="10" set "GRAIN_LABEL=TDCAT Super 16 Heavy"
if "%PSEL%"=="10" set "HEVC_SUFFIX=_FG_S16H_V20_HEVC"
if "%PSEL%"=="11" set "GRAIN_PATTERN=Filmgrain_4KDCI_8mm_24fps_Heavy.mov"
if "%PSEL%"=="11" set "GRAIN_LABEL=TDCAT 8mm Heavy"
if "%PSEL%"=="11" set "HEVC_SUFFIX=_FG_8H_V20_HEVC"

if not defined GRAIN_PATTERN set "GRAIN_PATTERN=CT 35mm Grain 4K DCI.mov"
if not defined GRAIN_LABEL set "GRAIN_LABEL=Cinema Tools 35mm 4K DCI"
if not defined HEVC_SUFFIX set "HEVC_SUFFIX=_FG_CT35_V20_HEVC"

set "GRAIN_FIND=%TEMP%\FilmGrain_find_%RANDOM%_%RANDOM%.txt"
dir /b /s /a-d "%GRAIN_ROOT%\%GRAIN_PATTERN%" > "%GRAIN_FIND%" 2>nul
if exist "%GRAIN_FIND%" set /p "GRAIN="<"%GRAIN_FIND%"
del /q "%GRAIN_FIND%" >nul 2>&1

if not defined GRAIN (
    echo.
    echo ERROR: Selected Grain plate was not found.
    echo.
    echo Expected filename:
    echo "%GRAIN_PATTERN%"
    echo.
    echo Search root:
    echo "%GRAIN_ROOT%"
    echo.
    exit /b 1
)

goto HEVC_GRAIN_RESOLVED

:HEVC_STUDIO_GRAIN_DIRECT
set "GRAIN=%FG_HEVC_GRAIN_PATH:"=%"
if not exist "%GRAIN%" (
    echo.
    echo ERROR: The Grain plate selected in Film Grain Studio no longer exists.
    echo "%GRAIN%"
    echo.
    exit /b 1
)
for %%G in ("%GRAIN%") do set "GRAIN_PATTERN=%%~nxG"
for %%G in ("%GRAIN%") do set "GRAIN_LABEL=%%~nG"
set "HEVC_SUFFIX=_FG_SCAN_V20_HEVC"
if defined FG_HEVC_GRAIN_TAG set "HEVC_SUFFIX=_FG_%FG_HEVC_GRAIN_TAG%_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="CT 35mm Grain 4K DCI.mov" set "HEVC_SUFFIX=_FG_CT35_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_35mm_24fps.mov" set "HEVC_SUFFIX=_FG_35L_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_Super_35mm_24fps.mov" set "HEVC_SUFFIX=_FG_S35L_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_16mm_24fps.mov" set "HEVC_SUFFIX=_FG_16L_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_Super_16mm_24fps.mov" set "HEVC_SUFFIX=_FG_S16L_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_8mm_24fps.mov" set "HEVC_SUFFIX=_FG_8L_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_35mm_24fps_Heavy.mov" set "HEVC_SUFFIX=_FG_35H_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_Super_35mm_24fps_Heavy.mov" set "HEVC_SUFFIX=_FG_S35H_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_16mm_24fps_Heavy.mov" set "HEVC_SUFFIX=_FG_16H_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_Super_16mm_24fps_Heavy.mov" set "HEVC_SUFFIX=_FG_S16H_V20_HEVC"
if /i "%GRAIN_PATTERN%"=="Filmgrain_4KDCI_8mm_24fps_Heavy.mov" set "HEVC_SUFFIX=_FG_8H_V20_HEVC"

:HEVC_GRAIN_RESOLVED

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
    echo 1080p cache: Found
) else (
    echo 1080p cache: Not found
)
if exist "%GRAIN_CACHE%" (
    echo 4K cache   : Found
) else (
    echo 4K cache   : Not found
)

echo.
echo Grain strength:
echo.
echo   [1] Light    65%%
echo   [2] Natural  75%%
echo   [3] Strong   85%%   ^(recommended^)
echo   [4] Full     100%%
echo.
set "GSEL=3"
if "%FG_STUDIO_MODE%"=="1" (
    if defined FG_HEVC_STRENGTH_SEL set "GSEL=%FG_HEVC_STRENGTH_SEL%"
) else (
    set /p "GSEL=Select [1-4, default 3]: "
)
set "GRAIN_OPACITY=0.85"
if "%GSEL%"=="1" set "GRAIN_OPACITY=0.65"
if "%GSEL%"=="2" set "GRAIN_OPACITY=0.75"
if "%GSEL%"=="3" set "GRAIN_OPACITY=0.85"
if "%GSEL%"=="4" set "GRAIN_OPACITY=1.00"

if /i "%SPEED_LABEL%"=="FAST" (
    set "HEVC_SUFFIX=%HEVC_SUFFIX:_V20_HEVC=_V20FAST_HEVC%"
)
exit /b 0


:PROMPT_HEVC_GRAIN_ROOT
echo.
echo Enter the Grain root folder.
echo Example: G:\FilmGrain
echo.
set "NEW_GRAIN_ROOT="
set /p "NEW_GRAIN_ROOT=Folder: "
if defined NEW_GRAIN_ROOT set "GRAIN_ROOT=%NEW_GRAIN_ROOT%"
exit /b 0


rem ============================================================
rem AV1 grav1synth setup
rem ============================================================

:SELECT_AV1_GRAIN
if not "%FG_STUDIO_MODE%"=="1" cls
echo ============================================================
echo              AV1 grav1synth Film Grain
echo ============================================================
echo.
echo Grain source:
echo.
echo   [1] Film preset   ^(default / recommended^)
echo   [2] Photon ISO    ^(advanced strength control^)
echo.
set "GRAIN_MODE_SEL=1"
if "%FG_STUDIO_MODE%"=="1" (
    if /i "%FG_AV1_GRAIN_MODE%"=="PRESET" set "GRAIN_MODE_SEL=1"
    if /i "%FG_AV1_GRAIN_MODE%"=="ISO"    set "GRAIN_MODE_SEL=2"
) else (
    set /p "GRAIN_MODE_SEL=Select [1-2, default 1]: "
)

set "GRAIN_MODE=PRESET"
set "GRAIN_APPLY_ARGS="
set "GRAIN_LABEL="
set "GRAIN_FILE_TAG="

if "%GRAIN_MODE_SEL%"=="2" goto AV1_GRAIN_PHOTON_MENU

echo.
echo Film format:
echo.
echo   [1] Classic35   - Super 35mm style   ^(default / tested^)
echo   [2] Modern35    - 35mm full-frame style
echo   [3] 16mm        - stronger / coarser
echo   [4] Super8      - very strong / coarse
echo   [5] MaxMid      - synthetic heavy midtone Grain
echo.
set "GRAIN_SEL=1"
if "%FG_STUDIO_MODE%"=="1" (
    if defined FG_AV1_FORMAT set "GRAIN_SEL=%FG_AV1_FORMAT%"
) else (
    set /p "GRAIN_SEL=Select [1-5, default 1]: "
)

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
if "%USE_STOCK%"=="0" goto AV1_GRAIN_PRESET_READY

echo.
echo Film stock:
echo.
echo   [1] Fujifilm Eterna 250D       ^(default^)
echo   [2] Fujifilm Eterna 500T
echo   [3] Kodak Vision3 250D
echo   [4] Kodak Vision3 200T
echo.
set "STOCK_SEL=1"
if "%FG_STUDIO_MODE%"=="1" (
    if defined FG_AV1_STOCK set "STOCK_SEL=%FG_AV1_STOCK%"
) else (
    set /p "STOCK_SEL=Select [1-4, default 1]: "
)

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

:AV1_GRAIN_PRESET_READY
set "GRAIN_MODE=PRESET"
set "GRAIN_APPLY_ARGS=--preset "%GRAIN_PRESET%""
set "GRAIN_LABEL=%GRAIN_PRESET% / %STOCK_LABEL%"
set "GRAIN_FILE_TAG=%GRAIN_PRESET:-=_%"
exit /b 0


:AV1_GRAIN_PHOTON_MENU
set "GRAIN_MODE=ISO"
echo.
echo Photon Grain ISO:
echo.
echo   [1] ISO 400    - Subtle
echo   [2] ISO 800    - Mild
echo   [3] ISO 1600   - Medium       ^(default^)
echo   [4] ISO 3200   - Strong
echo   [5] ISO 6400   - Very strong
echo   [6] Custom ISO
echo.
if "%FG_STUDIO_MODE%"=="1" goto AV1_GRAIN_ISO_STUDIO

set "ISO_SEL=3"
set /p "ISO_SEL=Select [1-6, default 3]: "

set "GRAIN_ISO=1600"
if "%ISO_SEL%"=="1" set "GRAIN_ISO=400"
if "%ISO_SEL%"=="2" set "GRAIN_ISO=800"
if "%ISO_SEL%"=="3" set "GRAIN_ISO=1600"
if "%ISO_SEL%"=="4" set "GRAIN_ISO=3200"
if "%ISO_SEL%"=="5" set "GRAIN_ISO=6400"
if "%ISO_SEL%"=="6" call :PROMPT_CUSTOM_ISO
goto AV1_GRAIN_ISO_READY

:AV1_GRAIN_ISO_STUDIO
set "CUSTOM_ISO=1600"
if defined FG_AV1_ISO set "CUSTOM_ISO=%FG_AV1_ISO%"
call :VALIDATE_CUSTOM_ISO

:AV1_GRAIN_ISO_READY

echo.
echo Chroma Grain:
echo.
echo   [1] Luma only                 ^(default / cleaner^)
echo   [2] Luma + chroma
echo.
set "CHROMA_SEL=1"
if "%FG_STUDIO_MODE%"=="1" (
    if "%FG_AV1_CHROMA%"=="1" set "CHROMA_SEL=2"
) else (
    set /p "CHROMA_SEL=Select [1-2, default 1]: "
)

set "CHROMA_ARGS="
set "CHROMA_LABEL=Luma only"
if "%CHROMA_SEL%"=="2" (
    set "CHROMA_ARGS=--chroma"
    set "CHROMA_LABEL=Luma + chroma"
)

set "GRAIN_APPLY_ARGS=--iso %GRAIN_ISO% %CHROMA_ARGS%"
set "GRAIN_LABEL=Photon ISO %GRAIN_ISO% / %CHROMA_LABEL%"
set "GRAIN_FILE_TAG=ISO%GRAIN_ISO%"
exit /b 0


:PROMPT_CUSTOM_ISO
set "CUSTOM_ISO="
set /p "CUSTOM_ISO=Enter ISO value [positive integer]: "
call :VALIDATE_CUSTOM_ISO
exit /b 0


:VALIDATE_CUSTOM_ISO
if not defined CUSTOM_ISO goto CUSTOM_ISO_INVALID
set "ISO_CALC=%TEMP%\FGU_iso_%RANDOM%_%RANDOM%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$n=0; if([int]::TryParse($env:CUSTOM_ISO,[ref]$n) -and $n -gt 0){[Console]::Out.Write($n); exit 0}; exit 1" > "%ISO_CALC%" 2>nul
if errorlevel 1 (
    del /q "%ISO_CALC%" >nul 2>&1
    goto CUSTOM_ISO_INVALID
)
set "GRAIN_ISO="
if exist "%ISO_CALC%" set /p "GRAIN_ISO="<"%ISO_CALC%"
del /q "%ISO_CALC%" >nul 2>&1
if not defined GRAIN_ISO goto CUSTOM_ISO_INVALID
exit /b 0

:CUSTOM_ISO_INVALID
echo Invalid ISO. Falling back to ISO 800.
set "GRAIN_ISO=800"
exit /b 0


rem ============================================================
rem Mode-specific bitrate menus
rem ============================================================

:APPLY_STUDIO_BITRATE
if not defined FG_BITRATE exit /b 1
if not defined FG_MAXRATE exit /b 1
if not defined FG_BUFSIZE exit /b 1
set "BITRATE_NUM=%FG_BITRATE%"
set "BITRATE=%FG_BITRATE%k"
set "MAXRATE=%FG_MAXRATE%k"
set "BUFSIZE=%FG_BUFSIZE%k"
echo.
echo Studio rate request:
echo   b:v      = %BITRATE%
echo   maxrate  = %MAXRATE%
echo   bufsize  = %BUFSIZE%
exit /b 0

:SELECT_HEVC_BITRATE
if "%FG_STUDIO_MODE%"=="1" goto HEVC_BITRATE_STUDIO
echo.
echo Average HEVC video bitrate:
echo.
echo   [1] 6000 kbps
echo   [2] 7500 kbps   ^(default^)
echo   [3] 9000 kbps
echo   [4] 12000 kbps
echo.
echo   Or enter a custom bitrate in kbps.
echo.
set "BSEL=2"
set /p "BSEL=Select [1-4, default 2, or custom kbps]: "

if "%BSEL%"=="1" goto HEVC_BITRATE_1
if "%BSEL%"=="2" goto HEVC_BITRATE_2
if "%BSEL%"=="3" goto HEVC_BITRATE_3
if "%BSEL%"=="4" goto HEVC_BITRATE_4

call :VALIDATE_CUSTOM_BITRATE
if errorlevel 1 goto HEVC_BITRATE_INVALID
set "BITRATE=%CUSTOM_BITRATE_NUM%k"
set "MAXRATE=%CUSTOM_BITRATE_MAX%k"
set "BUFSIZE=%CUSTOM_BITRATE_BUF%k"
exit /b 0

:HEVC_BITRATE_STUDIO
call :APPLY_STUDIO_BITRATE
if errorlevel 1 goto HEVC_BITRATE_INVALID
exit /b 0

:HEVC_BITRATE_1
set "BITRATE=6000k"
set "MAXRATE=12000k"
set "BUFSIZE=24000k"
exit /b 0

:HEVC_BITRATE_2
set "BITRATE=7500k"
set "MAXRATE=15000k"
set "BUFSIZE=30000k"
exit /b 0

:HEVC_BITRATE_3
set "BITRATE=9000k"
set "MAXRATE=18000k"
set "BUFSIZE=36000k"
exit /b 0

:HEVC_BITRATE_4
set "BITRATE=12000k"
set "MAXRATE=24000k"
set "BUFSIZE=48000k"
exit /b 0

:HEVC_BITRATE_INVALID
echo.
echo Invalid bitrate selection. Falling back to 7500 kbps.
set "BITRATE=7500k"
set "MAXRATE=15000k"
set "BUFSIZE=30000k"
exit /b 0


:SELECT_AV1_BITRATE
if "%FG_STUDIO_MODE%"=="1" goto AV1_BITRATE_STUDIO
echo.
echo Average AV1 video bitrate:
echo.
echo   [1] 1000 kbps
echo   [2] 1500 kbps   ^(default^)
echo   [3] 2500 kbps
echo   [4] 4000 kbps
echo.
echo   Or enter a custom bitrate in kbps.
echo.
set "BSEL=2"
set /p "BSEL=Select [1-4, default 2, or custom kbps]: "

if "%BSEL%"=="1" goto AV1_BITRATE_1
if "%BSEL%"=="2" goto AV1_BITRATE_2
if "%BSEL%"=="3" goto AV1_BITRATE_3
if "%BSEL%"=="4" goto AV1_BITRATE_4

call :VALIDATE_CUSTOM_BITRATE
if errorlevel 1 goto AV1_BITRATE_INVALID
set "BITRATE_NUM=%CUSTOM_BITRATE_NUM%"
set "BITRATE=%CUSTOM_BITRATE_NUM%k"
set "MAXRATE=%CUSTOM_BITRATE_MAX%k"
set "BUFSIZE=%CUSTOM_BITRATE_BUF%k"
exit /b 0

:AV1_BITRATE_STUDIO
call :APPLY_STUDIO_BITRATE
if errorlevel 1 goto AV1_BITRATE_INVALID
exit /b 0

:AV1_BITRATE_1
set "BITRATE_NUM=1000"
set "BITRATE=1000k"
set "MAXRATE=2000k"
set "BUFSIZE=4000k"
exit /b 0

:AV1_BITRATE_2
set "BITRATE_NUM=1500"
set "BITRATE=1500k"
set "MAXRATE=3000k"
set "BUFSIZE=6000k"
exit /b 0

:AV1_BITRATE_3
set "BITRATE_NUM=2500"
set "BITRATE=2500k"
set "MAXRATE=5000k"
set "BUFSIZE=10000k"
exit /b 0

:AV1_BITRATE_4
set "BITRATE_NUM=4000"
set "BITRATE=4000k"
set "MAXRATE=8000k"
set "BUFSIZE=16000k"
exit /b 0

:AV1_BITRATE_INVALID
echo.
echo Invalid bitrate selection. Falling back to 1500 kbps.
set "BITRATE_NUM=1500"
set "BITRATE=1500k"
set "MAXRATE=3000k"
set "BUFSIZE=6000k"
exit /b 0


:VALIDATE_CUSTOM_BITRATE
set "CUSTOM_BITRATE_NUM="
set "CUSTOM_BITRATE_MAX="
set "CUSTOM_BITRATE_BUF="
set "BITRATE_CALC=%TEMP%\FGU_bitrate_%RANDOM%_%RANDOM%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$n=0; if([int]::TryParse($env:BSEL,[ref]$n) -and $n -gt 10 -and $n -le 500000000){[Console]::Out.Write('{0}|{1}|{2}' -f $n,([long]$n*2),([long]$n*4)); exit 0}; exit 1" > "%BITRATE_CALC%" 2>nul
if errorlevel 1 (
    del /q "%BITRATE_CALC%" >nul 2>&1
    exit /b 1
)
for /f "usebackq tokens=1-3 delims=|" %%A in ("%BITRATE_CALC%") do (
    set "CUSTOM_BITRATE_NUM=%%A"
    set "CUSTOM_BITRATE_MAX=%%B"
    set "CUSTOM_BITRATE_BUF=%%C"
)
del /q "%BITRATE_CALC%" >nul 2>&1
if not defined CUSTOM_BITRATE_NUM exit /b 1
if not defined CUSTOM_BITRATE_MAX exit /b 1
if not defined CUSTOM_BITRATE_BUF exit /b 1
exit /b 0


rem ============================================================
rem Shared H.264 upload-copy option
rem ============================================================

:SELECT_UPLOAD
set "ENABLE_UPLOAD_BAKE=0"
set "UPLOAD_LABEL=Off"
set "UPLOAD_MODE=VBR"
set "UPLOAD_BITRATE_NUM=8000"
set "UPLOAD_BITRATE=8000k"
set "UPLOAD_MAXRATE=12000k"
set "UPLOAD_BUFSIZE=16000k"
set "UPLOAD_QP="
set "UPLOAD_FILE_TAG=8000k"
set "UPLOAD_CODEC_ARGS=-preset p7 -tune hq -rc vbr -b:v 8000k -maxrate:v 12000k -bufsize:v 16000k"
if /i "%MODE%"=="AV1" set "TOTAL_STAGES=4"

echo.
echo Social / video-sharing H.264 upload copy:
echo.
echo   [1] Off   ^(default^)
echo   [2] Create H.264 MP4 upload copy
echo.
echo       AV1 : Film Grain is synthesized and baked to pixels.
echo       HEVC: Same Grain / LUT pipeline is rendered directly to H.264.
echo.
set "UPLOAD_SEL=1"
if "%FG_STUDIO_MODE%"=="1" (
    if "%FG_UPLOAD%"=="1" set "UPLOAD_SEL=2"
    if not defined FG_UPLOAD if "%FG_AV1_UPLOAD%"=="1" set "UPLOAD_SEL=2"
) else (
    set /p "UPLOAD_SEL=Select [1-2, default 1]: "
)

if not "%UPLOAD_SEL%"=="2" exit /b 0

set "ENABLE_UPLOAD_BAKE=1"
if /i "%MODE%"=="AV1" set "TOTAL_STAGES=5"

if not "%FG_CAP_H264%"=="1" (
    echo.
    echo ERROR: H.264 NVENC is not supported by this GPU / driver.
    echo H.264 upload copy requires hardware H.264 encoding.
    echo.
    exit /b 1
)

if /i "%MODE%"=="AV1" (
    "%FFMPEG%" -hide_banner -h decoder=libdav1d >nul 2>&1
    if errorlevel 1 (
        echo.
        echo ERROR: This FFmpeg build does not contain libdav1d.
        echo AV1 upload copy requires libdav1d to synthesize Film Grain.
        echo.
        exit /b 1
    )
)

if "%FG_STUDIO_MODE%"=="1" goto SELECT_UPLOAD_STUDIO

echo.
echo H.264 upload quality:
echo.
echo   NVENC P7 / fixed bitrate:
echo   [1]  6000 kbps
echo   [2]  8000 kbps   ^(default^)
echo   [3] 15000 kbps
echo.
echo   CPU libx264 / slow / tune grain / 2-pass / FPS-linked:
echo   [4] Recommended   ^(24p~6M / 30p~7.5M / 60p~15M^)
echo   [5] High quality  ^(24p~8M / 30p~10M / 60p~20M^)
echo   [6] Very high     ^(24p~10M / 30p~12.5M / 60p~25M^)
echo.
set "UPLOAD_RATE_SEL=2"
set /p "UPLOAD_RATE_SEL=Select [1-6, default 2]: "

if "%UPLOAD_RATE_SEL%"=="1" call :SET_UPLOAD_RATE 6000
if "%UPLOAD_RATE_SEL%"=="2" call :SET_UPLOAD_RATE 8000
if "%UPLOAD_RATE_SEL%"=="3" call :SET_UPLOAD_RATE 15000
if "%UPLOAD_RATE_SEL%"=="4" call :SET_UPLOAD_X264_TIER 1
if "%UPLOAD_RATE_SEL%"=="5" call :SET_UPLOAD_X264_TIER 2
if "%UPLOAD_RATE_SEL%"=="6" call :SET_UPLOAD_X264_TIER 3
if errorlevel 1 exit /b 1
exit /b 0

:SELECT_UPLOAD_STUDIO
if /i "%FG_UPLOAD_MODE%"=="X264" goto SELECT_UPLOAD_STUDIO_X264
if not defined FG_UPLOAD_BITRATE (
    call :SET_UPLOAD_RATE 8000
    exit /b 0
)
call :SET_UPLOAD_RATE "%FG_UPLOAD_BITRATE%"
if errorlevel 1 (
    echo.
    echo ERROR: Invalid Studio H.264 upload bitrate: %FG_UPLOAD_BITRATE%
    echo Allowed NVENC bitrates: 6000, 8000, 15000
    echo.
    exit /b 1
)
exit /b 0

:SELECT_UPLOAD_STUDIO_QP
if not defined FG_UPLOAD_QP set "FG_UPLOAD_QP=16"
call :SET_UPLOAD_QP "%FG_UPLOAD_QP%"
if errorlevel 1 (
    echo.
    echo ERROR: Invalid Studio H.264 upload QP: %FG_UPLOAD_QP%
    echo Allowed: 18, 16, 14
    echo.
    exit /b 1
)
exit /b 0

:SET_UPLOAD_RATE
set "UPLOAD_BITRATE_NUM=%~1"
if "%UPLOAD_BITRATE_NUM%"=="6000" goto SET_UPLOAD_RATE_OK
if "%UPLOAD_BITRATE_NUM%"=="8000" goto SET_UPLOAD_RATE_OK
if "%UPLOAD_BITRATE_NUM%"=="15000" goto SET_UPLOAD_RATE_OK
exit /b 1

:SET_UPLOAD_RATE_OK
set /a UPLOAD_MAXRATE_NUM=(UPLOAD_BITRATE_NUM*3)/2
set /a UPLOAD_BUFSIZE_NUM=UPLOAD_BITRATE_NUM*2
set "UPLOAD_MODE=VBR"
set "UPLOAD_QP="
set "UPLOAD_BITRATE=%UPLOAD_BITRATE_NUM%k"
set "UPLOAD_MAXRATE=%UPLOAD_MAXRATE_NUM%k"
set "UPLOAD_BUFSIZE=%UPLOAD_BUFSIZE_NUM%k"
set "UPLOAD_FILE_TAG=%UPLOAD_BITRATE_NUM%k"
set "UPLOAD_CODEC_ARGS=-preset p7 -tune hq -rc vbr -b:v %UPLOAD_BITRATE% -maxrate:v %UPLOAD_MAXRATE% -bufsize:v %UPLOAD_BUFSIZE%"
set "UPLOAD_LABEL=H.264 MP4 / p7 VBR / %UPLOAD_BITRATE%"
exit /b 0

:SET_UPLOAD_QP
set "UPLOAD_QP=%~1"
if "%UPLOAD_QP%"=="18" goto SET_UPLOAD_QP_OK
if "%UPLOAD_QP%"=="16" goto SET_UPLOAD_QP_OK
if "%UPLOAD_QP%"=="14" goto SET_UPLOAD_QP_OK
exit /b 1

:SET_UPLOAD_QP_OK
set "UPLOAD_MODE=QP"
set "UPLOAD_BITRATE_NUM="
set "UPLOAD_BITRATE="
set "UPLOAD_MAXRATE="
set "UPLOAD_BUFSIZE="
set "UPLOAD_FILE_TAG=QP%UPLOAD_QP%"
set "UPLOAD_CODEC_ARGS=-preset p7 -tune hq -rc constqp -qp %UPLOAD_QP%"
set "UPLOAD_LABEL=H.264 MP4 / p7 CONSTQP / QP%UPLOAD_QP%"
exit /b 0


:SELECT_UPLOAD_STUDIO_X264
if not defined FG_UPLOAD_X264_TIER set "FG_UPLOAD_X264_TIER=1"
call :SET_UPLOAD_X264_TIER "%FG_UPLOAD_X264_TIER%"
if errorlevel 1 (
    echo.
    echo ERROR: Invalid Studio x264 FPS-linked tier or libx264 is unavailable: %FG_UPLOAD_X264_TIER%
    echo Allowed x264 tiers: 1, 2, 3
    echo.
    exit /b 1
)
exit /b 0

:SET_UPLOAD_X264_TIER
set "UPLOAD_X264_TIER=%~1"
if "%UPLOAD_X264_TIER%"=="1" goto SET_UPLOAD_X264_TIER_CHECK
if "%UPLOAD_X264_TIER%"=="2" goto SET_UPLOAD_X264_TIER_CHECK
if "%UPLOAD_X264_TIER%"=="3" goto SET_UPLOAD_X264_TIER_CHECK
exit /b 1

:SET_UPLOAD_X264_TIER_CHECK
"%FFMPEG%" -hide_banner -h encoder=libx264 >nul 2>&1
if errorlevel 1 (
    echo.
    echo ERROR: This FFmpeg build does not contain libx264.
    echo CPU x264 Slow Grain upload requires the libx264 encoder.
    echo.
    exit /b 1
)

set "UPLOAD_MODE=X264"
set "UPLOAD_QP="
set "UPLOAD_BITRATE_NUM="
set "UPLOAD_BITRATE="
set "UPLOAD_MAXRATE="
set "UPLOAD_BUFSIZE="
set "UPLOAD_FILE_TAG=X264FPS_T%UPLOAD_X264_TIER%"
set "UPLOAD_CODEC_ARGS="
set "UPLOAD_HIGH_MOTION=0"

if "%FG_STUDIO_MODE%"=="1" (
    if "%FG_UPLOAD_HIGH_MOTION%"=="1" set "UPLOAD_HIGH_MOTION=1"
) else (
    echo.
    echo High-motion video:
    echo   [1] No   ^(default / FPS-linked bitrate x 0.5^)
    echo   [2] Yes  ^(use full FPS-linked bitrate^)
    echo.
    set "UPLOAD_MOTION_SEL=1"
    set /p "UPLOAD_MOTION_SEL=Select [1-2, default 1]: "
    if "%UPLOAD_MOTION_SEL%"=="2" set "UPLOAD_HIGH_MOTION=1"
)

set "UPLOAD_LABEL=H.264 MP4 / x264 slow grain / 2-pass / FPS-linked tier %UPLOAD_X264_TIER%"
exit /b 0

:RESOLVE_X264_UPLOAD_RATE
if /i not "%UPLOAD_MODE%"=="X264" exit /b 0

set "UPLOAD_X264_BASE60="
set "UPLOAD_X264_TIER_LABEL="
if "%UPLOAD_X264_TIER%"=="1" (
    set "UPLOAD_X264_BASE60=15000"
    set "UPLOAD_X264_TIER_LABEL=Recommended"
)
if "%UPLOAD_X264_TIER%"=="2" (
    set "UPLOAD_X264_BASE60=20000"
    set "UPLOAD_X264_TIER_LABEL=High"
)
if "%UPLOAD_X264_TIER%"=="3" (
    set "UPLOAD_X264_BASE60=25000"
    set "UPLOAD_X264_TIER_LABEL=Very High"
)
if not defined UPLOAD_X264_BASE60 exit /b 1

set "FG_X264_OUT_FPS=%OUT_FPS%"
set "FG_X264_BASE60=%UPLOAD_X264_BASE60%"
set "FG_X264_HIGH_MOTION=%UPLOAD_HIGH_MOTION%"
set "FG_X264_OUT_W=%ACTIVE_WIDTH%"
set "FG_X264_OUT_H=%ACTIVE_HEIGHT%"
set "X264_RATE_FILE=%TEMP%\FGU_x264rate_%RANDOM%_%RANDOM%.txt"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=$env:FG_X264_OUT_FPS; try { $p=$s -split '/'; if($p.Count -eq 2){$fps=[double]$p[0]/[double]$p[1]}else{$fps=[double]$s}; $base=[double]$env:FG_X264_BASE60; $w=[double]$env:FG_X264_OUT_W; $h=[double]$env:FG_X264_OUT_H; if($fps -le 0 -or $base -le 0 -or $w -le 0 -or $h -le 0){exit 1}; $motion=0.5; if($env:FG_X264_HIGH_MOTION -eq '1'){$motion=1.0}; $res=[Math]::Sqrt(($w*$h)/(1920.0*1080.0)); $raw=$base*$fps/60.0*$res*$motion; $br=[int]([Math]::Floor(($raw+250.0)/500.0)*500.0); if($br -lt 1000){$br=1000}; [Console]::Out.Write($br) } catch { exit 1 }" > "%X264_RATE_FILE%" 2>nul

set "UPLOAD_BITRATE_NUM="
if exist "%X264_RATE_FILE%" set /p "UPLOAD_BITRATE_NUM="<"%X264_RATE_FILE%"
del /q "%X264_RATE_FILE%" >nul 2>&1
set "FG_X264_OUT_FPS="
set "FG_X264_BASE60="
set "FG_X264_HIGH_MOTION="
set "FG_X264_OUT_W="
set "FG_X264_OUT_H="

if not defined UPLOAD_BITRATE_NUM (
    echo.
    echo ERROR: Could not calculate FPS/resolution-linked x264 bitrate for %ACTIVE_WIDTH%x%ACTIVE_HEIGHT% @ %OUT_FPS%.
    echo.
    exit /b 1
)

set /a UPLOAD_MAXRATE_NUM=UPLOAD_BITRATE_NUM*3
set /a UPLOAD_BUFSIZE_NUM=UPLOAD_BITRATE_NUM*6
set "UPLOAD_BITRATE=%UPLOAD_BITRATE_NUM%k"
set "UPLOAD_MAXRATE=%UPLOAD_MAXRATE_NUM%k"
set "UPLOAD_BUFSIZE=%UPLOAD_BUFSIZE_NUM%k"
set "UPLOAD_FILE_TAG=X264FPS_%UPLOAD_BITRATE_NUM%k"
set "UPLOAD_CODEC_ARGS=-preset slow -tune grain -b:v %UPLOAD_BITRATE% -maxrate %UPLOAD_MAXRATE% -bufsize %UPLOAD_BUFSIZE%"
set "UPLOAD_X264_MOTION_LABEL=Normal motion / half rate"
if "%UPLOAD_HIGH_MOTION%"=="1" set "UPLOAD_X264_MOTION_LABEL=High motion / full rate"
set "UPLOAD_LABEL=H.264 MP4 / x264 slow grain / 2-pass / %UPLOAD_X264_TIER_LABEL% / %UPLOAD_X264_MOTION_LABEL% / %ACTIVE_WIDTH%x%ACTIVE_HEIGHT% @ %OUT_FPS% / %UPLOAD_BITRATE% / VBV %UPLOAD_MAXRATE% max / %UPLOAD_BUFSIZE% buf"
exit /b 0

:SELECT_UPLOAD_SUBTITLE
set "ENABLE_UPLOAD_SUBTITLE=0"
set "UPLOAD_SUB_SUFFIX="
set "SUB_MODE=OFF"
if not defined FG_SUB_MODE set "FG_SUB_MODE=OFF"
if not defined FG_SUB_INDEX set "FG_SUB_INDEX=0"
if not defined FG_SUB_FONT set "FG_SUB_FONT=huiwen-mincho"
if not defined FG_SUB_FONT_SIZE set "FG_SUB_FONT_SIZE=69"
if not defined FG_SUB_PRIMARY_HEX set "FG_SUB_PRIMARY_HEX=FFFFFF"
if not defined FG_SUB_BORDER_HEX set "FG_SUB_BORDER_HEX=000000"
if not defined FG_SUB_OUTLINE set "FG_SUB_OUTLINE=1"
if not defined FG_SUB_SHADOW set "FG_SUB_SHADOW=1"
if not defined FG_SUB_MARGINV set "FG_SUB_MARGINV=25"

if not "%ENABLE_UPLOAD_BAKE%"=="1" exit /b 0

if "%FG_STUDIO_MODE%"=="1" goto SELECT_UPLOAD_SUBTITLE_STUDIO

echo.
echo Burn hard subtitles into the H.264 upload copy:
echo.
echo   [1] Off ^(default^)
echo   [2] Auto - same-name external subtitle, otherwise embedded subtitle #1
echo   [3] Embedded text subtitle track
echo   [4] External subtitle file
echo.
set "SUB_SEL=1"
set /p "SUB_SEL=Select [1-4, default 1]: "
if "%SUB_SEL%"=="1" exit /b 0
if "%SUB_SEL%"=="2" set "FG_SUB_MODE=AUTO"
if "%SUB_SEL%"=="3" set "FG_SUB_MODE=EMBEDDED"
if "%SUB_SEL%"=="4" set "FG_SUB_MODE=EXTERNAL"
if /i "%FG_SUB_MODE%"=="OFF" (
    echo Invalid subtitle selection. Subtitle burn disabled.
    exit /b 0
)

if /i "%FG_SUB_MODE%"=="EMBEDDED" (
    set "SUB_TRACK=1"
    set /p "SUB_TRACK=Embedded subtitle track number [1]: "
    echo(%SUB_TRACK%| findstr /r /x "[1-9][0-9]*" >nul
    if errorlevel 1 set "SUB_TRACK=1"
    set /a FG_SUB_INDEX=SUB_TRACK-1
)
if /i "%FG_SUB_MODE%"=="EXTERNAL" (
    echo.
    echo Enter the full subtitle path. Leave blank to auto-use a same-name ASS/SRT/SSA/VTT file.
    set /p "FG_SUB_PATH=Subtitle path: "
)

echo.
echo Subtitle style. Press Enter to keep each default.
set "SUB_TMP="
set /p "SUB_TMP=Font [huiwen-mincho]: "
if defined SUB_TMP set "FG_SUB_FONT=%SUB_TMP%"
set "SUB_TMP="
set /p "SUB_TMP=Font size [23]: "
if defined SUB_TMP set "FG_SUB_FONT_SIZE=%SUB_TMP%"
set "SUB_TMP="
set /p "SUB_TMP=Text color RGB hex [FFFFFF]: "
if defined SUB_TMP set "FG_SUB_PRIMARY_HEX=%SUB_TMP%"
set "SUB_TMP="
set /p "SUB_TMP=Border/shadow color RGB hex [000000]: "
if defined SUB_TMP set "FG_SUB_BORDER_HEX=%SUB_TMP%"
set "SUB_TMP="
set /p "SUB_TMP=Outline [1]: "
if defined SUB_TMP set "FG_SUB_OUTLINE=%SUB_TMP%"
set "SUB_TMP="
set /p "SUB_TMP=Shadow [1]: "
if defined SUB_TMP set "FG_SUB_SHADOW=%SUB_TMP%"
set "SUB_TMP="
set /p "SUB_TMP=Gap below active picture [3]: "
if defined SUB_TMP set "FG_SUB_MARGINV=%SUB_TMP%"
goto SELECT_UPLOAD_SUBTITLE_ENABLE

:SELECT_UPLOAD_SUBTITLE_STUDIO
if not "%FG_UPLOAD_SUBTITLE%"=="1" exit /b 0
if defined FG_SUB_MODE set "SUB_MODE=%FG_SUB_MODE%"
if not defined FG_SUB_MODE set "SUB_MODE=OFF"
if /i "%SUB_MODE%"=="OFF" exit /b 0
if /i not "%SUB_MODE%"=="AUTO" if /i not "%SUB_MODE%"=="EMBEDDED" if /i not "%SUB_MODE%"=="EXTERNAL" (
    echo ERROR: Invalid Studio subtitle mode: %SUB_MODE%
    exit /b 1
)
set "FG_SUB_MODE=%SUB_MODE%"
if not defined FG_SUB_INDEX set "FG_SUB_INDEX=0"
if not defined FG_SUB_FONT set "FG_SUB_FONT=huiwen-mincho"
if not defined FG_SUB_FONT_SIZE set "FG_SUB_FONT_SIZE=69"
if not defined FG_SUB_PRIMARY_HEX set "FG_SUB_PRIMARY_HEX=FFFFFF"
if not defined FG_SUB_BORDER_HEX set "FG_SUB_BORDER_HEX=000000"
if not defined FG_SUB_OUTLINE set "FG_SUB_OUTLINE=1"
if not defined FG_SUB_SHADOW set "FG_SUB_SHADOW=1"
if not defined FG_SUB_MARGINV set "FG_SUB_MARGINV=25"

:SELECT_UPLOAD_SUBTITLE_ENABLE
if not exist "%SUBTITLE_HELPER%" (
    echo ERROR: Subtitle helper was not found:
    echo "%SUBTITLE_HELPER%"
    exit /b 1
)
"%FFMPEG%" -hide_banner -h filter=subtitles >nul 2>&1
if errorlevel 1 (
    echo ERROR: This FFmpeg build does not contain the subtitles/libass filter.
    exit /b 1
)
set "ENABLE_UPLOAD_SUBTITLE=1"
set "UPLOAD_SUB_SUFFIX=_SUB"
exit /b 0


rem ============================================================
rem Shared encoder arguments and session summary
rem ============================================================

:BUILD_ENCODER_ARGS
set "ENABLE_BF=0"
set "ENABLE_BREF=0"
set "ENABLE_SPATIAL_AQ=0"
set "ENABLE_TEMPORAL_AQ=0"
set "ENABLE_LOOKAHEAD=0"
set "ENABLE_QRES=0"
set "ENABLE_FULLRES=0"

if /i "%MODE%"=="AV1" (
    set "ENABLE_BF=%FG_CAP_AV1_BF%"
    set "ENABLE_BREF=%FG_CAP_AV1_BREF%"
    set "ENABLE_SPATIAL_AQ=%FG_CAP_AV1_SAQ%"
    set "ENABLE_TEMPORAL_AQ=%FG_CAP_AV1_TAQ%"
    set "ENABLE_LOOKAHEAD=%FG_CAP_AV1_LOOKAHEAD%"
    set "ENABLE_QRES=%FG_CAP_AV1_QRES%"
    set "ENABLE_FULLRES=%FG_CAP_AV1_FULLRES%"
)
if /i "%MODE%"=="HEVC" (
    set "ENABLE_BF=%FG_CAP_HEVC_BF%"
    set "ENABLE_BREF=%FG_CAP_HEVC_BREF%"
    set "ENABLE_SPATIAL_AQ=%FG_CAP_HEVC_SAQ%"
    set "ENABLE_TEMPORAL_AQ=%FG_CAP_HEVC_TAQ%"
    set "ENABLE_LOOKAHEAD=%FG_CAP_HEVC_LOOKAHEAD%"
    set "ENABLE_QRES=%FG_CAP_HEVC_QRES%"
    set "ENABLE_FULLRES=%FG_CAP_HEVC_FULLRES%"
)

set "BF_ARGS="
if "%ENABLE_BF%"=="1" set "BF_ARGS=-bf 4"
if "%ENABLE_BREF%"=="1" set "BF_ARGS=-bf 4 -b_ref_mode middle"

set "SPATIAL_AQ_ARGS="
if "%ENABLE_SPATIAL_AQ%"=="1" set "SPATIAL_AQ_ARGS=-spatial-aq 1 -aq-strength %AQ_STRENGTH%"

set "TAQ_ARGS="
if "%ENABLE_TEMPORAL_AQ%"=="1" set "TAQ_ARGS=-temporal-aq 1"

set "ACTIVE_LOOKAHEAD=Disabled"
set "LOOKAHEAD_ARGS="
if "%ENABLE_LOOKAHEAD%"=="1" (
    set "ACTIVE_LOOKAHEAD=%LOOKAHEAD%"
    set "LOOKAHEAD_ARGS=-rc-lookahead %LOOKAHEAD%"
)

set "ACTIVE_MULTIPASS=Disabled"
set "MULTIPASS_ARGS="
if /i "%MULTIPASS%"=="fullres" if "%ENABLE_FULLRES%"=="1" (
    set "ACTIVE_MULTIPASS=fullres"
    set "MULTIPASS_ARGS=-multipass fullres"
)
if /i "%MULTIPASS%"=="fullres" if not "%ENABLE_FULLRES%"=="1" if "%ENABLE_QRES%"=="1" (
    set "ACTIVE_MULTIPASS=qres ^(hardware fallback^)"
    set "MULTIPASS_ARGS=-multipass qres"
)
if /i "%MULTIPASS%"=="qres" if "%ENABLE_QRES%"=="1" (
    set "ACTIVE_MULTIPASS=qres"
    set "MULTIPASS_ARGS=-multipass qres"
)

set "ENCODER_CAP_ARGS=%MULTIPASS_ARGS% %LOOKAHEAD_ARGS% %SPATIAL_AQ_ARGS% %TAQ_ARGS% %BF_ARGS%"
if "%UHQ_MODE%"=="1" (
    set "ACTIVE_LOOKAHEAD=UHQ automatic"
    set "LOOKAHEAD_ARGS="
    set "TAQ_ARGS="
    set "BF_ARGS="
    set "ENCODER_CAP_ARGS=%MULTIPASS_ARGS% %SPATIAL_AQ_ARGS%"
)

set "H264_BF_ARGS="
if "%FG_CAP_H264_BF%"=="1" set "H264_BF_ARGS=-bf 4"
if "%FG_CAP_H264_BREF%"=="1" set "H264_BF_ARGS=-bf 4 -b_ref_mode middle"
set "H264_SAQ_ARGS="
if "%FG_CAP_H264_SAQ%"=="1" set "H264_SAQ_ARGS=-spatial-aq 1 -aq-strength %AQ_STRENGTH%"
set "H264_TAQ_ARGS="
if "%FG_CAP_H264_TAQ%"=="1" set "H264_TAQ_ARGS=-temporal-aq 1"
set "H264_LOOKAHEAD_ARGS="
if "%FG_CAP_H264_LOOKAHEAD%"=="1" set "H264_LOOKAHEAD_ARGS=-rc-lookahead 32"
set "H264_MULTIPASS_ARGS="
if "%FG_CAP_H264_FULLRES%"=="1" set "H264_MULTIPASS_ARGS=-multipass fullres"
if not "%FG_CAP_H264_FULLRES%"=="1" if "%FG_CAP_H264_QRES%"=="1" set "H264_MULTIPASS_ARGS=-multipass qres"
set "H264_CAP_ARGS=%H264_MULTIPASS_ARGS% %H264_LOOKAHEAD_ARGS% %H264_SAQ_ARGS% %H264_TAQ_ARGS% %H264_BF_ARGS%"
set "UPLOAD_CAP_ARGS=%H264_CAP_ARGS%"
if /i "%UPLOAD_MODE%"=="QP" set "UPLOAD_CAP_ARGS=%H264_BF_ARGS%"
if /i "%UPLOAD_MODE%"=="X264" set "UPLOAD_CAP_ARGS="

set "MAIN_HWACCEL_ARGS="
set "ENABLE_MAIN_NVDEC=0"
exit /b 0


:SHOW_SESSION_SUMMARY
if not "%FG_STUDIO_MODE%"=="1" cls
echo ============================================================
echo       Universal Film Grain Pipeline - Studio Bridge
echo ============================================================
echo.
echo Mode          : %MODE_LABEL%
echo Speed mode    : %SPEED_LABEL%
echo Bitrate       : %BITRATE%
echo Max bitrate   : %MAXRATE%
echo Frame rate    : %FPS_LABEL%
echo Deinterlace   : %DEINT_LABEL%
echo Cinema frame  : %FRAME_LABEL%
echo Container     : %CONTAINER_LABEL%
echo Film Look     : %LUT_LABEL%
echo Upload copy   : %UPLOAD_LABEL%
if "%ENABLE_UPLOAD_BAKE%"=="1" echo Upload       : %UPLOAD_LABEL%
if "%ENABLE_UPLOAD_SUBTITLE%"=="1" echo Upload subs   : Enabled / prepared per input
if "%LUT_ENABLED%"=="1" echo LUT compat    : DaVinci CUBE range converted for FFmpeg
echo GPU           : %FG_CAP_GPU_NAME%
if defined FG_CAP_DRIVER_VERSION echo Driver        : %FG_CAP_DRIVER_VERSION%
echo HW profile    : %FG_CAP_CACHE_STATE%
echo NVENC preset  : %PRESET%
echo NVENC tuning  : %ENCODER_TUNE%
echo Multipass     : %ACTIVE_MULTIPASS%
echo Lookahead     : %ACTIVE_LOOKAHEAD%
if "%ENABLE_SPATIAL_AQ%"=="1" (
    echo Spatial AQ    : Enabled
) else (
    echo Spatial AQ    : Disabled
)
if "%UHQ_MODE%"=="1" (
    echo B-frames      : UHQ automatic
) else if "%ENABLE_BF%"=="1" (
    if "%ENABLE_BREF%"=="1" (
        echo B-frames      : Enabled ^(4 / middle ref^)
    ) else (
        echo B-frames      : Enabled ^(4 / no B-ref^)
    )
) else (
    echo B-frames      : Disabled
)
if "%UHQ_MODE%"=="1" (
    echo Temporal AQ   : Not forced ^(UHQ temporal filter automatic^)
) else if "%ENABLE_TEMPORAL_AQ%"=="1" (
    echo Temporal AQ   : Enabled
) else (
    echo Temporal AQ   : Disabled
)

if /i "%MODE%"=="HEVC" goto SHOW_HEVC_SESSION
goto SHOW_AV1_SESSION

:SHOW_HEVC_SESSION
echo Grain folder  : %GRAIN_ROOT%
echo Grain plate   : %GRAIN_LABEL%
echo Grain opacity : %GRAIN_OPACITY%
echo Blend engine  : Vulkan GPU
echo Vulkan device : %VULKAN_DEVICE%
echo CUDA device   : %CUDA_DEVICE%
if "%FG_CAP_NVDEC%"=="1" (
    echo Main decode   : Auto ^(tested for each input^)
) else (
    echo Main decode   : Software
)
echo Output        : HEVC Main10 / %CONTAINER_MODE%
echo ============================================================
echo.
exit /b 0

:SHOW_AV1_SESSION
echo Grain mode    : %GRAIN_MODE%
echo Grain profile : %GRAIN_LABEL%
if "%LUT_ENABLED%"=="1" (
    echo Main decode   : Software ^(required for CPU lut3d / blend^)
) else (
    if "%FG_CAP_NVDEC%"=="1" (
        echo Main decode   : Auto ^(tested for each input^)
    ) else (
        echo Main decode   : Software
    )
)
echo Pipeline      : AV1 NVENC -^> IVF -^> grav1synth -^> remux -^> verify
echo Output        : AV1 Main10 / %CONTAINER_MODE%
echo ============================================================
echo.
exit /b 0


rem ============================================================
rem Shared multi-file loop and probe
rem ============================================================

:PROCESS_NEXT
if "%~1"=="" goto FINISHED

set "INPUT=%~f1"
set "INDIR=%~dp1"
set "NAME=%~n1"
set "LAST_ERROR_STAGE="
set "LAST_ERROR_LOG="

echo.
echo ============================================================
echo Input : "%INPUT%"
echo ============================================================

call :PROBE_INPUT
if errorlevel 1 (
    set /a FAIL_COUNT+=1
    shift
    goto PROCESS_NEXT
)

call :CONFIGURE_INPUT_DECODE

set "OUT_FPS=%FPS%"
set "FPS_FILTER="
set "FPS_DECISION=Source FPS"
set "FPS_SUFFIX="
if "%FPS_MODE%"=="AUTO" call :AUTO_CINEMA_FPS

set "ACTIVE_DEINT_FILTER="
set "ACTIVE_DEINT_HW_ARGS="
set "DEINT_FILE_SUFFIX="
set "DEINT_FILE_LABEL=Off"
if /i "%DEINT_MODE%"=="AUTO" call :PREPARE_DEINTERLACE_FOR_INPUT

if /i "%MODE%"=="HEVC" goto PROCESS_CURRENT_HEVC
goto PROCESS_CURRENT_AV1

:PROCESS_CURRENT_HEVC
call :PROCESS_HEVC_FILE
set "FILE_RC=%ERRORLEVEL%"
goto HANDLE_FILE_RESULT

:PROCESS_CURRENT_AV1
call :PROCESS_AV1_FILE
set "FILE_RC=%ERRORLEVEL%"

:HANDLE_FILE_RESULT
if "%FILE_RC%"=="0" set /a SUCCESS_COUNT+=1
if "%FILE_RC%"=="1" set /a FAIL_COUNT+=1
if "%FILE_RC%"=="2" set /a SKIP_COUNT+=1

shift
goto PROCESS_NEXT


:PROBE_INPUT
set "WIDTH="
set "HEIGHT="
set "FPS="
set "DURATION="
set "FIELD_ORDER="
set "DIM="

rem Write probe results to files. Do not use FOR /F command substitution;
rem CMD can otherwise damage filenames containing special characters.
set "PROBE_DIM=%TEMP%\FGU_dim_%RANDOM%_%RANDOM%.txt"
set "PROBE_FPS=%TEMP%\FGU_fps_%RANDOM%_%RANDOM%.txt"
set "PROBE_DUR=%TEMP%\FGU_dur_%RANDOM%_%RANDOM%.txt"
set "PROBE_FIELD=%TEMP%\FGU_field_%RANDOM%_%RANDOM%.txt"

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x "%INPUT%" > "%PROBE_DIM%" 2>nul
if errorlevel 1 (
    echo ERROR: FFprobe could not open the input video.
    set "LAST_ERROR_STAGE=FFprobe input open"
    del /q "%PROBE_DIM%" "%PROBE_FPS%" "%PROBE_DUR%" "%PROBE_FIELD%" >nul 2>&1
    exit /b 1
)

"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=avg_frame_rate -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_FPS%" 2>nul
"%FFPROBE%" -v error -show_entries format=duration -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_DUR%" 2>nul
"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=field_order -of default=nokey=1:noprint_wrappers=1 "%INPUT%" > "%PROBE_FIELD%" 2>nul

if exist "%PROBE_DIM%" set /p "DIM="<"%PROBE_DIM%"
if exist "%PROBE_FPS%" set /p "FPS="<"%PROBE_FPS%"
if exist "%PROBE_DUR%" set /p "DURATION="<"%PROBE_DUR%"
if exist "%PROBE_FIELD%" set /p "FIELD_ORDER="<"%PROBE_FIELD%"

del /q "%PROBE_DIM%" "%PROBE_FPS%" "%PROBE_DUR%" "%PROBE_FIELD%" >nul 2>&1

for /f "tokens=1,2 delims=x" %%A in ("%DIM%") do (
    set "WIDTH=%%A"
    set "HEIGHT=%%B"
)

if not defined WIDTH (
    echo ERROR: Could not read video width.
    set "LAST_ERROR_STAGE=FFprobe width missing"
    exit /b 1
)
if not defined HEIGHT (
    echo ERROR: Could not read video height.
    set "LAST_ERROR_STAGE=FFprobe height missing"
    exit /b 1
)

echo(%WIDTH%| findstr /r /x "[0-9][0-9]*" >nul
if errorlevel 1 (
    echo ERROR: Invalid video width returned by FFprobe: %WIDTH%
    set "LAST_ERROR_STAGE=FFprobe invalid width"
    exit /b 1
)
echo(%HEIGHT%| findstr /r /x "[0-9][0-9]*" >nul
if errorlevel 1 (
    echo ERROR: Invalid video height returned by FFprobe: %HEIGHT%
    set "LAST_ERROR_STAGE=FFprobe invalid height"
    exit /b 1
)

if not defined FPS set "FPS=30000/1001"
if "%FPS%"=="0/0" set "FPS=30000/1001"
if /i "%DURATION%"=="N/A" set "DURATION="
if not defined FIELD_ORDER set "FIELD_ORDER=unknown"
exit /b 0


:TEST_NVDEC_FILE
if not "%FG_CAP_NVDEC%"=="1" exit /b 1
"%FFMPEG%" -hide_banner -loglevel error -hwaccel cuda -hwaccel_device %CUDA_DEVICE% -i "%~1" -map 0:v:0 -frames:v 1 -f null NUL >nul 2>&1
exit /b %ERRORLEVEL%


:CONFIGURE_INPUT_DECODE
set "ENABLE_MAIN_NVDEC=0"
set "MAIN_HWACCEL_ARGS="
call :TEST_NVDEC_FILE "%INPUT%"
if errorlevel 1 exit /b 0
set "ENABLE_MAIN_NVDEC=1"
set "MAIN_HWACCEL_ARGS=-hwaccel cuda -hwaccel_device %CUDA_DEVICE%"
exit /b 0


rem ============================================================
rem HEVC backend - scanned Grain + Vulkan overlay
rem ============================================================

:PROCESS_HEVC_FILE
set "CROP_FILTER="
set "CROP_POST_FILTER="
set "LETTERBOX_FILTER="
set "ACTIVE_WIDTH=%WIDTH%"
set "ACTIVE_HEIGHT=%HEIGHT%"
if "%ENABLE_CROP%"=="1" call :PREPARE_CROP
if "%ENABLE_LETTERBOX%"=="1" call :PREPARE_LETTERBOX
if /i "%UPLOAD_MODE%"=="X264" call :RESOLVE_X264_UPLOAD_RATE
if errorlevel 1 exit /b 1

set "FRAME_POST_FILTER="
if "%ENABLE_CROP%"=="1" set "FRAME_POST_FILTER=%CROP_POST_FILTER%"
if "%ENABLE_LETTERBOX%"=="1" set "FRAME_POST_FILTER=%LETTERBOX_FILTER%"

set "OUTPUT_BASE=%INDIR%%NAME%%HEVC_SUFFIX%%FRAME_SUFFIX%%LUT_FILE_SUFFIX%"
set "OUTPUT=%OUTPUT_BASE%%FPS_SUFFIX%%DEINT_FILE_SUFFIX%.%EXT%"
set "UPLOAD_OUTPUT=%OUTPUT_BASE%%FPS_SUFFIX%%DEINT_FILE_SUFFIX%_UPLOAD_H264_GRAIN_%UPLOAD_FILE_TAG%%UPLOAD_SUB_SUFFIX%.mp4"

set "DURATION_ARGS="
set "GRAIN_TIME_ARGS="
if defined DURATION (
    set "DURATION_ARGS=-t %DURATION%"
    set "GRAIN_TIME_ARGS=-t %DURATION%"
)

echo Video     : %WIDTH%x%HEIGHT% @ %FPS%
echo Output FPS: %OUT_FPS%
echo Deinterlace: %DEINT_FILE_LABEL%
if "%FPS_MODE%"=="AUTO" echo FPS choice : %FPS_DECISION%
if defined DURATION echo Duration   : %DURATION% sec

if /i not "%FRAME_MODE%"=="OFF" echo Framing   : %FRAME_LABEL%
if "%ENABLE_CROP%"=="1" echo Output size: %ACTIVE_WIDTH%x%ACTIVE_HEIGHT%

rem Pick the fastest verified Grain source for this input size.
set "GRAIN_INPUT=%GRAIN_SOURCE_MOV%"
set "GRAIN_DECODE_LABEL=Original MOV / software decode"
set "GRAIN_HWACCEL_ARGS="
set "GRAIN_SCALE_REQUIRED=1"

set "USE_1080_CACHE=0"
if %WIDTH% LEQ 1920 if %HEIGHT% LEQ 1080 set "USE_1080_CACHE=1"

if "%USE_1080_CACHE%"=="1" if exist "%GRAIN_CACHE_1080%" (
    set "GRAIN_INPUT=%GRAIN_CACHE_1080%"
    set "GRAIN_DECODE_LABEL=1080p HEVC Lossless cache / software decode"
    if "%WIDTH%x%HEIGHT%"=="1920x1080" (
        set "GRAIN_SCALE_REQUIRED=0"
    ) else (
        set "GRAIN_SCALE_REQUIRED=1"
    )
)

if "%GRAIN_INPUT%"=="%GRAIN_SOURCE_MOV%" if exist "%GRAIN_CACHE%" (
    set "GRAIN_INPUT=%GRAIN_CACHE%"
    set "GRAIN_DECODE_LABEL=4K HEVC Lossless cache / software decode"
    set "GRAIN_SCALE_REQUIRED=1"
)

if not "%GRAIN_INPUT%"=="%GRAIN_SOURCE_MOV%" (
    call :TEST_NVDEC_FILE "%GRAIN_INPUT%"
    if not errorlevel 1 (
        set "GRAIN_HWACCEL_ARGS=-hwaccel cuda -hwaccel_device %CUDA_DEVICE%"
        call set "GRAIN_DECODE_LABEL=%%GRAIN_DECODE_LABEL:software decode=NVDEC CUDA%%"
    )
)

echo Grain     : %GRAIN_DECODE_LABEL%
echo             "%GRAIN_INPUT%"
echo Film Look : %LUT_LABEL%
echo Final file: "%OUTPUT%"
if "%ENABLE_UPLOAD_BAKE%"=="1" (
    echo Upload MP4: "%UPLOAD_OUTPUT%"
    echo Upload: %UPLOAD_LABEL%
)
echo.

set "GRAIN_FILTER=[1:v:0]fps=%OUT_FPS%,format=p010le,setpts=PTS-STARTPTS,hwupload"
if "%GRAIN_SCALE_REQUIRED%"=="1" set "GRAIN_FILTER=%GRAIN_FILTER%,scale_vulkan=w=%WIDTH%:h=%HEIGHT%:scaler=bilinear"
set "GRAIN_FILTER=%GRAIN_FILTER%[grainvk]"

rem No-LUT mode keeps the verified V20 branch.
set "BASE_FILTER=[0:v:0]%ACTIVE_DEINT_FILTER%%FPS_FILTER%format=p010le,setpts=PTS-STARTPTS,hwupload[basevk]"
if "%LUT_ENABLED%"=="1" set "BASE_FILTER=[0:v:0]%ACTIVE_DEINT_FILTER%%FPS_FILTER%format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='%LUT_FILTER_PATH%':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=%LUT_OPACITY%,format=p010le,hwupload[basevk]"

if exist "%OUTPUT%" (
    echo SKIP: Main HEVC output already exists:
    echo "%OUTPUT%"
    if "%ENABLE_UPLOAD_BAKE%"=="1" (
        if exist "%UPLOAD_OUTPUT%" (
            echo SKIP: H.264 upload copy already exists:
            echo "%UPLOAD_OUTPUT%"
            exit /b 2
        )
        call :RUN_HEVC_UPLOAD
        if errorlevel 1 exit /b 1
        exit /b 0
    )
    exit /b 2
)

rem Execute the command directly. Expanding a complete command stored in a
rem variable makes CMD reparse special filename characters such as ampersand.
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk %MAIN_HWACCEL_ARGS% -i "%INPUT%" -stream_loop -1 %GRAIN_TIME_ARGS% %GRAIN_HWACCEL_ARGS% -i "%GRAIN_INPUT%" -filter_complex "%BASE_FILTER%;%GRAIN_FILTER%;[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=%GRAIN_OPACITY%,hwdownload,format=p010le%FRAME_POST_FILTER%[vout]" -map "[vout]" %HEVC_STREAM_MAP_ARGS% -map_metadata 0 -map_chapters 0 -c:v hevc_nvenc -gpu %CUDA_DEVICE% -profile:v main10 -preset %PRESET% -tune hq -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% %ENCODER_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% %HEVC_AUDIO_MUX_ARGS% %HEVC_CONTAINER_EXTRA_ARGS% "%OUTPUT%"

if errorlevel 1 (
    echo.
    echo ERROR: HEVC encoding failed:
    echo "%INPUT%"
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=HEVC encode"
    exit /b 1
)

if not exist "%OUTPUT%" (
    echo.
    echo ERROR: HEVC output file was not created.
    set "LAST_ERROR_STAGE=HEVC output missing"
    exit /b 1
)

echo.
echo DONE:
echo "%OUTPUT%"

if "%ENABLE_UPLOAD_BAKE%"=="1" (
    call :RUN_HEVC_UPLOAD
    if errorlevel 1 exit /b 1
)
exit /b 0


rem ============================================================
rem AV1 backend - NVENC -> IVF -> grav1synth -> remux -> verify
rem ============================================================

:PROCESS_AV1_FILE
set "CROP_FILTER="
set "CROP_POST_FILTER="
set "CROP_DECISION=Off"
set "LETTERBOX_FILTER="
set "ACTIVE_WIDTH=%WIDTH%"
set "ACTIVE_HEIGHT=%HEIGHT%"
if "%ENABLE_CROP%"=="1" call :PREPARE_CROP
if "%ENABLE_LETTERBOX%"=="1" call :PREPARE_LETTERBOX
if /i "%UPLOAD_MODE%"=="X264" call :RESOLVE_X264_UPLOAD_RATE
if errorlevel 1 exit /b 1

set "OUTPUT=%INDIR%%NAME%_AV1GS_%GRAIN_FILE_TAG%_%SPEED_SUFFIX%_%BITRATE_NUM%k%FRAME_SUFFIX%%FPS_SUFFIX%%DEINT_FILE_SUFFIX%%LUT_FILE_SUFFIX%.%EXT%"
set "UPLOAD_OUTPUT=%INDIR%%NAME%_AV1GS_%GRAIN_FILE_TAG%_%SPEED_SUFFIX%_%BITRATE_NUM%k%FRAME_SUFFIX%%FPS_SUFFIX%%DEINT_FILE_SUFFIX%%LUT_FILE_SUFFIX%_UPLOAD_H264_GRAIN_%UPLOAD_FILE_TAG%%UPLOAD_SUB_SUFFIX%.mp4"

if exist "%OUTPUT%" (
    echo SKIP: Main AV1 output already exists:
    echo "%OUTPUT%"
    if "%ENABLE_UPLOAD_BAKE%"=="1" (
        if exist "%UPLOAD_OUTPUT%" (
            echo SKIP: H.264 upload copy already exists:
            echo "%UPLOAD_OUTPUT%"
            exit /b 2
        )
        call :RUN_AV1_UPLOAD
        if errorlevel 1 exit /b 1
        exit /b 0
    )
    exit /b 2
)

rem Isolated same-drive temporary workspace.
set "JOBID=%RANDOM%_%RANDOM%"
set "JOBDIR=%INDIR%__AV1GS_TMP_%JOBID%"
set "TMP_BASE=%JOBDIR%\base.ivf"
set "TMP_GRAIN=%JOBDIR%\grain.ivf"
set "VERIFY_TABLE=%JOBDIR%\verify.txt"
set "ENCODE_LOG=%JOBDIR%\encode.log"
set "GRAIN_LOG=%JOBDIR%\grain.log"
set "REMUX_LOG=%JOBDIR%\remux.log"
set "VERIFY_LOG=%JOBDIR%\verify.log"
set "LUT_LOCAL_FILE=%JOBDIR%\filmlook.cube"

if exist "%JOBDIR%" (
    echo ERROR: Random temporary-workspace collision:
    echo "%JOBDIR%"
    echo Existing directory was left untouched. Run the job again.
    set "LAST_ERROR_STAGE=AV1 temporary workspace collision"
    exit /b 1
)
mkdir "%JOBDIR%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Could not create temporary workspace:
    echo "%JOBDIR%"
    set "LAST_ERROR_STAGE=AV1 temporary workspace"
    exit /b 1
)

set "DURATION_ARGS="
if defined DURATION set "DURATION_ARGS=-t %DURATION%"
set "VIDEO_FILTER=%ACTIVE_DEINT_FILTER%%FPS_FILTER%%CROP_FILTER%format=p010le%LETTERBOX_FILTER%"

echo Source      : %WIDTH%x%HEIGHT% @ %FPS%
echo Output      : %ACTIVE_WIDTH%x%ACTIVE_HEIGHT% @ %OUT_FPS%
echo Deinterlace : %DEINT_FILE_LABEL%
if "%FPS_MODE%"=="AUTO" echo FPS choice  : %FPS_DECISION%
if /i not "%FRAME_MODE%"=="OFF" echo Framing     : %FRAME_LABEL%
if "%ENABLE_CROP%"=="1" echo Active size : %ACTIVE_WIDTH%x%ACTIVE_HEIGHT%
echo Grain       : %GRAIN_LABEL%
echo Bitrate     : %BITRATE%
echo Container   : %CONTAINER_LABEL%
echo Film Look   : %LUT_LABEL%
echo Final file  : "%OUTPUT%"
if "%ENABLE_UPLOAD_BAKE%"=="1" (
    echo Upload MP4 : "%UPLOAD_OUTPUT%"
    echo Upload: %UPLOAD_LABEL%
)
if defined DURATION echo Duration    : %DURATION% sec
echo.

rem Reuse the single validated FFmpeg-compatible CUBE.
if "%LUT_ENABLED%"=="1" (
    copy /y "%LUT_COMPAT_FILE%" "%LUT_LOCAL_FILE%" >nul
    if errorlevel 1 (
        echo.
        echo ERROR: Could not copy the compatible LUT into the AV1 workspace.
        echo Source:
        echo "%LUT_COMPAT_FILE%"
        echo Destination:
        echo "%LUT_LOCAL_FILE%"
        set "LAST_ERROR_STAGE=AV1 LUT local copy"
        call :HANDLE_AV1_FAILED_JOB
        exit /b 1
    )
)

rem ------------------------------------------------------------
rem Stage 1 - clean AV1 Main10 video-only encode to IVF
rem ------------------------------------------------------------
echo [1/%TOTAL_STAGES%] Encoding clean AV1 Main10 with NVENC...

if "%LUT_ENABLED%"=="1" goto AV1_STAGE1_LUT
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y %ACTIVE_DEINT_HW_ARGS% %MAIN_HWACCEL_ARGS% -i "%INPUT%" -vf "%VIDEO_FILTER%" -map 0:v:0 -an -sn -dn -c:v av1_nvenc -gpu %CUDA_DEVICE% -pix_fmt p010le -highbitdepth 1 -preset %PRESET% -tune %ENCODER_TUNE% -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% %ENCODER_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -f ivf "%TMP_BASE%"
set "STAGE_RC=%ERRORLEVEL%"
goto AV1_STAGE1_DONE

:AV1_STAGE1_LUT
call :RUN_LUT_AV1_ENCODE
set "STAGE_RC=%ERRORLEVEL%"

:AV1_STAGE1_DONE
if not "%STAGE_RC%"=="0" (
    echo.
    echo ERROR: AV1 NVENC encode failed.
    set "LAST_ERROR_STAGE=Stage 1 - AV1 NVENC encode"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

if not exist "%TMP_BASE%" (
    echo.
    echo ERROR: AV1 intermediate file was not created.
    set "LAST_ERROR_STAGE=Stage 1 - AV1 NVENC output missing"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

rem ------------------------------------------------------------
rem Stage 2 - inject AV1 Film Grain metadata
rem ------------------------------------------------------------
echo.
echo [2/%TOTAL_STAGES%] Injecting AV1 Film Grain with grav1synth...

pushd "%JOBDIR%"
"%GRAV1SYNTH%" apply "%TMP_BASE%" -o "%TMP_GRAIN%" %GRAIN_APPLY_ARGS% --replace -y > "%GRAIN_LOG%" 2>&1
set "GRAIN_RC=%ERRORLEVEL%"
popd

if not "%GRAIN_RC%"=="0" (
    echo.
    echo ============================================================
    echo grav1synth ERROR OUTPUT
    echo ============================================================
    if exist "%GRAIN_LOG%" type "%GRAIN_LOG%"
    echo ============================================================
    echo.
    echo ERROR: grav1synth failed with exit code %GRAIN_RC%.
    set "LAST_ERROR_STAGE=Stage 2 - grav1synth apply"
    set "LAST_ERROR_LOG=%GRAIN_LOG%"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

if not exist "%TMP_GRAIN%" (
    echo.
    echo ERROR: grav1synth did not create the Grain AV1 intermediate.
    set "LAST_ERROR_STAGE=Stage 2 - grav1synth output missing"
    set "LAST_ERROR_LOG=%GRAIN_LOG%"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

rem ------------------------------------------------------------
rem Stage 3 - restore original streams and metadata
rem ------------------------------------------------------------
echo.
echo [3/%TOTAL_STAGES%] Building final %CONTAINER_MODE% container...

"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -i "%TMP_GRAIN%" -i "%INPUT%" -map 0:v:0 %AV1_FINAL_REMUX_MAP% -map_metadata 1 -map_chapters 1 %AV1_FINAL_REMUX_CODEC% %AV1_FINAL_REMUX_EXTRA% "%OUTPUT%"

if errorlevel 1 (
    echo.
    echo ERROR: Final AV1 remux failed.
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=Stage 3 - FFmpeg remux"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

if not exist "%OUTPUT%" (
    echo.
    echo ERROR: Final AV1 file was not created.
    set "LAST_ERROR_STAGE=Stage 3 - final output missing"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

rem ------------------------------------------------------------
rem Stage 4 - end-to-end final-file verification
rem ------------------------------------------------------------
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
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

if not exist "%VERIFY_TABLE%" (
    echo.
    echo ERROR: No AV1 Film Grain table was produced.
    echo Final file has been kept for inspection:
    echo "%OUTPUT%"
    set "LAST_ERROR_STAGE=Stage 4 - no Film Grain table produced"
    set "LAST_ERROR_LOG=%VERIFY_LOG%"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

for %%Z in ("%VERIFY_TABLE%") do if %%~zZ LEQ 16 (
    echo.
    echo ERROR: Grain verification table is unexpectedly empty.
    echo Final file has been kept for inspection:
    echo "%OUTPUT%"
    set "LAST_ERROR_STAGE=Stage 4 - empty Film Grain table"
    set "LAST_ERROR_LOG=%VERIFY_LOG%"
    call :HANDLE_AV1_FAILED_JOB
    exit /b 1
)

if exist "%VERIFY_LOG%" type "%VERIFY_LOG%"
echo.
echo VERIFIED: AV1 Film Grain headers are present.

rem ------------------------------------------------------------
rem Stage 5 - optional Film Grain Bake-to-Pixels upload master
rem ------------------------------------------------------------
if "%ENABLE_UPLOAD_BAKE%"=="1" (
    call :RUN_AV1_UPLOAD
    if errorlevel 1 (
        call :HANDLE_AV1_FAILED_JOB
        exit /b 1
    )
)

echo.
echo DONE:
echo "%OUTPUT%"
rmdir /s /q "%JOBDIR%" >nul 2>&1
exit /b 0


:RUN_LUT_AV1_ENCODE
pushd "%JOBDIR%"
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y %ACTIVE_DEINT_HW_ARGS% -i "%INPUT%" -filter_complex "[0:v:0]%ACTIVE_DEINT_FILTER%%FPS_FILTER%%CROP_FILTER%format=gbrp16le,split=2[lutorig][lutsrc];[lutsrc]lut3d=file=filmlook.cube:interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=%LUT_OPACITY%,format=p010le%LETTERBOX_FILTER%[vout]" -map "[vout]" -an -sn -dn -c:v av1_nvenc -gpu %CUDA_DEVICE% -pix_fmt p010le -highbitdepth 1 -preset %PRESET% -tune %ENCODER_TUNE% -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% %ENCODER_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -f ivf "%TMP_BASE%"
set "RUN_LUT_RC=%ERRORLEVEL%"
popd
exit /b %RUN_LUT_RC%


:PREPARE_UPLOAD_SUBTITLE
set "UPLOAD_SUB_FILTER="
set "SUB_TEMP_NAME="
set "SUB_TEMP_PATH="
set "FG_SUB_OUT_ASS="
if not "%ENABLE_UPLOAD_SUBTITLE%"=="1" exit /b 0

set "SUB_TEMP_NAME=__FGSUB_%RANDOM%_%RANDOM%.ass"
set "SUB_TEMP_PATH=%INDIR%%SUB_TEMP_NAME%"
set "FG_SUB_OUT_ASS=%SUB_TEMP_PATH%"
set "FG_SUB_PLAYRESX=%ACTIVE_WIDTH%"
set "FG_SUB_PLAYRESY=%ACTIVE_HEIGHT%"
set "FG_SUB_BAR_H=%BAR_H%"
if not defined FG_SUB_BAR_H set "FG_SUB_BAR_H=0"

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SUBTITLE_HELPER%"
if errorlevel 1 (
    if exist "%SUB_TEMP_PATH%" del /q "%SUB_TEMP_PATH%" >nul 2>&1
    set "LAST_ERROR_STAGE=H.264 subtitle preparation"
    exit /b 1
)
if not exist "%SUB_TEMP_PATH%" (
    echo ERROR: Prepared ASS subtitle file is missing.
    set "LAST_ERROR_STAGE=H.264 subtitle prepared file missing"
    exit /b 1
)
set "UPLOAD_SUB_FILTER=,subtitles=filename='%SUB_TEMP_NAME%'"
exit /b 0

:CLEAN_UPLOAD_SUBTITLE
if defined SUB_TEMP_PATH if exist "%SUB_TEMP_PATH%" del /q "%SUB_TEMP_PATH%" >nul 2>&1
set "UPLOAD_SUB_FILTER="
set "SUB_TEMP_NAME="
set "SUB_TEMP_PATH="
set "FG_SUB_OUT_ASS="
set "FG_SUB_BAR_H="
exit /b 0


:RUN_HEVC_UPLOAD
echo.
echo Rendering H.264 upload copy directly from the HEVC Grain / LUT pipeline...
echo.
echo Source       : Original video + selected scanned Grain
if /i "%UPLOAD_MODE%"=="X264" echo Upload codec : libx264 / High / yuv420p / slow / tune grain / 2-pass
if /i not "%UPLOAD_MODE%"=="X264" echo Upload codec : H.264 NVENC / High / yuv420p / preset p7
echo Quality      : %UPLOAD_LABEL%
if /i "%UPLOAD_MODE%"=="X264" echo VBV          : max %UPLOAD_MAXRATE% / buf %UPLOAD_BUFSIZE%
if /i "%UPLOAD_MODE%"=="VBR" echo Max rate     : %UPLOAD_MAXRATE%
if /i "%UPLOAD_MODE%"=="VBR" echo Buffer       : %UPLOAD_BUFSIZE%
echo Audio        : AAC 320k stereo / 48 kHz
if "%ENABLE_UPLOAD_SUBTITLE%"=="1" echo Hard subtitle: Enabled / prepared per input
echo.

if exist "%UPLOAD_OUTPUT%" (
    echo SKIP: Upload MP4 already exists:
    echo "%UPLOAD_OUTPUT%"
    exit /b 0
)

call :PREPARE_UPLOAD_SUBTITLE
if errorlevel 1 exit /b 1
pushd "%INDIR%"
if /i "%UPLOAD_MODE%"=="X264" goto RUN_HEVC_UPLOAD_X264
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk %MAIN_HWACCEL_ARGS% -i "%INPUT%" -stream_loop -1 %GRAIN_TIME_ARGS% %GRAIN_HWACCEL_ARGS% -i "%GRAIN_INPUT%" -filter_complex "%BASE_FILTER%;%GRAIN_FILTER%;[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=%GRAIN_OPACITY%,hwdownload,format=p010le%FRAME_POST_FILTER%,format=yuv420p%UPLOAD_SUB_FILTER%[vout]" -map "[vout]" -map 0:a:0? -map_metadata 0 -c:v h264_nvenc -gpu %CUDA_DEVICE% -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% %UPLOAD_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%UPLOAD_OUTPUT%"
set "UPLOAD_RUN_RC=%ERRORLEVEL%"
popd
call :CLEAN_UPLOAD_SUBTITLE
if not "%UPLOAD_RUN_RC%"=="0" (
    echo.
    echo ERROR: HEVC H.264 upload encode failed.
    if exist "%UPLOAD_OUTPUT%" del /q "%UPLOAD_OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=HEVC H.264 upload encode"
    exit /b 1
)

if not exist "%UPLOAD_OUTPUT%" (
    echo.
    echo ERROR: HEVC H.264 upload MP4 was not created.
    set "LAST_ERROR_STAGE=HEVC H.264 upload missing"
    exit /b 1
)

echo.
echo UPLOAD COPY DONE:
echo "%UPLOAD_OUTPUT%"
exit /b 0



:RUN_HEVC_UPLOAD_X264
set "UPLOAD_PASSLOG=%TEMP%\FilmGrain_x264_%RANDOM%_%RANDOM%"
call :CLEAN_X264_PASSLOG

echo.
echo x264 pass 1/2: analysis...
pushd "%INDIR%"
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk %MAIN_HWACCEL_ARGS% -i "%INPUT%" -stream_loop -1 %GRAIN_TIME_ARGS% %GRAIN_HWACCEL_ARGS% -i "%GRAIN_INPUT%" -filter_complex "%BASE_FILTER%;%GRAIN_FILTER%;[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=%GRAIN_OPACITY%,hwdownload,format=p010le%FRAME_POST_FILTER%,format=yuv420p%UPLOAD_SUB_FILTER%[vout]" -map "[vout]" -an -c:v libx264 -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% -pass 1 -passlogfile "%UPLOAD_PASSLOG%" -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -f null NUL
set "UPLOAD_RUN_RC=%ERRORLEVEL%"
popd
if not "%UPLOAD_RUN_RC%"=="0" (
    call :CLEAN_X264_PASSLOG
    call :CLEAN_UPLOAD_SUBTITLE
    echo.
    echo ERROR: HEVC x264 upload pass 1 failed.
    set "LAST_ERROR_STAGE=HEVC x264 upload pass 1"
    exit /b 1
)

echo.
echo x264 pass 2/2: final encode...
pushd "%INDIR%"
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk %MAIN_HWACCEL_ARGS% -i "%INPUT%" -stream_loop -1 %GRAIN_TIME_ARGS% %GRAIN_HWACCEL_ARGS% -i "%GRAIN_INPUT%" -filter_complex "%BASE_FILTER%;%GRAIN_FILTER%;[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=%GRAIN_OPACITY%,hwdownload,format=p010le%FRAME_POST_FILTER%,format=yuv420p%UPLOAD_SUB_FILTER%[vout]" -map "[vout]" -map 0:a:0? -map_metadata 0 -c:v libx264 -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% -pass 2 -passlogfile "%UPLOAD_PASSLOG%" -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%UPLOAD_OUTPUT%"
set "UPLOAD_RUN_RC=%ERRORLEVEL%"
popd
call :CLEAN_X264_PASSLOG
call :CLEAN_UPLOAD_SUBTITLE

if not "%UPLOAD_RUN_RC%"=="0" (
    echo.
    echo ERROR: HEVC x264 upload pass 2 failed.
    if exist "%UPLOAD_OUTPUT%" del /q "%UPLOAD_OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=HEVC x264 upload pass 2"
    exit /b 1
)

if not exist "%UPLOAD_OUTPUT%" (
    echo.
    echo ERROR: HEVC x264 upload MP4 was not created.
    set "LAST_ERROR_STAGE=HEVC x264 upload missing"
    exit /b 1
)

echo.
echo UPLOAD COPY DONE:
echo "%UPLOAD_OUTPUT%"
exit /b 0

:RUN_AV1_UPLOAD
echo.
echo [5/5] Baking AV1 Film Grain to pixels for H.264 upload...
echo.
echo Decoder      : libdav1d / Film Grain default ON
if /i "%UPLOAD_MODE%"=="X264" echo Upload codec : libx264 / High / yuv420p / slow / tune grain / 2-pass
if /i not "%UPLOAD_MODE%"=="X264" echo Upload codec : H.264 NVENC / High / yuv420p / preset p7
echo Quality      : %UPLOAD_LABEL%
if /i "%UPLOAD_MODE%"=="X264" echo VBV          : max %UPLOAD_MAXRATE% / buf %UPLOAD_BUFSIZE%
echo Audio        : AAC 320k stereo / 48 kHz
if "%ENABLE_UPLOAD_SUBTITLE%"=="1" echo Hard subtitle: Enabled / prepared per input
echo.

if exist "%UPLOAD_OUTPUT%" (
    echo SKIP: Upload MP4 already exists:
    echo "%UPLOAD_OUTPUT%"
    exit /b 0
)

call :PREPARE_UPLOAD_SUBTITLE
if errorlevel 1 exit /b 1
pushd "%INDIR%"
rem Respect the same B-frame / Temporal-AQ switches as the main encode.
if /i "%UPLOAD_MODE%"=="X264" goto RUN_AV1_UPLOAD_X264
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -c:v libdav1d -i "%OUTPUT%" -map 0:v:0 -map 0:a:0? -map_metadata 0 -vf "format=yuv420p%UPLOAD_SUB_FILTER%" -c:v h264_nvenc -gpu %CUDA_DEVICE% -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% %UPLOAD_CAP_ARGS% -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%UPLOAD_OUTPUT%"
set "UPLOAD_RUN_RC=%ERRORLEVEL%"
popd
call :CLEAN_UPLOAD_SUBTITLE
if not "%UPLOAD_RUN_RC%"=="0" (
    echo.
    echo ERROR: Upload Bake encode failed.
    if exist "%UPLOAD_OUTPUT%" del /q "%UPLOAD_OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=Stage 5 - Film Grain Bake upload encode"
    exit /b 1
)

if not exist "%UPLOAD_OUTPUT%" (
    echo.
    echo ERROR: Upload Bake MP4 was not created.
    set "LAST_ERROR_STAGE=Stage 5 - upload MP4 missing"
    exit /b 1
)

echo.
echo UPLOAD MASTER DONE:
echo "%UPLOAD_OUTPUT%"
exit /b 0



:RUN_AV1_UPLOAD_X264
set "UPLOAD_PASSLOG=%TEMP%\FilmGrain_x264_%RANDOM%_%RANDOM%"
call :CLEAN_X264_PASSLOG

echo.
echo x264 pass 1/2: analysis...
pushd "%INDIR%"
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -c:v libdav1d -i "%OUTPUT%" -map 0:v:0 -vf "format=yuv420p%UPLOAD_SUB_FILTER%" -c:v libx264 -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% -pass 1 -passlogfile "%UPLOAD_PASSLOG%" -an -f null NUL
set "UPLOAD_RUN_RC=%ERRORLEVEL%"
popd
if not "%UPLOAD_RUN_RC%"=="0" (
    call :CLEAN_X264_PASSLOG
    call :CLEAN_UPLOAD_SUBTITLE
    echo.
    echo ERROR: AV1 x264 upload pass 1 failed.
    set "LAST_ERROR_STAGE=Stage 5 - x264 upload pass 1"
    exit /b 1
)

echo.
echo x264 pass 2/2: final encode...
pushd "%INDIR%"
"%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -c:v libdav1d -i "%OUTPUT%" -map 0:v:0 -map 0:a:0? -map_metadata 0 -vf "format=yuv420p%UPLOAD_SUB_FILTER%" -c:v libx264 -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% -pass 2 -passlogfile "%UPLOAD_PASSLOG%" -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%UPLOAD_OUTPUT%"
set "UPLOAD_RUN_RC=%ERRORLEVEL%"
popd
call :CLEAN_X264_PASSLOG
call :CLEAN_UPLOAD_SUBTITLE

if not "%UPLOAD_RUN_RC%"=="0" (
    echo.
    echo ERROR: AV1 x264 upload pass 2 failed.
    if exist "%UPLOAD_OUTPUT%" del /q "%UPLOAD_OUTPUT%" >nul 2>&1
    set "LAST_ERROR_STAGE=Stage 5 - x264 upload pass 2"
    exit /b 1
)

if not exist "%UPLOAD_OUTPUT%" (
    echo.
    echo ERROR: x264 upload MP4 was not created.
    set "LAST_ERROR_STAGE=Stage 5 - x264 upload MP4 missing"
    exit /b 1
)

echo.
echo UPLOAD MASTER DONE:
echo "%UPLOAD_OUTPUT%"
exit /b 0

:CLEAN_X264_PASSLOG
if defined UPLOAD_PASSLOG (
    del /q "%UPLOAD_PASSLOG%-0.log" >nul 2>&1
    del /q "%UPLOAD_PASSLOG%-0.log.mbtree" >nul 2>&1
    del /q "%UPLOAD_PASSLOG%-0.mbtree" >nul 2>&1
)
exit /b 0

:HANDLE_AV1_FAILED_JOB
if "%KEEP_FAILED_INTERMEDIATES%"=="1" (
    echo.
    echo Temporary workspace retained for troubleshooting:
    echo "%JOBDIR%"
    echo.
    echo Files here are intermediate/debug files, not final output.
) else (
    if defined JOBDIR if exist "%JOBDIR%" rmdir /s /q "%JOBDIR%" >nul 2>&1
)
exit /b 0


rem ============================================================
rem Shared FPS and framing helpers
rem ============================================================


rem ============================================================
rem Per-file field-rate deinterlace helper
rem ============================================================
:PREPARE_DEINTERLACE_FOR_INPUT
set "INPUT_INTERLACED=0"
if /i "%FIELD_ORDER%"=="tt" set "INPUT_INTERLACED=1"
if /i "%FIELD_ORDER%"=="bb" set "INPUT_INTERLACED=1"
if /i "%FIELD_ORDER%"=="tb" set "INPUT_INTERLACED=1"
if /i "%FIELD_ORDER%"=="bt" set "INPUT_INTERLACED=1"

if not "%INPUT_INTERLACED%"=="1" goto DEINT_PROGRESSIVE_BYPASS

set "ACTIVE_DEINT_FILTER=%DEINT_FILTER%"
set "ACTIVE_DEINT_HW_ARGS=%DEINT_HW_ARGS%"
set "DEINT_FILE_SUFFIX=%DEINT_SUFFIX%"
set "DEINT_FILE_LABEL=%DEINT_LABEL% / field_order=%FIELD_ORDER%"

rem Field-rate/Bob: one progressive frame per input field.
rem This overrides the normal cinematic/source FPS decision for this file.
set "FPS_FILTER="
set "FPS_SUFFIX="
call :DOUBLE_SOURCE_FPS
set "FPS_DECISION=Deinterlace field-rate 2x source"
exit /b 0

:DEINT_PROGRESSIVE_BYPASS
set "ACTIVE_DEINT_FILTER="
set "ACTIVE_DEINT_HW_ARGS="
set "DEINT_FILE_SUFFIX="
set "DEINT_FILE_LABEL=Auto bypass / field_order=%FIELD_ORDER%"
exit /b 0

:DOUBLE_SOURCE_FPS
for /f "tokens=1,2 delims=/" %%A in ("%FPS%") do call :DOUBLE_SOURCE_FPS_PARTS %%A %%B
exit /b 0

:DOUBLE_SOURCE_FPS_PARTS
set /a DEINT_FPS_NUM=%1*2
if "%2"=="" goto DOUBLE_SOURCE_FPS_INTEGER
if "%2"=="1" goto DOUBLE_SOURCE_FPS_INTEGER
set "OUT_FPS=%DEINT_FPS_NUM%/%2"
exit /b 0

:DOUBLE_SOURCE_FPS_INTEGER
set "OUT_FPS=%DEINT_FPS_NUM%"
exit /b 0
:AUTO_CINEMA_FPS
set "FPS_CLASS_NOTE="
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

rem FFprobe can describe VFR or mathematically equivalent frame rates with
rem non-canonical fractions (for example 1800000/60001 or 60/2). Normalize
rem the rational value, find the nearest known cinema family, and accept it
rem only when the distance is small enough to avoid changing unrelated rates.
set "FPS_CLASS_FILE=%TEMP%\FGU_fpsclass_%RANDOM%_%RANDOM%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=$env:FPS -split '/'; try { $c=[Globalization.CultureInfo]::InvariantCulture; if($p.Count -eq 2){$v=[double]::Parse($p[0],$c)/[double]::Parse($p[1],$c)}else{$v=[double]::Parse($env:FPS,$c)} } catch { exit 1 }; if($v -le 0){exit 1}; $best=1e9; $class='KEEP'; foreach($t in @(23.976023976,29.970029970,47.952047952,59.940059940,119.880119880)){ $d=[Math]::Abs($v-$t); if($d -lt $best){$best=$d;$class='23976'} }; foreach($t in @(24.0,25.0,30.0,48.0,50.0,60.0,100.0,120.0)){ $d=[Math]::Abs($v-$t); if($d -lt $best){$best=$d;$class='24'} }; if($best -le 0.25){[Console]::Out.Write($class)}else{[Console]::Out.Write('KEEP')}" > "%FPS_CLASS_FILE%" 2>nul
set "FPS_CLASS="
if exist "%FPS_CLASS_FILE%" set /p "FPS_CLASS="<"%FPS_CLASS_FILE%"
del /q "%FPS_CLASS_FILE%" >nul 2>&1
if /i "%FPS_CLASS%"=="23976" (
    set "FPS_CLASS_NOTE=Normalized/VFR source %FPS%"
    goto AUTO_23976
)
if /i "%FPS_CLASS%"=="24" (
    set "FPS_CLASS_NOTE=Normalized/VFR source %FPS%"
    goto AUTO_24
)

set "OUT_FPS=%FPS%"
set "FPS_FILTER="
set "FPS_DECISION=Unknown rate - keep source"
set "FPS_SUFFIX="
exit /b 0

:AUTO_23976
set "OUT_FPS=24000/1001"
set "FPS_FILTER=fps=24000/1001,"
set "FPS_DECISION=23.976 fps"
if defined FPS_CLASS_NOTE set "FPS_DECISION=23.976 fps - %FPS_CLASS_NOTE%"
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
if defined FPS_CLASS_NOTE set "FPS_DECISION=24.000 fps - %FPS_CLASS_NOTE%"
set "FPS_SUFFIX=_24p"
exit /b 0

:AUTO_24_SAME
set "OUT_FPS=24"
set "FPS_FILTER="
set "FPS_DECISION=Already 24.000 fps"
set "FPS_SUFFIX=_24p"
exit /b 0


:PREPARE_LETTERBOX
set "BAR_H=0"
set "LETTERBOX_FILTER="
if not "%ENABLE_LETTERBOX%"=="1" exit /b 0

rem If source is already 2.39:1 or wider, leave it untouched.
set /a ASPECT_LEFT=%WIDTH%*100
set /a ASPECT_RIGHT=%HEIGHT%*239
if %ASPECT_LEFT% GEQ %ASPECT_RIGHT% (
    echo Letterbox : source already 2.39:1 or wider - no bars added
    exit /b 0
)

rem Per-side bar = (height - width/2.39) / 2, forced even for P010.
set /a BAR_RAW=(%HEIGHT%*239-%WIDTH%*100)/478
set /a BAR_H=(%BAR_RAW%/2)*2

if %BAR_H% LEQ 0 (
    set "BAR_H=0"
    echo Letterbox : bars too small - no bars added
    exit /b 0
)

set "LETTERBOX_FILTER=,drawbox=x=0:y=0:w=iw:h=%BAR_H%:color=black:t=fill,drawbox=x=0:y=ih-%BAR_H%:w=iw:h=%BAR_H%:color=black:t=fill"
echo Letterbox : 2.39:1 - top %BAR_H% px / bottom %BAR_H% px
exit /b 0


:PREPARE_CROP
set "CROP_FILTER="
set "CROP_POST_FILTER="
set "CROP_DECISION=No crop needed"
set "ACTIVE_WIDTH=%WIDTH%"
set "ACTIVE_HEIGHT=%HEIGHT%"

set /a ASPECT_LEFT=%WIDTH%*100
set /a ASPECT_RIGHT=%HEIGHT%*239
if %ASPECT_LEFT% GEQ %ASPECT_RIGHT% exit /b 0

rem Round WIDTH/2.39 to the nearest even active height.
set /a TARGET_H=((%WIDTH%*100+239)/478)*2
if %TARGET_H% GEQ %HEIGHT% exit /b 0
if %TARGET_H% LEQ 0 exit /b 0

set /a CROP_Y=(%HEIGHT%-%TARGET_H%)/2
set /a CROP_Y=(CROP_Y/2)*2

set "ACTIVE_HEIGHT=%TARGET_H%"
set "CROP_FILTER=crop=w=iw:h=%TARGET_H%:x=0:y=%CROP_Y%,"
set "CROP_POST_FILTER=,crop=w=iw:h=%TARGET_H%:x=0:y=%CROP_Y%"
set "CROP_DECISION=%WIDTH%x%HEIGHT% to %WIDTH%x%TARGET_H% centered"
exit /b 0


rem ============================================================
rem Final summary and exits
rem ============================================================

:FINISHED
if defined LUT_COMPAT_FILE del /q "%LUT_COMPAT_FILE%" >nul 2>&1
if "%FAIL_COUNT%"=="0" if not "%FG_STUDIO_MODE%"=="1" cls
echo.
echo ============================================================
echo                    Batch Summary
echo ============================================================
echo.
echo Mode          : %MODE_LABEL%
echo Film Look     : %LUT_LABEL%
echo Speed mode    : %SPEED_LABEL%
echo Bitrate       : %BITRATE%
echo Frame rate    : %FPS_LABEL%
echo Deinterlace   : %DEINT_LABEL%
echo Cinema frame  : %FRAME_LABEL%
echo Container     : %CONTAINER_LABEL%
if /i "%MODE%"=="HEVC" echo Grain plate   : %GRAIN_LABEL%
if /i "%MODE%"=="AV1"  echo Grain profile : %GRAIN_LABEL%
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

if "%FILE_COUNT%"=="1" if not "%FG_STUDIO_MODE%"=="1" call :SHOW_ACTUAL_COMMANDS

echo.
echo ============================================================
echo.
if "%FG_STUDIO_MODE%"=="1" goto FINISHED_STUDIO
pause
endlocal
exit /b 0

:FINISHED_STUDIO
if "%FAIL_COUNT%"=="0" (
    endlocal
    exit /b 0
)
endlocal
exit /b 2


:SHOW_ACTUAL_COMMANDS
echo.
echo ============================================================
echo Actual commands
echo ============================================================
if /i "%MODE%"=="HEVC" goto SHOW_HEVC_COMMAND
goto SHOW_AV1_COMMANDS

:SHOW_HEVC_COMMAND
echo [HEVC] "%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -init_hw_device vulkan=vk:%VULKAN_DEVICE% -filter_hw_device vk %MAIN_HWACCEL_ARGS% -i "%INPUT%" -stream_loop -1 %GRAIN_TIME_ARGS% %GRAIN_HWACCEL_ARGS% -i "%GRAIN_INPUT%" -filter_complex "%BASE_FILTER%;%GRAIN_FILTER%;[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=%GRAIN_OPACITY%,hwdownload,format=p010le%FRAME_POST_FILTER%[vout]" -map "[vout]" %HEVC_STREAM_MAP_ARGS% -map_metadata 0 -map_chapters 0 -c:v hevc_nvenc -gpu %CUDA_DEVICE% -profile:v main10 -preset %PRESET% -tune hq -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% %ENCODER_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% %HEVC_AUDIO_MUX_ARGS% %HEVC_CONTAINER_EXTRA_ARGS% "%OUTPUT%"
exit /b 0

:SHOW_AV1_COMMANDS
if "%LUT_ENABLED%"=="1" goto SHOW_AV1_LUT_COMMAND
echo [Encode] "%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y %ACTIVE_DEINT_HW_ARGS% %MAIN_HWACCEL_ARGS% -i "%INPUT%" -vf "%VIDEO_FILTER%" -map 0:v:0 -an -sn -dn -c:v av1_nvenc -gpu %CUDA_DEVICE% -pix_fmt p010le -highbitdepth 1 -preset %PRESET% -tune %ENCODER_TUNE% -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% %ENCODER_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -f ivf "%TMP_BASE%"
goto SHOW_AV1_REMAINING_COMMANDS

:SHOW_AV1_LUT_COMMAND
echo [Encode] "%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y %ACTIVE_DEINT_HW_ARGS% -i "%INPUT%" -filter_complex "[0:v:0]%ACTIVE_DEINT_FILTER%%FPS_FILTER%%CROP_FILTER%format=gbrp16le,split=2[lutorig][lutsrc];[lutsrc]lut3d=file=filmlook.cube:interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=%LUT_OPACITY%,format=p010le%LETTERBOX_FILTER%[vout]" -map "[vout]" -an -sn -dn -c:v av1_nvenc -gpu %CUDA_DEVICE% -pix_fmt p010le -highbitdepth 1 -preset %PRESET% -tune %ENCODER_TUNE% -rc vbr -b:v %BITRATE% -maxrate:v %MAXRATE% -bufsize:v %BUFSIZE% %ENCODER_CAP_ARGS% -r %OUT_FPS% -fps_mode:v cfr %DURATION_ARGS% -f ivf "%TMP_BASE%"

:SHOW_AV1_REMAINING_COMMANDS
echo [Grain] "%GRAV1SYNTH%" apply "%TMP_BASE%" -o "%TMP_GRAIN%" %GRAIN_APPLY_ARGS% --replace -y
echo [Remux] "%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -i "%TMP_GRAIN%" -i "%INPUT%" -map 0:v:0 %AV1_FINAL_REMUX_MAP% -map_metadata 1 -map_chapters 1 %AV1_FINAL_REMUX_CODEC% %AV1_FINAL_REMUX_EXTRA% "%OUTPUT%"
if "%ENABLE_UPLOAD_BAKE%"=="1" if /i not "%UPLOAD_MODE%"=="X264" echo [Upload] "%FFMPEG%" -hide_banner -stats %STUDIO_FFMPEG_PROGRESS_ARGS% -y -c:v libdav1d -i "%OUTPUT%" -map 0:v:0 -map 0:a:0? -map_metadata 0 -c:v h264_nvenc -gpu %CUDA_DEVICE% -profile:v high -pix_fmt yuv420p %UPLOAD_CODEC_ARGS% %UPLOAD_CAP_ARGS% -c:a aac -b:a 320k -ac 2 -ar 48000 -movflags +faststart "%UPLOAD_OUTPUT%"
if "%ENABLE_UPLOAD_BAKE%"=="1" if /i "%UPLOAD_MODE%"=="X264" echo [Upload x264] 2-pass / preset slow / tune grain / %UPLOAD_BITRATE%
exit /b 0


:NO_INPUT
echo.
echo Drag one or more video files onto this BAT file.
echo.
if "%FG_STUDIO_MODE%"=="1" goto NO_INPUT_STUDIO
pause
endlocal
exit /b 0

:NO_INPUT_STUDIO
endlocal
exit /b 1


:FATAL_END
if defined LUT_COMPAT_FILE del /q "%LUT_COMPAT_FILE%" >nul 2>&1
echo.
if "%FG_STUDIO_MODE%"=="1" goto FATAL_END_STUDIO
pause
endlocal
exit /b 1

:FATAL_END_STUDIO
endlocal
exit /b 1
