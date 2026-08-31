@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "LUT_ROOT=E:\Adobe Portable\LUTs"
set "DEST_ROOT=%LUT_ROOT%\BT.709"
set "REPORT=%DEST_ROOT%\BT709_LUT_Scan_Report.csv"
set "PS1=%TEMP%\Collect_BT709_LUTs_%RANDOM%_%RANDOM%.ps1"

echo.
echo ============================================================
echo              BT.709 LUT Collector
echo ============================================================
echo.
echo Source:
echo   "%LUT_ROOT%"
echo.
echo Destination:
echo   "%DEST_ROOT%"
echo.

if not exist "%LUT_ROOT%" (
    echo ERROR: Source LUT folder does not exist.
    echo.
    pause
    exit /b 1
)

if not exist "%DEST_ROOT%" mkdir "%DEST_ROOT%"
if errorlevel 1 (
    echo ERROR: Could not create destination folder.
    echo.
    pause
    exit /b 1
)

rem ------------------------------------------------------------
rem Generate a temporary PowerShell script.
rem This avoids fragile CMD caret/quote continuation rules.
rem ------------------------------------------------------------

> "%PS1%" echo $ErrorActionPreference = 'Stop'
>>"%PS1%" echo $root = [IO.Path]::GetFullPath($env:LUT_ROOT).TrimEnd('\')
>>"%PS1%" echo $dest = [IO.Path]::GetFullPath($env:DEST_ROOT).TrimEnd('\')
>>"%PS1%" echo $report = $env:REPORT
>>"%PS1%" echo.
>>"%PS1%" echo $positive = '(?i)(BT[\s._-]*709^|Rec[\s._-]*709^|Rec\.?[\s._-]*709^|ITU[\s._-]*R[\s._-]*BT[\s._-]*709)'
>>"%PS1%" echo $negative = '(?i)(Cineon^|LogC^|ARRI[\s._-]*Log^|S[\s._-]*Log^|SLog^|V[\s._-]*Log^|VLog^|C[\s._-]*Log^|CLog^|D[\s._-]*Log^|DLog^|F[\s._-]*Log^|FLog^|N[\s._-]*Log^|NLog^|BMD[\s._-]*Film^|Blackmagic[\s._-]*(Film^|Wide[\s._-]*Gamut)^|DaVinci[\s._-]*Wide[\s._-]*Gamut^|ACES^|ACEScc^|ACEScct^|Alexa[\s._-]*Log^|Canon[\s._-]*Log^|RED[\s._-]*Log^|REDWideGamut^|Panasonic[\s._-]*V[\s._-]*Gamut^|Sony[\s._-]*S[\s._-]*Gamut^|PQ^|ST[\s._-]*2084^|HLG^|HDR10^|DCI[\s._-]*P3^|Display[\s._-]*P3)'
>>"%PS1%" echo.
>>"%PS1%" echo $results = New-Object System.Collections.Generic.List[object]
>>"%PS1%" echo $files = Get-ChildItem -LiteralPath $root -Filter '*.cube' -File -Recurse ^| Where-Object { -not $_.FullName.StartsWith($dest,[StringComparison]::OrdinalIgnoreCase) }
>>"%PS1%" echo.
>>"%PS1%" echo foreach($f in $files) {
>>"%PS1%" echo     $rel = $f.FullName.Substring($root.Length).TrimStart('\')
>>"%PS1%" echo     $headLines = Get-Content -LiteralPath $f.FullName -TotalCount 120 -ErrorAction SilentlyContinue
>>"%PS1%" echo     $head = $headLines -join "`n"
>>"%PS1%" echo     $probe = $f.Name + "`n" + $head
>>"%PS1%" echo.
>>"%PS1%" echo     $inputLines = ($headLines ^| Where-Object { $_ -match '(?i)^\s*#?\s*(Input^|Input Color ?Space^|Input_ColorSpace^|Source^|Source Color ?Space)\s*[:=]' }) -join ' '
>>"%PS1%" echo.
>>"%PS1%" echo     $status = 'Ambiguous'
>>"%PS1%" echo     $reason = 'No explicit BT.709/Rec.709 input marker'
>>"%PS1%" echo.
>>"%PS1%" echo     if($inputLines) {
>>"%PS1%" echo         if($inputLines -match $negative) {
>>"%PS1%" echo             $status = 'Skipped'
>>"%PS1%" echo             $reason = 'Explicit non-BT.709 input: ' + $inputLines.Trim()
>>"%PS1%" echo         } elseif($inputLines -match $positive) {
>>"%PS1%" echo             $status = 'Copied'
>>"%PS1%" echo             $reason = 'Explicit BT.709/Rec.709 input'
>>"%PS1%" echo         }
>>"%PS1%" echo     } elseif(($probe -match $positive) -and -not ($probe -match $negative)) {
>>"%PS1%" echo         $status = 'Copied'
>>"%PS1%" echo         $reason = 'BT.709/Rec.709 marker in LUT name/header; no conflicting input marker'
>>"%PS1%" echo     } elseif($probe -match $negative) {
>>"%PS1%" echo         $status = 'Skipped'
>>"%PS1%" echo         $reason = 'Log/HDR/ACES/P3 marker found'
>>"%PS1%" echo     }
>>"%PS1%" echo.
>>"%PS1%" echo     if($status -eq 'Copied') {
>>"%PS1%" echo         $target = Join-Path $dest $rel
>>"%PS1%" echo         $tdir = Split-Path -Parent $target
>>"%PS1%" echo         if(-not (Test-Path -LiteralPath $tdir)) {
>>"%PS1%" echo             New-Item -ItemType Directory -Path $tdir -Force ^| Out-Null
>>"%PS1%" echo         }
>>"%PS1%" echo         Copy-Item -LiteralPath $f.FullName -Destination $target -Force
>>"%PS1%" echo     }
>>"%PS1%" echo.
>>"%PS1%" echo     $results.Add([pscustomobject]@{
>>"%PS1%" echo         Status       = $status
>>"%PS1%" echo         RelativePath = $rel
>>"%PS1%" echo         Reason       = $reason
>>"%PS1%" echo         Source       = $f.FullName
>>"%PS1%" echo     }) ^| Out-Null
>>"%PS1%" echo }
>>"%PS1%" echo.
>>"%PS1%" echo $results ^| Sort-Object Status,RelativePath ^| Export-Csv -LiteralPath $report -NoTypeInformation -Encoding UTF8
>>"%PS1%" echo.
>>"%PS1%" echo $copied = @($results ^| Where-Object Status -eq 'Copied').Count
>>"%PS1%" echo $skipped = @($results ^| Where-Object Status -eq 'Skipped').Count
>>"%PS1%" echo $ambiguous = @($results ^| Where-Object Status -eq 'Ambiguous').Count
>>"%PS1%" echo.
>>"%PS1%" echo Write-Host ''
>>"%PS1%" echo Write-Host '============================================================'
>>"%PS1%" echo Write-Host 'Scan complete'
>>"%PS1%" echo Write-Host '============================================================'
>>"%PS1%" echo Write-Host ('Copied    : ' + $copied)
>>"%PS1%" echo Write-Host ('Skipped   : ' + $skipped)
>>"%PS1%" echo Write-Host ('Ambiguous : ' + $ambiguous)
>>"%PS1%" echo Write-Host ''
>>"%PS1%" echo Write-Host ('Report: ' + $report)
>>"%PS1%" echo Write-Host ''
>>"%PS1%" echo if($ambiguous -gt 0) { Write-Host 'NOTE: Ambiguous LUTs were NOT copied. Review the CSV if needed.' }

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
set "RC=%ERRORLEVEL%"

del /q "%PS1%" >nul 2>&1

if not "%RC%"=="0" (
    echo.
    echo ERROR: LUT scan failed. PowerShell exit code: %RC%
    echo.
    pause
    exit /b %RC%
)

echo.
echo Done.
echo.
pause
endlocal
