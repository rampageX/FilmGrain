param()

$ErrorActionPreference = 'Stop'

function Get-RequiredEnv {
    param([string]$Name)
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Missing environment variable: $Name" }
    return $value
}

function Normalize-HexColor {
    param([string]$Value, [string]$Fallback)
    $v = ([string]$Value).Trim().TrimStart('#')
    if ($v -notmatch '^[0-9A-Fa-f]{6}$') { $v = $Fallback }
    return $v.ToUpperInvariant()
}

function Convert-HexToAss {
    param([string]$Rgb)
    $r = $Rgb.Substring(0,2)
    $g = $Rgb.Substring(2,2)
    $b = $Rgb.Substring(4,2)
    return "&H00$b$g$r"
}

function Find-SameNameSubtitle {
    param([string]$VideoPath)
    $dir = [IO.Path]::GetDirectoryName($VideoPath)
    $base = [IO.Path]::GetFileNameWithoutExtension($VideoPath)
    foreach ($ext in @('.ass','.srt','.ssa','.vtt')) {
        $p = Join-Path $dir ($base + $ext)
        if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
    }
    return $null
}

function Get-FirstTextSubtitleOrdinal {
    param([string]$Ffprobe, [string]$VideoPath)
    if (-not $Ffprobe -or -not (Test-Path -LiteralPath $Ffprobe -PathType Leaf)) { return $null }
    try {
        $json = (& $Ffprobe -v error -select_streams s -show_entries 'stream=codec_name' -of json $VideoPath 2>$null | Out-String)
        if (-not $json) { return $null }
        $data = $json | ConvertFrom-Json
        $textCodecs = @('subrip','ass','ssa','webvtt','mov_text','text','sami','microdvd','jacosub','realtext','subviewer','subviewer1','vplayer')
        $ordinal = 0
        foreach ($stream in @($data.streams)) {
            if (([string]$stream.codec_name).ToLowerInvariant() -in $textCodecs) { return $ordinal }
            $ordinal++
        }
    } catch {}
    return $null
}

function Get-SubtitleBomEncoding {
    param([string]$Path)

    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $buf = New-Object byte[] 4
            $n = $fs.Read($buf, 0, 4)
        } finally {
            $fs.Dispose()
        }

        if ($n -ge 4) {
            if ($buf[0] -eq 0xFF -and $buf[1] -eq 0xFE -and $buf[2] -eq 0x00 -and $buf[3] -eq 0x00) { return 'UTF-32LE' }
            if ($buf[0] -eq 0x00 -and $buf[1] -eq 0x00 -and $buf[2] -eq 0xFE -and $buf[3] -eq 0xFF) { return 'UTF-32BE' }
        }
        if ($n -ge 3 -and $buf[0] -eq 0xEF -and $buf[1] -eq 0xBB -and $buf[2] -eq 0xBF) { return 'UTF-8' }
        if ($n -ge 2 -and $buf[0] -eq 0xFF -and $buf[1] -eq 0xFE) { return 'UTF-16LE' }
        if ($n -ge 2 -and $buf[0] -eq 0xFE -and $buf[1] -eq 0xFF) { return 'UTF-16BE' }
    } catch {}

    return $null
}

function Test-StrictUtf8File {
    param([string]$Path)

    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
        [void]$strictUtf8.GetString($bytes)
        return $true
    } catch {
        return $false
    }
}

function Invoke-FfmpegSubtitleConvertOnce {
    param(
        [string]$Ffmpeg,
        [string]$InputPath,
        [string]$OutputAss,
        [Nullable[int]]$SubtitleOrdinal,
        [string]$CharEnc
    )

    if (Test-Path -LiteralPath $OutputAss) {
        Remove-Item -LiteralPath $OutputAss -Force -ErrorAction SilentlyContinue
    }

    $args = @('-hide_banner','-loglevel','error','-y')
    if (-not [string]::IsNullOrWhiteSpace($CharEnc)) {
        $args += @('-sub_charenc',$CharEnc)
    }
    $args += @('-i',$InputPath)

    if ($SubtitleOrdinal -ne $null) {
        $args += @('-map',("0:s:{0}" -f $SubtitleOrdinal.Value))
    } else {
        $args += @('-map','0:s:0')
    }
    $args += @('-c:s','ass',$OutputAss)

    $oldErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Ffmpeg @args 2>$null
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldErrorActionPreference
    }

    return ($exitCode -eq 0 -and (Test-Path -LiteralPath $OutputAss -PathType Leaf))
}

