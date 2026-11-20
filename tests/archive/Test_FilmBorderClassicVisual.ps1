# Validate generated FGS Classic Black frame mask: bright perforations, black film, clear aperture.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path 'FilmGrain_Studio.exe').Path)
$type = $assembly.GetType('FilmGrainStudioPreview.FilmBorderCatalogCore', $true)
$method = $type.GetMethod('List', [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $method) { throw 'FilmBorderCatalogCore.List not found' }
$method.Invoke($null, @([string]$env:GITHUB_WORKSPACE)) | Out-Null
$root = Join-Path $env:GITHUB_WORKSPACE 'Utils\_FilmBorders'
$png = Join-Path $root 'fgs_classic_black_135.png'
$raw = Join-Path $root 'fgs_classic_black_135.raw'
if (-not (Test-Path $png) -or (Get-Item $raw).Length -ne (1920*1080*4)) { throw 'Classic PNG/RGBA not generated' }
$image = New-Object System.Drawing.Bitmap($png)
try {
    $topHole = $image.GetPixel(160, 120)
    $bottomHole = $image.GetPixel(160, 966)
    $film = $image.GetPixel(300, 120)
    $window = $image.GetPixel(960, 540)
    if ($topHole.A -ne 255 -or $topHole.R -lt 200 -or $bottomHole.R -lt 200 -or
        $film.R -gt 35 -or $window.A -ne 0) {
        throw "Classic mask check failed: top=$topHole bottom=$bottomHole black=$film aperture=$window"
    }
    Write-Host 'PASS Classic Black: top/bottom perforations, black substrate, transparent source aperture'
}
finally { $image.Dispose() }
