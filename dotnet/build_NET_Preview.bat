@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%~dp0.."
set "SRC=%SCRIPT_DIR%FilmGrain_Studio_NET_Preview.cs"
set "CORE=%SCRIPT_DIR%FilmGrain_BridgeExecutionCore.cs"
set "ENVCORE=%SCRIPT_DIR%FilmGrain_BridgeEnvironmentCore.cs"
set "HWCORE=%SCRIPT_DIR%FilmGrain_HardwareCapabilityCore.cs"
set "STATECORE=%SCRIPT_DIR%FilmGrain_BridgeStateCapture.cs"
set "TASKCORE=%SCRIPT_DIR%FilmGrain_BridgeTaskCoordinator.cs"
set "REQUESTCORE=%SCRIPT_DIR%FilmGrain_BridgeRequestPreparationCore.cs"
set "MEDIACORE=%SCRIPT_DIR%FilmGrain_MediaProbeCore.cs"
set "MEDIASUMMARY=%SCRIPT_DIR%FilmGrain_MediaSummaryCore.cs"
set "WORKSPACECORE=%SCRIPT_DIR%FilmGrain_WorkspacePreflightCore.cs"
set "AV1INSPECTCORE=%SCRIPT_DIR%FilmGrain_Av1GrainInspectCore.cs"
set "SUBCORE=%SCRIPT_DIR%FilmGrain_SubtitleCore.cs"
set "SUBDIALOG=%SCRIPT_DIR%FilmGrain_SubtitleDialog.cs"
set "CONFIGUTILITY=%SCRIPT_DIR%FilmGrain_ConfigUtility.cs"
set "BITRATECORE=%SCRIPT_DIR%FilmGrain_BitrateRecommendationCore.cs"
set "TABLECORE=%SCRIPT_DIR%FilmGrain_Av1GrainTableSelectionCore.cs"
set "CATALOGCORE=%SCRIPT_DIR%FilmGrain_GrainFileCatalogCore.cs"
set "LUTCATALOG=%SCRIPT_DIR%FilmGrain_LutCatalogCore.cs"
set "COLORFORM=%SCRIPT_DIR%FilmGrain_NativeColorCorrectionForm.cs"
set "LANGCORE=%SCRIPT_DIR%FilmGrain_LanguagePack.cs"
set "CONFIGCORE=%SCRIPT_DIR%FilmGrain_ConfigCore.cs"
set "SETTINGSDIALOGS=%SCRIPT_DIR%FilmGrain_SettingsDialogs.cs"
set "OUT_DIR=%SCRIPT_DIR%build"
set "OUT_EXE=%OUT_DIR%\FilmGrain_Studio_NET_Preview.exe"
set "ICON=%ROOT_DIR%\Utils\FGS.ico"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if exist "%CSC%" goto :compiler_found
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if exist "%CSC%" goto :compiler_found

echo [ERROR] .NET Framework C# compiler was not found.
echo Expected: %%WINDIR%%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
echo           %%WINDIR%%\Microsoft.NET\Framework\v4.0.30319\csc.exe
exit /b 1

:compiler_found
if not exist "%SRC%" goto :missing_source
if not exist "%CORE%" goto :missing_core
if not exist "%ENVCORE%" goto :missing_envcore
if not exist "%HWCORE%" goto :missing_hwcore
if not exist "%STATECORE%" goto :missing_statecore
if not exist "%TASKCORE%" goto :missing_taskcore
if not exist "%REQUESTCORE%" goto :missing_requestcore
if not exist "%MEDIACORE%" goto :missing_mediacore
if not exist "%MEDIASUMMARY%" exit /b 1
if not exist "%WORKSPACECORE%" exit /b 1
if not exist "%AV1INSPECTCORE%" goto :missing_av1inspectcore
if not exist "%SUBCORE%" exit /b 1
if not exist "%SUBDIALOG%" exit /b 1
if not exist "%CONFIGUTILITY%" exit /b 1
if not exist "%BITRATECORE%" exit /b 1
if not exist "%TABLECORE%" exit /b 1
if not exist "%CATALOGCORE%" exit /b 1
if not exist "%LUTCATALOG%" exit /b 1
if not exist "%COLORFORM%" exit /b 1
if not exist "%LANGCORE%" exit /b 1
if not exist "%CONFIGCORE%" exit /b 1
if not exist "%SETTINGSDIALOGS%" exit /b 1
if not exist "%~dp0FilmGrain_SetupPhase1.cs" exit /b 1
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if errorlevel 1 goto :mkdir_failed

