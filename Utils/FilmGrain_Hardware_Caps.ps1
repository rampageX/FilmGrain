[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FFmpeg,

    [int]$GpuIndex = 0,
    [int]$CudaDevice = 0,
    [int]$VulkanDevice = 0,
    [string]$CachePath = '',
    [string]$OutputCmd,
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$SchemaVersion = 3
$ProbeTimeoutSeconds = 20

if ([string]::IsNullOrWhiteSpace($CachePath)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $CachePath = Join-Path $scriptDirectory '_HardwareCaps.json'
}

function ConvertTo-CommandLineArgument {
    param([string]$Value)

    if ($null -eq $Value) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-ProcessCapture {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = $ProbeTimeoutSeconds
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    if (-not $process.Start()) { throw "Unable to start: $FilePath" }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch {}
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = 1460; StdOut = ''; StdErr = 'Probe timed out.' }
    }

    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exitCode; StdOut = $stdout; StdErr = $stderr }
}

function Test-FFmpeg {
    param([string[]]$Arguments)

    $result = Invoke-ProcessCapture -FilePath $FFmpeg -Arguments $Arguments
    return ($result.ExitCode -eq 0)
}

function Get-EncoderBaseArguments {
    param(
        [ValidateSet('av1_nvenc', 'hevc_nvenc', 'h264_nvenc')][string]$Encoder,
        [ValidateSet('hq', 'uhq')][string]$Tune = 'hq'
    )

    $args = @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=30',
        '-frames:v', '8', '-an',
        '-c:v', $Encoder, '-gpu', [string]$GpuIndex,
        '-preset', 'p5', '-tune', $Tune, '-rc', 'vbr',
        '-b:v', '1M', '-maxrate:v', '2M', '-bufsize:v', '4M'
    )

    if ($Encoder -eq 'av1_nvenc') {
        $args += @('-pix_fmt', 'p010le', '-highbitdepth', '1')
    } elseif ($Encoder -eq 'hevc_nvenc') {
        $args += @('-pix_fmt', 'p010le', '-profile:v', 'main10')
    } else {
        $args += @('-pix_fmt', 'yuv420p', '-profile:v', 'high')
    }
    return $args
}

function Test-EncoderOption {
    param(
        [ValidateSet('av1_nvenc', 'hevc_nvenc', 'h264_nvenc')][string]$Encoder,
        [ValidateSet('hq', 'uhq')][string]$Tune = 'hq',
        [string[]]$ExtraArguments = @()
    )

    $args = @(Get-EncoderBaseArguments -Encoder $Encoder -Tune $Tune)
    $args += $ExtraArguments
    $args += @('-f', 'null', 'NUL')
    return (Test-FFmpeg -Arguments $args)
}

function Get-EncoderCapabilities {
    param([ValidateSet('av1_nvenc', 'hevc_nvenc', 'h264_nvenc')][string]$Encoder)

    $caps = [ordered]@{
        available = $false
        uhq = $false
        bFrames = $false
        bReference = $false
        spatialAQ = $false
        temporalAQ = $false
        lookahead = $false
        multipassQres = $false
        multipassFullres = $false
    }

    $caps.available = Test-EncoderOption -Encoder $Encoder
    if (-not $caps.available) { return [pscustomobject]$caps }

    if ($Encoder -eq 'av1_nvenc') {
        $caps.uhq = Test-EncoderOption -Encoder $Encoder -Tune 'uhq' -ExtraArguments @(
            '-preset', 'p4', '-multipass', 'fullres',
            '-spatial-aq', '1', '-aq-strength', '8'
        )
    }

    $completeArgs = @(
        '-bf', '4', '-b_ref_mode', 'middle',
        '-spatial-aq', '1', '-aq-strength', '8',
        '-temporal-aq', '1', '-rc-lookahead', '32',
        '-multipass', 'fullres'
    )
    if (Test-EncoderOption -Encoder $Encoder -ExtraArguments $completeArgs) {
        $caps.bFrames = $true
        $caps.bReference = $true
        $caps.spatialAQ = $true
        $caps.temporalAQ = $true
        $caps.lookahead = $true
        $caps.multipassQres = $true
        $caps.multipassFullres = $true
        return [pscustomobject]$caps
    }

    $caps.bFrames = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-bf', '4')
    if ($caps.bFrames) {
        $caps.bReference = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-bf', '4', '-b_ref_mode', 'middle')
    }
    $caps.spatialAQ = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-spatial-aq', '1', '-aq-strength', '8')
    $caps.temporalAQ = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-temporal-aq', '1')
    $caps.lookahead = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-rc-lookahead', '32')
    $caps.multipassQres = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-multipass', 'qres')
    $caps.multipassFullres = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-multipass', 'fullres')
    return [pscustomobject]$caps
}

