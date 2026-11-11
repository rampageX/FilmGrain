# One source of truth for the Native-only release layout.
# Archive Legacy tools for compatibility; do not load them as release/runtime helpers.
$RequiredPackageFiles = @(
    '.gitattributes',
    'CHANGELOG.md',
    'FilmGrain_Config.default.ini',
    'Presets/Initial/01.fgspreset.json',
    'Presets/Initial/02.fgspreset.json',
    'Presets/Initial/03.fgspreset.json',
    'Presets/Initial/04.fgspreset.json',
    'Presets/Initial/05.fgspreset.json',
    'LICENSE',
    'README.md',
    'README_FilmGrain_Studio.txt',
    'README_Toolkit.txt',
    'STABLE_BASELINE.txt',
    'dotnet\build_NET_Preview.bat',
    'dotnet\BUILD_NET_FRAMEWORK.md',
    'dotnet\BuildTools\FrameworkApiAudit.cs',
    'dotnet\BuildTools\UIStartupAudit.cs',
    'dotnet\BuildTools\UIAuditProcess.cs',
    'dotnet\BuildTools\Test_UI_Startup.ps1',
    'dotnet\BuildTools\Test_Framework_Api.ps1',
    'dotnet\BuildTools\Mono.Cecil.dll',
    'dotnet\BuildTools\LICENSE_Mono.Cecil.txt',
    'dotnet\BuildTools\build_verified.py',
    'Utils\FGS.ico',
    'Utils\_FilmGrainSimplified\FilmGrainSimplified.hook.template',
    'Lang\zh-CN.ini',
    'Lang\en-US.ini',
    '_AV1_Grain_Tables\Netflix_FGS_Dune2_MOD_E3.txt',
    '_AV1_Grain_Tables\Netflix_FGS_Dune2_Orig.txt',
    '_OpenSVPFlow\Plugins\open-svpflow-version.txt',
    '_OpenSVPFlow\Plugins\svpflow1_vs.dll',
    '_OpenSVPFlow\Plugins\svpflow2_vs.dll',
    '_OpenSVPFlow\Plugins\BUNDLED_DLL_SHA256.txt',
    '_OpenSVPFlow\UPGRADE_GUIDE.md',
    'Utils\_FilmGrainSimplified\NoiseFallback_512x512_RGBA8.rgba',
    'dotnet\FilmGrain_Av1GrainInspectCore.cs',
    'dotnet\FilmGrain_Av1GrainTableSelectionCore.cs',
    'dotnet\FilmGrain_BitrateRecommendationCore.cs',
    'dotnet\FilmGrain_BridgeEnvironmentCore.cs',
    'dotnet\FilmGrain_BridgeExecutionCore.cs',
    'dotnet\FilmGrain_BridgeRequestPreparationCore.cs',
    'dotnet\FilmGrain_BridgeStateCapture.cs',
    'dotnet\FilmGrain_Preprocessing.cs',
    'dotnet\FilmGrain_QueueHistory.cs',
    'dotnet\FilmGrain_PresetCore.cs',
    'dotnet\FilmGrain_PresetUI.cs',
    'dotnet\FilmGrain_BridgeTaskCoordinator.cs',
    'dotnet\FilmGrain_ConfigCore.cs',
    'dotnet\FilmGrain_ConfigUtility.cs',
    'dotnet\FilmGrain_FgsimTextureCore.cs',
    'dotnet\FilmGrain_GpuAdvancedPreview.cs',
    'dotnet\FilmGrain_GrainFileCatalogCore.cs',
    'dotnet\FilmGrain_HardwareCapabilityCore.cs',
    'dotnet\FilmGrain_InterpolationFrameRateCore.cs',
    'dotnet\FilmGrain_LanguagePack.cs',
    'dotnet\FilmGrain_LutCatalogCore.cs',
    'dotnet\FilmGrain_FilmLutCore.cs',
    'dotnet\FilmGrain_MediaProbeCore.cs',
    'dotnet\FilmGrain_MediaSummaryCore.cs',
    'dotnet\FilmGrain_NativeArchiveCore.cs',
    'dotnet\FilmGrain_NativeAv1Core.cs',
    'dotnet\FilmGrain_NativeAv1GrainReplaceCore.cs',
    'dotnet\FilmGrain_NativeGrainPreviewForm.cs',
    'dotnet\FilmGrain_NativeColorCorrectionForm.cs',
    'dotnet\FilmGrain_NativeHevcCore.cs',
    'dotnet\FilmGrain_NativeLibraryTools.cs',
    'dotnet\FilmGrain_NativeLutGalleryForm.cs',
    'dotnet\FilmGrain_NativeLutRecords.cs',
    'dotnet\FilmGrain_NativeSetupCore.cs',
    'dotnet\FilmGrain_NativeStartupCore.cs',
    'dotnet\FilmGrain_NativeSubtitlePrepareCore.cs',
    'dotnet\FilmGrain_NativeX264DigitalGrainCore.cs',
    'dotnet\FilmGrain_NetworkProxyCore.cs',
    'dotnet\FilmGrain_SettingsDialogs.cs',
    'dotnet\FilmGrain_ResourceMonitor.cs',
    'dotnet\FilmGrain_SetupPhase1.cs',
    'dotnet\FilmGrain_SharedHdrPreprocessCore.cs',
    'dotnet\FilmGrain_SharedPipeTimelineCore.cs',
    'dotnet\FilmGrain_Studio_NET_Preview.cs',
    'dotnet\FilmGrain_SubtitleCore.cs',
    'dotnet\FilmGrain_SubtitleDialog.cs',
    'dotnet\FilmGrain_WorkspacePreflightCore.cs',
    '_LUT_Tools\FilmLut\Profiles\Vision3_50D_2383.cube',
    '_LUT_Tools\FilmLut\Profiles\Vision3_250D_2383.cube',
    '_LUT_Tools\FilmLut\Profiles\Vision3_500T_2383.cube',
    '_LUT_Tools\FilmLut\Profiles\Vision3_500T_2393.cube',
    '_LUT_Tools\FilmLut\Profiles\Eterna500_3513DI.cube',
    '_LUT_Tools\FilmLut\profiles.json',
    '_LUT_Tools\FilmLut\LICENSE_SpectralFilmLUT.txt',
    '_LUT_Tools\FilmLut\SOURCE_ATTRIBUTION.txt',
    'Legacy\FilmGrain_Universal_CLI.bat',
    'Legacy\FilmGrain_Universal_GUI.bat',
    'Legacy\Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat',
    'Legacy\Utils\AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat',
    'Legacy\Utils\Collect_BT709_LUTs_Conservative.bat',
    'Legacy\Utils\FGS_Benchmark.cmd',
    'Legacy\Utils\FGS_Benchmark.ps1',
    'Legacy\Utils\FilmGrain_AV1_FinalizeName.ps1',
    'Legacy\Utils\FilmGrain_ColorCorrection.ps1',
    'Legacy\Utils\FilmGrain_Config.ps1',
    'Legacy\Utils\FilmGrain_Config_Load.bat',
    'Legacy\Utils\FilmGrain_Hardware_Caps.ps1',
    'Legacy\Utils\FilmGrain_InterpolationRate.ps1',
    'Legacy\Utils\FilmGrain_Language.ps1',
    'Legacy\Utils\FilmGrain_MOV_to_HEVC_Lossless_Cache.bat',
    'Legacy\Utils\FilmGrain_Studio.ps1',
    'Legacy\Utils\FilmGrain_Studio_Launcher.vbs',
    'Legacy\Utils\FilmGrain_Subtitle_Prepare.ps1',
    'Legacy\Utils\FilmGrain_Temp_Check.ps1',
    'Legacy\Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat',
    'Legacy\Utils\LUT_Preview_Batch_Gallery.bat',
    'Legacy\Utils\_FilmGrainSimplified\Prepare_FGSIM.ps1',
    'Legacy\_LUT_Tools\LUT_Gallery_Selector.ps1',
    'Legacy\_LUT_Tools\LUT_Preview_Batch_Gallery.ps1',
    'Legacy\_OpenSVPFlow\00_Setup.bat',
    'Legacy\_OpenSVPFlow\01_Update_OpenSVPFlow.bat',
    'Legacy\_OpenSVPFlow\Setup_OpenSVPFlow.ps1',
    'Legacy\_OpenSVPFlow\Update_OpenSVPFlow.ps1',
    'Legacy\dotnet\Setup_Dependencies.ps1',
    '_OpenSVPFlow\FilmGrain_OpenSVPFlow.vpy',
    '_OpenSVPFlow\Check_OpenSVPFlow.vpy'
)
$RequiredPackageDirectories = @(
    'images',
    'dotnet',
    'dotnet/BuildTools',
    'experiments/GpuSingleFrame',
    'Lang',
    'Utils',
    '_AV1_Grain_Tables',
    '_LUT_Tools',
    '_OpenSVPFlow',
    'Legacy'
)
$ReleaseSourceItems = @(
    'AGENTS.md',
    'images',
    'Lang',
    'Utils',
    '_AV1_Grain_Tables',
    '_LUT_Tools',
    '_OpenSVPFlow',
    'Legacy',
    '.gitattributes',
    '.gitignore',
    'CHANGELOG.md',
    'FilmGrain_Config.default.ini',
    'Presets/Initial/01.fgspreset.json',
    'Presets/Initial/02.fgspreset.json',
    'Presets/Initial/03.fgspreset.json',
    'Presets/Initial/04.fgspreset.json',
    'Presets/Initial/05.fgspreset.json',
    'LICENSE',
    'README.md',
    'README_FilmGrain_Studio.txt',
    'README_Toolkit.txt',
    'STABLE_BASELINE.txt',
    'release.bat',
    '.github/release/ACCEPTANCE_v5.0.33.md',
    'dotnet/*.cs',
    'dotnet/BuildTools',
    'experiments/GpuSingleFrame',
    'Tools/FilmLut',
    'dotnet/BUILD_NET_FRAMEWORK.md',
    'dotnet/build_NET_Preview.bat',
    '.github/release/version.txt',
    '.github/release/RELEASE_NOTES.md',
    '.github/release/build_release.ps1',
    '.github/release/sync_native_version.ps1',
    '.github/release/release_core.bat',
    '.github/release/release_layout.ps1',
    '.github/release/stage_working_tree.ps1',
    '.github/workflows/release.yml'
)
$RetiredSourceFiles = @(
    'FilmGrain_Universal_CLI.bat',
    'FilmGrain_Universal_GUI.bat',
    'Utils/AV1_FilmGrain_Bake_for_Social_Upload.bat',
    'Utils/AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat',
    'Utils/Collect_BT709_LUTs_Conservative.bat',
    'Utils/FGS_Benchmark.cmd',
    'Utils/FGS_Benchmark.ps1',
    'Utils/FilmGrain_AV1_FinalizeName.ps1',
    'Utils/FilmGrain_ColorCorrection.ps1',
    'Utils/FilmGrain_Config.ps1',
    'Utils/FilmGrain_Config_Load.bat',
    'Utils/FilmGrain_Hardware_Caps.ps1',
    'Utils/FilmGrain_InterpolationRate.ps1',
    'Utils/FilmGrain_Language.ps1',
    'Utils/FilmGrain_MOV_to_HEVC_Lossless_Cache.bat',
    'Utils/FilmGrain_Studio.ps1',
    'Utils/FilmGrain_Studio_Launcher.vbs',
    'Utils/FilmGrain_Subtitle_Prepare.ps1',
    'Utils/FilmGrain_Temp_Check.ps1',
    'Utils/FilmGrain_Universal_HEVC_AV1_StudioBridge.bat',
    'Utils/LUT_Preview_Batch_Gallery.bat',
    'Utils/_FilmGrainSimplified/Prepare_FGSIM.ps1',
    '_LUT_Tools/LUT_Gallery_Selector.ps1',
    '_LUT_Tools/LUT_Preview_Batch_Gallery.ps1',
    '_OpenSVPFlow/00_Setup.bat',
    '_OpenSVPFlow/01_Update_OpenSVPFlow.bat',
    '_OpenSVPFlow/Setup_OpenSVPFlow.ps1',
    '_OpenSVPFlow/Update_OpenSVPFlow.ps1',
    'dotnet/Setup_Dependencies.ps1'
)
$ExcludedSourcePaths = @(
    'Verify_Preprocessing_K7M4VX',
    'Tests_LutGallery_M8Q4VP',
    'Tests_LutGallery_C6R9WN',
    'Tests_MediaQueue_Q6M8VN',
    'Tests_MediaQueue_R8K3WP',
    'Tests_MediaQueue_V9N4QK',
    'Tests_MediaQueue_P7X4MN',
    'Tests_Timeline_T7M4QX',
    'WORKING_SOURCE_INFO.txt',
    'Tests_UserPresetPath_P2M8VN',
    'Test_UserPresetPath_P2M8VN.cmd',
    'Tests_Cleanup_CPU_Encoder',
    'Tests_EncoderUI_C4M8VN',
    'Test_Encoder_UI_C4M8VN.cmd',
    'Tests_CPU_C7M4VN',
    'Test_CPU_C7M4VN.cmd',
    'Tests_Presets_R8M3VN',
    'Tests_Cleanup_Presets_R8M3VN',
    'FilmGrain_Config.ini',
    'Utils/_HardwareCaps.json',
    'Utils/_HardwareCaps_NET.json',
    'Utils/_HardwareCaps_Startup.json',
    'Utils/NVEncC_Interpolation.cache.json',
    'Utils/_FilmGrainSimplified/Generated',
    'Logs',
    'Tests_Shared',
    'Tests_Queue_Q8M4VN',
    'Tests_Black_V7K3MN',
    'Tests_Cleanup_Q8M4VN',
    'Tests_CQ_N7V4PK',
    'Tests_Tooltip_Q7M3VN',
    'Tests_Cleanup_CQ_N7V4PK',
    'docs/CQ_AUDIT_N7V4PK.md',
    'docs/CQ_MANIFEST_N7V4PK.json',
    'docs/CQ_CUMULATIVE_N7V4PK.patch',
    'dotnet/BuildTools/UiAudit',
    'benchmark',
    '_Dependencies',
    '_OpenSVPFlow/_UserConfig',
    '_OpenSVPFlow/_PluginBackup',
    '_LUT_Tools/LUT_Reference_Current.jpg',
    'Lang/FilmGrain_Language.ini',
    'Legacy/UserPreserved',
    'Legacy/Utils/_HardwareCaps.json',
    'Legacy/Utils/_HardwareCaps_NET.json',
    'dotnet/build',
    'FilmGrain_Studio.exe'
)
$ExcludedPackagePaths = @(
    'Verify_Preprocessing_K7M4VX',
    'Tests_LutGallery_M8Q4VP',
    'Tests_LutGallery_C6R9WN',
    'Tests_MediaQueue_Q6M8VN',
    'Tests_MediaQueue_R8K3WP',
    'Tests_MediaQueue_V9N4QK',
    'Tests_MediaQueue_P7X4MN',
    'Tests_Timeline_T7M4QX',
    'WORKING_SOURCE_INFO.txt',
    'Tests_UserPresetPath_P2M8VN',
    'Test_UserPresetPath_P2M8VN.cmd',
    'Tests_EncoderUI_C4M8VN',
    'Test_Encoder_UI_C4M8VN.cmd',
    'Tests_CPU_C7M4VN',
    'Test_CPU_C7M4VN.cmd',
    'Clean_Preset_Tests.cmd',
    'Tests_Presets_R8M3VN',
    'Tests_Cleanup_Presets_R8M3VN',
    '.github',
    '.gitignore',
    'release.bat',
    'Clean_Queue_Tests.cmd',
    'Clean_CQ_Tests.cmd',
    'FilmGrain_Config.ini',
    'Utils/_HardwareCaps.json',
    'Utils/_HardwareCaps_NET.json',
    'Utils/_HardwareCaps_Startup.json',
    'Utils/NVEncC_Interpolation.cache.json',
    'Utils/_FilmGrainSimplified/Generated',
    'Logs',
    'Tests_Shared',
    'Tests_Queue_Q8M4VN',
    'Tests_Black_V7K3MN',
    'Tests_Cleanup_Q8M4VN',
    'Tests_CQ_N7V4PK',
    'Tests_Tooltip_Q7M3VN',
    'Tests_Cleanup_CQ_N7V4PK',
    'docs/CQ_AUDIT_N7V4PK.md',
    'docs/CQ_MANIFEST_N7V4PK.json',
    'docs/CQ_CUMULATIVE_N7V4PK.patch',
    'dotnet/BuildTools/UiAudit',
    'benchmark',
    '_Dependencies',
    '_OpenSVPFlow/_UserConfig',
    '_OpenSVPFlow/_PluginBackup',
    '_LUT_Tools/LUT_Reference_Current.jpg',
    'Lang/FilmGrain_Language.ini',
    'Legacy/UserPreserved',
    'Legacy/Utils/_HardwareCaps.json',
    'Legacy/Utils/_HardwareCaps_NET.json'
)
$ExcludedRecursiveDirectoryNames = @(
    'UserPresets',
    '.env',
    '.venv',
    '__pycache__'
)
$ExcludedRootDeliveryPatterns = @(
    'Build_Check_PresetCompat_*',
    'Launch_PresetCompat_*',
    'Test_PresetCompat_*',
    'PRESET_COMPAT_*',
    'Build_Check_QuickSettings_*',
    'Launch_QuickSettings_*',
    'Test_QuickSettings_*',
    'QUICK_SETTINGS_*',
    'Build_Check_WindowPaint_*',
    'Launch_WindowPaint_*',
    'WindowPaint_AuditHost_*',
    'WINDOW_PAINT_AUDIT_*',
    'Build_Check_SvpPaint_*',
    'Launch_SvpPaint_*',
    'SvpPaint_AuditHost_*',
    'SVP_PAINT_*',
    'Clean_Release_UI_*',
    'RELEASE_PREP_5.0.56_*',
    'FGS_v5.0.56_Release_Prepare_*',

    'Build_Check_FirstPaint_*',
    'Launch_FirstPaint_*',
    'Test_FirstPaint_*',
    'FIRST_PAINT_*',
    'FirstPaint_AuditHost_*',
    'Clean_FirstPaint_*',
    'FGS_Advanced_FirstPaint_*.zip',
    'FGS_Advanced_DropdownWidth_*.zip',
    'RELEASE_PREP_5.0.55_*',
    'Build_Check_SvpLayout_*',
    'Launch_SvpLayout_*',
    'Test_SvpLayout_*',
    'SVP_LAYOUT_*',
    'Clean_SvpLayout_*',
    'FGS_Interpolation_Layout_*.zip',
    'RELEASE_PREP_5.0.54_*',
    'Build_Check_DarkPrompts_*',
    'Launch_DarkPrompts_*',
    'Test_DarkPrompts_*',
    'Preview_LongPrompt_*',
    'DARK_PROMPTS_*',
    'Clean_DarkPrompts_*',
    'RELEASE_PREP_5.0.53_*',
    'FGS_DarkPrompts_*.zip',
    'Build_Run_NVEncC_*',
    'Launch_NVEncC_*',
    'Test_NVEncC_*',
    'NVEncC_SDR_*_说明.md',
    'NVEncC_Interpolation.cache.json',
    'FGS_NVEncC_*.zip',
    'Build_Status_*.cmd',
    'Launch_Status_*.ps1',
    'Clean_NVEncC_*.ps1',
    'RELEASE_PREP_5.0.52_*.md',
    'APPLY_SVT_HDR_*.txt',
    'FILES_SVT_HDR_*.json',
    'APPLY_X265_HDR_*.txt',
    'FILES_X265_HDR_*.json',
    'PATH_AUDIT_*.txt',
    'FGS_SVT_HDR_*.zip',
    'FGS_X265_HDR_*.zip',
    'APPLY_RELEASE_*.txt',
    'FILES_RELEASE_*.json',
    'FGS_v5.0.49_*.zip',
    'FGS_v5.0.50_*.zip',
    'FGS_v5.0.51_*.zip',
    'APPLY_HDR_F4M8QX.txt',
    'FGS_v5.0.48_*.zip',
    'FGS_v5.0.45_ReleasePrep_*.zip',
    'FGS_v5.0.45_StartupListFix_*.zip',
    'FGS_Flicker_*.zip',
    'FGS_SelectedGPU_Test_*_Logs.zip',
    'FGS_Vulkan_SelectedGPU_*.zip',
    'FGS_StatusGroups_SelectedGPU_*.zip',
    'FGS_StatusCompact_SelectedGPU_*.zip',
    'FGS_RemoveX264Probes_*.zip',
    'Test_Flicker_*.cmd',
    'Start_FGS_Flicker_*.cmd',
    'Test_Selected_GPU_*.cmd',
    'FGS_Av1CopyGuard_*.zip',
    'TEST_Av1CopyGuard_*.txt',
    'FGS_QueueRecord_*.zip',
    'TEST_QueueRecord_*.txt',
    'FGS_QueueHints_Timer_*.zip',
    'TEST_QueueHints_Timer_*.txt',
    'FGS_QueueRestore_Fix_*.zip',
    'TEST_QueueRestore_*.txt',
    'Clean_QueueRestore_*.cmd',
    'Clean_Queue44_*.cmd',
    'FGS_v5.0.44_ReleasePrep_*.zip',
    'Test_Resize_*.cmd',
    'Return_Resize_*',
    'FGS_Resize_*.zip',
    'FGS_Preprocess_Width_*.zip',
    'Resize_Evidence_*',
    'Resize_Manifest_*.json',
    'TEST_Resize_*.md',
    'TEST_UI_R3_*.txt',
    'TEST_Width_R4_*.txt',
    'Clean_Resize_*.cmd',
    'FGS_v5.0.43_ReleasePrep_*.zip',
    'Test_Preprocessing_*.cmd',
    'FGS_Preprocessing_*.zip',
    'FGS_v5.0.42_ReleasePrep_*.zip',
    'Clean_Preprocessing_*.cmd',
    'Test_TableScale_*.cmd',
    'Test_Av1Preview_*.cmd',
    'Test_PresetScale_*.cmd',
    'TableScale_Fixture_*.tbl',
    'TableScale_*_说明.md',
    'AV1_颗粒审计_*.md',
    'PresetScale_来源_*.md',
    'PresetScale_测试说明_*.md',
    'FGS_TableScale_*.zip',
    'FGS_Av1Preview_*.zip',
    'FGS_PresetScale_*.zip',
    'Clean_Tests_v5.0.40_*.cmd',
    'FGS_v5.0.40_ReleasePrep_*.zip',
    'FGS_BakeBitrate_*.zip',
    'README_BakeBitrate_*.txt',
    'CHANGELOG_BakeBitrate_*.txt',
    'FGS_v5.0.41_ReleasePrep_*.zip',
    'Clean_Tests_v5.0.39.cmd',
    'Test_EncodingMonitor_*.cmd',
    'MONITOR_PREVIEW_*.md',
    'FGS_EncodingMonitor*.zip',
    'Clean_Tests_v5.0.39_*.cmd',
    'FGS_v5.0.39_ReleasePrep_*.zip',
    'Build_GeneratorLinks_K7M4VX.cmd',
    'FGS_GeneratorLinks_K7M4VX.zip',
    'Clean_Tests_v5.0.38_N6R8VX.cmd',
    'FGS_v5.0.38_ReleasePrep_N6R8VX.zip',
    'Test_NoGrainCache_R8M4VN.cmd',
    'Test_GrainPreview_K6N9VX.cmd',
    'FGS_GrainCache_Benchmark_G7M4VN.cmd',
    'R8M4VN_说明.txt',
    'K6N9VX_说明.txt',
    'Return_NoGrainCache_R8M4VN_*.zip',
    'Return_GrainPreview_K6N9VX_*.zip',
    'Return_GrainCache_G7M4VN_*.zip',
    'FGS_NoGrainCache_R8M4VN*.zip',
    'FGS_GrainPreview_K6N9VX*.zip',
    'FGS_v5.0.37_ReleasePrep_*.zip',
    'Clean_Tests_v5.0.37_M8Q4VN.cmd',
    'v5.0.37_发布说明.txt',
    'FGS_v5.0.33_ReleasePrep_*.zip',
    'FGS_UIRefresh_*.zip',
    'FGS_AutoLanguage_*.zip',
    'FGS_LutGalleryPaint_*.zip',
    'FGS_MainFirstPaint_*.zip',
    'UIRefresh_*.txt',
    'AutoLanguage_*.txt',
    'LutGalleryPaint_*.txt',
    'MainFirstPaint_*.txt',

    'FGS_v5.0.32_ReleasePrep_*.zip',
    'FGS_QueueReset_*.zip',
    'FGS_TaskbarProgress_*.zip',
    'QueueReset_*.txt',
    'TaskbarProgress_*.txt',
    'Test_LutGallery_*.bat',
    'Return_LutGallery_*.zip',
    '__FGS_LutGallery_Audit_*.exe',
    'FGS_v5.0.30_Lut*FIX_*.zip',
    'FGS_v5.0.31_ReleasePrep_*.zip',
    'Test_MediaQueue_*.bat',
    'Return_MediaQueue_*.zip',
    '__FGS_MediaQueue_Audit_*.exe',
    'FGS_v5.0.29_*FIX_*.zip',
    'FGS_v5.0.30_ReleasePrep_*.zip',
    'Clean_ReleasePrep_*.ps1',
    'Test_Timeline_T7M4QX*.bat',
    'Test_ProductionTimeline_R2_Q4N8VK.bat',
    'Return_Timeline_T7M4QX_*.zip',
    'Return_ProductionTimeline_R2_Q4N8VK_*.zip',
    'FGS_Timeline_*.zip',
    'FGS_v5.0.29_ReleasePrep_*.zip',
    'Clean_Timeline_ReleasePrep_*.ps1',
    'Return_UserPresetPath_P2M8VN_*.zip',
    'Return_CPU_C7M4VN_*.zip',
    'Return_EncoderUI_C4M8VN_*.zip',
    'Return_Presets_R8M3VN_*.zip',
    'Return_CQ_N7V4PK_*.zip',
    'Return_Queue_Q8M4VN_*.zip',
    'Return_Black_V7K3MN_*.zip',
    'Start_HDRMerge_Auto_*.cmd',
    'PatchManifest_*.json',
    'PATCH_MANIFEST_*.json',
    'TEST_BUILD_*.txt',
    '迁移与测试_*.md',
    '测试说明_*.md',
    '审查与改动_*.md',
    '更新与测试_*.md',
    '启动说明_NET_*.md',
    '验收与发布交接_*.md',
    '发布准备与测试_*.md',
    'SHA256_*.txt',
    'FilmGrain_Studio_*.zip',
    'FilmGrain_Studio_*.zip.sha256'
)

