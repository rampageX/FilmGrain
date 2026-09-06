@echo off
setlocal DisableDelayedExpansion

rem ============================================================
rem AV1 Film Grain Add / Replace - NO VIDEO RE-ENCODE
rem
rem Input:
rem   Existing AV1 video in MKV / MP4 / WebM / etc.
rem
rem Pipeline:
rem   Original AV1
rem       -> FFmpeg stream-copy to IVF
rem       -> grav1synth --replace
rem       -> final MKV / MP4
rem       -> grav1synth inspect verification
rem
rem Video is NEVER re-encoded.
rem ============================================================


rem ============================================================
rem Paths
rem ============================================================

call "%~dp0FilmGrain_Config_Load.bat"
if errorlevel 1 exit /b 1

rem Keep failed temporary workspace:
rem   1 = keep for troubleshooting
rem   0 = delete
set "KEEP_FAILED_INTERMEDIATES=1"


rem ============================================================
rem Tool checks
rem ============================================================

"%FFMPEG%" -version >nul 2>&1
if errorlevel 1 (
    echo ERROR: FFmpeg not found:
    echo "%FFMPEG%"
    if not "%FG_TOOL_NO_PAUSE%"=="1" pause
    exit /b 1
)

"%FFPROBE%" -version >nul 2>&1
if errorlevel 1 (
    echo ERROR: FFprobe not found:
    echo "%FFPROBE%"
    if not "%FG_TOOL_NO_PAUSE%"=="1" pause
    exit /b 1
)

"%GRAV1SYNTH%" --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: grav1synth not found:
    echo "%GRAV1SYNTH%"
    if not "%FG_TOOL_NO_PAUSE%"=="1" pause
    exit /b 1
)

if "%~1"=="" (
    echo.
    echo Drag one or more existing AV1 video files onto this BAT.
    echo.
    echo The AV1 video stream will NOT be re-encoded.
    echo Film Grain will be added or replaced in the AV1 bitstream.
    echo.
    if not "%FG_TOOL_NO_PAUSE%"=="1" pause
    exit /b 0
)


rem ============================================================
rem Final container
rem ============================================================

if "%FG_STUDIO_MODE%"=="1" (
    set "CONTAINER_SEL=1"
    if /i "%FG_CONTAINER%"=="MP4" set "CONTAINER_SEL=2"
    goto CONTAINER_SELECTED
)

cls
echo ============================================================
echo       AV1 Film Grain Add / Replace - No Re-encode
echo ============================================================
echo.
echo Final container:
echo.
echo   [1] MKV - preserve original streams ^(default / recommended^)
echo   [2] MP4 - compatibility mode
echo.
echo MKV:
echo   - AV1 video stream copy
echo   - Original audio / subtitles / attachments / data copied
echo   - Chapters / metadata preserved
echo.
echo MP4:
echo   - AV1 video stream copy
echo   - Audio converted to AAC 256k
echo   - Subtitles / attachments / data omitted
echo   - Chapters / metadata preserved
echo   - faststart enabled
echo.
set "CONTAINER_SEL=1"
set /p "CONTAINER_SEL=Select [1-2, default 1]: "

:CONTAINER_SELECTED
set "EXT=mkv"
set "CONTAINER_MODE=MKV"
set "CONTAINER_LABEL=MKV / preserve original streams"
set "FINAL_REMUX_MAP=-map 1:a? -map 1:s? -map 1:t? -map 1:d?"
set "FINAL_REMUX_CODEC=-c copy"
set "FINAL_REMUX_EXTRA="

if "%CONTAINER_SEL%"=="2" (
    set "EXT=mp4"
    set "CONTAINER_MODE=MP4"
    set "CONTAINER_LABEL=MP4 compatibility / AAC 256k"
    set "FINAL_REMUX_MAP=-map 1:a?"
    set "FINAL_REMUX_CODEC=-c:v copy -c:a aac -b:a 256k"
    set "FINAL_REMUX_EXTRA=-movflags +faststart"
)


rem ============================================================
rem Grain mode
rem ============================================================

if "%FG_STUDIO_MODE%"=="1" (
    set "GRAIN_MODE_SEL=1"
    if /i "%FG_AV1_GRAIN_MODE%"=="ISO" set "GRAIN_MODE_SEL=2"
    if /i "%FG_AV1_GRAIN_MODE%"=="TABLE" set "GRAIN_MODE_SEL=3"
    goto GRAIN_SOURCE_SELECTED
)

