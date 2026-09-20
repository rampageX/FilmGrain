$ErrorActionPreference = 'Stop'

$script:FilmGrainConfigPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'FilmGrain_Config.ini'

$script:FilmGrainConfigDefaults = [ordered]@{
    FFMPEG_DIR = 'E:\EnCoder\FFMpeg\x64\bin'
    GRAV1SYNTH = 'E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe'
    GRAIN_ROOT = 'D:\Film_Grain'
    LUT_ROOT = 'E:\Adobe Portable\LUTs'

    LANGUAGE = 'zh-CN'
    SMART_FILTER_ENABLED = 'false'

    H264_HIGH10 = 'false'
    X264_RATE_MODE = 'VBR1'
    X264_PRESET = 'faster'
    HEVC_SPATIAL_AQ = '8'
    HEVC_TEMPORAL_AQ = 'true'

    SVP_ALGO = '13'
    SVP_ANALYSE = 'ENCODEGUI'
    SVP_MASK_AREA = '100'

    HDR_POLICY = 'AUTO'
    TONE_MAP_ALGO = 'hable'

    CINEMATIC_CROP_PER_SIDE = '0'
}

$script:FilmGrainConfigSections = [ordered]@{
    Paths = @(
        'FFMPEG_DIR',
        'GRAV1SYNTH',
        'GRAIN_ROOT',
        'LUT_ROOT'
    )
    General = @(
        'LANGUAGE'
    )
    LUTGallery = @(
        'SMART_FILTER_ENABLED'
    )
    'Advanced.Encoding' = @(
        'H264_HIGH10',
        'X264_RATE_MODE',
        'X264_PRESET',
        'HEVC_SPATIAL_AQ',
        'HEVC_TEMPORAL_AQ'
    )
    'Advanced.Interpolation' = @(
        'SVP_ALGO',
        'SVP_ANALYSE',
        'SVP_MASK_AREA'
    )
    'Advanced.HDR' = @(
        'HDR_POLICY',
        'TONE_MAP_ALGO'
    )
    'Advanced.Other' = @(
        'CINEMATIC_CROP_PER_SIDE'
    )
}

function Read-FilmGrainConfigValues {
    param([string]$Path = $script:FilmGrainConfigPath)

    $result = @{}
    $configured = @{}
    foreach ($key in $script:FilmGrainConfigDefaults.Keys) {
        $result[$key] = [string]$script:FilmGrainConfigDefaults[$key]
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            Values = $result
            Configured = $configured
        }
    }

    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        $lines = [System.IO.File]::ReadAllLines($Path, $utf8Strict)
    } catch {
        throw "FilmGrain_Config.ini is not valid UTF-8: $Path"
    }

    $legacyFfmpeg = ''
    $legacyFfprobe = ''
    $hasFfmpegDir = $false

    foreach ($line in $lines) {
        $text = [string]$line
        $trimmed = $text.Trim()
        if (-not $trimmed -or $trimmed.StartsWith(';') -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']')) { continue }

        $eq = $text.IndexOf('=')
        if ($eq -le 0) { continue }

        $key = $text.Substring(0, $eq).Trim().ToUpperInvariant()
        $value = $text.Substring($eq + 1).Trim().Trim('"')
        if (-not $value) { continue }

        if ($key -eq 'FFMPEG') { $legacyFfmpeg = $value; continue }
        if ($key -eq 'FFPROBE') { $legacyFfprobe = $value; continue }

        if ($script:FilmGrainConfigDefaults.Contains($key)) {
            $result[$key] = $value
            $configured[$key] = $true
            if ($key -eq 'FFMPEG_DIR') { $hasFfmpegDir = $true }
        }
    }

    if (-not $hasFfmpegDir) {
        $legacyExe = if ($legacyFfmpeg) { $legacyFfmpeg } else { $legacyFfprobe }
        if ($legacyExe) {
            try {
                $legacyDir = Split-Path -Parent $legacyExe
                if ($legacyDir) {
                    $result['FFMPEG_DIR'] = $legacyDir.TrimEnd('\')
                    $configured['FFMPEG_DIR'] = $true
                }
            } catch {}
        }
    }

    return [pscustomobject]@{
        Values = $result
        Configured = $configured
    }
}