function Invoke-FfmpegSubtitleConvert {
    param(
        [string]$Ffmpeg,
        [string]$InputPath,
        [string]$OutputAss,
        [Nullable[int]]$SubtitleOrdinal
    )

    # Embedded streams are decoded from the container and do not need
    # external text-file charset handling.
    if ($SubtitleOrdinal -ne $null) {
        if (Invoke-FfmpegSubtitleConvertOnce $Ffmpeg $InputPath $OutputAss $SubtitleOrdinal $null) {
            return
        }
        throw 'Subtitle conversion to ASS failed. Only text subtitle streams such as SRT/ASS/SSA/WebVTT are supported.'
    }

    # External subtitle charset handling.
    # Do not deliberately invoke FFmpeg once with a possibly wrong charset:
    # Windows PowerShell 5.1 may promote native stderr to a terminating error
    # when ErrorActionPreference is Stop.
    #
    # Unicode BOM  -> let FFmpeg autodetect it
    # Strict UTF-8 -> normal/default decode
    # Otherwise    -> GB18030 (covers common Chinese ANSI / GBK subtitles)
    $bomEnc = Get-SubtitleBomEncoding $InputPath

    if ($bomEnc) {
        $charEnc = $null
        $charLabel = $bomEnc + ' (BOM / autodetect)'
    } elseif (Test-StrictUtf8File $InputPath) {
        $charEnc = $null
        $charLabel = 'UTF-8'
    } else {
        $charEnc = 'GB18030'
        $charLabel = 'GB18030 fallback (GBK compatible)'
    }

    if (Invoke-FfmpegSubtitleConvertOnce $Ffmpeg $InputPath $OutputAss $null $charEnc) {
        Write-Host ('Subtitle charset : ' + $charLabel)
        return
    }

    throw ('Subtitle conversion to ASS failed using charset: ' + $charLabel + '. Only text subtitle files such as SRT/ASS/SSA/WebVTT are supported.')
}