cls
echo ============================================================
echo              grav1synth Film Grain
echo ============================================================
echo.
echo Grain source:
echo.
echo   [1] Film preset   ^(default / recommended^)
echo   [2] Photon ISO    ^(advanced strength control^)
echo   [3] Grain table   ^(.tbl / .txt via grav1synth --grain^)
echo.
set "GRAIN_MODE_SEL=1"
set /p "GRAIN_MODE_SEL=Select [1-3, default 1]: "

:GRAIN_SOURCE_SELECTED
set "GRAIN_MODE=PRESET"
set "GRAIN_APPLY_ARGS="
set "GRAIN_LABEL="
set "GRAIN_FILE_TAG="

if "%GRAIN_MODE_SEL%"=="2" goto GRAIN_PHOTON_MENU
if "%GRAIN_MODE_SEL%"=="3" goto GRAIN_TABLE_MENU


rem ------------------------------------------------------------
rem Built-in film preset mode
rem ------------------------------------------------------------

if "%FG_STUDIO_MODE%"=="1" (
    set "GRAIN_SEL=1"
    if defined FG_AV1_FORMAT set "GRAIN_SEL=%FG_AV1_FORMAT%"
    goto FILM_FORMAT_SELECTED
)

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

:FILM_FORMAT_SELECTED
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

if "%FG_STUDIO_MODE%"=="1" (
    set "STOCK_SEL=1"
    if defined FG_AV1_STOCK set "STOCK_SEL=%FG_AV1_STOCK%"
    goto FILM_STOCK_SELECTED
)

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

:FILM_STOCK_SELECTED
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
rem Photon ISO mode
rem ------------------------------------------------------------

:GRAIN_PHOTON_MENU
set "GRAIN_MODE=ISO"

if "%FG_STUDIO_MODE%"=="1" (
    set "GRAIN_ISO=1600"
    if defined FG_AV1_ISO set "GRAIN_ISO=%FG_AV1_ISO%"
    goto ISO_READY
)

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
set "ISO_SEL=3"
set /p "ISO_SEL=Select [1-6, default 3]: "

set "GRAIN_ISO=1600"

if "%ISO_SEL%"=="1" set "GRAIN_ISO=400"
if "%ISO_SEL%"=="2" set "GRAIN_ISO=800"
if "%ISO_SEL%"=="3" set "GRAIN_ISO=1600"
if "%ISO_SEL%"=="4" set "GRAIN_ISO=3200"

if "%ISO_SEL%"=="5" set "GRAIN_ISO=6400"
if "%ISO_SEL%"=="6" goto CUSTOM_ISO_INPUT
goto ISO_READY

