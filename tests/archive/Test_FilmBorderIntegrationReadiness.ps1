param([switch]$RequireDarkAssets)
$ErrorActionPreference = 'Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$build=Get-Content -LiteralPath (Join-Path $root 'dotnet\build_NET_Preview.bat') -Raw
$motion=[regex]::Matches($build,'FilmGrain_FilmBorderMotionCore\.cs').Count
$catalog=[regex]::Matches($build,'FilmGrain_FilmBorderCatalog\.cs').Count
if($motion -ne 2 -or $catalog -ne 2) { throw "Duplicate/missing C# compile inputs: motion=$motion catalog=$catalog (expected once per build target)" }
$ui=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_Studio_NET_Preview.cs') -Raw
if($ui -match 'UpdateFilmBorderButton\(\);\s*UpdateFilmBorderButton\(\);') { throw 'Duplicate Film Border UI refresh remains' }
$config=Get-Content -LiteralPath (Join-Path $root 'FilmGrain_Config.default.ini') -Raw
foreach($key in @('FILM_BORDER_MODE','FILM_BORDER_AMP','FILM_BORDER_SPEED','FILM_BORDER_FREQUENCY')) {
    if($config -notmatch "(?m)^$key=") { throw "Missing default configuration: $key" }
}
foreach($file in @('docs/film-border/FILM_BORDER_ASSET_SPEC.md','docs/film-border/FILM_BORDER_INTEGRATION.md')) {
    if(-not (Test-Path -LiteralPath (Join-Path $root $file))) { throw "Missing documentation: $file" }
}
$dir=Join-Path $root 'Utils\_FilmBorders'
$png=Join-Path $dir 'cinema35_dark_1920x1080.png'
$raw=Join-Path $dir 'cinema35_dark_1920x1080_rgba.raw'
$missing=@()
if(-not (Test-Path -LiteralPath $png -PathType Leaf)) { $missing+= $png }
if(-not (Test-Path -LiteralPath $raw -PathType Leaf)) { $missing+= $raw }
if($missing.Count -eq 0) {
    if((Get-Item -LiteralPath $raw).Length -ne (1920*1080*4)) {
        throw 'Original Dark RGBA byte length is invalid (expected 8294400)'
    }
    Add-Type -AssemblyName System.Drawing
    $image=New-Object System.Drawing.Bitmap($png)
    try {
        if($image.Width -ne 1920 -or $image.Height -ne 1080) {
            throw 'Original Dark PNG dimensions are not 1920x1080'
        }
    } finally { $image.Dispose() }
    $accepted='d51d6f9496c939d6b8b8ab60e5cd96bddc4111497ba7e9d010669b479b781b66'
    if((Get-FileHash -LiteralPath $raw -Algorithm SHA256).Hash.ToLowerInvariant() -ne $accepted) {
        throw 'Accepted Dark RAW SHA-256 mismatch'
    }
    Write-Host 'PASS: original Dark assets present, SHA256 matched accepted RAW'
} elseif($RequireDarkAssets) {
    throw ("Release blocked: original Dark assets missing: " + ($missing -join '; '))
} else {
    Write-Warning ("Not yet a clean-install release: Dark assets missing: " + ($missing -join '; '))
}
Write-Host 'PASS: Film Border integration source/config/documentation checks'
