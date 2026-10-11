[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+(?:\.\d+){2,3}$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$path = Join-Path $RepositoryRoot 'dotnet\FilmGrain_Studio_NET_Preview.cs'
$text = [System.IO.File]::ReadAllText($path)
$pattern = '(internal const string ReleaseVersion\s*=\s*)"([0-9.]+)"'
$nativeVersionMatches = [regex]::Matches($text, $pattern)
if ($nativeVersionMatches.Count -ne 1) { throw 'Native ReleaseVersion must occur exactly once.' }
if ($text -notmatch 'private const string TestBuild\s*=\s*"Stable"') {
    throw 'Native source is not a formal Stable build.'
}
$nativeVersionMatch = $nativeVersionMatches[0]
$replacement = $nativeVersionMatch.Groups[1].Value + [char]34 + $Version + [char]34
$text = $text.Substring(0, $nativeVersionMatch.Index) + $replacement + $text.Substring($nativeVersionMatch.Index + $nativeVersionMatch.Length)
$encoding = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($path, $text, $encoding)