function Get-GpuIdentity {
    $gpu = [ordered]@{
        index = $GpuIndex
        name = 'NVIDIA GPU'
        pciBusId = ''
        driverVersion = ''
        identity = "GPU-$GpuIndex"
    }

    $nvidiaSmi = $null
    $command = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
    if ($command) { $nvidiaSmi = $command.Source }
    if (-not $nvidiaSmi) {
        $candidate = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $nvidiaSmi = $candidate }
    }

    if ($nvidiaSmi) {
        $query = Invoke-ProcessCapture -FilePath $nvidiaSmi -Arguments @(
            '--query-gpu=index,name,pci.bus_id,driver_version',
            '--format=csv,noheader,nounits'
        )
        if ($query.ExitCode -eq 0) {
            foreach ($line in ($query.StdOut -split "`r?`n")) {
                $parts = @($line -split '\s*,\s*')
                if ($parts.Count -ge 4 -and $parts[0] -eq [string]$GpuIndex) {
                    $gpu.name = $parts[1].Trim()
                    $gpu.pciBusId = $parts[2].Trim()
                    $gpu.driverVersion = $parts[3].Trim()
                    $gpu.identity = ($parts -join '|')
                    return [pscustomobject]$gpu
                }
            }
        }
    }

    try {
        $controllers = @(Get-WmiObject Win32_VideoController -ErrorAction Stop | Where-Object { $_.Name -match 'NVIDIA' })
        if ($GpuIndex -lt $controllers.Count) {
            $controller = $controllers[$GpuIndex]
            $gpu.name = [string]$controller.Name
            $gpu.driverVersion = [string]$controller.DriverVersion
            $gpu.identity = '{0}|{1}|{2}' -f $GpuIndex, $gpu.name, $gpu.driverVersion
        }
    } catch {}
    return [pscustomobject]$gpu
}

