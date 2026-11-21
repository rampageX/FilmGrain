[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Exe
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw 'FGS test executable missing.' }
$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Exe).Path)
$type = $assembly.GetType('FilmGrainStudioPreview.NativeLibraryTools', $true)
$flags = [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic
$method = $type.GetMethod('RunProcessEnvironment', $flags)
if ($null -eq $method) { throw 'NativeLibraryTools.RunProcessEnvironment is missing.' }

$child = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $child -PathType Leaf)) { throw 'Windows PowerShell executable missing.' }
$childArgs = [string[]]@(
    '-NoProfile',
    '-NonInteractive',
    '-Command',
    "[Console]::Out.WriteLine('FGS_STDOUT'); [Console]::Error.WriteLine('FGS_STDERR')"
)
# Retain the nested string[] as a single reflection argument; do not let PowerShell flatten it.
$call = [object[]]@($null, $null, $null, $null, $null, $null, $null)
$call[0] = $child
$call[1] = $childArgs
$call[2] = $env:TEMP
$call[3] = $null
$call[4] = [int]30000
$call[5] = [System.Threading.CancellationToken]::None
$call[6] = $null

$exitCode = [int]$method.Invoke($null, $call)
if ($exitCode -ne 0) { throw ('Null-log subprocess failed: exit=' + $exitCode) }
Write-Host 'PASS: stdout and stderr with null Action<string> completed without throwing or terminating FGS.'