function Normalize-FilmGrainConfigValue {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [Parameter(Mandatory=$true)][object]$Value
    )

    $text = [string]$Value
    if ($Key -in @('FFMPEG_DIR','GRAV1SYNTH','GRAIN_ROOT','LUT_ROOT')) {
        $text = $text.Trim().Trim('"')
        if ($Key -eq 'FFMPEG_DIR') { $text = $text.TrimEnd('\') }
    } else {
        $text = $text.Trim()
    }

    if ($text.IndexOf([char]13) -ge 0 -or $text.IndexOf([char]10) -ge 0) {
        throw "Invalid FilmGrain_Config value for $Key."
    }

    return $text
}

function Save-FilmGrainConfig {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Values,
        [string]$Path = $script:FilmGrainConfigPath
    )

    $read = Read-FilmGrainConfigValues -Path $Path
    $current = $read.Values

    foreach ($inputKey in $Values.Keys) {
        $key = ([string]$inputKey).Trim().ToUpperInvariant()
        if (-not $script:FilmGrainConfigDefaults.Contains($key)) { continue }
        $current[$key] = Normalize-FilmGrainConfigValue -Key $key -Value $Values[$inputKey]
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($section in $script:FilmGrainConfigSections.Keys) {
        [void]$lines.Add('[' + $section + ']')
        foreach ($key in $script:FilmGrainConfigSections[$section]) {
            [void]$lines.Add($key + '=' + [string]$current[$key])
        }
        [void]$lines.Add('')
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

    $read = Read-FilmGrainConfigValues -Path $Path
    if ($read.Configured.Count -lt $script:FilmGrainConfigDefaults.Count) {
        Save-FilmGrainConfig -Values @{} -Path $Path
        $read = Read-FilmGrainConfigValues -Path $Path
    }
    $result = $read.Values

    $ffmpegDir = ([string]$result['FFMPEG_DIR']).Trim().Trim('"').TrimEnd('\')

    return [pscustomobject][ordered]@{
        FFMPEG_DIR = $ffmpegDir
        FFMPEG = Join-Path $ffmpegDir 'ffmpeg.exe'
        FFPROBE = Join-Path $ffmpegDir 'ffprobe.exe'
        GRAV1SYNTH = [string]$result['GRAV1SYNTH']
        GRAIN_ROOT = [string]$result['GRAIN_ROOT']
        LUT_ROOT = [string]$result['LUT_ROOT']

        LANGUAGE = [string]$result['LANGUAGE']
        SMART_FILTER_ENABLED = [string]$result['SMART_FILTER_ENABLED']

        H264_HIGH10 = [string]$result['H264_HIGH10']
        X264_RATE_MODE = [string]$result['X264_RATE_MODE']
        X264_PRESET = [string]$result['X264_PRESET']
        HEVC_SPATIAL_AQ = [string]$result['HEVC_SPATIAL_AQ']
        HEVC_TEMPORAL_AQ = [string]$result['HEVC_TEMPORAL_AQ']

        SVP_ALGO = [string]$result['SVP_ALGO']
        SVP_ANALYSE = [string]$result['SVP_ANALYSE']
        SVP_MASK_AREA = [string]$result['SVP_MASK_AREA']

        HDR_POLICY = [string]$result['HDR_POLICY']
        TONE_MAP_ALGO = [string]$result['TONE_MAP_ALGO']

        CINEMATIC_CROP_PER_SIDE = [string]$result['CINEMATIC_CROP_PER_SIDE']

        ConfigPath = $Path
    }
}