:CUSTOM_ISO_INPUT
set "CUSTOM_ISO="
set /p "CUSTOM_ISO=Enter ISO value [positive integer]: "
echo(%CUSTOM_ISO%| findstr /r /x "[1-9][0-9]*" >nul
if errorlevel 1 (
    echo Invalid ISO value.
    goto CUSTOM_ISO_INPUT
)
set "GRAIN_ISO=%CUSTOM_ISO%"

:ISO_READY
if "%FG_STUDIO_MODE%"=="1" (
    set "CHROMA_SEL=1"
    if "%FG_AV1_CHROMA%"=="1" set "CHROMA_SEL=2"
    goto CHROMA_SELECTED
)

echo.
echo Chroma grain:
echo.
echo   [1] Luma only                 ^(default / cleaner^)
echo   [2] Luma + chroma
echo.
set "CHROMA_SEL=1"
set /p "CHROMA_SEL=Select [1-2, default 1]: "

:CHROMA_SELECTED
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

rem ------------------------------------------------------------
rem Existing Grain table mode
rem ------------------------------------------------------------

:GRAIN_TABLE_MENU
set "GRAIN_MODE=TABLE"
set "GRAIN_TABLE_PATH="
if "%FG_STUDIO_MODE%"=="1" (
    if defined FG_AV1_GRAIN_TABLE set "GRAIN_TABLE_PATH=%FG_AV1_GRAIN_TABLE%"
) else (
    echo.
    echo Grain table file ^(.tbl / .txt^):
    echo You can drag a table file into this window and press Enter.
    echo.
    set /p "GRAIN_TABLE_PATH=Table file: "
)
set "GRAIN_TABLE_PATH=%GRAIN_TABLE_PATH:"=%"

if not defined GRAIN_TABLE_PATH (
    echo ERROR: No Grain table file was selected.
    exit /b 1
)
if not exist "%GRAIN_TABLE_PATH%" (
    echo ERROR: Grain table file not found:
    echo "%GRAIN_TABLE_PATH%"
    exit /b 1
)

set "FG_TABLE_TAG_SOURCE=%GRAIN_TABLE_PATH%"
set "GRAIN_TABLE_STEM="
for /f "usebackq delims=" %%T in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$n=[System.IO.Path]::GetFileNameWithoutExtension($env:FG_TABLE_TAG_SOURCE); $n=[System.Text.RegularExpressions.Regex]::Replace($n,'[^A-Za-z0-9]+','_').Trim('_'); if([string]::IsNullOrWhiteSpace($n)){$n='TABLE'}; if($n.Length -gt 48){$n=$n.Substring(0,48)}; [Console]::Out.Write($n)"`) do set "GRAIN_TABLE_STEM=%%T"
set "FG_TABLE_TAG_SOURCE="
if not defined GRAIN_TABLE_STEM set "GRAIN_TABLE_STEM=TABLE"
set "GRAIN_APPLY_ARGS="
set "GRAIN_LABEL=Grain Table / %GRAIN_TABLE_STEM%"
set "GRAIN_FILE_TAG=TABLE_%GRAIN_TABLE_STEM%"
goto GRAIN_MENU_DONE

:GRAIN_MENU_DONE


rem ============================================================
rem Session summary
rem ============================================================

cls
echo ============================================================
echo       AV1 Film Grain Add / Replace - No Re-encode
echo ============================================================
echo.
echo Video          : AV1 stream copy - NO RE-ENCODE
echo Grain          : %GRAIN_LABEL%
echo Container      : %CONTAINER_LABEL%
echo Replace mode   : Enabled
echo.
echo Existing Film Grain:
echo   - absent  : new Film Grain is added
echo   - present : Film Grain is replaced
echo.
echo ============================================================
echo.

set /a TOTAL_COUNT=0
set /a SUCCESS_COUNT=0
set /a FAIL_COUNT=0
set /a SKIP_COUNT=0

:PROCESS_NEXT
if "%~1"=="" goto ALL_DONE

set /a TOTAL_COUNT+=1
call :PROCESS_FILE "%~1"
shift
goto PROCESS_NEXT


rem ============================================================
rem Process one file
rem ============================================================

:PROCESS_FILE
set "INPUT=%~f1"
set "INDIR=%~dp1"
set "NAME=%~n1"

echo.
echo ============================================================
echo [%TOTAL_COUNT%] "%INPUT%"
echo ============================================================

rem ---------- verify source video codec is AV1 ----------
set "CODEC_FILE=%TEMP%\AV1FG_codec_%RANDOM%_%RANDOM%.txt"
"%FFPROBE%" -v error -select_streams v:0 -show_entries stream=codec_name -of default=nw=1:nk=1 "%INPUT%" > "%CODEC_FILE%" 2>nul
set "VIDEO_CODEC="
set /p "VIDEO_CODEC="<"%CODEC_FILE%"
del /q "%CODEC_FILE%" >nul 2>&1

if /i not "%VIDEO_CODEC%"=="av1" (
    echo.
    echo SKIP: Video codec is "%VIDEO_CODEC%", not AV1.
    set /a SKIP_COUNT+=1
    exit /b
)

rem Keep the raw remux filename short. The verified file is renamed by
rem FilmGrain_AV1_FinalizeName.ps1 after processing succeeds. This avoids
rem path growth when a FilmGrain Studio AV1 output is processed repeatedly.
set "JOBID=%RANDOM%_%RANDOM%"
set "OUTPUT=%INDIR%__AV1FG_OUT_%JOBID%.%EXT%"

rem ---------- isolated temporary workspace ----------
set "JOBDIR=%INDIR%__AV1FG_TMP_%JOBID%"

mkdir "%JOBDIR%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Could not create temporary workspace:
    echo "%JOBDIR%"
    set /a FAIL_COUNT+=1
    exit /b
)

set "TMP_BASE=%JOBDIR%\base.ivf"
set "TMP_GRAIN=%JOBDIR%\grain.ivf"
set "GRAIN_LOG=%JOBDIR%\grain.log"
set "VERIFY_TABLE=%JOBDIR%\verify.txt"
set "VERIFY_LOG=%JOBDIR%\verify.log"

echo Output         : "%OUTPUT%"
echo Grain          : %GRAIN_LABEL%
echo Container      : %CONTAINER_MODE%
echo Video encoding : NONE
echo.


rem ============================================================
rem Stage 1 - extract existing AV1 without re-encoding
rem ============================================================

echo [1/4] Extracting AV1 stream to IVF ^(stream copy^)...
echo.

"%FFMPEG%" -hide_banner -stats -y -i "%INPUT%" -map 0:v:0 -c:v copy -f ivf "%TMP_BASE%"

