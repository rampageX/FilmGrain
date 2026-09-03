[CmdletBinding()]
param(
    [string]$LutRoot,
    [string]$ReferencePath,
    [string]$OutputRoot,
    [switch]$ForceOverwrite,
    [switch]$NonInteractive,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

# ============================================================
# User defaults - edit these values if you want different defaults.
# At runtime, just press Enter to accept the value shown in [brackets].
# ============================================================
$FFMPEG = "E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
$DefaultLutRoot = "E:\Adobe Portable\LUTs"
$DefaultReference = Join-Path $PSScriptRoot "LUT_Reference_Default.jpg"
$DefaultVideoSeek = "0"
$DefaultOutputFolderName = "_LUT_PREVIEWS"
$PreviewWidth = 1920
# ============================================================

$LutExts = @(".cube", ".3dl", ".dat", ".m3d", ".csp")

function AskPathWithDefault($label, $defaultValue, $wantDirectory) {
    while ($true) {
        $prompt = if ($defaultValue) { "$label [$defaultValue]" } else { $label }
        $x = (Read-Host $prompt).Trim().Trim('"')
        if (-not $x) { $x = $defaultValue }

        if ($x -and (Test-Path -LiteralPath $x)) {
            $r = (Resolve-Path -LiteralPath $x).Path
            if (($wantDirectory -and (Test-Path -LiteralPath $r -PathType Container)) -or
                (-not $wantDirectory -and (Test-Path -LiteralPath $r -PathType Leaf))) {
                return $r
            }
        }
        Write-Host "Invalid path: $x" -ForegroundColor Yellow
    }
}

function IsVideo($p) {
    @(".mp4", ".mkv", ".mov", ".avi", ".m2ts", ".mts", ".ts", ".webm", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".vob", ".mxf") -contains [IO.Path]::GetExtension($p).ToLowerInvariant()
}

function RelPath($b, $c) {
    $u = New-Object Uri(($b.TrimEnd('\') + '\'))
    $v = New-Object Uri($c)
    [Uri]::UnescapeDataString($u.MakeRelativeUri($v).ToString()).Replace("/", "\")
}

# Explicit recursion follows normal folders AND Windows junctions/symlinks.
# Visited real paths prevent junction loops.
function FindLuts($root, $outRoot) {
    $result = New-Object Collections.ArrayList
    $stack = New-Object Collections.Generic.Stack[string]
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([StringComparer]::OrdinalIgnoreCase)
    $stack.Push($root)

    while ($stack.Count) {
        $d = $stack.Pop()
        try { $real = (Resolve-Path -LiteralPath $d -ErrorAction Stop).Path.TrimEnd('\') } catch { continue }
        if (-not $seen.Add($real)) { continue }
        try { $items = Get-ChildItem -LiteralPath $d -Force -ErrorAction Stop } catch { continue }

        foreach ($i in $items) {
            if ($i.PSIsContainer) {
                if (-not $i.FullName.StartsWith($outRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    $stack.Push($i.FullName)
                }
            } elseif ($LutExts -contains $i.Extension.ToLowerInvariant()) {
                [void]$result.Add($i)
            }
        }
    }
    return $result
}

# DaVinci Resolve ships some .cube files containing LUT_3D_INPUT_RANGE.
# FFmpeg's lut3d parser can reject that Resolve-specific directive.
# For .cube files, create a temporary FFmpeg-compatible copy in the output
# directory, replacing:
#   LUT_3D_INPUT_RANGE min max
# with:
#   DOMAIN_MIN min min min
#   DOMAIN_MAX max max max
# The original LUT is NEVER modified.
# Returns an object with Path/Name/Directory/Cleanup, or $null for unsupported 1D cube.
function PrepareLutForFFmpeg($lut, $tempDirectory, $sequence) {
    if ($lut.Extension.ToLowerInvariant() -ne ".cube") {
        return [pscustomobject]@{
            Path      = $lut.FullName
            Name      = $lut.Name
            Directory = $lut.DirectoryName
            Cleanup   = $false
            Converted = $false
        }
    }

    try {
        $lines = [IO.File]::ReadAllLines($lut.FullName)
    } catch {
        throw "Cannot read CUBE file: $($lut.FullName)"
    }

    $has3D = $false
    $hasResolveRange = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*LUT_3D_SIZE\s+\d+') { $has3D = $true }
        if ($line -match '^\s*LUT_3D_INPUT_RANGE\s+') { $hasResolveRange = $true }
    }

    # lut3d is a 3D LUT filter; do not send pure 1D .cube files to it.
    if (-not $has3D) { return $null }

    if (-not $hasResolveRange) {
        return [pscustomobject]@{
            Path      = $lut.FullName
            Name      = $lut.Name
            Directory = $lut.DirectoryName
            Cleanup   = $false
            Converted = $false
        }
    }

    $converted = New-Object Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line -match '^\s*LUT_3D_INPUT_RANGE\s+([^\s#]+)\s+([^\s#]+)') {
            $min = $matches[1]
            $max = $matches[2]
            $converted.Add("DOMAIN_MIN $min $min $min")
            $converted.Add("DOMAIN_MAX $max $max $max")
        } else {
            $converted.Add($line)
        }
    }

    $tempName = "__ffmpeg_cube_{0:D6}.cube" -f $sequence
    $tempPath = Join-Path $tempDirectory $tempName
    [IO.File]::WriteAllLines($tempPath, $converted.ToArray(), (New-Object Text.UTF8Encoding($false)))

    return [pscustomobject]@{
        Path      = $tempPath
        Name      = $tempName
        Directory = $tempDirectory
        Cleanup   = $true
        Converted = $true
    }
}

if (-not (Test-Path -LiteralPath $FFMPEG)) { throw "FFmpeg not found: $FFMPEG" }

Write-Host "`n=== Recursive LUT Preview Generator / Gallery + Resolve-Compatible ===`n" -ForegroundColor Cyan
if ($NonInteractive) {
    Write-Host "Running from LUT Gallery with overwrite enabled.`n" -ForegroundColor DarkGray
} else {
    Write-Host "Press Enter at prompts to use the value shown in [brackets].`n" -ForegroundColor DarkGray
}

if ($LutRoot) {
    if (-not (Test-Path -LiteralPath $LutRoot -PathType Container)) { throw "Invalid LUT root directory: $LutRoot" }
    $root = (Resolve-Path -LiteralPath $LutRoot).Path
} else {
    if ($NonInteractive) { throw 'LutRoot is required in non-interactive mode.' }
    $root = AskPathWithDefault "LUT root directory" $DefaultLutRoot $true
}

if ($ReferencePath) {
    if (-not (Test-Path -LiteralPath $ReferencePath -PathType Leaf)) { throw "Invalid reference image or video: $ReferencePath" }
    $source = (Resolve-Path -LiteralPath $ReferencePath).Path
} else {
    if ($NonInteractive) { throw 'ReferencePath is required in non-interactive mode.' }
    $source = AskPathWithDefault "Reference image or video" $DefaultReference $false
}

$video = IsVideo $source
$seek = $DefaultVideoSeek
if ($video) {
    if (-not $NonInteractive) {
        $s = (Read-Host "Video capture time in seconds [$DefaultVideoSeek]").Trim()
        if ($s) { $seek = $s }
    }
}

if ($OutputRoot) {
    $out = $OutputRoot.Trim().Trim('"')
    if (-not [IO.Path]::IsPathRooted($out)) { $out = Join-Path $root $out }
} else {
    if ($NonInteractive) {
        $out = Join-Path $root $DefaultOutputFolderName
    } else {
        $o = (Read-Host "Output folder [Enter = $DefaultOutputFolderName]").Trim().Trim('"')
        if (-not $o) {
            $out = Join-Path $root $DefaultOutputFolderName
        } else {
            $out = $o
            if (-not [IO.Path]::IsPathRooted($out)) { $out = Join-Path $root $out }
        }
    }
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
$out = (Resolve-Path $out).Path
$overwrite = if ($NonInteractive) { [bool]$ForceOverwrite } else { (Read-Host "Overwrite existing previews? [y/N]") -match '^(y|yes)$' }

$luts = @(FindLuts $root $out | Sort-Object FullName)
Write-Host "`nFound $($luts.Count) LUT files (junctions included).`n"

$ok = 0
$skip = 0
$fail = 0
$unsupported = 0
$convertedCount = 0
$galleryRows = New-Object Collections.Generic.List[object]
$n = 0
$log = Join-Path $out "_LUT_PREVIEW_FAILED.txt"
Remove-Item $log -Force -ErrorAction SilentlyContinue

foreach ($lut in $luts) {
    $n++

    # For junction targets outside root, MakeRelativeUri can produce .. paths.
    # Flatten those into a safe _JUNCTIONS subtree.
    $rel = RelPath $root $lut.FullName
    if ($rel.StartsWith("..\")) {
        $rd = "_JUNCTIONS\" + $lut.Directory.Name
    } else {
        $rd = Split-Path $rel -Parent
    }

    if (-not $rd -or $rd -eq ".") { $dst = $out } else { $dst = Join-Path $out $rd }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null

    $jpg = Join-Path $dst ($lut.BaseName + "_preview.jpg")
    Write-Host "[$n/$($luts.Count)] $($lut.FullName)"

    if ((Test-Path $jpg) -and -not $overwrite) {
        Write-Host "  SKIP"
        $galleryRows.Add([pscustomobject]@{
            Name = $lut.BaseName
            Relative = $rel
            LutPath = $lut.FullName
            PreviewPath = $jpg
        })
        $skip++
        continue
    }

    $prepared = $null
    try {
        $prepared = PrepareLutForFFmpeg $lut $dst $n
        if ($null -eq $prepared) {
            $msg = "UNSUPPORTED (no LUT_3D_SIZE / likely 1D CUBE): $($lut.FullName)"
            Write-Host "  UNSUPPORTED 1D CUBE" -ForegroundColor Yellow
            Add-Content -LiteralPath $log -Value $msg -Encoding UTF8
            $unsupported++
            continue
        }

        if ($prepared.Converted) {
            Write-Host "  Resolve CUBE -> temporary FFmpeg-compatible CUBE" -ForegroundColor DarkCyan
            $convertedCount++
        }

        # Run from the LUT/temp directory and pass a simple relative LUT filename.
        # This avoids Windows drive-colon escaping problems inside the lut3d filter.
        $vf = "lut3d=file='$($prepared.Name)',scale='min($PreviewWidth,iw)':-2:flags=lanczos"
        $a = @("-hide_banner", "-loglevel", "error", "-y")
        if ($video) { $a += @("-ss", $seek) }
        $a += @("-i", $source, "-frames:v", "1", "-vf", $vf, "-q:v", "2", $jpg)

        Push-Location $prepared.Directory
        try {
            & $FFMPEG @a 2>>$log
            $rc = $LASTEXITCODE
        } finally {
            Pop-Location
        }

        if ($rc -eq 0 -and (Test-Path $jpg)) {
            Write-Host "  OK" -ForegroundColor Green
            $galleryRows.Add([pscustomobject]@{
                Name = $lut.BaseName
                Relative = $rel
                LutPath = $lut.FullName
                PreviewPath = $jpg
            })
            $ok++
        } else {
            Write-Host "  FAILED" -ForegroundColor Red
            Add-Content -LiteralPath $log -Value "FAILED: $($lut.FullName)" -Encoding UTF8
            $fail++
        }
    } catch {
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
        Add-Content -LiteralPath $log -Value "FAILED: $($lut.FullName)`r`n$($_.Exception.Message)" -Encoding UTF8
        $fail++
    } finally {
        if ($prepared -and $prepared.Cleanup -and (Test-Path -LiteralPath $prepared.Path)) {
            Remove-Item -LiteralPath $prepared.Path -Force -ErrorAction SilentlyContinue
        }
    }
}

# Write a gallery index that maps each preview image to its source LUT.
# This is consumed by LUT_Gallery_Selector.ps1 and the AV1 pipeline.
$galleryIndex = Join-Path $out "_LUT_GALLERY_INDEX.json"
$galleryRows | Sort-Object Relative | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $galleryIndex -Encoding UTF8

Write-Host "`nGallery entries: $($galleryRows.Count)"
Write-Host "Gallery index: $galleryIndex"
Write-Host "`nSuccess: $ok  Skipped: $skip  Unsupported: $unsupported  Failed: $fail"
Write-Host "Resolve CUBE files converted temporarily: $convertedCount"
Write-Host "Output: $out"
if (Test-Path -LiteralPath $log) { Write-Host "Log: $log" }
if (-not $NoPause) { Read-Host "Press Enter to exit" }
