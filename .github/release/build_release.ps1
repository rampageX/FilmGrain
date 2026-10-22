[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+(?:\.\d+){2,3}$')]
    [string]$Version,

    [Parameter()]
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,

    [Parameter()]
    [string]$OutputDirectory = '',

    [Parameter()]
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$CrLf = [char]13 + [char]10
$Utf8Bom = New-Object System.Text.UTF8Encoding($true)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$Utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)

. (Join-Path $PSScriptRoot 'release_layout.ps1')

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Assert-File {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file missing: $RelativePath"
    }
}

function Assert-Directory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required directory missing: $RelativePath"
    }
}

function Convert-ByteNewlinesToCrLf {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $stream = New-Object System.IO.MemoryStream
    try {
        for ($i = 0; $i -lt $Bytes.Length; $i++) {
            $b = $Bytes[$i]
            if ($b -eq 13) {
                if (($i + 1) -lt $Bytes.Length -and $Bytes[$i + 1] -eq 10) {
                    $i++
                }
                $stream.WriteByte(13)
                $stream.WriteByte(10)
                continue
            }
            if ($b -eq 10) {
                $stream.WriteByte(13)
                $stream.WriteByte(10)
                continue
            }
            $stream.WriteByte($b)
        }
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Read-LangKeys {
    param([Parameter(Mandatory = $true)][string]$Path)

    $keys = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)) {
        $trim = $line.Trim()
        if (-not $trim -or $trim.StartsWith('#') -or $trim.StartsWith(';')) {
            continue
        }
        $eq = $line.IndexOf('=')
        if ($eq -gt 0) {
            $keys[$line.Substring(0, $eq).Trim()] = $true
        }
    }
    return $keys
}

function Test-ReleaseMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion
    )

    $versionFile = Join-Path $Root '.github\release\version.txt'
    $notesFile = Join-Path $Root '.github\release\RELEASE_NOTES.md'
    $baselineFile = Join-Path $Root 'STABLE_BASELINE.txt'
    $changeFile = Join-Path $Root 'CHANGELOG.md'

    foreach ($path in @($versionFile, $notesFile, $baselineFile, $changeFile)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release metadata file missing: $path"
        }
    }

    $versionText = [System.IO.File]::ReadAllText($versionFile).Trim()
    if ($versionText -ne $ReleaseVersion) {
        throw "version.txt does not match v$ReleaseVersion"
    }

    $escapedVersion = [regex]::Escape($ReleaseVersion)

    $baseline = [System.IO.File]::ReadAllText($baselineFile)
    $baselineMatch = [regex]::Match($baseline, '(?m)^\s*v?(\d+(?:\.\d+){2,3})\s*$')
    if (-not $baselineMatch.Success -or $baselineMatch.Groups[1].Value -ne $ReleaseVersion) {
        throw "STABLE_BASELINE.txt version does not match v$ReleaseVersion"
    }

    $change = [System.IO.File]::ReadAllText($changeFile)
    if ($change -notmatch ('(?m)^##\s+v' + $escapedVersion + '\b')) {
        throw "CHANGELOG.md has no top-level entry for v$ReleaseVersion"
    }

    $notes = [System.IO.File]::ReadAllText($notesFile)
    if ($notes -notmatch ('(?m)^#\s+Film Grain Studio v' + $escapedVersion + '\s*$')) {
        throw "RELEASE_NOTES.md title does not match v$ReleaseVersion"
    }

    $native = [System.IO.File]::ReadAllText((Join-Path $Root 'dotnet\FilmGrain_Studio_NET_Preview.cs'))
    $nativeVersion = [regex]::Match($native, 'internal const string ReleaseVersion\s*=\s*"([0-9.]+)"')
    if (-not $nativeVersion.Success -or $nativeVersion.Groups[1].Value -ne $ReleaseVersion) {
        throw '.NET release version does not match release metadata.'
    }
    if ($native -notmatch 'internal const string BuildLabel\s*=\s*"Stable"' -or $native -notmatch 'private const string TestBuild\s*=\s*Program\.BuildLabel\s*;') {
        throw '.NET formal source still contains a test-build label.'
    }


}