$ExcludedPackagePaths += $RetiredSourceFiles

function Invoke-ReleaseGitUtf8 {
    param([string]$Root, [string]$GitArguments)
    # Decode redirected Git streams explicitly, independent of PowerShell 5.1
    # and the console's OEM/ANSI code page. No console encoding is changed.
    if ($Root.IndexOf('"') -ge 0) { throw 'Invalid repository path.' }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = 'git'
    $start.Arguments = '-C "' + [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + '" -c core.quotepath=false ' + $GitArguments
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $text = $process.StandardOutput.ReadToEnd()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw ('Git query failed: ' + $errorText.Trim()) }
        return $text
    } finally { $process.Dispose() }
}

function Get-ReleaseGitTopLevel {
    param([string]$Root)
    $text = Invoke-ReleaseGitUtf8 -Root $Root -GitArguments 'rev-parse --show-toplevel'
    $top = $text.Trim([char]13, [char]10)
    if ([string]::IsNullOrEmpty($top) -or $top.IndexOf([char]13) -ge 0 -or $top.IndexOf([char]10) -ge 0) { throw 'Invalid Git top-level response.' }
    return $top
}

function Get-ReleaseTrackedPaths {
    param([string]$Root, [switch]$IncludeUntracked)
    $arguments = 'ls-files -z'
    if ($IncludeUntracked) { $arguments += ' --cached --others --exclude-standard' }
    $text = Invoke-ReleaseGitUtf8 -Root $Root -GitArguments $arguments
    return @($text.Split([char]0) | Where-Object { $_.Length -gt 0 })
}


