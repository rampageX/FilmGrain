[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture

function Convert-ToRate([string]$Value) {
    $v = ($Value -replace '\s', '')
    if ($v -match '^([0-9]+)/([0-9]+)$') {
        $n = [long]$Matches[1]
        $d = [long]$Matches[2]
        if ($d -eq 0) { throw 'zero denominator' }
    } elseif ($v -match '^([0-9]+(?:\.[0-9]+)?)$') {
        $parsed = [decimal]::Parse($v, [Globalization.NumberStyles]::AllowDecimalPoint, $culture)
        $n = [long][decimal]::Round($parsed * 1000000, 0, [MidpointRounding]::AwayFromZero)
        $d = 1000000L
    } else { throw 'invalid frame-rate syntax' }
    if ($d -lt 0) { $n = -$n; $d = -$d }
    if ($n -le 0 -or $d -le 0) { throw 'frame rate must be positive' }
    $a = [Math]::Abs($n); $b = $d
    while ($b -ne 0) { $t = $a % $b; $a = $b; $b = $t }
    if ($a -gt 1) { $n = [long]($n / $a); $d = [long]($d / $a) }
    return [pscustomobject]@{ N = $n; D = $d }
}

try {
    $requested = $env:FG_INTERPOLATION_TARGET_FPS
    if ([string]::IsNullOrWhiteSpace($requested)) { $requested = '60' }
    $requested = $requested.Trim().ToLowerInvariant()
    if ($requested.EndsWith('x')) {
        $factor = Convert-ToRate $requested.Substring(0, $requested.Length - 1)
        $source = Convert-ToRate $env:FPS
        $n = [long]($factor.N * $source.N)
        $d = [long]($factor.D * $source.D)
        $rate = [pscustomobject]@{ N = $n; D = $d }
    } else { $rate = Convert-ToRate $requested }
    $fps = [double]$rate.N / [double]$rate.D
    if ($fps -lt 1 -or $fps -gt 1000) { throw 'output frame rate must be between 1 and 1000 fps' }
    $tag = $fps.ToString('0.###', $culture).Replace('.', '_')
    [Console]::Out.Write($rate.N.ToString($culture) + '|' + $rate.D.ToString($culture) + '|' + $tag)
    exit 0
} catch {
    [Console]::Error.WriteLine('Invalid interpolation target frame rate: ' + $_.Exception.Message)
    exit 2
}