function Test-SourceLayout {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion
    )

    foreach ($relative in $RequiredPackageFiles) {
        Assert-File -Root $Root -RelativePath $relative
    }
    foreach ($relative in $RequiredPackageDirectories) {
        Assert-Directory -Root $Root -RelativePath $relative
    }
    foreach ($retired in $RetiredSourceFiles) {
        if (Test-Path -LiteralPath (Join-Path $Root $retired)) { throw ('Retired script remains outside Legacy: ' + $retired) }
    }
    Test-ReleaseMetadata -Root $Root -ReleaseVersion $ReleaseVersion
}

function Remove-PackageExclusions {
    param([Parameter(Mandatory = $true)][string]$StageRoot)

    foreach ($relative in $ExcludedPackagePaths) {
        $path = Join-Path $StageRoot $relative
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    foreach ($pattern in $ExcludedRootDeliveryPatterns) {
        Get-ChildItem -LiteralPath $StageRoot -File -Force |
            Where-Object { $_.Name -like $pattern } |
            Remove-Item -Force
    }

    foreach ($dirName in $ExcludedRecursiveDirectoryNames) {
        $matches = @(
            Get-ChildItem -LiteralPath $StageRoot -Directory -Force -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $dirName } |
                Sort-Object { $_.FullName.Length } -Descending
        )
        foreach ($match in $matches) {
            if (Test-Path -LiteralPath $match.FullName) {
                Remove-Item -LiteralPath $match.FullName -Recurse -Force
            }
        }
    }
}

function Normalize-PackageTextFiles {
    param([Parameter(Mandatory = $true)][string]$StageRoot)

    Get-ChildItem -LiteralPath $StageRoot -Recurse -Filter '*.ps1' -File | ForEach-Object {
        $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
        $offset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $offset = 3
        }
        $text = $Utf8Strict.GetString($bytes, $offset, $bytes.Length - $offset)
        $text = [regex]::Replace($text, '\r\n|\r|\n', $CrLf)
        [System.IO.File]::WriteAllText($_.FullName, $text, $Utf8Bom)
    }

    $langRoot = Join-Path $StageRoot 'Lang'
    if (Test-Path -LiteralPath $langRoot -PathType Container) {
        Get-ChildItem -LiteralPath $langRoot -Filter '*.ini' -File | ForEach-Object {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            $offset = 0
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                $offset = 3
            }
            $text = $Utf8Strict.GetString($bytes, $offset, $bytes.Length - $offset)
            $text = [regex]::Replace($text, '\r\n|\r|\n', $CrLf)
            [System.IO.File]::WriteAllText($_.FullName, $text, $Utf8NoBom)
        }
    }

    Get-ChildItem -LiteralPath $StageRoot -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.bat', '.cmd', '.vbs') } |
        ForEach-Object {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                throw "UTF-8 BOM found in Windows script: $($_.FullName)"
            }
            [byte[]]$normalized = Convert-ByteNewlinesToCrLf -Bytes $bytes
            [System.IO.File]::WriteAllBytes($_.FullName, $normalized)
        }
}

function Build-DotNetPreview {
    param(
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][ValidatePattern('^\d+(?:\.\d+){2,3}$')][string]$ReleaseVersion
    )

    $script = Join-Path $StageRoot 'dotnet\build_NET_Preview.bat'
    $buildDirectory = Join-Path $StageRoot 'dotnet\build'
    $executable = Join-Path $buildDirectory 'FilmGrain_Studio_NET_Preview.exe'
    $rootExecutable = Join-Path $StageRoot 'FilmGrain_Studio.exe'

    # The release must compile committed source, never reuse a local test executable.
    if (Test-Path -LiteralPath $buildDirectory) {
        Remove-Item -LiteralPath $buildDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $rootExecutable) { Remove-Item -LiteralPath $rootExecutable -Force }
    Assert-File -Root $StageRoot -RelativePath 'dotnet\build_NET_Preview.bat'
    Write-Host '[FGS] Building .NET Preview from staged source...'
    & $script
    if ($LASTEXITCODE -ne 0) {
        throw ".NET Preview build failed with exit code $LASTEXITCODE"
    }
    Assert-File -Root $StageRoot -RelativePath 'dotnet\build\FilmGrain_Studio_NET_Preview.exe'
    Assert-File -Root $StageRoot -RelativePath 'FilmGrain_Studio.exe'
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $rootExecutable -Algorithm SHA256).Hash) {
        throw 'Root and compatible .NET executables differ.'
    }
    if ((Get-Item -LiteralPath $executable).Length -le 0) {
        throw '.NET Preview executable is empty.'
    }
    Test-NativeExecutableMetadata -Executable $rootExecutable -ReleaseVersion $ReleaseVersion
}

