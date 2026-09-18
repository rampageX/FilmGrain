param([ValidateSet('LIGHT','MEDIUM','HEAVY')][string]$Preset = 'MEDIUM',[string]$FFmpegPath = '')
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Generated = Join-Path $Root 'Generated'
New-Item -ItemType Directory -Force -Path $Generated | Out-Null

$ffmpegCandidates = @(
    $FFmpegPath,
    (Join-Path (Split-Path -Parent $Root) '..\\ffmpeg.exe'),
    'E:\\EnCoder\\FFMpeg\\x64\\bin\\ffmpeg.exe',
    'ffmpeg.exe'
)
$FFmpeg = $null
foreach ($candidate in $ffmpegCandidates) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    try {
        if ($candidate -eq 'ffmpeg.exe') {
            $cmd = Get-Command $candidate -ErrorAction Stop
            $FFmpeg = $cmd.Source
            break
        }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $FFmpeg = (Resolve-Path -LiteralPath $candidate).Path
            break
        }
    } catch {}
}
if (-not $FFmpeg) { throw 'ffmpeg.exe not found for FilmGrainSimplified noise preparation.' }

$templatePath = Join-Path $Root 'FilmGrainSimplified.hook.template'
$fallback = Join-Path $Root 'NoiseFallback_512x512_RGBA8.rgba'
$raw = Join-Path $Generated 'Noise_512x512_RGBA8.rgba'
$png = Join-Path $Generated 'LDR_RGBA_0.png'
$source = 'FALLBACK_PERF_ONLY'

if (-not (Test-Path -LiteralPath $raw) -or (Get-Item -LiteralPath $raw).Length -ne 1048576) {
    Copy-Item -LiteralPath $fallback -Destination $raw -Force
    try {
        $url = 'https://raw.githubusercontent.com/kanzwataru/filmgrain-simplified/master/resources/LDR_RGBA_0.png'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $png -TimeoutSec 20
        & $FFmpeg -hide_banner -loglevel error -y -i $png -frames:v 1 -pix_fmt rgba -f rawvideo $raw
        if ($LASTEXITCODE -ne 0) { throw 'FFmpeg failed to decode upstream noise PNG.' }
        if ((Get-Item -LiteralPath $raw).Length -ne 1048576) { throw 'Unexpected upstream noise size.' }
        $source = 'ORIGINAL_UPSTREAM_LDR_RGBA_0'
    } catch {
        Copy-Item -LiteralPath $fallback -Destination $raw -Force
        Write-Host ('[FGSIM WARN] Original upstream noise unavailable: ' + $_.Exception.Message)
        Write-Host '[FGSIM WARN] Using bundled fallback texture. Visual appearance may differ from the validated upstream-noise tests.'
    }
} else {
    $sourceFile = Join-Path $Generated 'NOISE_SOURCE.txt'
    if (Test-Path -LiteralPath $sourceFile) {
        $source = ([System.IO.File]::ReadAllText($sourceFile)).Trim()
    }
}

$bytes = [System.IO.File]::ReadAllBytes($raw)
if ($bytes.Length -ne 1048576) { throw 'Noise texture must be exactly 512x512 RGBA8.' }
$hex = ([System.BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()
$sb = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt $hex.Length; $i += 256) {
    $n = [Math]::Min(256, $hex.Length - $i)
    [void]$sb.Append($hex.Substring($i, $n))
    [void]$sb.Append("`n")
}
$hexText = $sb.ToString()
$template = [System.IO.File]::ReadAllText($templatePath)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$variants = @(
    @{ Name='LIGHT'; Strength='0.10' },
    @{ Name='MEDIUM'; Strength='0.20' },
    @{ Name='HEAVY'; Strength='0.30' }
)
foreach ($v in $variants) {
    $body = $template.Replace('@@TILE_SIZE@@','2').Replace('@@STRENGTH@@',$v.Strength).Replace('@@HL_START@@','0.55').Replace('@@HL_END@@','0.85').Replace('@@HL_REDUCE@@','0.50').Replace('@@NOISE_HEX@@',$hexText)
    [System.IO.File]::WriteAllText((Join-Path $Generated ('FilmGrainSimplified_' + $v.Name + '.hook')), $body, $utf8NoBom)
}
[System.IO.File]::WriteAllText((Join-Path $Generated 'NOISE_SOURCE.txt'), ($source + "`r`n"), $utf8NoBom)
$target = Join-Path $Generated ('FilmGrainSimplified_' + $Preset + '.hook')
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Requested FilmGrainSimplified hook was not generated.' }
Write-Host ('[FGSIM] Preset=' + $Preset + ' Noise=' + $source)