if errorlevel 1 (
    echo.
    echo ERROR: Could not extract AV1 video stream.
    call :FAILED_JOB
    exit /b
)

if not exist "%TMP_BASE%" (
    echo.
    echo ERROR: base.ivf was not created.
    call :FAILED_JOB
    exit /b
)


rem ============================================================
rem Stage 2 - add / replace AV1 Film Grain
rem ============================================================

echo.
echo [2/4] Adding / replacing AV1 Film Grain with grav1synth...
echo.

if /i "%GRAIN_MODE%"=="TABLE" (
    "%GRAV1SYNTH%" apply "%TMP_BASE%" -o "%TMP_GRAIN%" --grain "%GRAIN_TABLE_PATH%" --replace -y > "%GRAIN_LOG%" 2>&1
) else (
    "%GRAV1SYNTH%" apply "%TMP_BASE%" -o "%TMP_GRAIN%" %GRAIN_APPLY_ARGS% --replace -y > "%GRAIN_LOG%" 2>&1
)
set "GRAIN_RC=%ERRORLEVEL%"

if not "%GRAIN_RC%"=="0" (
    echo.
    echo ============================================================
    echo grav1synth ERROR OUTPUT
    echo ============================================================
    if exist "%GRAIN_LOG%" type "%GRAIN_LOG%"
    echo ============================================================
    echo.
    echo ERROR: grav1synth failed with exit code %GRAIN_RC%.
    call :FAILED_JOB
    exit /b
)

if not exist "%TMP_GRAIN%" (
    echo.
    echo ERROR: grain.ivf was not created.
    call :FAILED_JOB
    exit /b
)


rem ============================================================
rem Stage 3 - final remux
rem ============================================================

echo.
echo [3/4] Building final %CONTAINER_MODE% container...
echo.

"%FFMPEG%" -hide_banner -stats -y -i "%TMP_GRAIN%" -i "%INPUT%" -map 0:v:0 %FINAL_REMUX_MAP% -map_metadata 1 -map_chapters 1 %FINAL_REMUX_CODEC% %FINAL_REMUX_EXTRA% "%OUTPUT%"

if errorlevel 1 (
    echo.
    echo ERROR: Final remux failed.
    if exist "%OUTPUT%" del /q "%OUTPUT%" >nul 2>&1
    call :FAILED_JOB
    exit /b
)

if not exist "%OUTPUT%" (
    echo.
    echo ERROR: Final output was not created.
    call :FAILED_JOB
    exit /b
)


rem ============================================================
rem Stage 4 - verify final Film Grain
rem ============================================================

echo.
echo [4/4] Verifying Film Grain headers in FINAL file...
echo.

"%GRAV1SYNTH%" inspect "%OUTPUT%" -o "%VERIFY_TABLE%" -y > "%VERIFY_LOG%" 2>&1
set "VERIFY_RC=%ERRORLEVEL%"

if not "%VERIFY_RC%"=="0" (
    echo.
    echo ============================================================
    echo grav1synth VERIFY ERROR
    echo ============================================================
    if exist "%VERIFY_LOG%" type "%VERIFY_LOG%"
    echo ============================================================
    echo.
    echo ERROR: Final Film Grain verification failed.
    call :FAILED_JOB
    exit /b
)

if exist "%VERIFY_LOG%" type "%VERIFY_LOG%"

echo.
echo VERIFIED: AV1 Film Grain headers are present.
echo.

rem ============================================================
rem Finalize clean AV1 Film Grain filename
rem
rem The proven processing path above stays unchanged.
rem Filename cleanup happens only after successful verification.
rem ============================================================

set "AV1FG_FINAL_HELPER=%~dp0FilmGrain_AV1_FinalizeName.ps1"
set "AV1FG_FINAL_RESULT=%TEMP%\AV1FG_final_%RANDOM%_%RANDOM%.txt"
set "AV1FG_FINAL_LOG=%TEMP%\AV1FG_final_%RANDOM%_%RANDOM%.log"

if not exist "%AV1FG_FINAL_HELPER%" (
    echo ERROR: AV1 filename finalizer not found:
    echo "%AV1FG_FINAL_HELPER%"
    set /a FAIL_COUNT+=1
    rmdir /s /q "%JOBDIR%" >nul 2>&1
    exit /b
)

if exist "%AV1FG_FINAL_RESULT%" del /q "%AV1FG_FINAL_RESULT%" >nul 2>&1
if exist "%AV1FG_FINAL_LOG%" del /q "%AV1FG_FINAL_LOG%" >nul 2>&1

