param([string]$Exe=(Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference='Stop'
$tmp=Join-Path $PSScriptRoot 'UiAudit'
$report=Join-Path $tmp 'UI_Startup_Result.txt'
[void][IO.Directory]::CreateDirectory($tmp)
$previousTemp=$env:TEMP;$previousTmp=$env:TMP
$env:TEMP=$tmp;$env:TMP=$tmp
try {
 $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
 if(-not (Test-Path -LiteralPath $csc)){$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'}
 $runner=Join-Path $tmp 'UIStartupSmoke.exe'
 & $csc /nologo /utf8output /target:exe /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/out:'+$runner) (Join-Path $PSScriptRoot 'UIStartupAudit.cs')
 if($LASTEXITCODE -ne 0){throw 'UI startup runner compilation failed.'}
 if(-not ('BenchProcess' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'UIAuditProcess.cs')}
 $p=New-Object BenchProcess
 try {
  $p.Start($runner,[string[]]@([IO.Path]::GetFullPath($Exe),$report),[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')))
  while(-not $p.Done){if($p.Seconds -gt 60){throw 'UI startup runner timed out.'};Start-Sleep -Milliseconds 100}
  $code=$p.Finish()
  if($p.Output){Write-Host $p.Output}
  if($code -ne 0){throw ('UI startup check failed: '+$p.Error)}
 } finally {$p.Dispose()}
 $global:LASTEXITCODE=0
} catch {
 [IO.File]::AppendAllText($report,('FAIL '+($_|Out-String)),(New-Object Text.UTF8Encoding($true)))
 $global:LASTEXITCODE=1
 throw
} finally {$env:TEMP=$previousTemp;$env:TMP=$previousTmp}
