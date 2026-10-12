[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RepositoryRoot,
    [Parameter()][string]$TargetRoot = '',
    [Parameter(Mandatory=$true)][ValidateSet('Stage','Overlay')][string]$Mode
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'release_layout.ps1')
if ($Mode -eq 'Overlay') {
    if (-not $TargetRoot) { throw 'Overlay requires isolated TargetRoot.' }
    Copy-ReleaseWorkingTree -SourceRoot $RepositoryRoot -TargetRoot $TargetRoot
} else {
    Add-ReleaseSource -Root $RepositoryRoot
}