# Known temporary artifacts from the 2026-11-03 AV1/HDR work.
$CurrentAv1HdrTestArtifacts = @(
    'APPLY_N7Q4VX.txt',
    'APPLY_RELEASE_K9P6MX.txt',
    'APPLY_RELEASE_P9M4VX.txt',
    'APPLY_S2V8MX.txt',
    'Check_AV1_Consistency_R4_P8N6VX.cmd',
    'Clean_Tests_v5.0.48_K9P6MX.cmd',
    'Clean_Tests_v5.0.48_K9P6MX.ps1',
    'FILES_N7Q4VX.json',
    'FILES_RELEASE_K9P6MX.json',
    'FILES_RELEASE_P9M4VX.json',
    'FILES_S2V8MX.json',
    'HDR_Investigation_N6V9QX.md',
    'MERGE_T8M6QX.json',
    'MERGE_T8M6QX.txt',
    'PACKAGE_N6V9QX.json',
    'PACKAGE_Q8M6VX.json',
    'Probe_H8N5VX.ps1',
    'Probe_K7M4VX.ps1',
    'Probe_P6R9VX.ps1',
    'R6_AUDIT_Q8M6VX.md',
    'R6_OBU_Audit_Q8M6VX.json',
    'README.txt',
    'README_R8_M4T9VX.txt',
    'README_R9_V6K2MX.txt',
    'Run_20261103_094405_430_5a4ac6',
    'Run_20261103_094405_430_5a4ac6_Return.zip',
    'Run_20261103_100439_926_9c909f',
    'Run_20261103_100439_926_9c909f_Return.zip',
    'Run_20261103_101456_492_293cbb',
    'Run_20261103_101456_492_293cbb_Return.zip',
    'Run_H8N5VX.cmd',
    'Run_HDR10_R5_20261103_123348_413_8af81c',
    'Run_HDR10_R5_20261103_123348_413_8af81c_Return.zip',
    'Run_HDR10_R5_V8Q4NX.cmd',
    'Run_HDR_Boundary_R7_20261103_140507_148_1a5007',
    'Run_HDR_Boundary_R7_20261103_140507_148_1a5007_Return.zip',
    'Run_HDR_Boundary_R7_Q8M6VX.cmd',
    'Run_HDR_Matrix_R6_20261103_134323_822_e11afe',
    'Run_HDR_Matrix_R6_20261103_134323_822_e11afe_Return.zip',
    'Run_HDR_Matrix_R6_M4N8VX.cmd',
    'Run_HDR_R8_20261103_143420_610_8a054e',
    'Run_HDR_R8_20261103_143420_610_8a054e_Return.zip',
    'Run_HDR_R8_N6V9QX.cmd',
    'Run_K7M4VX.cmd',
    'Run_NVENC_Build_R5_T8C5VX.cmd',
    'Run_NVENC_Build_R5_T8C5VX_20261103_223134_746_ee9f24',
    'Run_NVENC_Build_R5_T8C5VX_20261103_223134_746_ee9f24_Return.zip',
    'Run_NVENC_HDR_J8R5MX.cmd',
    'Run_NVENC_HDR_J8R5MX_20261103_220138_758_dda932',
    'Run_NVENC_HDR_J8R5MX_20261103_220138_758_dda932_Return.zip',
    'Run_NVENC_HDR_R2_K4V8NX.cmd',
    'Run_NVENC_HDR_R2_K4V8NX_20261103_220847_745_015e3e',
    'Run_NVENC_HDR_R2_K4V8NX_20261103_220847_745_015e3e_Return.zip',
    'Run_NVENC_HDR_R3_M6Q9VX.cmd',
    'Run_NVENC_HDR_R3_M6Q9VX_20261103_221326_723_ed0639',
    'Run_NVENC_HDR_R3_M6Q9VX_20261103_221326_723_ed0639_Return.zip',
    'Run_NVENC_HDR_R4_P7N5VX.cmd',
    'Run_NVENC_HDR_R4_P7N5VX_20261103_222159_653_c00afe',
    'Run_NVENC_HDR_R4_P7N5VX_20261103_222159_653_c00afe_Return.zip',
    'Run_NVENC_HDR_R6_N9K4VX.cmd',
    'Run_NVENC_HDR_R6_N9K4VX_20261103_224943_221_f3b945',
    'Run_NVENC_HDR_R6_N9K4VX_20261103_224943_221_f3b945_Return.zip',
    'Run_NVENC_HDR_R7_C8M5VX.cmd',
    'Run_NVENC_HDR_R7_C8M5VX_20261103_230555_115_b0e650',
    'Run_NVENC_HDR_R7_C8M5VX_20261103_230555_115_b0e650_Return.zip',
    'Run_NVENC_HDR_R8_M4T9VX.cmd',
    'Run_NVENC_HDR_R8_M4T9VX_20261103_231708_854_319ae5',
    'Run_NVENC_HDR_R8_M4T9VX_20261103_231708_854_319ae5_Return.zip',
    'Run_NVENC_HDR_R9_V6K2MX.cmd',
    'Run_NVENC_HDR_R9_V6K2MX_20261103_232511_977_4d2c7a',
    'Run_NVENC_HDR_R9_V6K2MX_20261103_232511_977_4d2c7a_Return.zip',
    'Run_P6R9VX.cmd',
    'START_HERE_N6V9QX.txt',
    'START_HERE_Q8M6VX.txt',
    'START_HERE_R6_M4N8VX.txt',
    'Tests_HDR10_R5_V8Q4NX',
    'Tests_HDR_Boundary_R7_Q8M6VX',
    'Tests_HDR_Matrix_R6_M4N8VX',
    'Tests_HDR_R8_N6V9QX',
    'Tests_NVENC_Build_R5_T8C5VX',
    'Tests_NVENC_HDR_J8R5MX',
    'Tests_NVENC_HDR_R2_K4V8NX',
    'Tests_NVENC_HDR_R3_M6Q9VX',
    'Tests_NVENC_HDR_R4_P7N5VX',
    'Tests_NVENC_HDR_R6_N9K4VX',
    'Tests_NVENC_HDR_R7_C8M5VX',
    'Tests_NVENC_HDR_R8_M4T9VX',
    'Tests_NVENC_HDR_R9_V6K2MX',
    'Tests_SVT_N6V8RX',
    'VALIDATION.txt',
    'VALIDATION_S2V8MX.md'
)
$ExcludedSourcePaths += $CurrentAv1HdrTestArtifacts
$ExcludedPackagePaths += $CurrentAv1HdrTestArtifacts

