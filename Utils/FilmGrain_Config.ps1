$ErrorActionPreference = 'Stop'

$script:FilmGrainConfigPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'FilmGrain_Config.ini'
$script:FilmGrainConfigDefaults = [ordered]@{
    FFMPEG_DIR = 'E:\EnCoder\FFMpeg\x64\bin'
    GRAV1SYNTH = 'E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe'
    GRAIN_ROOT = 'D:\Film_Grain'
    LUT_ROOT = 'E:\Adobe Portable\LUTs'
}

function Save-FilmGrainConfig {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Values,
        [string]$Path = $script:FilmGrainConfigPath
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('[Paths]')
    foreach ($key in $script:FilmGrainConfigDefaults.Keys) {
        $value = if ($Values.ContainsKey($key) -and $null -ne $Values[$key]) { [string]$Values[$key] } else { [string]$script:FilmGrainConfigDefaults[$key] }
        $value = $value.Trim().Trim('"')
        if ($key -eq 'FFMPEG_DIR') { $value = $value.TrimEnd('\') }
        if ($value.Contains("`r") -or $value.Contains("`n")) { throw "Invalid path value for $key." }
        $lines.Add($key + '=' + $value)
    }

    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir -PathType Container)) {
        [void](New-Item -ItemType Directory -Force -Path $dir)
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($Path, $lines.ToArray(), $utf8NoBom)
}

function Get-FilmGrainConfig {
    param([string]$Path = $script:FilmGrainConfigPath)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Save-FilmGrainConfig -Values @{} -Path $Path
    }

    $result = @{}
    foreach ($key in $script:FilmGrainConfigDefaults.Keys) {
        $result[$key] = [string]$script:FilmGrainConfigDefaults[$key]
    }

    $legacyFfmpeg = ''
    $legacyFfprobe = ''
    $hasFfmpegDir = $false
    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        $lines = [System.IO.File]::ReadAllLines($Path, $utf8Strict)
    } catch {
        throw "FilmGrain_Config.ini is not valid UTF-8: $Path"
    }

    foreach ($line in $lines) {
        $text = [string]$line
        $trimmed = $text.Trim()
        if (-not $trimmed -or $trimmed.StartsWith(';') -or $trimmed.StartsWith('#') -or ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']'))) { continue }
        $eq = $text.IndexOf('=')
        if ($eq -le 0) { continue }
        $key = $text.Substring(0, $eq).Trim().ToUpperInvariant()
        $value = $text.Substring($eq + 1).Trim().Trim('"')
        if (-not $value) { continue }

        if ($key -eq 'FFMPEG_DIR') {
            $result['FFMPEG_DIR'] = $value.TrimEnd('\')
            $hasFfmpegDir = $true
            continue
        }
        if ($key -eq 'FFMPEG') { $legacyFfmpeg = $value; continue }
        if ($key -eq 'FFPROBE') { $legacyFfprobe = $value; continue }
        if ($script:FilmGrainConfigDefaults.Contains($key)) { $result[$key] = $value }
    }

    if (-not $hasFfmpegDir) {
        $legacyExe = if ($legacyFfmpeg) { $legacyFfmpeg } else { $legacyFfprobe }
        if ($legacyExe) {
            try {
                $legacyDir = Split-Path -Parent $legacyExe
                if ($legacyDir) { $result['FFMPEG_DIR'] = $legacyDir.TrimEnd('\') }
            } catch {}
        }
    }

    $ffmpegDir = [string]$result['FFMPEG_DIR']
    return [pscustomobject][ordered]@{
        FFMPEG_DIR = $ffmpegDir
        FFMPEG = Join-Path $ffmpegDir 'ffmpeg.exe'
        FFPROBE = Join-Path $ffmpegDir 'ffprobe.exe'
        GRAV1SYNTH = $result['GRAV1SYNTH']
        GRAIN_ROOT = $result['GRAIN_ROOT']
        LUT_ROOT = $result['LUT_ROOT']
        ConfigPath = $Path
    }
}
