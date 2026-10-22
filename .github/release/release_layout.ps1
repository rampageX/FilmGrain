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
    '_AV1_Grain_Tables\Netflix_FGS_Dune2.txt',
    '_AV1_Grain_Tables\Netflix_FGS_Dune2_Strength75.txt',
    '_OpenSVPFlow\Plugins\open-svpflow-version.txt',
    '_OpenSVPFlow\Plugins\svpflow1_vs.dll',
    '_OpenSVPFlow\Plugins\svpflow2_vs.dll',
    'Utils\_FilmGrainSimplified\NoiseFallback_512x512_RGBA8.rgba',
    'dotnet\FilmGrain_Av1GrainInspectCore.cs',
    'dotnet\FilmGrain_Av1GrainTableSelectionCore.cs',
    'dotnet\FilmGrain_BitrateRecommendationCore.cs',
    'dotnet\FilmGrain_BridgeEnvironmentCore.cs',
    'dotnet\FilmGrain_BridgeExecutionCore.cs',
    'dotnet\FilmGrain_BridgeRequestPreparationCore.cs',
    'dotnet\FilmGrain_BridgeStateCapture.cs',
    'dotnet\FilmGrain_PresetCore.cs',
    'dotnet\FilmGrain_PresetUI.cs',
    'dotnet\FilmGrain_BridgeTaskCoordinator.cs',
    'dotnet\FilmGrain_ConfigCore.cs',
    'dotnet\FilmGrain_ConfigUtility.cs',
    'dotnet\FilmGrain_FgsimTextureCore.cs',
    'dotnet\FilmGrain_GrainFileCatalogCore.cs',
    'dotnet\FilmGrain_HardwareCapabilityCore.cs',
    'dotnet\FilmGrain_InterpolationFrameRateCore.cs',
    'dotnet\FilmGrain_LanguagePack.cs',
    'dotnet\FilmGrain_LutCatalogCore.cs',
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
    'Lang',
    'Utils',
    '_AV1_Grain_Tables',
    '_LUT_Tools',
    '_OpenSVPFlow',
    'Legacy'
)
$ReleaseSourceItems = @(
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
    'Clean_Preset_Tests.cmd',
    '.github/release/clean_preset_tests.ps1',
    '.github/release/ACCEPTANCE_v5.0.25.md',
    'Clean_Queue_Tests.cmd',
    'Clean_CQ_Tests.cmd',
    '.github/release/clean_queue_tests.ps1',
    '.github/release/clean_cq_tests.ps1',
    '.github/release/ACCEPTANCE_v5.0.21.md',
    '.github/release/ACCEPTANCE_v5.0.20.md',
    '.github/release/CQ_AUDIT_v5.0.20.md',
    '.github/release/ACCEPTANCE_v5.0.19.md',
    'dotnet/*.cs',
    'dotnet/BuildTools',
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
    'Tests_Presets_R8M3VN',
    'Tests_Cleanup_Presets_R8M3VN',
    'FilmGrain_Config.ini',
    'Utils/_HardwareCaps.json',
    'Utils/_HardwareCaps_NET.json',
    'Utils/_HardwareCaps_Startup.json',
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

function Test-ExcludedSourcePath {
    param([string]$RelativePath)
    $n = $RelativePath.Replace('\', '/').Trim('/')
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
        } elseif ($RequiredPackageDirectories -contains $item -and $n.StartsWith($item + '/', [StringComparison]::OrdinalIgnoreCase)) {
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