function Test-ExcludedSourcePath {
    param([string]$RelativePath)
    $n = $RelativePath.Replace('\', '/').Trim('/')
    if ($n -match '^(Results_GrainCache_G7M4VN_|Results_NoGrainCache_R8M4VN_|Results_GrainPreview_K6N9VX_)[^/]*(/|$)') { return $true }
    if ($n -match '(^|/)Presets/User(?:\.seed\.[^/]+)?(/|$)') { return $true }
    if ($n -match '(^|/)UserPresets(?:\.seed\.[^/]+)?(/|$)') { return $true }
    if ($n.StartsWith('Presets/', [StringComparison]::OrdinalIgnoreCase) -and $RequiredPackageFiles -notcontains $n) { return $true }
    foreach ($p in $ExcludedSourcePaths) {
        if ($n -eq $p -or $n.StartsWith($p + '/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    foreach ($p in $ExcludedRootDeliveryPatterns) { if ($n -notmatch '/' -and $n -like $p) { return $true } }
    foreach ($p in $ExcludedRecursiveDirectoryNames) {
        if ($n -match ('(^|/)' + [regex]::Escape($p) + '(/|$)')) { return $true }
    }
    return $false
}

# Existing repository reports remain in Git history, but never enter Stable
# packages or the release source staging allowlist.
function Test-RepositoryArchivePath {
    param([string]$RelativePath)
    $n = $RelativePath.Replace('\', '/').Trim('/')
    return $n -eq 'benchmark' -or $n.StartsWith('benchmark/', [StringComparison]::OrdinalIgnoreCase)
}

function Test-ForbiddenTrackedSourcePath {
    param([string]$RelativePath)
    return (Test-ExcludedSourcePath $RelativePath) -and -not (Test-RepositoryArchivePath $RelativePath)
}

function Copy-ReleaseWorkingTree {
    param([string]$SourceRoot, [string]$TargetRoot)
    # Called only with the isolated clone created by release_core.bat.
    $src = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\','/')
    $dst = [IO.Path]::GetFullPath($TargetRoot).TrimEnd('\','/')
    if ([string]::Equals($src, $dst, [StringComparison]::OrdinalIgnoreCase)) { throw 'Dry Run source equals target.' }
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $dst.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($dst) -notlike 'FGS_DryRun_Repo_*') { throw 'Unsafe Dry Run target.' }
    foreach ($rel in @($ReleaseSourceItems) + @($RetiredSourceFiles)) {
        if ($rel -eq 'dotnet/*.cs') {
            Get-ChildItem -LiteralPath (Join-Path $dst 'dotnet') -Filter '*.cs' -File -ErrorAction SilentlyContinue | Remove-Item -Force
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $src 'dotnet') -Filter '*.cs' -File) {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dst ('dotnet/' + $file.Name)) -Force
            }
            continue
        }
        $s = Join-Path $src $rel; $d = Join-Path $dst $rel
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
        if (Test-Path -LiteralPath $s) {
            $parent = Split-Path -Parent $d
            if (-not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent -Force) }
            Copy-Item -LiteralPath $s -Destination $d -Recurse -Force
        }
    }
    foreach ($rel in $ExcludedSourcePaths) {
        if (Test-RepositoryArchivePath $rel) { continue }
        $p = Join-Path $dst $rel
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
    foreach ($p in @(Get-ChildItem -LiteralPath $dst -Recurse -File -Force | Where-Object { $_.FullName -notlike (Join-Path $dst '.git/*') })) {
        $rel = $p.FullName.Substring($dst.Length + 1)
        if ((Test-ExcludedSourcePath $rel) -and -not (Test-RepositoryArchivePath $rel)) { Remove-Item -LiteralPath $p.FullName -Force }
    }
}

function Test-AllowedReleaseSourcePath {
    param([string]$RelativePath)
    $n = $RelativePath.Replace('\', '/').Trim('/')
    foreach ($item in @($ReleaseSourceItems) + @($RetiredSourceFiles)) {
        if ($item -eq 'dotnet/*.cs') {
            if ($n -match '^dotnet/[^/]+\.cs$') { return $true }
        } elseif ($n -eq $item) {
            return $true
        } elseif (($RequiredPackageDirectories -contains $item -or $item -eq 'Tools/FilmLut') -and $n.StartsWith($item + '/', [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Add-ReleaseSource {
    param([string]$Root)
    # Git supplies tracked files (including deletions) plus non-ignored new files.
    # Never pass ignored paths as exclude pathspecs to git add: Git for Windows
    # rejects those even when they were intended as exclusions.
    $paths = @(Get-ReleaseTrackedPaths -Root $Root -IncludeUntracked |
        Where-Object { (Test-AllowedReleaseSourcePath $_) -and -not (Test-ExcludedSourcePath $_) } |
        Sort-Object -Unique)
    # Use literal names and small batches to preserve special paths and avoid
    # the Windows process command-line length limit with large grain libraries.
    for ($i = 0; $i -lt $paths.Count; $i += 64) {
        $last = [Math]::Min($i + 63, $paths.Count - 1)
        $batch = @($paths[$i..$last])
        & git --literal-pathspecs -C $Root add -A -- @batch
        if ($LASTEXITCODE -ne 0) { throw 'Release source staging failed.' }
    }
    # Restore the whole report path in the index, including pre-staged deletions
    # which no longer appear in git ls-files. Working files are never changed.
    & git --literal-pathspecs -C $Root reset -q HEAD -- benchmark
    if ($LASTEXITCODE -ne 0) { throw 'Could not exclude repository report changes.' }
    # Exclude even files manually staged before release.bat was started.
    $tracked = @(Get-ReleaseTrackedPaths -Root $Root)
    $excluded = @($tracked | Where-Object { Test-ExcludedSourcePath $_ })
    if ($excluded.Count) {
        & git -C $Root reset -q HEAD -- @excluded
        if ($LASTEXITCODE -ne 0) { throw 'Could not unstage local-only files.' }
    }
    $tracked = @(Get-ReleaseTrackedPaths -Root $Root)
    $bad = @($tracked | Where-Object { Test-ForbiddenTrackedSourcePath $_ })
    if ($bad.Count) { throw ('Local-only files already tracked in HEAD; remove them from Git before release: ' + ($bad -join ', ')) }
    $required = @($RequiredPackageFiles | ForEach-Object { $_.Replace('\', '/') })
    $required += @($ReleaseSourceItems | Where-Object { $_ -like '.github/*' -or $_ -eq 'release.bat' })
    $missing = @($required | Where-Object { $tracked -notcontains $_ })
    if ($missing.Count) { throw ('Required release source not staged (possibly ignored): ' + ($missing -join ', ')) }
}
