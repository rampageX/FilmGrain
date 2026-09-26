param(
    [ValidateSet('FFmpeg','Grav1synth','Python','OpenSVPFlow')]
    [string]$Tool,
    [string]$ProxyUrl = '',
    [switch]$UseSystemPython,
    [switch]$PauseOnFailure
)
$ErrorActionPreference = 'Stop'
$WebProxy = @{}
if ($ProxyUrl) {
    $parsed = $null
    if (-not [Uri]::TryCreate($ProxyUrl, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -notin @('http','https') -or -not $parsed.Host -or $parsed.UserInfo) {
        throw 'Proxy must be an HTTP(S) URL without credentials, e.g. http://127.0.0.1:7890.'
    }
    $WebProxy['Proxy'] = $ProxyUrl
    $env:FGS_SETUP_PROXY = $ProxyUrl
    Write-Host ('Using proxy: ' + $ProxyUrl)
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Root = Split-Path -Parent $PSScriptRoot
$Deps = Join-Path $Root '_Dependencies'
$Stage = Join-Path $Deps ('_stage_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $Deps, $Stage | Out-Null

function Download-File([string]$Url, [string]$Output, [long]$Limit) {
    Write-Host ('Downloading ' + $Url)
    Invoke-WebRequest @WebProxy -UseBasicParsing -Uri $Url -OutFile $Output -MaximumRedirection 5
    if (-not (Test-Path -LiteralPath $Output -PathType Leaf) -or (Get-Item -LiteralPath $Output).Length -gt $Limit) {
        throw 'Download missing or larger than expected.'
    }
}
function Check-Hash([string]$Path, [string]$Expected, [string]$Algorithm) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash
    if ($actual -ine $Expected) { throw ('Checksum mismatch: ' + $Path) }
}
function Assert-ZipEntry([string]$Name, [hashtable]$Seen) {
    $n = $Name.Replace('\','/')
    if ($n.StartsWith('/') -or $n -match '^[A-Za-z]:' -or $n -match ':' -or $n -match '[\x00-\x1f]' -or $n.Split('/') -contains '..' -or $n.Split('/') -contains '.' -or $n.Contains('//')) { throw ('Unsafe archive entry: ' + $Name) }
    $key = $n.TrimEnd('/').ToLowerInvariant()
    if ($key.Length -eq 0 -or $Seen.ContainsKey($key)) { throw ('Duplicate archive entry: ' + $Name) }
    $Seen[$key] = $true
    return $n
}
function Expand-ZipRuntime([string]$ZipPath, [string]$Output) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $seen = @{}; $match = $null; $total = [long]0
        if ($zip.Entries.Count -gt 10000) { throw 'Too many archive entries.' }
        foreach ($entry in $zip.Entries) {
            $n = Assert-ZipEntry $entry.FullName $seen
            $total += $entry.Length
            if ($total -gt 300MB -or $entry.Length -gt 200MB) { throw 'Archive exceeds size limit.' }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw ('Symbolic link is not allowed: ' + $n) }
            if ($n.EndsWith('/grav1synth.exe', [StringComparison]::OrdinalIgnoreCase) -or
                $n.Equals('grav1synth.exe', [StringComparison]::OrdinalIgnoreCase)) {
                if ($null -ne $match) { throw 'More than one grav1synth.exe in archive.' }
                $match = $n
            }
        }
        if (-not $match) { throw 'grav1synth.exe absent from release archive.' }
        New-Item -ItemType Directory -Path $Output | Out-Null
        foreach ($entry in $zip.Entries) {
            $n = $entry.FullName.Replace('\','/')
            if ($n.EndsWith('/')) { continue }
            $target = Join-Path $Output ($n.Replace('/','\'))
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            $source = $entry.Open()
            try { $destStream = [IO.File]::Create($target); try { $source.CopyTo($destStream) } finally { $destStream.Dispose() } }
            finally { $source.Dispose() }
        }
        return (Join-Path $Output ($match.Replace('/','\')))
    } finally { $zip.Dispose() }
}
function Test-Command([string]$Exe, [string[]]$CommandArgs) {
    & $Exe @CommandArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ('Verification failed: ' + $Exe) }
}
function Install-FFmpeg {
    $url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z'
    $cacheDir = Join-Path $Deps 'Downloads'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $archive = Join-Path $cacheDir 'ffmpeg-release-full.7z'
    $hashFile = Join-Path $Stage 'full.sha256'
    Download-File ($url + '.sha256') $hashFile 4096
    $hashText = [IO.File]::ReadAllText($hashFile).Trim()
    $m = [regex]::Match($hashText, '(?i)\b[0-9a-f]{64}\b')
    if (-not $m.Success) { throw 'No valid SHA256 checksum from build provider.' }
    if (Test-Path -LiteralPath $archive -PathType Leaf) {
        try { Check-Hash $archive $m.Value 'SHA256'; Write-Host 'Using verified cached FFmpeg archive.' }
        catch { Remove-Item -LiteralPath $archive -Force }
    }
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $newArchive = Join-Path $Stage 'full.download.7z'
        Download-File $url $newArchive 600MB
        Check-Hash $newArchive $m.Value 'SHA256'
        Move-Item -LiteralPath $newArchive -Destination $archive
    }
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar)) { throw 'Windows tar.exe is needed to unpack the verified full .7z archive.' }
    $list = & $tar -tf $archive 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Windows tar.exe cannot read this 7z archive.' }
    if ($list.Count -gt 25000) { throw 'Too many archive entries.' }
    $seen = @{}
    foreach ($name in $list) { [void](Assert-ZipEntry ([string]$name) $seen) }
    $unpacked = Join-Path $Stage 'unpacked'
    New-Item -ItemType Directory -Path $unpacked | Out-Null
    & $tar -xf $archive -C $unpacked
    if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed.' }
    $bins = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File -Filter ffmpeg.exe)
    if ($bins.Count -ne 1) { throw 'Expected exactly one ffmpeg.exe.' }
    $probe = Join-Path $bins[0].DirectoryName 'ffprobe.exe'
    if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'ffprobe.exe missing alongside ffmpeg.exe.' }
    Test-Command $bins[0].FullName @('-version')
    Test-Command $probe @('-version')
    $filterOut = Join-Path $Stage 'ffmpeg-filters.out.txt'
    $filterErr = Join-Path $Stage 'ffmpeg-filters.err.txt'
    $filterProcess = Start-Process -FilePath $bins[0].FullName -ArgumentList '-filters' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $filterOut -RedirectStandardError $filterErr
    if ($filterProcess.ExitCode -ne 0) { throw ('FFmpeg filter verification failed: ' + [IO.File]::ReadAllText($filterErr)) }
    $features = [IO.File]::ReadAllText($filterOut)
    if ($features -notmatch '\blibplacebo\b') { throw 'This build does not expose the libplacebo filter.' }
    $dest = Join-Path $Deps 'FFmpeg'
    if (Test-Path -LiteralPath $dest) { throw ('Existing destination is preserved: ' + $dest) }
    New-Item -ItemType Directory -Path $dest | Out-Null
    Copy-Item -LiteralPath $bins[0].FullName -Destination $dest
    Copy-Item -LiteralPath $probe -Destination $dest
    Write-Host ('Installed: ' + $dest)
}
function Install-Grav1synth {
    $headers = @{ 'User-Agent' = 'FilmGrainStudio-Setup'; 'Accept' = 'application/vnd.github+json' }
    $release = Invoke-RestMethod @WebProxy -Uri 'https://api.github.com/repos/rampageX/grav1synth/releases/latest' -Headers $headers
    $assets = @($release.assets | Where-Object { $_.name -match '(?i)\.(zip|exe)$' -and $_.name -match '(?i)(windows|win64|x86_64|amd64|grav1synth)' })
    if ($assets.Count -ne 1) { throw ('Could not uniquely select a Windows release asset. Inspect https://github.com/rampageX/grav1synth/releases/latest . Assets: ' + (($release.assets | ForEach-Object name) -join ', ')) }
    $asset = $assets[0]
    if ($asset.size -gt 200MB) { throw 'Release asset exceeds 200 MB.' }
    $cacheDir = Join-Path $Deps 'Downloads'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $file = Join-Path $cacheDir $asset.name
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        $good = (Get-Item -LiteralPath $file).Length -eq $asset.size
        if ($good -and $asset.digest -match '^sha256:([0-9a-fA-F]{64})$') {
            try { Check-Hash $file $Matches[1] 'SHA256' } catch { $good = $false }
        }
        if (-not $good) { Remove-Item -LiteralPath $file -Force }
        else { Write-Host 'Using cached grav1synth release asset.' }
    }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        $download = Join-Path $Stage $asset.name
        Download-File $asset.browser_download_url $download 200MB
        if ($asset.size -gt 0 -and (Get-Item -LiteralPath $download).Length -ne $asset.size) { throw 'Release asset size mismatch.' }
        if ($asset.digest -match '^sha256:([0-9a-fA-F]{64})$') { Check-Hash $download $Matches[1] 'SHA256' }
        Move-Item -LiteralPath $download -Destination $file
    }
    $runtime = Join-Path $Stage 'grav_runtime'
    if ($asset.name -match '(?i)\.zip$') { $exe = Expand-ZipRuntime $file $runtime }
    else {
        New-Item -ItemType Directory -Path $runtime | Out-Null
        $exe = Join-Path $runtime 'grav1synth.exe'
        Copy-Item -LiteralPath $file -Destination $exe
    }
    $out = Join-Path $Stage 'grav-version.out.txt'
    $err = Join-Path $Stage 'grav-version.err.txt'
    try {
        $process = Start-Process -FilePath $exe -ArgumentList '--version' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    } catch { throw ('Could not launch grav1synth from its complete release directory: ' + $_.Exception.Message) }
    if ($process.ExitCode -ne 0) {
        throw ('grav1synth --version failed, exit code 0x{0:X8}. stdout: {1} stderr: {2}' -f
            ($process.ExitCode -band 0xffffffff), [IO.File]::ReadAllText($out), [IO.File]::ReadAllText($err))
    }
    Write-Host ([IO.File]::ReadAllText($out))
    $dest = Join-Path $Deps 'Grav1synth'
    if (Test-Path -LiteralPath $dest) { throw ('Existing destination is preserved: ' + $dest) }
    Copy-Item -LiteralPath (Split-Path -Parent $exe) -Destination $dest -Recurse
    if (-not (Test-Path -LiteralPath (Join-Path $dest 'grav1synth.exe'))) { throw 'Installed executable not found.' }
    Write-Host ('Installed: ' + $dest)
}
function Find-Python([bool]$IncludeSystem) {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($IncludeSystem) {
        foreach ($name in @('python.exe','py.exe')) {
            $cmd = Get-Command $name -ErrorAction SilentlyContinue
            if ($cmd) { $candidates.Add($cmd.Source) }
        }
    }
    $known = Join-Path $Deps 'Python\tools\python.exe'
    if (Test-Path -LiteralPath $known) { $candidates.Add($known) }
    foreach ($p in $candidates) {
        try {
            $code = & $p -c 'import sys,venv,ensurepip;print(sys.version_info.major*100+sys.version_info.minor)' 2>$null
            if ($LASTEXITCODE -eq 0 -and [int]($code | Select-Object -Last 1) -ge 312) {
                $probeVenv = Join-Path $Stage ('venv_check_' + [Guid]::NewGuid().ToString('N'))
                try {
                    & $p -m venv $probeVenv 2>$null | Out-Null
                    $venvPython = Join-Path $probeVenv 'Scripts\python.exe'
                    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $venvPython)) {
                        & $venvPython -m pip --version 2>$null | Out-Null
                        if ($LASTEXITCODE -eq 0) { return $p }
                    }
                } finally { Remove-Item -LiteralPath $probeVenv -Recurse -Force -ErrorAction SilentlyContinue }
            }
        } catch { }
    }
    return $null
}
function Install-Python {
    $existing = Find-Python $UseSystemPython.IsPresent
    if ($existing) { Write-Host ('Usable existing Python: ' + $existing); return }
    $version = '3.14.0'
    $base = 'https://api.nuget.org/v3-flatcontainer/python/' + $version + '/python.' + $version + '.nupkg'
    $archive = Join-Path $Stage 'python.nupkg'
    # The NuGet flat-container API serves the .nupkg, not a .nupkg.sha512 URL.
    # Keep the package version pinned, enforce extraction limits, and test the runtime.
    Download-File $base $archive 150MB
    Write-Host 'NuGet does not publish a separate checksum at .nupkg.sha512; checking the ZIP and Python runtime.'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $seen = @{}; $count = 0; $total = [long]0
        foreach ($entry in $zip.Entries) {
            $n = Assert-ZipEntry $entry.FullName $seen
            $count++; $total += $entry.Length
            if ($count -gt 30000 -or $total -gt 500MB -or $entry.Length -gt 100MB) { throw 'Python package exceeds extraction limits.' }
        }
        $dest = Join-Path $Deps 'Python'
        if (Test-Path -LiteralPath $dest) { throw ('Existing destination is preserved: ' + $dest) }
        $tmp = Join-Path $Stage 'Python'
        New-Item -ItemType Directory -Path $tmp | Out-Null
        foreach ($entry in $zip.Entries) {
            $n = $entry.FullName.Replace('\','/')
            if (-not $n.StartsWith('tools/', [StringComparison]::OrdinalIgnoreCase) -or $n.EndsWith('/')) { continue }
            $target = Join-Path $tmp ($n.Replace('/','\'))
            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            $inputStream = $entry.Open()
            try { $outputStream = [IO.File]::Create($target); try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() } }
            finally { $inputStream.Dispose() }
        }
        $python = Join-Path $tmp 'tools\python.exe'
        Test-Command $python @('-c','import sys,venv,ensurepip;print(sys.version)')
        Move-Item -LiteralPath $tmp -Destination $dest
        Write-Host ('Installed: ' + (Join-Path $dest 'tools\python.exe'))
    } finally { $zip.Dispose() }
}
function Install-OpenSVPFlow {
    $python = $null
    $venvPython = Join-Path $Root '_OpenSVPFlow\.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $venvPython -PathType Leaf) {
        try {
            & $venvPython -c 'import vapoursynth;print(1)' 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { $python = $venvPython; Write-Host 'Reusing existing isolated OpenSVPFlow environment.' }
        } catch { }
    }
    if (-not $python) { Install-Python; $python = Find-Python $UseSystemPython.IsPresent }
    if (-not $python) { throw 'No usable Python after installation.' }
    $installer = Join-Path $Root '_OpenSVPFlow\Setup_OpenSVPFlow.ps1'
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $installer -PythonExe $python -SkipUserConfig
    if ($LASTEXITCODE -ne 0) { throw 'OpenSVPFlow installation failed; see output above.' }
}
try {
    switch ($Tool) {
        FFmpeg { Install-FFmpeg }
        Grav1synth { Install-Grav1synth }
        Python { Install-Python }
        OpenSVPFlow { Install-OpenSVPFlow }
    }
    Write-Host ('SUCCESS: ' + $Tool)
} catch {
    Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
    if ($PauseOnFailure) { [void](Read-Host '安装失败。请复制上方错误，按回车关闭窗口') }
    throw
} finally {
    Remove-Item -LiteralPath $Stage -Recurse -Force -ErrorAction SilentlyContinue
}