function Set-AssStyle {
    param(
        [string]$AssPath,
        [string]$FontName,
        [int]$FontSize,
        [string]$PrimaryHex,
        [string]$BorderHex,
        [double]$Outline,
        [double]$Shadow,
        [int]$Alignment,
        [int]$AssMarginV,
        [int]$PlayResX,
        [int]$PlayResY
    )

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $lines = [IO.File]::ReadAllLines($AssPath)
    $primary = Convert-HexToAss $PrimaryHex
    $border = Convert-HexToAss $BorderHex

    $foundPlayResX = $false
    $foundPlayResY = $false
    $foundScaled = $false
    $styleFormat = $null
    $inStyles = $false
    $styleCount = 0

    for ($i=0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*PlayResX\s*:') { $lines[$i] = "PlayResX: $PlayResX"; $foundPlayResX=$true; continue }
        if ($line -match '^\s*PlayResY\s*:') { $lines[$i] = "PlayResY: $PlayResY"; $foundPlayResY=$true; continue }
        if ($line -match '^\s*ScaledBorderAndShadow\s*:') { $lines[$i] = 'ScaledBorderAndShadow: yes'; $foundScaled=$true; continue }

        if ($line -match '^\s*\[(.+)\]\s*$') {
            $inStyles = ($Matches[1] -ieq 'V4+ Styles')
            $styleFormat = $null
            continue
        }
        if (-not $inStyles) { continue }
        if ($line -match '^\s*Format\s*:\s*(.+)$') {
            $styleFormat = @($Matches[1].Split(',') | ForEach-Object { $_.Trim() })
            continue
        }
        if ($line -notmatch '^\s*Style\s*:\s*(.+)$' -or -not $styleFormat) { continue }

        $values = @($Matches[1].Split(','))
        if ($values.Count -lt $styleFormat.Count) { continue }
        $map = @{}
        for ($j=0; $j -lt $styleFormat.Count; $j++) { $map[$styleFormat[$j].ToLowerInvariant()] = $j }

        $set = {
            param($name,$value)
            $key=$name.ToLowerInvariant()
            if ($map.ContainsKey($key)) { $values[$map[$key]] = [string]$value }
        }
        & $set 'Fontname' $FontName
        & $set 'Fontsize' $FontSize
        & $set 'PrimaryColour' $primary
        & $set 'SecondaryColour' $primary
        & $set 'OutlineColour' $border
        & $set 'BackColour' $border
        & $set 'BorderStyle' 1
        & $set 'Outline' ([string]::Format([Globalization.CultureInfo]::InvariantCulture,'{0:0.##}',$Outline))
        & $set 'Shadow' ([string]::Format([Globalization.CultureInfo]::InvariantCulture,'{0:0.##}',$Shadow))
        & $set 'Alignment' $Alignment
        & $set 'MarginV' $AssMarginV
        & $set 'MarginL' 10
        & $set 'MarginR' 10

        $lines[$i] = 'Style: ' + ($values -join ',')
        $styleCount++
    }

    if ($styleCount -eq 0) { throw 'No ASS style section was found after subtitle conversion.' }

    # Insert missing script-info fields just before the first section after [Script Info].
    $insertAt = -1
    $scriptInfo = -1
    for ($i=0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match '^\s*\[Script Info\]\s*$') { $scriptInfo = $i; continue }
        if ($scriptInfo -ge 0 -and $i -gt $scriptInfo -and $lines[$i] -match '^\s*\[') { $insertAt = $i; break }
    }
    if ($insertAt -lt 0) { $insertAt = $lines.Length }

    $extra = New-Object System.Collections.Generic.List[string]
    if (-not $foundPlayResX) { $extra.Add("PlayResX: $PlayResX") }
    if (-not $foundPlayResY) { $extra.Add("PlayResY: $PlayResY") }
    if (-not $foundScaled) { $extra.Add('ScaledBorderAndShadow: yes') }
    if ($extra.Count -gt 0) {
        $newLines = New-Object System.Collections.Generic.List[string]
        for ($i=0; $i -lt $lines.Length; $i++) {
            if ($i -eq $insertAt) { foreach ($e in $extra) { $newLines.Add($e) } }
            $newLines.Add($lines[$i])
        }
        if ($insertAt -eq $lines.Length) { foreach ($e in $extra) { $newLines.Add($e) } }
        $lines = $newLines.ToArray()
    }

    [IO.File]::WriteAllLines($AssPath, $lines, $utf8)
}