echo ============================================================
echo Film Grain Studio .NET Preview Build
echo ============================================================
echo Compiler: "%CSC%"
echo Source  : "%SRC%"
echo Core    : "%CORE%"
echo EnvCore : "%ENVCORE%"
echo HwCore  : "%HWCORE%"
echo State   : "%STATECORE%"
echo Task    : "%TASKCORE%"
echo Request : "%REQUESTCORE%"
echo Media   : "%MEDIACORE%"
echo AV1 Ins : "%AV1INSPECTCORE%"
echo Output  : "%OUT_EXE%"
echo.

if exist "%ICON%" goto :build_with_icon
goto :build_without_icon

:build_with_icon
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /out:"%OUT_EXE%" /win32icon:"%ICON%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%SRC%" "%CORE%" "%ENVCORE%" "%HWCORE%" "%STATECORE%" "%TASKCORE%" "%REQUESTCORE%" "%MEDIACORE%" "%MEDIASUMMARY%" "%WORKSPACECORE%" "%AV1INSPECTCORE%" "%SUBCORE%" "%SUBDIALOG%" "%CONFIGUTILITY%" "%BITRATECORE%" "%TABLECORE%" "%CATALOGCORE%" "%LUTCATALOG%" "%COLORFORM%" "%LANGCORE%" "%CONFIGCORE%" "%SETTINGSDIALOGS%" "%~dp0FilmGrain_SetupPhase1.cs"
goto :build_done

:build_without_icon
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /out:"%OUT_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%SRC%" "%CORE%" "%ENVCORE%" "%HWCORE%" "%STATECORE%" "%TASKCORE%" "%REQUESTCORE%" "%MEDIACORE%" "%MEDIASUMMARY%" "%WORKSPACECORE%" "%AV1INSPECTCORE%" "%SUBCORE%" "%SUBDIALOG%" "%CONFIGUTILITY%" "%BITRATECORE%" "%TABLECORE%" "%CATALOGCORE%" "%LUTCATALOG%" "%COLORFORM%" "%LANGCORE%" "%CONFIGCORE%" "%SETTINGSDIALOGS%" "%~dp0FilmGrain_SetupPhase1.cs"

:build_done
if errorlevel 1 goto :compile_failed

echo.
echo [OK] Build completed.
echo "%OUT_EXE%"
exit /b 0

:missing_source
echo [ERROR] Source file not found:
echo "%SRC%"
exit /b 1

:missing_core
echo [ERROR] Core source file not found:
echo "%CORE%"
exit /b 1

:missing_envcore
echo [ERROR] Environment core source file not found:
echo "%ENVCORE%"
exit /b 1

:missing_hwcore
echo [ERROR] Hardware capability core source file not found:
echo "%HWCORE%"
exit /b 1

:missing_statecore
echo [ERROR] Bridge state-capture source file not found:
echo "%STATECORE%"
exit /b 1

:missing_taskcore
echo [ERROR] Bridge task coordinator source file not found:
echo "%TASKCORE%"
exit /b 1

:missing_requestcore
echo [ERROR] Bridge request-preparation source file not found:
echo "%REQUESTCORE%"
exit /b 1

:missing_mediacore
echo [ERROR] Media probe core source file not found:
echo "%MEDIACORE%"
exit /b 1

:missing_av1inspectcore
echo [ERROR] AV1 grain inspect core source file not found:
echo "%AV1INSPECTCORE%"
exit /b 1

:mkdir_failed
echo [ERROR] Failed to create output directory:
echo "%OUT_DIR%"
exit /b 1

:compile_failed
echo.
echo [ERROR] Build failed.
exit /b 1
