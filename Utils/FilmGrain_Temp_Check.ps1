param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [Parameter(Mandatory=$true)][string]$TempRoot,
    [ValidateSet('AV1','HEVC','X264','NOREENCODE')][string]$Mode = 'HEVC',
    [switch]$HdrWorkFile,
    [switch]$OutputCheck
)

$ErrorActionPreference = 'Stop'

try {
    $TempRoot = [System.IO.Path]::GetFullPath($TempRoot.Trim().Trim('"'))
    [void][System.IO.Directory]::CreateDirectory($TempRoot)
    $testPath = Join-Path $TempRoot ('.fgs_write_test_' + [guid]::NewGuid().ToString('N') + '.tmp')
    [System.IO.File]::WriteAllText($testPath, 'FGS')
    Remove-Item -LiteralPath $testPath -Force

    $sourceBytes = [double](Get-Item -LiteralPath $InputPath).Length
    $factor = if ($OutputCheck) { 1.2 } else { switch ($Mode) {
        'NOREENCODE' { 2.2 }
        'AV1' { 2.6 }
        default { 1.0 }
    }}
    if ($HdrWorkFile -and -not $OutputCheck) { $factor += 4.0 }
    $required = [Math]::Max(512MB, [Math]::Ceiling($sourceBytes * $factor))
    $reserve = [Math]::Max(2GB, [Math]::Ceiling($required * 0.2))
    $driveRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($TempRoot))
    $free = [double](New-Object System.IO.DriveInfo($driveRoot)).AvailableFreeSpace

    Write-Output ('TEMP_ROOT=' + $TempRoot)
    Write-Output ('FREE_GB=' + [Math]::Round($free / 1GB, 2).ToString([Globalization.CultureInfo]::InvariantCulture))
    Write-Output ('REQUIRED_GB=' + [Math]::Round($required / 1GB, 2).ToString([Globalization.CultureInfo]::InvariantCulture))
    Write-Output ('RESERVE_GB=' + [Math]::Round($reserve / 1GB, 2).ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($free -lt ($required + $reserve)) { exit 2 }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
