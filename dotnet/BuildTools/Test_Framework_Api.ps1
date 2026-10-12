[CmdletBinding()]
param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($env:OS -ne 'Windows_NT') { throw 'Windows Framework runtime audit requires Windows.' }
$source = Join-Path $PSScriptRoot 'FrameworkApiAudit.cs'
$cecil = Join-Path $PSScriptRoot 'Mono.Cecil.dll'
if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $cecil -PathType Leaf)) { throw 'Required Framework audit files are missing.' }
if ((Get-FileHash -LiteralPath $cecil -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b') { throw 'Mono.Cecil audit dependency hash mismatch.' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw 'Windows Framework csc.exe is missing.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$targets = @($Executable)
if (-not $Executable) { $targets = @((Join-Path $root 'FilmGrain_Studio.exe'), (Join-Path $root 'dotnet\build\FilmGrain_Studio_NET_Preview.exe')) }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('FGS_FrameworkAudit_' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    $audit = Join-Path $temp 'FrameworkApiAudit.exe'
    Copy-Item -LiteralPath $cecil -Destination (Join-Path $temp 'Mono.Cecil.dll')
    $compileArgs = @('/nologo', '/target:exe', '/utf8output', ('/out:' + $audit), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', ('/reference:' + $cecil), $source)
    & $csc @compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Framework API auditor compilation failed.' }
    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw ('Product executable is missing: ' + $target) }
        & $audit $target '--windows-runtime'
        if ($LASTEXITCODE -ne 0) { throw ('Product Framework API audit rejected: ' + $target) }
    }
    if ($targets.Count -eq 2 -and (Get-FileHash -LiteralPath $targets[0] -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $targets[1] -Algorithm SHA256).Hash) { throw 'Product executables differ.' }
}
finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
