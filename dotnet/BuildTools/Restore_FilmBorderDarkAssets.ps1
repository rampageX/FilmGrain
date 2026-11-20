param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)
$ErrorActionPreference = 'Stop'

# Restore the EXACT Dark 35mm cache accepted by the user. Do not rebuild it
# from PNG: the source RAW also contains non-displayed RGB in alpha-zero pixels.
$folder = Join-Path $Root 'Utils\_FilmBorders'
$png = Join-Path $folder 'cinema35_dark_1920x1080.png'
$archive = Join-Path $folder 'cinema35_dark_1920x1080_rgba.raw.gz'
$raw = Join-Path $folder 'cinema35_dark_1920x1080_rgba.raw'
$expectedPng = '921d54afb402c4eb66aa85bdd9343e85455eab885c09fff3b26a5c15f2c830d0'
$expectedArchive = '3248de0f141d8426b646256ebb0ae54840d034ade829fc9e94f1a96749d68540'
$expectedRaw = 'd51d6f9496c939d6b8b8ab60e5cd96bddc4111497ba7e9d010669b479b781b66'

foreach ($item in @(@($png,$expectedPng),@($archive,$expectedArchive))) {
    if (-not (Test-Path -LiteralPath $item[0] -PathType Leaf)) {
        throw ('Missing accepted Dark source asset: ' + $item[0])
    }
    $actual=(Get-FileHash -LiteralPath $item[0] -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $item[1]) {
        throw ('Dark source SHA256 mismatch: ' + $item[0] + ' actual=' + $actual)
    }
}
if (Test-Path -LiteralPath $raw -PathType Leaf) {
    $actual=(Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $expectedRaw -and (Get-Item -LiteralPath $raw).Length -eq 8294400) {
        Write-Host 'PASS: accepted Dark PNG + byte-exact RAW already present'
        return
    }
}

$temp = $raw + '.tmp-' + [guid]::NewGuid().ToString('N')
try {
    $source=[IO.File]::OpenRead($archive)
    try {
        $gzip=New-Object IO.Compression.GZipStream($source,[IO.Compression.CompressionMode]::Decompress)
        try {
            $target=[IO.File]::Create($temp)
            try { $gzip.CopyTo($target) }
            finally { $target.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $source.Dispose() }
    $result=(Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($result -ne $expectedRaw -or (Get-Item -LiteralPath $temp).Length -ne 8294400) {
        throw ('Restored Dark RAW SHA256/length mismatch: ' + $result)
    }
    Move-Item -LiteralPath $temp -Destination $raw -Force
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}
Write-Host 'PASS: accepted Dark source assets, RAW restored byte-for-byte (SHA256 verified)'