function Get-Sha256Text {
    param([string]$Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Write-Utf8NoBomAtomic {
    param([string]$Path, [string]$Text)

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    $tempPath = "$Path.$PID.tmp"
    [System.IO.File]::WriteAllText($tempPath, $Text, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tempPath -Destination $Path -Force
}

function ConvertTo-CmdValue {
    param([object]$Value)

    $text = [string]$Value
    $text = $text.Replace('%', '%%').Replace('"', '')
    return $text.Replace("`r", ' ').Replace("`n", ' ')
}

function ConvertTo-CmdBool {
    param([bool]$Value)
    if ($Value) { return '1' }
    return '0'
}

function Write-CmdEnvironment {
    param([pscustomobject]$Profile, [string]$Path)

    $av1 = $Profile.caps.av1
    $hevc = $Profile.caps.hevc
    $h264 = $Profile.caps.h264
    $lines = @(
        '@rem Generated by FilmGrain_Hardware_Caps.ps1. Do not edit.',
        ('set "FG_CAP_CACHE_STATE={0}"' -f (ConvertTo-CmdValue $Profile.cacheState)),
        ('set "FG_CAP_GPU_NAME={0}"' -f (ConvertTo-CmdValue $Profile.gpu.name)),
        ('set "FG_CAP_DRIVER_VERSION={0}"' -f (ConvertTo-CmdValue $Profile.gpu.driverVersion)),
        ('set "FG_CAP_AV1={0}"' -f (ConvertTo-CmdBool $av1.available)),
        ('set "FG_CAP_AV1_UHQ={0}"' -f (ConvertTo-CmdBool $av1.uhq)),
        ('set "FG_CAP_HEVC={0}"' -f (ConvertTo-CmdBool $hevc.available)),
        ('set "FG_CAP_H264={0}"' -f (ConvertTo-CmdBool $h264.available)),
        ('set "FG_CAP_VULKAN={0}"' -f (ConvertTo-CmdBool $Profile.caps.vulkan)),
        ('set "FG_CAP_HEVC_PIPELINE={0}"' -f (ConvertTo-CmdBool $Profile.caps.hevcPipeline)),
        ('set "FG_CAP_NVDEC={0}"' -f (ConvertTo-CmdBool $Profile.caps.nvdec)),
        ('set "FG_CAP_AV1_BF={0}"' -f (ConvertTo-CmdBool $av1.bFrames)),
        ('set "FG_CAP_AV1_BREF={0}"' -f (ConvertTo-CmdBool $av1.bReference)),
        ('set "FG_CAP_AV1_SAQ={0}"' -f (ConvertTo-CmdBool $av1.spatialAQ)),
        ('set "FG_CAP_AV1_TAQ={0}"' -f (ConvertTo-CmdBool $av1.temporalAQ)),
        ('set "FG_CAP_AV1_LOOKAHEAD={0}"' -f (ConvertTo-CmdBool $av1.lookahead)),
        ('set "FG_CAP_AV1_QRES={0}"' -f (ConvertTo-CmdBool $av1.multipassQres)),
        ('set "FG_CAP_AV1_FULLRES={0}"' -f (ConvertTo-CmdBool $av1.multipassFullres)),
        ('set "FG_CAP_HEVC_BF={0}"' -f (ConvertTo-CmdBool $hevc.bFrames)),
        ('set "FG_CAP_HEVC_BREF={0}"' -f (ConvertTo-CmdBool $hevc.bReference)),
        ('set "FG_CAP_HEVC_SAQ={0}"' -f (ConvertTo-CmdBool $hevc.spatialAQ)),
        ('set "FG_CAP_HEVC_TAQ={0}"' -f (ConvertTo-CmdBool $hevc.temporalAQ)),
        ('set "FG_CAP_HEVC_LOOKAHEAD={0}"' -f (ConvertTo-CmdBool $hevc.lookahead)),
        ('set "FG_CAP_HEVC_QRES={0}"' -f (ConvertTo-CmdBool $hevc.multipassQres)),
        ('set "FG_CAP_HEVC_FULLRES={0}"' -f (ConvertTo-CmdBool $hevc.multipassFullres)),
        ('set "FG_CAP_H264_BF={0}"' -f (ConvertTo-CmdBool $h264.bFrames)),
        ('set "FG_CAP_H264_BREF={0}"' -f (ConvertTo-CmdBool $h264.bReference)),
        ('set "FG_CAP_H264_SAQ={0}"' -f (ConvertTo-CmdBool $h264.spatialAQ)),
        ('set "FG_CAP_H264_TAQ={0}"' -f (ConvertTo-CmdBool $h264.temporalAQ)),
        ('set "FG_CAP_H264_LOOKAHEAD={0}"' -f (ConvertTo-CmdBool $h264.lookahead)),
        ('set "FG_CAP_H264_QRES={0}"' -f (ConvertTo-CmdBool $h264.multipassQres)),
        ('set "FG_CAP_H264_FULLRES={0}"' -f (ConvertTo-CmdBool $h264.multipassFullres))
    )
    Write-Utf8NoBomAtomic -Path $Path -Text (($lines -join "`r`n") + "`r`n")
}

try {
    if (-not (Test-Path -LiteralPath $FFmpeg -PathType Leaf)) {
        throw "FFmpeg was not found: $FFmpeg"
    }

    $ffmpegFile = Get-Item -LiteralPath $FFmpeg
    $versionResult = Invoke-ProcessCapture -FilePath $FFmpeg -Arguments @('-version')
    if ($versionResult.ExitCode -ne 0) { throw 'FFmpeg could not be started.' }
    $versionLine = (($versionResult.StdOut -split "`r?`n") | Select-Object -First 1)
    $gpu = Get-GpuIdentity
    $signatureText = @(
        "schema=$SchemaVersion",
        "ffmpeg=$($ffmpegFile.FullName)",
        "size=$($ffmpegFile.Length)",
        "write=$($ffmpegFile.LastWriteTimeUtc.Ticks)",
        "version=$versionLine",
        "gpu=$($gpu.identity)",
        "gpuIndex=$GpuIndex",
        "cuda=$CudaDevice",
        "vulkan=$VulkanDevice"
    ) -join "`n"
    $signature = Get-Sha256Text $signatureText

    $profile = $null
    if (-not $Force -and (Test-Path -LiteralPath $CachePath -PathType Leaf)) {
        try {
            $cached = Get-Content -LiteralPath $CachePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ([int]$cached.schemaVersion -eq $SchemaVersion -and [string]$cached.signature -eq $signature) {
                $profile = $cached
                $profile.cacheState = 'Cached'
                $cachedJson = $profile | ConvertTo-Json -Depth 8
                Write-Utf8NoBomAtomic -Path $CachePath -Text ($cachedJson + "`r`n")
            }
        } catch {}
    }

    if (-not $profile) {
        if (-not $Quiet) {
            Write-Host '[Hardware Detection] Testing the current GPU, driver and FFmpeg build...'
        }

        $av1Caps = Get-EncoderCapabilities -Encoder 'av1_nvenc'
        $hevcCaps = Get-EncoderCapabilities -Encoder 'hevc_nvenc'
        $h264Caps = Get-EncoderCapabilities -Encoder 'h264_nvenc'

        $vulkanArgs = @(
            '-hide_banner', '-loglevel', 'error',
            '-init_hw_device', "vulkan=vk:$VulkanDevice", '-filter_hw_device', 'vk',
            '-f', 'lavfi', '-i', 'color=c=black:s=128x128:r=30',
            '-vf', 'format=p010le,hwupload,scale_vulkan=w=64:h=64,hwdownload,format=p010le',
            '-frames:v', '1', '-f', 'null', 'NUL'
        )
        $vulkan = Test-FFmpeg -Arguments $vulkanArgs

        $nvdec = $false
        $decodeProbePath = Join-Path ([System.IO.Path]::GetTempPath()) ("FilmGrain_NVDEC_{0}_{1}.mp4" -f $PID, [Guid]::NewGuid().ToString('N'))
        try {
            if ($h264Caps.available) {
                $encodeArgs = @(Get-EncoderBaseArguments -Encoder 'h264_nvenc')
                $encodeArgs += @('-movflags', '+faststart', '-y', $decodeProbePath)
                if (Test-FFmpeg -Arguments $encodeArgs) {
                    $decodeArgs = @(
                        '-hide_banner', '-loglevel', 'error',
                        '-hwaccel', 'cuda', '-hwaccel_device', [string]$CudaDevice,
                        '-i', $decodeProbePath,
                        '-map', '0:v:0', '-frames:v', '1', '-f', 'null', 'NUL'
                    )
                    $nvdec = Test-FFmpeg -Arguments $decodeArgs
                }
            }
        } finally {
            if (Test-Path -LiteralPath $decodeProbePath) {
                Remove-Item -LiteralPath $decodeProbePath -Force -ErrorAction SilentlyContinue
            }
        }

        $profile = [pscustomobject][ordered]@{
            schemaVersion = $SchemaVersion
            signature = $signature
            generatedAt = [DateTime]::UtcNow.ToString('o')
            cacheState = 'Detected'
            ffmpeg = [pscustomobject][ordered]@{
                path = $ffmpegFile.FullName
                version = $versionLine
                size = $ffmpegFile.Length
                lastWriteTimeUtc = $ffmpegFile.LastWriteTimeUtc.ToString('o')
            }
            gpu = $gpu
            caps = [pscustomobject][ordered]@{
                av1 = $av1Caps
                hevc = $hevcCaps
                h264 = $h264Caps
                nvdec = $nvdec
                vulkan = $vulkan
                hevcPipeline = ([bool]$hevcCaps.available -and $vulkan)
            }
        }
        $json = $profile | ConvertTo-Json -Depth 8
        Write-Utf8NoBomAtomic -Path $CachePath -Text ($json + "`r`n")
    }

    if ($OutputCmd) { Write-CmdEnvironment -Profile $profile -Path $OutputCmd }

    if (-not $Quiet) {
        $mark = @('NO', 'OK')
        Write-Host ('GPU .............. {0}' -f $profile.gpu.name)
        Write-Host ('Driver ........... {0}' -f $profile.gpu.driverVersion)
        Write-Host ('Profile .......... {0}' -f $profile.cacheState)
        Write-Host ('AV1 Main10 NVENC . {0}' -f $mark[[int][bool]$profile.caps.av1.available])
        Write-Host ('AV1 UHQ ......... {0}' -f $mark[[int][bool]$profile.caps.av1.uhq])
        Write-Host ('HEVC Main10 ...... {0}' -f $mark[[int][bool]$profile.caps.hevcPipeline])
        Write-Host ('NVDEC CUDA ....... {0}' -f $mark[[int][bool]$profile.caps.nvdec])
        Write-Host ('Vulkan ........... {0}' -f $mark[[int][bool]$profile.caps.vulkan])
    }
} catch {
    Write-Error ("Hardware capability detection failed: " + $_.Exception.Message)
    exit 1
}
