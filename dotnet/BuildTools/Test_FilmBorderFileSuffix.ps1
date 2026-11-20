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
    @('OFF', $false, '', ''),
    @('DARK', $false, '', '_FILM35S'),
    @('DARK', $true, '', '_FILM35D'),
    @('CLASSIC', $false, '', '_FILMCLASSIC35S'),
    @('CLASSIC', $true, '', '_FILMCLASSIC35D'),
    @(('USER_'+$guidA), $false, 'ILFORD HP5 PLUS 400', '_FILMUSER_ILFORD HP5 PLUS 400_S'),
    @(('USER_'+$guidA), $true, 'ILFORD HP5 PLUS 400', '_FILMUSER_ILFORD HP5 PLUS 400_D'),
    @(('USER_'+$guidB), $false, '胶片 & 35mm ^ (原片)!', '_FILMUSER_胶片 & 35mm ^ (原片)!_S'),
    @(('USER_'+$guidB), $true, '胶片 & 35mm ^ (原片)!', '_FILMUSER_胶片 & 35mm ^ (原片)!_D'),
    @(('USER_'+$guidA), $false, 'Test / Film: A?', '_FILMUSER_Test _ Film_ A_S')
)
foreach($case in $cases){
    $result=[string]$method.Invoke($null,@([string]$case[0],[bool]$case[1],[string]$case[2]))
    if($result -cne $case[3]){throw "Wrong suffix for $($case[0]) dynamic=$($case[1]): got $result expected $($case[3])"}
    if($result -match '[<>:"/\\|?*]'){throw "Unsafe Windows filename suffix: $result"}
}
$catalog=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_FilmBorderCatalog.cs') -Raw
if($catalog -notmatch 'UserName=user\?userName:null' -or
   $catalog -notmatch 'path,false,true,label' -or $catalog -notmatch 'path,true,true,label'){
    throw 'Imported label is not passed from the catalog into the suffix builder'
}
$hevc=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_NativeHevcCore.cs') -Raw
$cpu=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_NativeX264DigitalGrainCore.cs') -Raw
if($hevc -notmatch 'string borderSuffix = filmBorder == null \? "" : filmBorder\.FileSuffix;' -or
   $cpu -notmatch 'filmBorder == null \? "" : filmBorder\.FileSuffix'){
    throw 'Not every encoding path uses the shared Film Border output suffix'
}
Write-Host 'PASS: builtin suffixes unchanged; imported labels used directly without ID'