set "FG_AV1_FINAL_INPUT=%INPUT%"
set "FG_AV1_FINAL_RAW_OUTPUT=%OUTPUT%"
set "FG_AV1_FINAL_TAG=%GRAIN_FILE_TAG%"
set "FG_AV1_FINAL_EXT=%EXT%"
set "FG_AV1_FINAL_GRAV1SYNTH=%GRAV1SYNTH%"
set "FG_AV1_FINAL_RESULT=%AV1FG_FINAL_RESULT%"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%AV1FG_FINAL_HELPER%" > "%AV1FG_FINAL_LOG%" 2>&1
set "AV1FG_FINAL_RC=%ERRORLEVEL%"

if not "%AV1FG_FINAL_RC%"=="0" (
    echo.
    echo ============================================================
    echo AV1 OUTPUT NAMING ERROR
    echo ============================================================
    if exist "%AV1FG_FINAL_LOG%" type "%AV1FG_FINAL_LOG%"
    echo ============================================================
    echo.
    echo The processed video was kept with its temporary output name:
    echo "%OUTPUT%"
    set /a FAIL_COUNT+=1
    rmdir /s /q "%JOBDIR%" >nul 2>&1
    goto FINALIZE_NAME_CLEANUP_FAILED
)

echo DONE - VIDEO WAS NOT RE-ENCODED:
if exist "%AV1FG_FINAL_RESULT%" type "%AV1FG_FINAL_RESULT%"
echo.

set /a SUCCESS_COUNT+=1
rmdir /s /q "%JOBDIR%" >nul 2>&1

if exist "%AV1FG_FINAL_RESULT%" del /q "%AV1FG_FINAL_RESULT%" >nul 2>&1
if exist "%AV1FG_FINAL_LOG%" del /q "%AV1FG_FINAL_LOG%" >nul 2>&1
set "FG_AV1_FINAL_INPUT="
set "FG_AV1_FINAL_RAW_OUTPUT="
set "FG_AV1_FINAL_TAG="
set "FG_AV1_FINAL_EXT="
set "FG_AV1_FINAL_GRAV1SYNTH="
set "FG_AV1_FINAL_RESULT="
set "AV1FG_FINAL_HELPER="
set "AV1FG_FINAL_RESULT="
set "AV1FG_FINAL_LOG="
set "AV1FG_FINAL_RC="
exit /b

:FINALIZE_NAME_CLEANUP_FAILED
if exist "%AV1FG_FINAL_RESULT%" del /q "%AV1FG_FINAL_RESULT%" >nul 2>&1
if exist "%AV1FG_FINAL_LOG%" del /q "%AV1FG_FINAL_LOG%" >nul 2>&1
set "FG_AV1_FINAL_INPUT="
set "FG_AV1_FINAL_RAW_OUTPUT="
set "FG_AV1_FINAL_TAG="
set "FG_AV1_FINAL_EXT="
set "FG_AV1_FINAL_GRAV1SYNTH="
set "FG_AV1_FINAL_RESULT="
set "AV1FG_FINAL_HELPER="
set "AV1FG_FINAL_RESULT="
set "AV1FG_FINAL_LOG="
set "AV1FG_FINAL_RC="
exit /b


rem ============================================================
rem Failed job
rem ============================================================

:FAILED_JOB
set /a FAIL_COUNT+=1

if "%KEEP_FAILED_INTERMEDIATES%"=="1" (
    echo.
    echo Temporary workspace retained for troubleshooting:
    echo "%JOBDIR%"
) else (
    rmdir /s /q "%JOBDIR%" >nul 2>&1
)

exit /b


rem ============================================================
rem Final summary
rem ============================================================

:ALL_DONE
echo.
echo ============================================================
echo                    Batch Summary
echo ============================================================
echo.
echo Operation      : AV1 Film Grain Add / Replace
echo Video encoding : NONE / stream copy
echo Grain          : %GRAIN_LABEL%
echo Container      : %CONTAINER_LABEL%
echo Total dragged  : %TOTAL_COUNT%
echo Successful     : %SUCCESS_COUNT%
echo Failed         : %FAIL_COUNT%
echo Skipped        : %SKIP_COUNT%
echo.

if "%FAIL_COUNT%"=="0" (
    echo RESULT: COMPLETED SUCCESSFULLY.
) else (
    echo RESULT: COMPLETED WITH ERRORS.
)

echo.
set "FINAL_RC=0"
if not "%FAIL_COUNT%"=="0" set "FINAL_RC=1"
if not "%FG_TOOL_NO_PAUSE%"=="1" pause
endlocal & exit /b %FINAL_RC%
