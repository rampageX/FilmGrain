# Verify the published PNG import specification with decoded images, not just file headers.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path 'FilmGrain_Studio.exe').Path)
$type=$assembly.GetType('FilmGrainStudioPreview.FilmBorderCatalogCore',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$validate=$type.GetMethod('ValidatePng',$flags)
$import=$type.GetMethod('Import',$flags,$null,[type[]]@([string],[string],[string]),$null)
$checkName=$type.GetMethod('ValidateDisplayName',$flags)
if(-not $validate -or -not $import -or -not $checkName){throw 'PNG spec validator/importer/name validation not found'}
$dir=Join-Path $env:RUNNER_TEMP ('FGS_ImportSpec_'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
function New-Png($name,$mode){
    $w=1920;$h=1080
    if($mode -eq 'wrong-size'){$w=1280;$h=720}
    $pixel=if($mode -eq 'rgb'){
        [System.Drawing.Imaging.PixelFormat]::Format24bppRgb
    } else { [System.Drawing.Imaging.PixelFormat]::Format32bppArgb }
    $image=[System.Drawing.Bitmap]::new($w,$h,$pixel)
    try {
        $g=[System.Drawing.Graphics]::FromImage($image)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            if($mode -ne 'blank'){
                $brush=[System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255,6,7,9))
                try {
                    $g.FillRectangle($brush,0,0,$w,166)
                    $g.FillRectangle($brush,0,914,$w,166)
                    $g.FillRectangle($brush,0,166,120,748)
                    $g.FillRectangle($brush,1800,166,120,748)
                } finally {$brush.Dispose()}
            }
            if($mode -eq 'dirty-aperture'){
                $b=[System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(1,255,255,255))
                try {$g.FillRectangle($b,140,200,4,4)} finally {$b.Dispose()}
            }
        } finally {$g.Dispose()}
        $file=Join-Path $dir ($name+'.png')
        $image.Save($file,[System.Drawing.Imaging.ImageFormat]::Png)
        return $file
    } finally {$image.Dispose()}
}
function Validate($path){
    try {$validate.Invoke($null,@([string]$path)) | Out-Null}
    catch { $err=$_.Exception; while($err.InnerException){$err=$err.InnerException}; throw $err }
}
function Assert-Invalid($path,$contains){
    try {Validate $path;throw ('EXPECTED_REJECTION: '+$path)}
    catch {
        $message=$_.Exception.Message
        if($message.StartsWith('EXPECTED_REJECTION') -or -not $message.Contains($contains)){
            throw ('Wrong rejection for '+$path+': '+$message)
        }
        Write-Host ('PASS reject '+[IO.Path]::GetFileName($path)+' : '+$message)
    }
}
try {
    $ok=New-Png 'valid' 'valid'
    Validate $ok
    $descriptiveName='柔和暗角 · 135'
    $mode=$import.Invoke($null,@([string]$dir,[string]$ok,[string]$descriptiveName))
    if(-not [string]$mode -or -not ([string]$mode).StartsWith('USER_')){throw 'valid import mode missing'}
    $userDir=Join-Path $dir 'Utils\_FilmBorders\User'
    $raws=@(Get-ChildItem $userDir -Filter '*.raw')
    if($raws.Count -ne 1 -or $raws[0].Length -ne (1920*1080*4)){throw 'valid PNG raw cache missing/invalid'}
    $labels=@(Get-ChildItem $userDir -Filter '*.label')
    if($labels.Count -ne 1 -or ([IO.File]::ReadAllText($labels[0].FullName,[Text.Encoding]::UTF8)) -cne $descriptiveName){
        throw 'Imported display name was not persisted in UTF-8'
    }
    $list=$type.GetMethod('List',$flags)
    $styles=$list.Invoke($null,@([string]$dir))
    $names=@($styles | ForEach-Object { $_.Label })
    if($names -notcontains ('自定义 · '+$descriptiveName+' · 静态') -or
       $names -notcontains ('自定义 · '+$descriptiveName+' · 动态')){
        throw 'Custom display name not propagated to static/dynamic dropdown choices'
    }
    foreach($bad in @('', '   ', ('X'*41), "bad`tname", "bad`nname")){
        try {
            $checkName.Invoke($null,@([string]$bad)) | Out-Null
            throw 'EXPECTED_NAME_REJECTION'
        } catch {
            if($_.Exception.Message -match 'EXPECTED_NAME_REJECTION') {throw 'Invalid border name was accepted'}
        }
    }
    Write-Host 'PASS custom display name persistence and static/dynamic dropdown labels'
    Write-Host 'PASS 1920x1080 RGBA8 PNG import and 8294400-byte RGBA raw cache'
    Assert-Invalid (New-Png 'wrong-size' 'wrong-size') '1920'
    Assert-Invalid (New-Png 'rgb' 'rgb') 'RGBA'
    Assert-Invalid (New-Png 'dirty-aperture' 'dirty-aperture') 'Alpha=0'
    Assert-Invalid (New-Png 'blank' 'blank') '可见像素不足'
    $fake=Join-Path $dir 'fake.png'
    [IO.File]::WriteAllText($fake,'not a PNG')
    Assert-Invalid $fake 'PNG'
    $apng=Join-Path $dir 'animated.png'
    $bytes=[IO.File]::ReadAllBytes($ok)
    # PNG IHDR ends at byte 33; insert an acTL chunk after IHDR to test rejection.
    $chunk=[byte[]](0,0,0,8,97,99,84,76,0,0,0,2,0,0,0,0,0,0,0,0)
    $out=[byte[]]::new($bytes.Length+$chunk.Length)
    [Array]::Copy($bytes,0,$out,0,33)
    [Array]::Copy($chunk,0,$out,33,$chunk.Length)
    [Array]::Copy($bytes,33,$out,33+$chunk.Length,$bytes.Length-33)
    [IO.File]::WriteAllBytes($apng,$out)
    Assert-Invalid $apng 'APNG'
    $tooLarge=Join-Path $dir 'oversize.png'
    $stream=[IO.File]::Open($tooLarge,[IO.FileMode]::Create)
    try {$stream.SetLength(33MB)} finally {$stream.Dispose()}
    Assert-Invalid $tooLarge '32 MiB'
    Write-Host 'PASS Film Border user-import v1.0 checks'
}
finally {Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue}