[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FFmpeg,

    [int]$GpuIndex = 0,
    [int]$CudaDevice = 0,
    [int]$VulkanDevice = 0,
    [string]$CachePath = '',
    [string]$OutputCmd,
    [string]$OpenSvpRoot = '',
    [string]$Grav1synth = '',
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$SchemaVersion = 8
$ProbeTimeoutSeconds = 20

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($CachePath)) {
    $CachePath = Join-Path $scriptDirectory '_HardwareCaps.json'
}
if ([string]::IsNullOrWhiteSpace($OpenSvpRoot)) {
    $OpenSvpRoot = Join-Path (Split-Path -Parent $scriptDirectory) '_OpenSVPFlow'
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

function Test-Av1SplitEncodeMode {
    param([int]$EngineCount)

    $args = @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=3840x2160:rate=30',
        '-frames:v', '4', '-an',
        '-c:v', 'av1_nvenc', '-gpu', [string]$GpuIndex,
        '-pix_fmt', 'p010le', '-highbitdepth', '1',
        '-preset', 'p6', '-tune', 'hq', '-rc', 'vbr',
        '-b:v', '12M', '-maxrate:v', '36M', '-bufsize:v', '72M',
        '-multipass', 'fullres',
        '-split_encode_mode', [string]$EngineCount,
        '-f', 'null', 'NUL'
    )
    return (Test-FFmpeg -Arguments $args)
}

function Get-Av1EncoderEngineCount {
    if ([IntPtr]::Size -ne 8) { return 1 }

    if (-not ('FilmGrainNvencCaps' -as [type])) {
        $source = @'
using System;
using System.Runtime.InteropServices;

public static class FilmGrainNvencCaps
{
    // NVENC API 13.0 is sufficient for Split Frame Encoding capability queries.
    private const uint NvencApiVersion = 13u;
    private const uint FunctionListVersion = 0x7002000Du;
    private const uint OpenSessionVersion = 0x7001000Du;
    private const uint CapsParamVersion = 0x7001000Du;

    // NV_ENC_CAPS_NUM_ENCODER_ENGINES
    private const int NumEncoderEnginesCap = 49;

    private static readonly Guid Av1Guid =
        new Guid("0a352289-0aa7-4759-862d-5d15cd16d254");

    [DllImport("nvcuda.dll", EntryPoint = "cuInit",
        CallingConvention = CallingConvention.Winapi)]
    private static extern int CuInit(uint flags);

    [DllImport("nvcuda.dll", EntryPoint = "cuDeviceGet",
        CallingConvention = CallingConvention.Winapi)]
    private static extern int CuDeviceGet(out int device, int ordinal);

    [DllImport("nvcuda.dll", EntryPoint = "cuCtxCreate_v2",
        CallingConvention = CallingConvention.Winapi)]
    private static extern int CuCtxCreate(out IntPtr context, uint flags, int device);

    [DllImport("nvcuda.dll", EntryPoint = "cuCtxDestroy_v2",
        CallingConvention = CallingConvention.Winapi)]
    private static extern int CuCtxDestroy(IntPtr context);

    [DllImport("nvEncodeAPI64.dll", EntryPoint = "NvEncodeAPIGetMaxSupportedVersion",
        CallingConvention = CallingConvention.StdCall)]
    private static extern int NvEncodeAPIGetMaxSupportedVersion(out uint version);

    [DllImport("nvEncodeAPI64.dll", EntryPoint = "NvEncodeAPICreateInstance",
        CallingConvention = CallingConvention.StdCall)]
    private static extern int NvEncodeAPICreateInstance(IntPtr functionList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEncodeCapsDelegate(
        IntPtr encoder, Guid encodeGuid, IntPtr capsParam, out int capsValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DestroyEncoderDelegate(IntPtr encoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenEncodeSessionExDelegate(
        IntPtr openParams, out IntPtr encoder);

    private static void ZeroMemory(IntPtr ptr, int size)
    {
        byte[] zero = new byte[size];
        Marshal.Copy(zero, 0, ptr, size);
    }

    public static int GetAv1EncoderEngineCount(int gpuIndex)
    {
        IntPtr functionList = IntPtr.Zero;
        IntPtr openParams = IntPtr.Zero;
        IntPtr capsParam = IntPtr.Zero;
        IntPtr cudaContext = IntPtr.Zero;
        IntPtr encoder = IntPtr.Zero;
        DestroyEncoderDelegate destroyEncoder = null;

        try
        {
            uint maxSupported;
            if (NvEncodeAPIGetMaxSupportedVersion(out maxSupported) != 0)
                return 1;

            // NvEncodeAPIGetMaxSupportedVersion encodes major in the upper
            // bits and minor in the low 4 bits: 13.0 == 0xD0.
            if (maxSupported < (13u << 4))
                return 1;

            if (CuInit(0) != 0)
                return 1;

            int cudaDevice;
            if (CuDeviceGet(out cudaDevice, gpuIndex) != 0)
                return 1;

            if (CuCtxCreate(out cudaContext, 0, cudaDevice) != 0 ||
                cudaContext == IntPtr.Zero)
                return 1;

            // NV_ENCODE_API_FUNCTION_LIST starts with two uint32 values,
            // followed by function pointers. We only read three known ABI
            // positions from the official API 13.x function list.
            functionList = Marshal.AllocHGlobal(4096);
            ZeroMemory(functionList, 4096);
            Marshal.WriteInt32(functionList, 0, unchecked((int)FunctionListVersion));

            if (NvEncodeAPICreateInstance(functionList) != 0)
                return 1;

            int pointerSize = IntPtr.Size;
            IntPtr getCapsPtr = Marshal.ReadIntPtr(functionList, 8 + (7 * pointerSize));
            IntPtr destroyPtr = Marshal.ReadIntPtr(functionList, 8 + (27 * pointerSize));
            IntPtr openSessionPtr = Marshal.ReadIntPtr(functionList, 8 + (29 * pointerSize));

            if (getCapsPtr == IntPtr.Zero ||
                destroyPtr == IntPtr.Zero ||
                openSessionPtr == IntPtr.Zero)
                return 1;

            GetEncodeCapsDelegate getCaps =
                (GetEncodeCapsDelegate)Marshal.GetDelegateForFunctionPointer(
                    getCapsPtr, typeof(GetEncodeCapsDelegate));
            destroyEncoder =
                (DestroyEncoderDelegate)Marshal.GetDelegateForFunctionPointer(
                    destroyPtr, typeof(DestroyEncoderDelegate));
            OpenEncodeSessionExDelegate openSession =
                (OpenEncodeSessionExDelegate)Marshal.GetDelegateForFunctionPointer(
                    openSessionPtr, typeof(OpenEncodeSessionExDelegate));

            // NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS on Win64:
            // version, deviceType, device, reserved, apiVersion,
            // reserved1[253], reserved2[64] = 1552 bytes.
            openParams = Marshal.AllocHGlobal(1552);
            ZeroMemory(openParams, 1552);
            Marshal.WriteInt32(openParams, 0, unchecked((int)OpenSessionVersion));
            Marshal.WriteInt32(openParams, 4, 1); // NV_ENC_DEVICE_TYPE_CUDA
            Marshal.WriteIntPtr(openParams, 8, cudaContext);
            Marshal.WriteInt32(openParams, 24, unchecked((int)NvencApiVersion));

            if (openSession(openParams, out encoder) != 0 ||
                encoder == IntPtr.Zero)
                return 1;

            // NV_ENC_CAPS_PARAM = version + capsToQuery + reserved[62].
            capsParam = Marshal.AllocHGlobal(256);
            ZeroMemory(capsParam, 256);
            Marshal.WriteInt32(capsParam, 0, unchecked((int)CapsParamVersion));
            Marshal.WriteInt32(capsParam, 4, NumEncoderEnginesCap);

            int engines;
            if (getCaps(encoder, Av1Guid, capsParam, out engines) != 0)
                return 1;

            if (engines < 1)
                return 1;

            // Current FFmpeg exposes explicit split modes up to four engines.
            if (engines > 4)
                engines = 4;

            return engines;
        }
        catch
        {
            return 1;
        }
        finally
        {
            if (encoder != IntPtr.Zero && destroyEncoder != null)
            {
                try { destroyEncoder(encoder); } catch {}
            }

            if (capsParam != IntPtr.Zero)
                Marshal.FreeHGlobal(capsParam);
            if (openParams != IntPtr.Zero)
                Marshal.FreeHGlobal(openParams);
            if (functionList != IntPtr.Zero)
                Marshal.FreeHGlobal(functionList);

            if (cudaContext != IntPtr.Zero)
            {
                try { CuCtxDestroy(cudaContext); } catch {}
            }
        }
    }
}
'@

        try {
            Add-Type -TypeDefinition $source -Language CSharp -ErrorAction Stop
        } catch {
            return 1
        }
    }

    try {
        $count = [FilmGrainNvencCaps]::GetAv1EncoderEngineCount($GpuIndex)
        if ($count -lt 1) { return 1 }
        if ($count -gt 4) { return 4 }
        return [int]$count
    } catch {
        return 1
    }
}

function Get-Grav1synthCapabilities {
    param([string]$Path)

    $result = [ordered]@{
        path = $Path
        available = $false
        version = '未检测'
        sfeCompatible = $false
    }

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [pscustomobject]$result
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $result.version = '未找到'
        return [pscustomobject]$result
    }

    try {
        $probe = Invoke-ProcessCapture -FilePath $Path -Arguments @('--version') -TimeoutSeconds 15
        if ($probe.ExitCode -ne 0) {
            $result.version = '检测失败'
            return [pscustomobject]$result
        }

        $result.available = $true
        $text = ([string]$probe.StdOut + "`n" + [string]$probe.StdErr).Trim()
        $match = [regex]::Match($text, '(?i)(\d+)\.(\d+)\.(\d+)')
        if (-not $match.Success) {
            $result.version = '未知'
            return [pscustomobject]$result
        }

        $versionText = '{0}.{1}.{2}' -f `
            $match.Groups[1].Value, `
            $match.Groups[2].Value, `
            $match.Groups[3].Value

        $result.version = $versionText
        try {
            $version = [version]$versionText
            $result.sfeCompatible = ($version -ge [version]'0.2.2')
        } catch {
            $result.sfeCompatible = $false
        }
    } catch {
        $result.version = '检测失败'
    }

    return [pscustomobject]$result
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
        adaptiveB = $false
        sceneCut = $false
        multipassQres = $false
        multipassFullres = $false
        splitEncodeMaxEngines = 1
    }

    $caps.available = Test-EncoderOption -Encoder $Encoder
    if (-not $caps.available) { return [pscustomobject]$caps }

    if ($Encoder -eq 'av1_nvenc') {
        $caps.uhq = Test-EncoderOption -Encoder $Encoder -Tune 'uhq' -ExtraArguments @(
            '-preset', 'p4', '-multipass', 'fullres',
            '-spatial-aq', '1', '-aq-strength', '8'
        )
        $engineCount = Get-Av1EncoderEngineCount
        if ($engineCount -ge 2 -and (Test-Av1SplitEncodeMode -EngineCount $engineCount)) {
            $caps.splitEncodeMaxEngines = $engineCount
        } else {
            $caps.splitEncodeMaxEngines = 1
        }
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
        $caps.adaptiveB = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-rc-lookahead', '32', '-b_adapt', '1')
        $caps.sceneCut = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-rc-lookahead', '32', '-no-scenecut', '0')
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
    if ($caps.lookahead) {
        $caps.adaptiveB = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-rc-lookahead', '32', '-b_adapt', '1')
        $caps.sceneCut = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-rc-lookahead', '32', '-no-scenecut', '0')
    }
    $caps.multipassQres = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-multipass', 'qres')
    $caps.multipassFullres = Test-EncoderOption -Encoder $Encoder -ExtraArguments @('-multipass', 'fullres')
    return [pscustomobject]$caps
}

function Get-X264Capabilities {
    $caps = [ordered]@{
        available = $false
        tuneGrain = $false
        high10 = $false
        errorDiffusionDither = $false
        bStrategy2 = $false
    }

    $base = @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=30',
        '-frames:v', '2', '-an',
        '-c:v', 'libx264', '-preset', 'veryfast'
    )

    $caps.available = Test-FFmpeg -Arguments ($base + @('-pix_fmt', 'yuv420p', '-profile:v', 'high', '-f', 'null', 'NUL'))
    if (-not $caps.available) { return [pscustomobject]$caps }

    $caps.tuneGrain = Test-FFmpeg -Arguments ($base + @('-tune', 'grain', '-pix_fmt', 'yuv420p', '-profile:v', 'high', '-f', 'null', 'NUL'))
    $caps.bStrategy2 = Test-FFmpeg -Arguments ($base + @('-tune', 'grain', '-b_strategy', '2', '-pix_fmt', 'yuv420p', '-profile:v', 'high', '-f', 'null', 'NUL'))
    $caps.high10 = Test-FFmpeg -Arguments @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=30',
        '-frames:v', '2', '-an', '-vf', 'format=yuv420p10le',
        '-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'grain',
        '-pix_fmt', 'yuv420p10le', '-profile:v', 'high10',
        '-f', 'null', 'NUL'
    )
    $caps.errorDiffusionDither = Test-FFmpeg -Arguments @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=30',
        '-frames:v', '1', '-an',
        '-vf', 'format=yuv420p10le,zscale=dither=error_diffusion,format=yuv420p',
        '-f', 'null', 'NUL'
    )
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

function Get-OpenSvpCapabilities {
    param([string]$Root)

    $result = [ordered]@{
        runtimeReady = $false
        cpu = $false
        gpu = $false
    }

    if ([string]::IsNullOrWhiteSpace($Root)) { return [pscustomobject]$result }

    $vspipe = Join-Path $Root '.venv\Scripts\vspipe.exe'
    $check = Join-Path $Root 'Check_OpenSVPFlow.vpy'
    $plugins = Join-Path $Root 'Plugins'
    $svp1 = Join-Path $plugins 'svpflow1_vs.dll'
    $svp2 = Join-Path $plugins 'svpflow2_vs.dll'
    if (-not (Test-Path -LiteralPath $vspipe -PathType Leaf)) { return [pscustomobject]$result }
    if (-not (Test-Path -LiteralPath $check -PathType Leaf)) { return [pscustomobject]$result }
    if (-not (Test-Path -LiteralPath $svp1 -PathType Leaf)) { return [pscustomobject]$result }
    if (-not (Test-Path -LiteralPath $svp2 -PathType Leaf)) { return [pscustomobject]$result }

    $result.runtimeReady = $true

    $cpuProbe = Invoke-ProcessCapture -FilePath $vspipe -Arguments @(
        '--arg', "plugin_dir=$plugins", '--arg', 'gpu=0', '--arg', 'algo=13',
        '--start', '0', '--end', '0', $check, 'NUL'
    )
    $result.cpu = ($cpuProbe.ExitCode -eq 0)

    $gpuProbe = Invoke-ProcessCapture -FilePath $vspipe -Arguments @(
        '--arg', "plugin_dir=$plugins", '--arg', 'gpu=1', '--arg', 'algo=13',
        '--start', '0', '--end', '0', $check, 'NUL'
    )
    $result.gpu = ($gpuProbe.ExitCode -eq 0)
    return [pscustomobject]$result
}

function Write-CmdEnvironment {
    param([pscustomobject]$Profile, [string]$Path)

    $av1 = $Profile.caps.av1
    $hevc = $Profile.caps.hevc
    $h264 = $Profile.caps.h264
    $x264 = $Profile.caps.x264
    $openSvp = $Profile.caps.openSvp
    $lines = @(
        '@rem Generated by FilmGrain_Hardware_Caps.ps1. Do not edit.',
        ('set "FG_CAP_CACHE_STATE={0}"' -f (ConvertTo-CmdValue $Profile.cacheState)),
        ('set "FG_CAP_GPU_NAME={0}"' -f (ConvertTo-CmdValue $Profile.gpu.name)),
        ('set "FG_CAP_DRIVER_VERSION={0}"' -f (ConvertTo-CmdValue $Profile.gpu.driverVersion)),
        ('set "FG_CAP_AV1={0}"' -f (ConvertTo-CmdBool $av1.available)),
        ('set "FG_CAP_AV1_UHQ={0}"' -f (ConvertTo-CmdBool $av1.uhq)),
        ('set "FG_CAP_AV1_SFE_MAX={0}"' -f (ConvertTo-CmdValue $av1.splitEncodeMaxEngines)),
        ('set "FG_CAP_GRAV1SYNTH_VERSION={0}"' -f (ConvertTo-CmdValue $Profile.grav1synth.version)),
        ('set "FG_CAP_GRAV1SYNTH_SFE={0}"' -f (ConvertTo-CmdBool $Profile.grav1synth.sfeCompatible)),
        ('set "FG_CAP_HEVC={0}"' -f (ConvertTo-CmdBool $hevc.available)),
        ('set "FG_CAP_H264={0}"' -f (ConvertTo-CmdBool $h264.available)),
        ('set "FG_CAP_X264={0}"' -f (ConvertTo-CmdBool $x264.available)),
        ('set "FG_CAP_X264_GRAIN={0}"' -f (ConvertTo-CmdBool $x264.tuneGrain)),
        ('set "FG_CAP_X264_HIGH10={0}"' -f (ConvertTo-CmdBool $x264.high10)),
        ('set "FG_CAP_X264_DITHER={0}"' -f (ConvertTo-CmdBool $x264.errorDiffusionDither)),
        ('set "FG_CAP_X264_BSTRATEGY2={0}"' -f (ConvertTo-CmdBool $x264.bStrategy2)),
        ('set "FG_CAP_VULKAN={0}"' -f (ConvertTo-CmdBool $Profile.caps.vulkan)),
        ('set "FG_CAP_VULKAN_GRAIN={0}"' -f (ConvertTo-CmdBool $Profile.caps.vulkanGrain)),
        ('set "FG_CAP_HEVC_PIPELINE={0}"' -f (ConvertTo-CmdBool $Profile.caps.hevcPipeline)),
        ('set "FG_CAP_X264_PIPELINE={0}"' -f (ConvertTo-CmdBool $Profile.caps.x264Pipeline)),
        ('set "FG_CAP_NVDEC={0}"' -f (ConvertTo-CmdBool $Profile.caps.nvdec)),
        ('set "FG_CAP_SVP_RUNTIME={0}"' -f (ConvertTo-CmdBool $openSvp.runtimeReady)),
        ('set "FG_CAP_SVP_CPU={0}"' -f (ConvertTo-CmdBool $openSvp.cpu)),
        ('set "FG_CAP_SVP_GPU={0}"' -f (ConvertTo-CmdBool $openSvp.gpu)),
        ('set "FG_CAP_AV1_BF={0}"' -f (ConvertTo-CmdBool $av1.bFrames)),
        ('set "FG_CAP_AV1_BREF={0}"' -f (ConvertTo-CmdBool $av1.bReference)),
        ('set "FG_CAP_AV1_SAQ={0}"' -f (ConvertTo-CmdBool $av1.spatialAQ)),
        ('set "FG_CAP_AV1_TAQ={0}"' -f (ConvertTo-CmdBool $av1.temporalAQ)),
        ('set "FG_CAP_AV1_LOOKAHEAD={0}"' -f (ConvertTo-CmdBool $av1.lookahead)),
        ('set "FG_CAP_AV1_BADAPT={0}"' -f (ConvertTo-CmdBool $av1.adaptiveB)),
        ('set "FG_CAP_AV1_SCENECUT={0}"' -f (ConvertTo-CmdBool $av1.sceneCut)),
        ('set "FG_CAP_AV1_QRES={0}"' -f (ConvertTo-CmdBool $av1.multipassQres)),
        ('set "FG_CAP_AV1_FULLRES={0}"' -f (ConvertTo-CmdBool $av1.multipassFullres)),
        ('set "FG_CAP_HEVC_BF={0}"' -f (ConvertTo-CmdBool $hevc.bFrames)),
        ('set "FG_CAP_HEVC_BREF={0}"' -f (ConvertTo-CmdBool $hevc.bReference)),
        ('set "FG_CAP_HEVC_SAQ={0}"' -f (ConvertTo-CmdBool $hevc.spatialAQ)),
        ('set "FG_CAP_HEVC_TAQ={0}"' -f (ConvertTo-CmdBool $hevc.temporalAQ)),
        ('set "FG_CAP_HEVC_LOOKAHEAD={0}"' -f (ConvertTo-CmdBool $hevc.lookahead)),
        ('set "FG_CAP_HEVC_BADAPT={0}"' -f (ConvertTo-CmdBool $hevc.adaptiveB)),
        ('set "FG_CAP_HEVC_SCENECUT={0}"' -f (ConvertTo-CmdBool $hevc.sceneCut)),
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
    $gravCaps = Get-Grav1synthCapabilities -Path $Grav1synth
    $gravSignature = 'missing'
    if (-not [string]::IsNullOrWhiteSpace($Grav1synth) -and (Test-Path -LiteralPath $Grav1synth -PathType Leaf)) {
        try {
            $gravFile = Get-Item -LiteralPath $Grav1synth
            $gravSignature = $gravFile.FullName + '|' + $gravFile.Length + '|' + $gravFile.LastWriteTimeUtc.Ticks + '|' + [string]$gravCaps.version
        } catch {}
    }

    $openSvpVspipe = Join-Path $OpenSvpRoot '.venv\Scripts\vspipe.exe'
    $openSvpDll1 = Join-Path $OpenSvpRoot 'Plugins\svpflow1_vs.dll'
    $openSvpDll2 = Join-Path $OpenSvpRoot 'Plugins\svpflow2_vs.dll'
    $openSvpCheck = Join-Path $OpenSvpRoot 'Check_OpenSVPFlow.vpy'
    $openSvpSignature = @()
    foreach ($p in @($openSvpVspipe, $openSvpDll1, $openSvpDll2, $openSvpCheck)) {
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            $f = Get-Item -LiteralPath $p
            $openSvpSignature += ($f.FullName + '|' + $f.Length + '|' + $f.LastWriteTimeUtc.Ticks)
        } else {
            $openSvpSignature += ($p + '|missing')
        }
    }

    $signatureText = @(
        "schema=$SchemaVersion",
        "ffmpeg=$($ffmpegFile.FullName)",
        "size=$($ffmpegFile.Length)",
        "write=$($ffmpegFile.LastWriteTimeUtc.Ticks)",
        "version=$versionLine",
        "gpu=$($gpu.identity)",
        "gpuIndex=$GpuIndex",
        "cuda=$CudaDevice",
        "vulkan=$VulkanDevice",
        "grav1synth=$gravSignature",
        "openSvpRoot=$OpenSvpRoot",
        ('openSvp=' + ($openSvpSignature -join ';'))
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
        $x264Caps = Get-X264Capabilities

        $vulkanArgs = @(
            '-hide_banner', '-loglevel', 'error',
            '-init_hw_device', "vulkan=vk:$VulkanDevice", '-filter_hw_device', 'vk',
            '-f', 'lavfi', '-i', 'color=c=black:s=128x128:r=30',
            '-vf', 'format=p010le,hwupload,scale_vulkan=w=64:h=64,hwdownload,format=p010le',
            '-frames:v', '1', '-f', 'null', 'NUL'
        )
        $vulkan = Test-FFmpeg -Arguments $vulkanArgs

        $vulkanGrainArgs = @(
            '-hide_banner', '-loglevel', 'error',
            '-init_hw_device', "vulkan=vk:$VulkanDevice", '-filter_hw_device', 'vk',
            '-f', 'lavfi', '-i', 'color=c=black:s=128x128:r=30',
            '-f', 'lavfi', '-i', 'color=c=gray:s=128x128:r=30',
            '-filter_complex', '[0:v]format=p010le,hwupload[a];[1:v]format=p010le,hwupload[b];[a][b]blend_vulkan=all_mode=overlay:all_opacity=0.5,hwdownload,format=p010le',
            '-frames:v', '1', '-f', 'null', 'NUL'
        )
        $vulkanGrain = Test-FFmpeg -Arguments $vulkanGrainArgs

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

        $openSvpCaps = Get-OpenSvpCapabilities -Root $OpenSvpRoot

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
            grav1synth = $gravCaps
            caps = [pscustomobject][ordered]@{
                av1 = $av1Caps
                hevc = $hevcCaps
                h264 = $h264Caps
                x264 = $x264Caps
                nvdec = $nvdec
                vulkan = $vulkan
                vulkanGrain = $vulkanGrain
                hevcPipeline = ([bool]$hevcCaps.available -and $vulkan)
                x264Pipeline = ([bool]$x264Caps.available -and [bool]$x264Caps.tuneGrain -and $vulkan -and $vulkanGrain)
                openSvp = $openSvpCaps
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
        Write-Host ('AV1 SFE engines . x{0}' -f [int]$profile.caps.av1.splitEncodeMaxEngines)
        Write-Host ('grav1synth ....... {0}' -f [string]$profile.grav1synth.version)
        Write-Host ('grav1synth SFE ... {0}' -f $mark[[int][bool]$profile.grav1synth.sfeCompatible])
        Write-Host ('HEVC Main10 ...... {0}' -f $mark[[int][bool]$profile.caps.hevcPipeline])
        Write-Host ('x264 Grain ....... {0}' -f $mark[[int][bool]$profile.caps.x264Pipeline])
        Write-Host ('x264 High10 ...... {0}' -f $mark[[int][bool]$profile.caps.x264.high10])
        Write-Host ('x264 10->8 dither . {0}' -f $mark[[int][bool]$profile.caps.x264.errorDiffusionDither])
        Write-Host ('NVDEC CUDA ....... {0}' -f $mark[[int][bool]$profile.caps.nvdec])
        Write-Host ('Vulkan ........... {0}' -f $mark[[int][bool]$profile.caps.vulkan])
        Write-Host ('Vulkan Grain ..... {0}' -f $mark[[int][bool]$profile.caps.vulkanGrain])
    }
} catch {
    Write-Error ("Hardware capability detection failed: " + $_.Exception.Message)
    exit 1
}
