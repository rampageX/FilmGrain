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

$RequiredPackageFiles = @(
    '.gitattributes',
    'CHANGELOG.md',
    'FilmGrain_Config.ini',
    'FilmGrain_Universal_CLI.bat',
    'FilmGrain_Universal_GUI.bat',
    'LICENSE',
    'README.md',
    'README_FilmGrain_Studio.txt',
    'README_Toolkit.txt',
    'STABLE_BASELINE.txt',
    'Utils\FilmGrain_Studio.ps1',
    'Utils\FGS_Benchmark.cmd',
    'Utils\FGS_Benchmark.ps1',
    'Utils\FilmGrain_Language.ps1',
    'Lang\zh-CN.ini',
    'Lang\en-US.ini',
    '_OpenSVPFlow\Plugins\open-svpflow-version.txt',
    '_OpenSVPFlow\Plugins\svpflow1_vs.dll',
    '_OpenSVPFlow\Plugins\svpflow2_vs.dll',
    'Utils\_FilmGrainSimplified\NoiseFallback_512x512_RGBA8.rgba'
)

$RequiredPackageDirectories = @(
    'images',
    'Lang',
    'Utils',
    '_AV1_Grain_Tables',
    '_LUT_Tools',
    '_OpenSVPFlow'
)

$ExcludedPackagePaths = @(
    '.github',
    '.gitignore',
    'benchmark',
    'release.bat',
    'Utils\_HardwareCaps.json',
    '_LUT_Tools\LUT_Reference_Current.jpg',
    '_OpenSVPFlow\_PluginBackup',
    'Lang\FilmGrain_Language.ini'
)

$ExcludedRecursiveDirectoryNames = @(
    '.env'
)

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
    $studioFile = Join-Path $Root 'Utils\FilmGrain_Studio.ps1'

    foreach ($path in @($versionFile, $notesFile, $baselineFile, $changeFile, $studioFile)) {
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

    $studio = [System.IO.File]::ReadAllText($studioFile)
    $statusPattern = '(?m)^\s*\$statusVersion\.Text\s*=\s*' + [char]39 + 'v' + $escapedVersion + [char]39 + '\s*$'
    if ($studio -notmatch $statusPattern) {
        throw "GUI status version does not match v$ReleaseVersion"
    }

    if ($studio -match '\$statusVersion\.Text[^\r\n]*TEST|I18N TEST|TEST P7L4N|TEST M5Q8T|TEST R4N8C|TEST K7M2Q') {
        throw 'Formal GUI still contains a test-build marker.'
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

function Test-PackageContent {
    param([Parameter(Mandatory = $true)][string]$StageRoot)

    foreach ($relative in $RequiredPackageFiles) {
        Assert-File -Root $StageRoot -RelativePath $relative
    }
    foreach ($relative in $RequiredPackageDirectories) {
        Assert-Directory -Root $StageRoot -RelativePath $relative
    }

    . (Join-Path $StageRoot 'Utils\FilmGrain_Language.ps1')
    $languages = @(Get-FgAvailableLanguages)
    $codes = @($languages | ForEach-Object { [string]$_.Code })
    if ($codes -notcontains 'zh-CN' -or $codes -notcontains 'en-US') {
        throw ('Required languages missing: ' + ($codes -join ', '))
    }

    $studioPath = Join-Path $StageRoot 'Utils\FilmGrain_Studio.ps1'
    $studioText = [System.IO.File]::ReadAllText($studioPath)
    $usedKeys = @(
        [regex]::Matches($studioText, "\bL\s+'([^']+)'") |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object -Unique
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

function Test-ZipContent {
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })

        foreach ($relative in $RequiredPackageFiles) {
            $entry = $relative.Replace('\', '/')
            if ($entries -notcontains $entry) {
                throw "Required ZIP entry missing: $entry"
            }
        }

        foreach ($relative in $RequiredPackageDirectories) {
            $prefix = $relative.Replace('\', '/').TrimEnd('/') + '/'
            $directoryEntries = @(
                $entries | Where-Object {
                    $_.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
                }
            )
            if ($directoryEntries.Count -eq 0) {
                throw "Required ZIP directory missing: $prefix"
            }
        }

        foreach ($relative in $ExcludedPackagePaths) {
            $entry = $relative.Replace('\', '/').Trim('/')
            $found = @(
                $entries | Where-Object {
                    $_ -eq $entry -or $_.StartsWith($entry + '/', [System.StringComparison]::OrdinalIgnoreCase)
                }
            )
            if ($found.Count) {
                throw "Excluded path found in final ZIP: $entry"
            }
        }

        foreach ($dirName in $ExcludedRecursiveDirectoryNames) {
            $escaped = [regex]::Escape($dirName)
            if (@($entries | Where-Object { $_ -match ('(^|/)' + $escaped + '(/|$)') }).Count) {
                throw "Excluded runtime directory found in final ZIP: $dirName"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$RepositoryRoot = Get-NormalizedFullPath -Path $RepositoryRoot
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root not found: $RepositoryRoot"
}

$gitTop = @(& git -C $RepositoryRoot rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $gitTop.Count) {
    throw "Not a Git repository: $RepositoryRoot"
}
$gitTopPath = Get-NormalizedFullPath -Path $gitTop[0]
if (-not [string]::Equals($RepositoryRoot, $gitTopPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "RepositoryRoot must be the Git top-level directory: $gitTopPath"
}

Test-SourceLayout -Root $RepositoryRoot -ReleaseVersion $Version

if ($ValidateOnly) {
    Write-Host "[FGS] Shared release source validation passed for v$Version."
    exit 0
}

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
$stageRoot = Join-Path $env:TEMP ('FGS_Release_' + [guid]::NewGuid().ToString('N'))
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
    Test-PackageContent -StageRoot $stageRoot

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (Test-Path -LiteralPath $shaPath) {
        Remove-Item -LiteralPath $shaPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stageRoot,
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

    Write-Host "[FGS] Shared release build passed."
    Write-Host "[FGS] ZIP    : $zipPath"
    Write-Host "[FGS] SHA256 : $shaPath"
    Write-Host "[FGS] Digest : $hash"
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $sourceArchive) {
        Remove-Item -LiteralPath $sourceArchive -Force -ErrorAction SilentlyContinue
    }
}
