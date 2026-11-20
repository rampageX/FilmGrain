# Check new-import aperture defaults, validation and persisted global UI preference.
$ErrorActionPreference = 'Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root 'FilmGrain_Studio.exe'))
$catalog=$assembly.GetType('FilmGrainStudioPreview.FilmBorderCatalogCore',$true)
$main=$assembly.GetType('FilmGrainStudioPreview.MainForm',$true)
$configType=$assembly.GetType('FilmGrainStudioPreview.FgsConfig',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$widthKey=[string]$catalog.GetField('ImportLastWidthKey',$flags).GetRawConstantValue()
$heightKey=[string]$catalog.GetField('ImportLastHeightKey',$flags).GetRawConstantValue()
$defaultWidth=[int]$catalog.GetField('ImportDialogDefaultWidth',$flags).GetRawConstantValue()
$defaultHeight=[int]$catalog.GetField('ImportDialogDefaultHeight',$flags).GetRawConstantValue()
if($defaultWidth -ne 1650 -or $defaultHeight -ne 720){throw 'First import must default to 1650x720'}
if($widthKey -eq $heightKey -or -not $widthKey -or -not $heightKey){throw 'Invalid preference key pair'}

$validate=$main.GetMethod('ValidImportDialogAperture',$flags)
if(-not $validate){throw 'Import dialog size-validation helper missing'}
function CheckedSize([int]$value,[int]$nominal,[int]$fallback){
    return [int]$validate.Invoke($null,[object[]]@($value,$nominal,$fallback))
}
if((CheckedSize 1650 1680 1650) -ne 1650 -or (CheckedSize 720 748 720) -ne 720){
    throw 'New default dimensions not accepted'
}
foreach($case in @(
    [pscustomobject]@{Value=1630; Nominal=1680; Default=1650; Expected=1630}
    [pscustomobject]@{Value=1730; Nominal=1680; Default=1650; Expected=1730}
    [pscustomobject]@{Value=1629; Nominal=1680; Default=1650; Expected=1650}
    [pscustomobject]@{Value=1731; Nominal=1680; Default=1650; Expected=1650}
    [pscustomobject]@{Value=698; Nominal=748; Default=720; Expected=698}
    [pscustomobject]@{Value=798; Nominal=748; Default=720; Expected=798}
    [pscustomobject]@{Value=697; Nominal=748; Default=720; Expected=720}
    [pscustomobject]@{Value=799; Nominal=748; Default=720; Expected=720}
)){
    $actual=CheckedSize $case.Value $case.Nominal $case.Default
    if($actual -ne $case.Expected){throw "Unexpected bound behavior: $($case.Value) -> $actual"}
}
$taskCheck=$configType.GetMethod('IsTaskKey',$flags)
foreach($key in @($widthKey,$heightKey)){
    if([bool]$taskCheck.Invoke($null,[object[]]@($key))){
        throw "Import preference must be global rather than per task: $key"
    }
}
$temp=Join-Path $env:RUNNER_TEMP ('FGS_ImportPreference_'+[Guid]::NewGuid().ToString('N'))
$ini=Join-Path $temp 'Test.ini'
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $ctor=$configType.GetConstructor([type[]]@([string],[string]))
    if(-not $ctor){throw 'FgsConfig constructor missing'}
    $cfg=$ctor.Invoke([object[]]@($temp,$ini))
    if($cfg.Get($widthKey) -ne '1650' -or $cfg.Get($heightKey) -ne '720'){
        throw 'Clean configuration does not use 1650x720 defaults'
    }
    $update=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $update[$widthKey]='1700'
    $update[$heightKey]='760'
    $cfg.Save($update)
    $fresh=$ctor.Invoke([object[]]@($temp,$ini))
    if($fresh.Get($widthKey) -ne '1700' -or $fresh.Get($heightKey) -ne '760'){
        throw 'Saved import aperture not retained across configuration reload'
    }
    $iniText=[IO.File]::ReadAllText($ini)
    if(-not $iniText.Contains("FILM_BORDER_IMPORT_LAST_WIDTH=1700") -or
       -not $iniText.Contains("FILM_BORDER_IMPORT_LAST_HEIGHT=760")){
        throw 'Saved global preference keys not serialized to INI'
    }
    if([int]$catalog.GetField('ImportApertureWidth',$flags).GetRawConstantValue() -ne 1680 -or
       [int]$catalog.GetField('ImportApertureHeight',$flags).GetRawConstantValue() -ne 748){
        throw 'Existing imported border geometry was modified'
    }
    $uiSource=[IO.File]::ReadAllText((Join-Path $root 'dotnet\FilmGrain_GpuAdvancedPreview.cs'))
    $importBegin=$uiSource.IndexOf('private void ImportFilmBorderFromPreview()', [StringComparison]::Ordinal)
    $promptBegin=$uiSource.IndexOf('private static int ValidImportDialogAperture', [StringComparison]::Ordinal)
    if($importBegin -lt 0 -or $promptBegin -le $importBegin){throw 'Import UI method not found'}
    $importBlock=$uiSource.Substring($importBegin,$promptBegin-$importBegin)
    $success=$importBlock.IndexOf('FilmBorderCatalogCore.Import(', [StringComparison]::Ordinal)
    $save=$importBlock.IndexOf('config.Save(', [StringComparison]::Ordinal)
    if($success -lt 0 -or $save -le $success){
        throw 'Import preference must only be saved after successful PNG import'
    }
    Write-Host 'PASS Film Border import preference: 1650x720 default, bounded reuse, global persisted widths/heights, legacy geometry'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