try {
    $ffmpeg = Get-RequiredEnv 'FFMPEG'
    $ffprobe = [Environment]::GetEnvironmentVariable('FFPROBE')
    $inputVideo = Get-RequiredEnv 'INPUT'
    $outputAss = Get-RequiredEnv 'FG_SUB_OUT_ASS'
    $mode = [Environment]::GetEnvironmentVariable('FG_SUB_MODE')
    if ([string]::IsNullOrWhiteSpace($mode)) { $mode = 'OFF' }
    $mode = $mode.ToUpperInvariant()
    $externalPath = [Environment]::GetEnvironmentVariable('FG_SUB_PATH')

    $idx = 0
    [void][int]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_INDEX'), [ref]$idx)
    $font = [Environment]::GetEnvironmentVariable('FG_SUB_FONT')
    if ([string]::IsNullOrWhiteSpace($font)) { $font = 'huiwen-mincho' }
    $fontSize = 69
    [void][int]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_FONT_SIZE'), [ref]$fontSize)
    if ($fontSize -lt 6 -or $fontSize -gt 300) { $fontSize = 69 }
    $primaryHex = Normalize-HexColor ([Environment]::GetEnvironmentVariable('FG_SUB_PRIMARY_HEX')) 'FFFFFF'
    $borderHex = Normalize-HexColor ([Environment]::GetEnvironmentVariable('FG_SUB_BORDER_HEX')) '000000'
    $outline = 1.0
    [void][double]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_OUTLINE'), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$outline)
    $shadow = 1.0
    [void][double]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_SHADOW'), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$shadow)
    $marginV = 25
    [void][int]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_MARGINV'), [ref]$marginV)
    $playResX = 1920
    [void][int]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_PLAYRESX'), [ref]$playResX)
    $playResY = 1080
    [void][int]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_PLAYRESY'), [ref]$playResY)
    $barH = 0
    [void][int]::TryParse([Environment]::GetEnvironmentVariable('FG_SUB_BAR_H'), [ref]$barH)

    # GUI subtitle dimensions are defined as a 1920x1080 reference.
    # Scale by output width so 2.39:1 crops keep the same apparent subtitle
    # size as a 1920-wide 16:9 master, while 4K doubles the dimensions.
    $renderScale = 1.0
    if ($playResX -gt 0) { $renderScale = [double]$playResX / 1920.0 }
    if ($renderScale -le 0) { $renderScale = 1.0 }

    $renderFontSize = [int][Math]::Round($fontSize * $renderScale)
    if ($renderFontSize -lt 6) { $renderFontSize = 6 }

    $renderMarginV = [int][Math]::Round($marginV * $renderScale)
    if ($renderMarginV -lt 0) { $renderMarginV = 0 }

    $renderOutline = $outline * $renderScale
    $renderShadow = $shadow * $renderScale

    # User-facing MarginV is the gap below the active picture. When a lower
    # cinematic bar exists, top-align the subtitle inside that bar at
    # active-picture-bottom + scaled gap. Without a bar, fall back to normal
    # bottom-center placement using the same scaled gap as the bottom margin.
    $alignment = 2
    $assMarginV = $renderMarginV
    $placementLabel = "bottom-center / base-gap ${marginV}px / render-gap ${renderMarginV}px"
    if ($barH -gt 0 -and $barH -lt $playResY) {
        $alignment = 8
        $assMarginV = ($playResY - $barH) + $renderMarginV
        if ($assMarginV -gt ($playResY - 1)) { $assMarginV = $playResY - 1 }
        $placementLabel = "lower black bar / base-gap ${marginV}px / render-gap ${renderMarginV}px / centered"
    }

    if (Test-Path -LiteralPath $outputAss) { Remove-Item -LiteralPath $outputAss -Force }

    $sourceLabel = ''
    switch ($mode) {
        'EXTERNAL' {
            if ([string]::IsNullOrWhiteSpace($externalPath)) { $externalPath = Find-SameNameSubtitle $inputVideo }
            if (-not $externalPath -or -not (Test-Path -LiteralPath $externalPath -PathType Leaf)) { throw 'External subtitle file was not found.' }
            Invoke-FfmpegSubtitleConvert $ffmpeg $externalPath $outputAss $null
            $sourceLabel = 'External: ' + [IO.Path]::GetFileName($externalPath)
        }
        'EMBEDDED' {
            Invoke-FfmpegSubtitleConvert $ffmpeg $inputVideo $outputAss ([Nullable[int]]$idx)
            $sourceLabel = 'Embedded subtitle #' + ($idx + 1)
        }
        'AUTO' {
            $same = Find-SameNameSubtitle $inputVideo
            if ($same) {
                Invoke-FfmpegSubtitleConvert $ffmpeg $same $outputAss $null
                $sourceLabel = 'Auto same-name: ' + [IO.Path]::GetFileName($same)
            } else {
                $autoIndex = Get-FirstTextSubtitleOrdinal $ffprobe $inputVideo
                if ($null -eq $autoIndex) { throw 'No same-name external subtitle or embedded text subtitle was found.' }
                Invoke-FfmpegSubtitleConvert $ffmpeg $inputVideo $outputAss ([Nullable[int]][int]$autoIndex)
                $sourceLabel = 'Auto embedded subtitle #' + ([int]$autoIndex + 1)
            }
        }
        default { throw 'Subtitle mode is OFF or invalid.' }
    }

    Set-AssStyle $outputAss $font $renderFontSize $primaryHex $borderHex $renderOutline $renderShadow $alignment $assMarginV $playResX $playResY
    Write-Host ('Subtitle prepared: ' + $sourceLabel)
    Write-Host ("Subtitle style   : {0} / base {1}px -> render {2}px / #{3} / border-shadow #{4} / outline {5:0.##} / shadow {6:0.##} / {7}" -f $font,$fontSize,$renderFontSize,$primaryHex,$borderHex,$renderOutline,$renderShadow,$placementLabel)
    exit 0
} catch {
    Write-Host ('ERROR: Subtitle preparation failed: ' + $_.Exception.Message)
    try {
        $out = [Environment]::GetEnvironmentVariable('FG_SUB_OUT_ASS')
        if ($out -and (Test-Path -LiteralPath $out)) { Remove-Item -LiteralPath $out -Force }
    } catch {}
    exit 1
}
