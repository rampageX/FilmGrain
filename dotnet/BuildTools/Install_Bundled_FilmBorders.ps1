[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$DestinationRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$bundle = Join-Path $SourceRoot 'Utils\_FilmBorders\FGS_Bundled_15.zip'
$manifest = Join-Path $SourceRoot 'Utils\_FilmBorders\FGS_Bundled_15.manifest.txt'
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) { throw 'Required 15-border source bundle is missing.' }
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Required 15-border SHA256 manifest is missing.' }

$expected = @{}
$layoutCount = @{ SIDE = 0; TOP_BOTTOM = 0 }
foreach ($line in [IO.File]::ReadAllLines($manifest)) {
    if (-not $line.Trim() -or $line.StartsWith('#')) { continue }
    $match = [regex]::Match($line, '^([0-9a-fA-F]{64})  ([A-F0-9]{32}\.(?:png|raw|label|aperture))$')
    if (-not $match.Success) { throw ('Malformed 15-border source manifest line: ' + $line) }
    $name = $match.Groups[2].Value
    if ($expected.ContainsKey($name)) { throw ('Duplicate manifest entry: ' + $name) }
    $expected.Add($name, $match.Groups[1].Value.ToLowerInvariant())
}
if ($expected.Count -ne 60) { throw ('Expected 60 bundled files, got ' + $expected.Count) }

$targetRoot = Join-Path $DestinationRoot 'Utils\_FilmBorders\User'
[IO.Directory]::CreateDirectory($targetRoot) | Out-Null
$seen = @{}
$ids = @{}
$archive = [IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    if ($entries.Count -ne 60) { throw ('15-border bundle must contain 60 files, got ' + $entries.Count) }
    foreach ($entry in $entries) {
        $name = $entry.FullName
        if ($name -notmatch '^[A-F0-9]{32}\.(png|raw|label|aperture)$') {
            throw ('Unexpected or nested border archive entry: ' + $name)
        }
        if (-not $expected.ContainsKey($name) -or $seen.ContainsKey($name)) {
            throw ('Unexpected/duplicate border entry: ' + $name)
        }
        $seen[$name] = $true
        $id = $name.Substring(0,32)
        $ids[$id] = $true
        if ($name.EndsWith('.raw') -and $entry.Length -ne (1920L * 1080L * 4L)) {
            throw ('Incorrect bundled RAW length: ' + $name)
        }
        $target = Join-Path $targetRoot $name
        $input = $entry.Open()
        $output = $null
        try {
            $output = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            $input.CopyTo($output)
        } finally {
            if ($null -ne $output) { $output.Dispose() }
            $input.Dispose()
        }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected[$name]) { throw ('Bundled border SHA256 mismatch: ' + $name) }
    }
} finally {
    $archive.Dispose()
}
if ($seen.Count -ne 60 -or $ids.Count -ne 15) { throw '15-border collection is incomplete.' }
foreach ($id in $ids.Keys) {
    foreach ($extension in @('.png','.raw','.label','.aperture')) {
        if (-not $seen.ContainsKey($id + $extension)) { throw ('Incomplete border asset set: ' + $id) }
    }
    $parts = ([IO.File]::ReadAllText((Join-Path $targetRoot ($id + '.aperture')),[Text.Encoding]::UTF8).Trim()).Split([char]9)
    if ($parts.Length -ne 3 -or -not $layoutCount.ContainsKey($parts[2])) {
        throw ('Invalid border aperture layout: ' + $id)
    }
    $w = 0
    $h = 0
    if (-not [int]::TryParse($parts[0],[ref]$w) -or -not [int]::TryParse($parts[1],[ref]$h)) {
        throw ('Invalid aperture dimensions: ' + $id)
    }
    if ($parts[2] -eq 'SIDE') {
        if ($w -lt 1600 -or $w -gt 1780 -or $h -lt 900 -or $h -gt 1020) {
            throw ('Invalid SIDE aperture: ' + $id)
        }
    } else {
        if ($w -lt 1600 -or $w -gt 1910 -or $h -lt 700 -or $h -gt 860) {
            throw ('Invalid TOP_BOTTOM aperture: ' + $id)
        }
    }
    $layoutCount[$parts[2]] += 1
    $label = [IO.File]::ReadAllText((Join-Path $targetRoot ($id + '.label')),[Text.Encoding]::UTF8).Trim()
    if (-not $label -or $label.Length -gt 40) { throw ('Invalid bundled border name: ' + $id) }
}
if ($layoutCount['SIDE'] -ne 9 -or $layoutCount['TOP_BOTTOM'] -ne 6) {
    throw ('Wrong bundled layout counts: SIDE=' + $layoutCount['SIDE'] + ' TOP_BOTTOM=' + $layoutCount['TOP_BOTTOM'])
}
Write-Host 'PASS: preinstalled FGS film borders: 15 verified styles, 60 SHA256-matched files, 9 SIDE / 6 TOP_BOTTOM.'