function Test-NativeExecutableMetadata {
    param([string]$Executable, [string]$ReleaseVersion)
    & (Join-Path ([IO.Path]::GetDirectoryName($Executable)) 'dotnet\BuildTools\Test_Framework_Api.ps1') -Executable $Executable
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Executable))
    $program = $assembly.GetType('FilmGrainStudioPreview.Program', $true)
    $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
    $version = $program.GetField('ReleaseVersion', $flags)
    $label = $program.GetField('BuildLabel', $flags)
    if ($null -eq $version -or $null -eq $label -or $version.GetRawConstantValue() -ne $ReleaseVersion -or $label.GetRawConstantValue() -ne 'Stable') {
        throw 'Built .NET EXE release version / Stable label does not match staged source.'
    }
}

function Test-PackageContent {
    param(
        [Parameter(Mandatory = $true)][string]$StageRoot,
        [Parameter(Mandatory = $true)][string]$ReleaseVersion
    )

    foreach ($relative in $RequiredPackageFiles) {
        Assert-File -Root $StageRoot -RelativePath $relative
    }
    foreach ($relative in $ExcludedPackagePaths) {
        if (Test-Path -LiteralPath (Join-Path $StageRoot $relative)) { throw ('Excluded path remains in stage: ' + $relative) }
    }
    Assert-File -Root $StageRoot -RelativePath 'dotnet\build\FilmGrain_Studio_NET_Preview.exe'
    Assert-File -Root $StageRoot -RelativePath 'FilmGrain_Studio.exe'
    $nativeExe = Join-Path $StageRoot 'dotnet/build/FilmGrain_Studio_NET_Preview.exe'
    $rootExe = Join-Path $StageRoot 'FilmGrain_Studio.exe'
    if ((Get-FileHash -LiteralPath $nativeExe -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $rootExe -Algorithm SHA256).Hash) { throw 'Root and compatible EXEs differ.' }
    Test-NativeExecutableMetadata -Executable $rootExe -ReleaseVersion $ReleaseVersion
    foreach ($relative in $RequiredPackageDirectories) {
        Assert-Directory -Root $StageRoot -RelativePath $relative
    }

    # Read .NET literal language keys directly; never execute a Legacy helper.
    $usedKeys = @(
        Get-ChildItem -LiteralPath (Join-Path $StageRoot 'dotnet') -Filter '*.cs' -File | ForEach-Object {
            $text = [System.IO.File]::ReadAllText($_.FullName)
            [regex]::Matches($text, '\b(?:lang|language)\.T\("([^"\\]+)"\)') | ForEach-Object { $_.Groups[1].Value }
        } | Sort-Object -Unique
    )

    $zh = Read-LangKeys -Path (Join-Path $StageRoot 'Lang\zh-CN.ini')
    $en = Read-LangKeys -Path (Join-Path $StageRoot 'Lang\en-US.ini')
    $missingZh = @($usedKeys | Where-Object { -not $zh.ContainsKey($_) })
    $missingEn = @($usedKeys | Where-Object { -not $en.ContainsKey($_) })

    if ($missingZh.Count) {
        throw ('Missing zh-CN keys: ' + ($missingZh -join ', '))
    }
    if ($missingEn.Count) {
        throw ('Missing en-US keys: ' + ($missingEn -join ', '))
    }

    Get-ChildItem -LiteralPath $StageRoot -Recurse -Filter '*.ps1' -File | ForEach-Object {
        $tokens = $null
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count) {
            throw ('PowerShell parse failed: ' + $_.FullName + ' :: ' + (($errors | ForEach-Object { $_.Message }) -join ' | '))
        }
        $text = [System.IO.File]::ReadAllText($_.FullName)
        if (
            $text.IndexOf([char]0x201C) -ge 0 -or
            $text.IndexOf([char]0x201D) -ge 0 -or
            $text.IndexOf([char]0x2018) -ge 0 -or
            $text.IndexOf([char]0x2019) -ge 0
        ) {
            throw ('Curly quote found in ' + $_.FullName)
        }
    }

    Get-ChildItem -LiteralPath $StageRoot -Recurse -File |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.bat', '.cmd', '.vbs') } |
        ForEach-Object {
            $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                throw ('UTF-8 BOM found in Windows script: ' + $_.FullName)
            }

            for ($i = 0; $i -lt $bytes.Length; $i++) {
                if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) {
                    throw ('LF-only newline found in Windows script: ' + $_.FullName)
                }
            }

            $raw = [System.Text.Encoding]::Default.GetString($bytes)
            if ($_.Extension.ToLowerInvariant() -in @('.bat', '.cmd')) {
                $lines = $raw -split '\r\n'
                for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
                    if ($lines[$lineIndex] -match '\^[ \t]+$') {
                        throw ('Caret trailing whitespace: ' + $_.FullName + ':' + ($lineIndex + 1))
                    }
                }
                $first = $lines | Where-Object { $_.Trim() } | Select-Object -First 1
                if ($null -eq $first -or $first.Trim().ToLowerInvariant() -ne '@echo off') {
                    throw ('Missing @echo off: ' + $_.FullName)
                }
            }
        }
}

