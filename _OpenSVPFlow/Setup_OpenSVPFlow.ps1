$ErrorActionPreference = "Stop"

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$VenvDir = Join-Path $Root ".venv"
$PluginsDir = Join-Path $Root "Plugins"
$VenvPython = Join-Path $VenvDir "Scripts\python.exe"
$VSPipe = Join-Path $VenvDir "Scripts\vspipe.exe"
$VSCLI = Join-Path $VenvDir "Scripts\vapoursynth.exe"
$CheckScript = Join-Path $Root "Check_OpenSVPFlow.vpy"

$OpenSVPTag = "nightly-20260804-5ef4260"
$OpenSVPBase = "https://github.com/Z1xus/open-svpflow/releases/download/$OpenSVPTag"

function Assert-LastExitCode {
    param([string]$Step)
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed. ExitCode=$LASTEXITCODE"
    }
}

function Find-CompatiblePython {
    $Python = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($Python) {
        try {
            $VersionCode = & $Python.Source -c "import sys; print(sys.version_info.major*100+sys.version_info.minor)" 2>$null
            if ($LASTEXITCODE -eq 0 -and [int]$VersionCode -ge 312) {
                return $Python.Source
            }
        } catch {
        }
    }

    $PyLauncher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($PyLauncher) {
        try {
            $Resolved = & $PyLauncher.Source -3.14 -c "import sys; print(sys.executable)" 2>$null
            if ($LASTEXITCODE -eq 0 -and $Resolved) {
                return ($Resolved | Select-Object -First 1)
            }
        } catch {
        }

        try {
            $Resolved = & $PyLauncher.Source -3 -c "import sys; print(sys.executable if sys.version_info >= (3,12) else '')" 2>$null
            if ($LASTEXITCODE -eq 0 -and $Resolved) {
                return ($Resolved | Select-Object -First 1)
            }
        } catch {
        }
    }

    throw "Python 3.12 or later was not found. Python 3.14.x is supported and recommended."
}

Write-Host "============================================================"
Write-Host " Film Grain Studio OpenSVPFlow Runtime Setup"
Write-Host "============================================================"
Write-Host ""

$PythonExe = Find-CompatiblePython
$PythonVersion = & $PythonExe -c "import sys; print(sys.version.split()[0])"
Assert-LastExitCode "Python version check"
Write-Host "[OK] Python: $PythonVersion"
Write-Host "     $PythonExe"

if (-not (Test-Path $VenvPython)) {
    Write-Host ""
    Write-Host "[1/5] Creating isolated Python environment..."
    & $PythonExe -m venv $VenvDir
    Assert-LastExitCode "Create virtual environment"
} else {
    Write-Host ""
    Write-Host "[1/5] Existing isolated Python environment found."
}

Write-Host "[2/5] Installing pinned VapourSynth and BestSource..."
& $VenvPython -m pip install --disable-pip-version-check --upgrade pip
Assert-LastExitCode "pip upgrade"
& $VenvPython -m pip install --disable-pip-version-check "vapoursynth==79" "vapoursynth-bestsource==21.0"
Assert-LastExitCode "VapourSynth / BestSource install"

Write-Host "[3/5] Configuring VapourSynth..."
if (Test-Path $VSCLI) {
    & $VSCLI config
    Assert-LastExitCode "vapoursynth config"
} else {
    & $VenvPython -m vapoursynth config
    Assert-LastExitCode "vapoursynth config"
}

New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null
$SVP1 = Join-Path $PluginsDir "svpflow1_vs.dll"
$SVP2 = Join-Path $PluginsDir "svpflow2_vs.dll"

Write-Host "[4/5] Downloading open-svpflow $OpenSVPTag..."
Invoke-WebRequest -UseBasicParsing -Uri "$OpenSVPBase/svpflow1_vs.dll" -OutFile $SVP1
Invoke-WebRequest -UseBasicParsing -Uri "$OpenSVPBase/svpflow2_vs.dll" -OutFile $SVP2

if (-not (Test-Path $SVP1)) { throw "svpflow1_vs.dll download failed." }
if (-not (Test-Path $SVP2)) { throw "svpflow2_vs.dll download failed." }

Write-Host "[5/5] Verifying VapourSynth, BestSource and open-svpflow..."
& $VSPipe --version
Assert-LastExitCode "vspipe version check"

Write-Host ""
Write-Host "CPU smoke test..."
& $VSPipe --arg "plugin_dir=$PluginsDir" --arg "gpu=0" --arg "algo=13" --start 0 --end 0 $CheckScript --
Assert-LastExitCode "open-svpflow CPU smoke test"

Write-Host "GPU/OpenCL smoke test..."
& $VSPipe --arg "plugin_dir=$PluginsDir" --arg "gpu=1" --arg "algo=13" --start 0 --end 0 $CheckScript --
Assert-LastExitCode "open-svpflow GPU smoke test"

Write-Host ""
Write-Host "============================================================"
Write-Host " Setup OK"
Write-Host "============================================================"
Write-Host "VapourSynth : R79"
Write-Host "BestSource  : 21.0"
Write-Host "open-svpflow: $OpenSVPTag"
Write-Host "Plugin dir  : $PluginsDir"
Write-Host "Config      : %APPDATA%\vapoursynth\vapoursynth.toml"
Write-Host ""
Write-Host "Next: restart Film Grain Studio, then enable OpenSVPFlow interpolation."
