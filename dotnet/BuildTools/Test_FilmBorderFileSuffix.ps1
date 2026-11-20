# Validate distinct filename suffixes for Dark, Classic and user designs,
# and verify both NVENC/HEVC and software x264/x265 naming use one shared value.
$ErrorActionPreference = 'Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe=Join-Path $root 'FilmGrain_Studio.exe'
$assembly=[Reflection.Assembly]::LoadFrom($exe)
$type=$assembly.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore', $true)
$method=$type.GetMethod('OutputFileSuffix',[Reflection.BindingFlags]'Static,NonPublic')
if($null -eq $method){throw 'OutputFileSuffix was not found in compiled FGS'}
$guidA='0123456789ABCDEF0123456789ABCDEF'
$guidB='FEDCBA9876543210FEDCBA9876543210'
$cases=@(
    @('OFF', $false, ''),
    @('DARK', $false, '_FILM35S'),
    @('DARK', $true, '_FILM35D'),
    @('CLASSIC', $false, '_FILMCLASSIC35S'),
    @('CLASSIC', $true, '_FILMCLASSIC35D'),
    @(('USER_'+$guidA), $false, ('_FILMUSER_'+$guidA+'_S')),
    @(('USER_'+$guidA), $true, ('_FILMUSER_'+$guidA+'_D')),
    @(('USER_'+$guidB), $false, ('_FILMUSER_'+$guidB+'_S'))
)
$seen=@{}
foreach($case in $cases){
    $result=$method.Invoke($null,@([string]$case[0],[bool]$case[1]))
    if($result -cne $case[2]){throw "Wrong suffix for $($case[0]) dynamic=$($case[1]): got $result expected $($case[2])"}
    if($result -and $seen.ContainsKey($result)){throw "Filename suffix collision: $result"}
    if($result){$seen[$result]=$true}
}
$hevc=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_NativeHevcCore.cs') -Raw
$cpu=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_NativeX264DigitalGrainCore.cs') -Raw
if($hevc -notmatch 'string borderSuffix = filmBorder == null \? "" : filmBorder\.FileSuffix;' -or
   $cpu -notmatch 'filmBorder == null \? "" : filmBorder\.FileSuffix'){
    throw 'Not every encoding path uses the shared Film Border output suffix'
}
Write-Host 'PASS: Dark, Classic, imported borders have distinct static/dynamic suffixes across encoder paths'