# Runtime packaging is independent of repository source staging.
$RuntimeFiles = @(
    'FilmGrain_Studio.exe', 'FilmGrain_Config.default.ini',
    'CHANGELOG.md', 'LICENSE', 'README_FilmGrain_Studio.txt',
    'Utils/FGS.ico',
    'Utils/_FilmGrainSimplified/FilmGrainSimplified.hook.template',
    'Utils/_FilmGrainSimplified/NoiseFallback_512x512_RGBA8.rgba',
    'Utils/_FilmGrainSimplified/THIRD_PARTY_LICENSE_FilmGrainSimplified.txt',
    '_LUT_Tools/LUT_Reference_Default.jpg'
)
$RuntimeDirectories = @('Lang', '_AV1_Grain_Tables', '_OpenSVPFlow')

function New-RuntimeStage {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($relative in $RuntimeFiles) {
        Assert-File -Root $Source -RelativePath $relative
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $relative) -Destination $target -Force
    }
    foreach ($relative in $RuntimeDirectories) {
        Assert-Directory -Root $Source -RelativePath $relative
        Copy-Item -LiteralPath (Join-Path $Source $relative) -Destination (Join-Path $Destination $relative) -Recurse -Force
    }
    # Only sanitize the disposable runtime copy, never the developer defaults.
    $configPath = Join-Path $Destination 'FilmGrain_Config.default.ini'
    $config = [IO.File]::ReadAllText($configPath)
    $config = [regex]::Replace($config, '(?m)^(FFMPEG_DIR|GRAV1SYNTH|GRAIN_ROOT|LUT_ROOT|FRUC_FFMPEG_PATH|FRUC_FFMPEG_SAME_AS_MAIN)=[^\r\n]*', '$1=')
    [IO.File]::WriteAllText($configPath, $config, (New-Object Text.UTF8Encoding($true)))
    $readmePath = Join-Path $Destination 'README_FilmGrain_Studio.txt'
    $readme = [IO.File]::ReadAllText($readmePath)
    $readme = $readme.Replace('README.md', 'https://github.com/rampageX/FilmGrain#readme')
    [IO.File]::WriteAllText($readmePath, $readme, $Utf8NoBom)
    foreach ($relative in $RuntimeFiles) { Assert-File -Root $Destination -RelativePath $relative }
    foreach ($relative in $RuntimeDirectories) { Assert-Directory -Root $Destination -RelativePath $relative }
    $script:RuntimeExpectedEntries = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Force | ForEach-Object {
        $_.FullName.Substring(($Destination.TrimEnd('\')).Length).TrimStart('\').Replace('\', '/')
    } | Sort-Object)
}

function Test-ZipContent {
    param([Parameter(Mandatory = $true)][string]$ZipPath)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
        if (@(Compare-Object $script:RuntimeExpectedEntries $entries).Count) { throw 'Runtime ZIP does not match the runtime stage.' }
        if (@($entries | Where-Object { $_ -match '^(dotnet|Legacy|images|benchmark|[.]github)/' }).Count) { throw 'Developer content found in runtime ZIP.' }
        foreach ($entry in $archive.Entries) {
            if (-not $entry.Name) { continue }
            $stream = $entry.Open()
            try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
}

function New-ReleaseSourceArchive {
    param([string]$CommittedArchive, [string]$Destination, [string]$Commit, [string]$ReleaseVersion, [string]$RuntimeExeHash)
    $inputZip = [IO.Compression.ZipFile]::OpenRead($CommittedArchive)
    $outputZip = $null
    try {
        $outputZip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
        $expected = @()
        foreach ($entry in $inputZip.Entries) {
            if (-not $entry.Name) { continue }
            $relative = $entry.FullName.Replace('\', '/')
            if (Test-ExcludedSourcePath $relative) { continue }
            if ($relative -eq 'SOURCE_BUILD_INFO.txt') { throw 'Reserved source build metadata already exists in committed source.' }
            $target = $outputZip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $target.LastWriteTime = $entry.LastWriteTime
            $sourceStream = $entry.Open()
            $targetStream = $target.Open()
            try { $sourceStream.CopyTo($targetStream) } finally { $targetStream.Dispose(); $sourceStream.Dispose() }
            $expected += $relative
        }
        foreach ($relative in @($RequiredPackageFiles) + @('release.bat', '.github/release/build_release.ps1', '.github/release/release_layout.ps1', '.github/workflows/release.yml')) {
            if ($expected -notcontains $relative.Replace('\', '/')) { throw ('Source ZIP required entry missing: ' + $relative) }
        }
        $info = 'Version=v' + $ReleaseVersion + $CrLf + 'Commit=' + $Commit + $CrLf + 'RuntimeExecutableSHA256=' + $RuntimeExeHash + $CrLf + 'Source=committed HEAD used for the Stable build' + $CrLf
        $metadata = $outputZip.CreateEntry('SOURCE_BUILD_INFO.txt')
        $writer = New-Object IO.StreamWriter($metadata.Open(), $Utf8NoBom)
        try { $writer.Write($info) } finally { $writer.Dispose() }
        $outputZip.Dispose(); $outputZip = $null
    } finally {
        if ($outputZip) { $outputZip.Dispose() }
        $inputZip.Dispose()
    }
    $check = [IO.Compression.ZipFile]::OpenRead($Destination)
    try {
        $actual = @($check.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
        if (@(Compare-Object (@($expected) + @('SOURCE_BUILD_INFO.txt')) $actual).Count) { throw 'Source ZIP entry set mismatch.' }
        foreach ($entry in $check.Entries) {
            if (-not $entry.Name) { continue }
            $stream = $entry.Open()
            try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() }
        }
    } finally { $check.Dispose() }
}

$RepositoryRoot = Get-NormalizedFullPath -Path $RepositoryRoot
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root not found: $RepositoryRoot"
}

$gitTopPath = Get-NormalizedFullPath -Path (Get-ReleaseGitTopLevel -Root $RepositoryRoot)
if (-not [string]::Equals($RepositoryRoot, $gitTopPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "RepositoryRoot must be the Git top-level directory: $gitTopPath"
}

Test-SourceLayout -Root $RepositoryRoot -ReleaseVersion $Version

if ($ValidateOnly) {
    Write-Host "[FGS] Shared release source validation passed for v$Version."
    exit 0
}

$trackedSource = @(Get-ReleaseTrackedPaths -Root $RepositoryRoot)
$badSource = @($trackedSource | Where-Object { Test-ForbiddenTrackedSourcePath $_ })
if ($badSource.Count) { throw ('Local-only files tracked in release source: ' + ($badSource -join ', ')) }

if (-not $OutputDirectory) {
    $OutputDirectory = $RepositoryRoot
}
$OutputDirectory = Get-NormalizedFullPath -Path $OutputDirectory
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$name = "FilmGrain_Studio_v${Version}_Stable"
$zipPath = Join-Path $OutputDirectory ($name + '.zip')
$shaPath = $zipPath + '.sha256'
$sourceZipPath = Join-Path $OutputDirectory ("FilmGrain_Studio_v${Version}_Source.zip")
$sourceShaPath = $sourceZipPath + '.sha256'
$sourceZipPending = Join-Path $env:TEMP ('FGS_SourceOutput_' + [guid]::NewGuid().ToString('N') + '.zip')
$sourceCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40,64}$') { throw 'Could not identify source HEAD.' }
$stageRoot = Join-Path $env:TEMP ('FGS_Release_' + [guid]::NewGuid().ToString('N'))
$runtimeRoot = Join-Path $env:TEMP ('FGS_Runtime_' + [guid]::NewGuid().ToString('N'))
$sourceArchive = Join-Path $env:TEMP ('FGS_Source_' + [guid]::NewGuid().ToString('N') + '.zip')

Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

    & git -C $RepositoryRoot archive --format=zip "--output=$sourceArchive" HEAD
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourceArchive -PathType Leaf)) {
        throw 'git archive failed.'
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($sourceArchive, $stageRoot)

    # Validate the committed HEAD before any package-only exclusions are applied.
    Test-SourceLayout -Root $stageRoot -ReleaseVersion $Version

    Remove-PackageExclusions -StageRoot $stageRoot
    Normalize-PackageTextFiles -StageRoot $stageRoot
    Build-DotNetPreview -StageRoot $stageRoot -ReleaseVersion $Version
    Test-PackageContent -StageRoot $stageRoot -ReleaseVersion $Version
    New-RuntimeStage -Source $stageRoot -Destination $runtimeRoot

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (Test-Path -LiteralPath $shaPath) {
        Remove-Item -LiteralPath $shaPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $runtimeRoot,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    Test-ZipContent -ZipPath $zipPath

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $shaPath,
        ($hash + '  ' + [System.IO.Path]::GetFileName($zipPath) + $CrLf),
        $Utf8NoBom
    )

    $runtimeExeHash = (Get-FileHash -LiteralPath (Join-Path $runtimeRoot 'FilmGrain_Studio.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    New-ReleaseSourceArchive -CommittedArchive $sourceArchive -Destination $sourceZipPending -Commit $sourceCommit -ReleaseVersion $Version -RuntimeExeHash $runtimeExeHash
    $sourceHash = (Get-FileHash -LiteralPath $sourceZipPending -Algorithm SHA256).Hash.ToLowerInvariant()
    Move-Item -LiteralPath $sourceZipPending -Destination $sourceZipPath -Force
    [IO.File]::WriteAllText($sourceShaPath, ($sourceHash + '  ' + [IO.Path]::GetFileName($sourceZipPath) + $CrLf), $Utf8NoBom)
    Write-Host "[FGS] Source ZIP    : $sourceZipPath"
    Write-Host "[FGS] Source SHA256 : $sourceShaPath"
    Write-Host "[FGS] Source commit : $sourceCommit"

    Write-Host "[FGS] Shared release build passed."
    Write-Host "[FGS] ZIP    : $zipPath"
    Write-Host "[FGS] SHA256 : $shaPath"
    Write-Host "[FGS] Digest : $hash"
}
finally {
    if (Test-Path -LiteralPath $sourceZipPending) {
        Remove-Item -LiteralPath $sourceZipPending -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runtimeRoot) {
        Remove-Item -LiteralPath $runtimeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $sourceArchive) {
        Remove-Item -LiteralPath $sourceArchive -Force -ErrorAction SilentlyContinue
    }
}
