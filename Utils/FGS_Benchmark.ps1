param([ValidateSet('Init','Run','Finish')][string]$Action,[string]$Test,[string]$Encoder,[string]$Grain,[string]$Quality)
$ErrorActionPreference='Stop'
$root=$env:FGB_ROOT
$utf8=New-Object System.Text.UTF8Encoding($true)
function Write-Text($Path,$Text) { [IO.File]::WriteAllText($Path,[string]$Text,$utf8) }
function Num($Value) { $n=0.0; if([double]::TryParse([string]$Value,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$n)){return $n}; return 0.0 }
function Round($Value) { [Math]::Round($Value,3) }
function Probe($Path,[switch]$Count) {
    $a=@('-v','error','-select_streams','v:0','-show_streams','-show_format','-of','json')
    if($Count){$a+='-count_frames'}
    $a+=$Path
    $j=& $script:ffprobe @a 2>> (Join-Path $root 'Logs\Probe.log')
    if($LASTEXITCODE -ne 0){throw "FFprobe failed: $Path"}
    $p=($j -join "`n") | ConvertFrom-Json
    if(!$p.streams -or !$p.streams[0].width){throw "No video stream: $Path"}
    return $p
}
function Load-State {
    $script:state=Get-Content -LiteralPath (Join-Path $root 'Logs\State.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $script:ffmpeg=$state.FFmpeg; $script:ffprobe=$state.FFprobe
}
function Read-BridgeLog($Path) {
    $bytes=[IO.File]::ReadAllBytes($Path)
    try {return (New-Object Text.UTF8Encoding($false,$true)).GetString($bytes)}
    catch {return [Text.Encoding]::Default.GetString($bytes)}
}
function Parse-FinalPath($Text) {
    $matches=[regex]::Matches($Text,'(?im)^\s*Final file\s*:\s*"(?<path>[^"\r\n]+)"\s*$')
    if(!$matches.Count){throw 'StudioBridge did not report Final file.'}
    return $matches[$matches.Count-1].Groups['path'].Value
}
function Escape-Md($Text) { ([string]$Text).Replace('|','\|').Replace("`r",' ').Replace("`n",' ') }
function Report {
    $csv=Join-Path $root 'Benchmark.csv'
    $rows=@(); if(Test-Path -LiteralPath $csv){$rows=@(Import-Csv -LiteralPath $csv)}
    $ok=@($rows | Where-Object Status -eq 'OK')
    $lines=New-Object 'Collections.Generic.List[string]'
    $lines.Add('# FGS Benchmark v1.0')
    $lines.Add('')
    $lines.Add(('Source: '+(Escape-Md $state.Source)))
    $lines.Add(('Started: '+$state.Started+'; report updated: '+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')))
    $lines.Add(('Matrix: 12 tests; recorded: '+$rows.Count+'; OK: '+$ok.Count+'; failed: '+@($rows|Where-Object Status -ne 'OK').Count))
    $lines.Add('')
    $lines.Add('Elapsed = complete StudioBridge wall time in seconds (includes detection, preparation, encoding and muxing; excludes probing, file relocation and screenshots). FPS = Frames / Elapsed; MPix/s = Width * Height * FPS / 1e6; Speed = output video duration / Elapsed. Output Size is bytes; Bitrate is measured whole-file average in kbit/s, including audio/container. Rates are not FFmpeg instantaneous FPS.')
    $lines.Add('')
    $lines.Add('FAST / source FPS / MP4 / AUTO bitrate / Digital Grain 55 / FGSIM Medium / x264 faster + tune grain + single-pass / no LUT, subtitles, interpolation or deinterlacing. Grain Plate strength 85%. B06: Classic35, Fujifilm Eterna 250D. B02 and Q01 deliberately repeat the same VBR settings. One pass per test, fixed order, no warm-up; plate-cache state may affect timing. Hardware support is checked by StudioBridge, never inferred from GPU model.')
    $lines.Add(('Grain plate: '+(Escape-Md $state.Plate)))
    $lines.Add('')
    $cols=@('Test','Encoder','Grain','Status','Elapsed','Resolution','FPS','Frames','MPix/s','Output Size','Bitrate','Speed','Output File','Error')
    $lines.Add('| '+($cols -join ' | ')+' |'); $lines.Add('| '+(($cols|ForEach-Object{'---'}) -join ' | ')+' |')
    foreach($r in $rows){$lines.Add('| '+(($cols|ForEach-Object{Escape-Md $r.$_}) -join ' | ')+' |')}
    if($ok.Count){
        $total=($ok|ForEach-Object{Num $_.Elapsed}|Measure-Object -Sum).Sum
        $lines.Add(''); $lines.Add(('Successful tests total wall time: '+(Round $total)+' seconds.'))
    }
    $lines.Add(''); $lines.Add('## Screenshots')
    $lines.Add('Frame numbers are zero-based decoded frame indices. PNG is lossless after RGB conversion. AV1 uses libdav1d with film grain enabled. HDR screenshots are raw RGB conversions, not a common SDR tone-map; compare HDR/SDR cases with care. Out-of-range or failed frames are reported, never replaced with another frame.')
    foreach($frame in @($state.Frames)) {
        $lines.Add(''); $lines.Add(('### Frame '+$frame))
        foreach($id in @('SOURCE')+@($rows.Test)) {
            $rel="Screenshots/Frame_$frame/$id.png"
            if(Test-Path -LiteralPath (Join-Path $root $rel)){$lines.Add("[$id]($rel)")}
        }
    }
    $sc=Join-Path $root 'Logs\Screenshots.csv'
    if(Test-Path -LiteralPath $sc){
        $fails=@(Import-Csv -LiteralPath $sc|Where-Object Status -ne 'OK')
        if($fails.Count){$lines.Add('');$lines.Add('Screenshot issues:');foreach($r in $fails){$lines.Add(('- '+$r.Test+' / '+$r.Frame+': '+(Escape-Md $r.Error)))}}
    }
    Write-Text (Join-Path $root 'Benchmark.md') ($lines -join "`r`n")
}
function Shots($Path,$Id,$Info) {
    foreach($frame in @($state.Frames)) {
        $status='OK';$errorText='';$dir=Join-Path $root "Screenshots\Frame_$frame"
        [IO.Directory]::CreateDirectory($dir)|Out-Null
        $dest=Join-Path $dir "$Id.png"
        try {
            $n=Num $Info.streams[0].nb_frames
            if($n -gt 0 -and $frame -ge $n){throw "OUT_OF_RANGE: $n frames; valid indices 0..$($n-1)"}
            $a=@('-hide_banner','-nostdin','-loglevel','error','-y')
            if($Info.streams[0].codec_name -eq 'av1'){$a+=@('-c:v','libdav1d','-filmgrain','1')}
            $a+=@('-i',$Path,'-map','0:v:0','-vf',"select=eq(n\,$frame),format=rgb24",'-frames:v','1','-fps_mode','vfr','-update','1',$dest)
            & $script:ffmpeg @a 2>> (Join-Path $root "Logs\Screenshot_${Id}_${frame}.log") | Out-Null
            if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $dest)){throw 'Screenshot failed or frame is out of range; see screenshot log.'}
            if((Get-Item -LiteralPath $dest).Length -eq 0){throw 'Empty screenshot.'}
        } catch {$status='FAILED';$errorText=$_.Exception.Message; if(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest -Force}}
        [pscustomobject]@{Test=$Id;Frame=$frame;Status=$status;Error=$errorText}|Export-Csv -LiteralPath (Join-Path $root 'Logs\Screenshots.csv') -NoTypeInformation -Encoding UTF8 -Append
    }
}
try {
if($Action -eq 'Init') {
    $src=[IO.Path]::GetFullPath($env:FGB_SOURCE)
    if(!(Test-Path -LiteralPath $src -PathType Leaf)){throw 'Source video not found.'}
    $homeDir=$env:FGB_HOME
    # Only two explicit installation locations; no recursive or wildcard discovery.
    $bridge=Join-Path $homeDir 'FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'
    if(!(Test-Path -LiteralPath $bridge)){$bridge=Join-Path $homeDir 'Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'}
    if(!(Test-Path -LiteralPath $bridge)){throw 'Place CMD and PS1 in the FGS root or beside StudioBridge in Utils.'}
    $utils=Split-Path -Parent $bridge; $fgs=Split-Path -Parent $utils
    $ini=Join-Path $fgs 'FilmGrain_Config.ini'
    if(!(Test-Path -LiteralPath $ini)){throw "Configuration missing: $ini"}
    $cfg=@{}
    foreach($line in Get-Content -LiteralPath $ini -Encoding UTF8){if($line -match '^([^;#\[=]+)=(.*)$'){$cfg[$matches[1]]=$matches[2]}}
    $bin='E:\EnCoder\FFMpeg\x64\bin'
    if($cfg.ContainsKey('FFMPEG_DIR')){$bin=$cfg.FFMPEG_DIR}
    elseif($cfg.FFMPEG){$bin=Split-Path -Parent $cfg.FFMPEG}
    elseif($cfg.FFPROBE){$bin=Split-Path -Parent $cfg.FFPROBE}
    $script:ffmpeg=Join-Path $bin 'ffmpeg.exe';$script:ffprobe=Join-Path $bin 'ffprobe.exe'
    foreach($tool in @($ffmpeg,$ffprobe)){if(!(Test-Path -LiteralPath $tool -PathType Leaf)){throw "Tool not found: $tool"}}
    $framesText=Read-Host 'Screenshot frames, zero-based (example 247,865,1420; blank skips)'
    $frames=@()
    if($framesText.Trim()){
        if($framesText -notmatch '^\s*\d+\s*(,\s*\d+\s*)*$'){throw 'Frames must be nonnegative integers separated by commas.'}
        $frames=@($framesText.Split(',')|ForEach-Object{[int]$_.Trim()}|Select-Object -Unique)
    }
    $plate=(Read-Host 'Exact Grain Plate file path for B03/B09 (blank records those tests as SKIPPED)').Trim().Trim('"')
    if($plate -and !(Test-Path -LiteralPath $plate -PathType Leaf)){throw 'Grain Plate file not found. No path scan was attempted.'}
    if(Test-Path -LiteralPath $root){throw 'Output directory already exists; run again for a new directory.'}
    foreach($d in @('','Logs','Outputs','Screenshots','Work')){[IO.Directory]::CreateDirectory((Join-Path $root $d))|Out-Null}
    $sourceInfo=Probe $src
    Write-Text (Join-Path $root 'Source_Info.txt') ($sourceInfo|ConvertTo-Json -Depth 12)
    # Isolated input avoids overwriting any pre-existing Studio outputs next to the source.
    $work=Join-Path $root ('Work\'+[IO.Path]::GetFileName($src))
    $method='hard link'
    try {New-Item -ItemType HardLink -Path $work -Target $src -ErrorAction Stop|Out-Null}
    catch {Copy-Item -LiteralPath $src -Destination $work; $method='copy'}
    $script:state=[pscustomobject]@{Source=$src;Input=$work;Bridge=$bridge;FFmpeg=$ffmpeg;FFprobe=$ffprobe;Frames=$frames;Plate=$plate;Started=(Get-Date -Format 'yyyy-MM-dd HH:mm:ss');InputMethod=$method}
    Write-Text (Join-Path $root 'Logs\State.json') ($state|ConvertTo-Json -Depth 5)
    $envLines=New-Object 'Collections.Generic.List[string]'
    $envLines.Add('FGS Benchmark v1.0');$envLines.Add(('Started: '+$state.Started));$envLines.Add(('Source staging: '+$method))
    foreach($pair in @(@('CPU','Win32_Processor'),@('GPU / Driver','Win32_VideoController'),@('RAM','Win32_ComputerSystem'),@('Windows','Win32_OperatingSystem'))){
        try {
            $v=Get-CimInstance $pair[1]
            switch($pair[1]) {
                'Win32_Processor' {$t=($v|ForEach-Object{"$($_.Name); cores=$($_.NumberOfCores); threads=$($_.NumberOfLogicalProcessors)"}) -join "`r`n"}
                'Win32_VideoController' {$t=($v|ForEach-Object{"$($_.Name); Driver=$($_.DriverVersion)"}) -join "`r`n"}
                'Win32_ComputerSystem' {$t=('{0:N2} GiB ({1} bytes)' -f ($v.TotalPhysicalMemory/1073741824.0),$v.TotalPhysicalMemory)}
                'Win32_OperatingSystem' {$t="$($v.Caption); version=$($v.Version); build=$($v.BuildNumber); $($v.OSArchitecture)"}
            }
            $envLines.Add(($pair[0]+': '+$t))
        } catch {$envLines.Add(($pair[0]+': unavailable - '+$_.Exception.Message))}
    }
    $envLines.Add(('FFmpeg path: '+$ffmpeg));$envLines.Add(((& $ffmpeg -version 2>&1|ForEach-Object{[string]$_}) -join "`r`n"))
    $version='Unknown';$versionSource='not available'
    $gui=Join-Path $utils 'FilmGrain_Studio.ps1'
    if(Test-Path -LiteralPath $gui){
        $m=[regex]::Match([IO.File]::ReadAllText($gui),"statusVersion\.Text\s*=\s*'(?<v>v[0-9.]+)'")
        if($m.Success){$version=$m.Groups['v'].Value;$versionSource='GUI statusVersion'}
    }
    $changelog=Join-Path $fgs 'CHANGELOG.md'
    if($version -eq 'Unknown' -and (Test-Path -LiteralPath $changelog)){
        $m=[regex]::Match([IO.File]::ReadAllText($changelog),'(?m)^##\s+(?<v>v[0-9.]+)')
        if($m.Success){$version=$m.Groups['v'].Value;$versionSource='CHANGELOG first version'}
    }
    $envLines.Add(('FGS version: '+$version+' ('+$versionSource+')'));$envLines.Add(('StudioBridge: '+$bridge))
    $envLines.Add(('StudioBridge SHA256: '+(Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash))
    Write-Text (Join-Path $root 'Environment.txt') ($envLines -join "`r`n")
    # No CALL or path interpolation: inherited environment is expanded once by CMD.
    $runner="@echo off`r`nsetlocal EnableExtensions DisableDelayedExpansion`r`nchcp 65001 >nul`r`n"+'"%FGB_BRIDGE%" "%FGB_INPUT%" >"%FGB_LOG%" 2>&1 <nul'+"`r`n"
    [IO.File]::WriteAllText((Join-Path $root 'Logs\BridgeRunner.cmd'),$runner,[Text.Encoding]::ASCII)
    Shots $src 'SOURCE' $sourceInfo
    Report
    Write-Host "Initialized: $root"
    exit 0
}
Load-State
if($Action -eq 'Run') {
    $label=switch($Grain){'PROCEDURAL'{'Digital Grain'}; 'FGSIM'{"FGSIM $Quality"}; default{if($Encoder -eq 'AV1'){'Native Film Grain'}else{'Grain Plate'}}}
    $row=[pscustomobject][ordered]@{Test=$Test;Encoder=$Encoder;Grain=$label;Elapsed='';Resolution='';FPS='';Frames='';'MPix/s'='';'Output Size'='';Bitrate='';Speed='';'Output File'='';Status='FAILED';Error=''}
    $log=Join-Path $root "Logs\$Test.log"
    try {
        if($Grain -eq 'NATIVE' -and $Encoder -ne 'AV1' -and !$state.Plate){$row.Status='SKIPPED';throw 'No exact Grain Plate path supplied.'}
        $settings=@{FG_STUDIO_MODE='1';FG_MODE=$Encoder;FG_SPEED='FAST';FG_CONTAINER='MP4';FG_FPS_MODE='SOURCE';FG_SVP_INTERPOLATE='0';FG_DEINTERLACE='OFF';FG_CINEMATIC_FRAME='0';FG_HDR_POLICY='AUTO';FG_GRAIN_ENGINE=$Grain;FG_PROC_STRENGTH='55';FG_FGSIM_PRESET='MEDIUM';FG_FGSIM_QUALITY=$Quality;FG_BITRATE_MODE='AUTO';FG_UPLOAD='0';FG_AV1_UPLOAD='0';FG_X264_PRESET='faster';FG_X264_PASS_MODE='VBR1';FG_HIGH_MOTION='0';FG_H264_HIGH10='0';FG_SUB_MODE='OFF';FG_AV1_GRAIN_MODE='PRESET';FG_AV1_FORMAT='1';FG_AV1_STOCK='1';FG_AV1_CHROMA='0';FG_HEVC_STRENGTH_SEL='3'}
        if($state.Plate){$settings.FG_HEVC_GRAIN_PATH=$state.Plate;$settings.FG_GRAIN_ROOT=Split-Path -Parent $state.Plate}
        $psi=New-Object Diagnostics.ProcessStartInfo
        $psi.FileName=$env:ComSpec;$psi.Arguments='/d /v:off /s /c ""%FGB_RUNNER%""';$psi.UseShellExecute=$false
        $psi.WorkingDirectory=Split-Path -Parent $state.Bridge
        foreach($key in @($psi.EnvironmentVariables.Keys)){if($key -like 'FG_*'){$psi.EnvironmentVariables.Remove($key)}}
        foreach($key in $settings.Keys){$psi.EnvironmentVariables[$key]=[string]$settings[$key]}
        $psi.EnvironmentVariables['FGB_RUNNER']=Join-Path $root 'Logs\BridgeRunner.cmd'
        $psi.EnvironmentVariables['FGB_BRIDGE']=$state.Bridge;$psi.EnvironmentVariables['FGB_INPUT']=$state.Input;$psi.EnvironmentVariables['FGB_LOG']=$log
        Write-Text (Join-Path $root "Logs\${Test}_Settings.json") ($settings|ConvertTo-Json)
        $timer=[Diagnostics.Stopwatch]::StartNew();$proc=[Diagnostics.Process]::Start($psi);$proc.WaitForExit();$timer.Stop()
        $elapsed=$timer.Elapsed.TotalSeconds;$row.Elapsed=Round $elapsed
        $text=Read-BridgeLog $log
        if($proc.ExitCode -ne 0){throw "StudioBridge exit code $($proc.ExitCode). See $Test.log."}
        if($text -notmatch '(?im)^\s*Successful\s*:\s*1\s*$' -or $text -notmatch '(?im)^\s*Failed\s*:\s*0\s*$'){throw 'StudioBridge did not confirm one successful job and zero failures.'}
        $output=Parse-FinalPath $text
        $full=[IO.Path]::GetFullPath($output);$workDir=[IO.Path]::GetFullPath((Join-Path $root 'Work'))+'\'
        if(!$full.StartsWith($workDir,[StringComparison]::OrdinalIgnoreCase) -or $full -eq $state.Input){throw 'Reported output is outside isolated Work directory.'}
        if(!(Test-Path -LiteralPath $full -PathType Leaf)){throw 'Reported output file does not exist.'}
        $p=Probe $full;$v=$p.streams[0];$frames=Num $v.nb_frames
        if($frames -le 0){$p=Probe $full -Count;$v=$p.streams[0];$frames=Num $v.nb_read_frames}
        if($frames -le 0){throw 'Cannot determine actual output frame count.'}
        $duration=Num $v.duration;if($duration -le 0){$duration=Num $p.format.duration}
        if($duration -le 0){throw 'Cannot determine output duration.'}
        $expected=switch($Encoder){'HEVC'{'hevc'}; 'AV1'{'av1'}; 'X264'{'h264'}}
        if($v.codec_name -ne $expected){throw "Wrong output codec: $($v.codec_name)"}
        $dest=Join-Path $root ("Outputs\$Test"+[IO.Path]::GetExtension($full))
        Move-Item -LiteralPath $full -Destination $dest
        $size=(Get-Item -LiteralPath $dest).Length
        $row.Resolution="$($v.width)x$($v.height)";$row.Frames=$frames;$row.FPS=Round ($frames/$elapsed)
        $row.'MPix/s'=Round ($v.width*$v.height*$frames/$elapsed/1000000.0)
        $row.'Output Size'=$size;$containerDuration=Num $p.format.duration;if($containerDuration -le 0){$containerDuration=$duration};$row.Bitrate=Round ($size*8.0/$containerDuration/1000.0);$row.Speed=Round ($duration/$elapsed)
        $row.'Output File'=$dest;$row.Status='OK'
        Shots $dest $Test $p
    } catch {$row.Error=$_.Exception.Message;Write-Warning "$Test : $($row.Error)";if(!(Test-Path -LiteralPath $log)){Write-Text $log $row.Error}}
    $row|Export-Csv -LiteralPath (Join-Path $root 'Benchmark.csv') -NoTypeInformation -Encoding UTF8 -Append
    Report
    if($row.Status -eq 'FAILED'){exit 1};exit 0
}
if($Action -eq 'Finish') {
    # Only remove our exact staged source. Preserve any failed intermediates for diagnosis.
    if(Test-Path -LiteralPath $state.Input){Remove-Item -LiteralPath $state.Input -Force}
    Report
    $rows=@(Import-Csv -LiteralPath (Join-Path $root 'Benchmark.csv'))
    if($rows.Count -ne 12){throw "Incomplete matrix: $($rows.Count)/12 rows. See earlier errors."}
    Write-Host ('OK: '+@($rows|Where-Object Status -eq 'OK').Count+' / 12. Report: '+(Join-Path $root 'Benchmark.md'))
}
} catch {Write-Host ('ERROR: '+$_.Exception.Message) -ForegroundColor Red;exit 1}
