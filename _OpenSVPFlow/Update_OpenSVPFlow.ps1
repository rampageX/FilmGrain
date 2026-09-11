$ErrorActionPreference = "Stop"

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PluginsDir = Join-Path $Root "Plugins"
$BackupRoot = Join-Path $Root "_PluginBackup"
$VSPipe = Join-Path $Root ".venv\Scripts\vspipe.exe"
$CheckScript = Join-Path $Root "Check_OpenSVPFlow.vpy"
$VersionFile = Join-Path $PluginsDir "open-svpflow-version.txt"

$ApiUrl = "https://api.github.com/repos/Z1xus/open-svpflow/releases/latest"
$WindowsAssetName = "x86_64-pc-windows-msvc.zip"
$UserAgent = "FilmGrain-OpenSVPFlow-Updater"

function Assert-LastExitCode {
    param([string]$Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed. ExitCode=$LASTEXITCODE"
    }
}

function Get-SafeName {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return "unknown"
    }

    return ($Text -replace '[<>:"/\\|?*]', "_")
}

function Remove-SafeTemp {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $RootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\")
    $PathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd("\")
    $Prefix = $RootFull + "\"
    $Leaf = Split-Path -Leaf $PathFull

    if (-not $PathFull.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete temp path outside updater root: $PathFull"
    }

    if (-not $Leaf.StartsWith("_UpdateTemp_", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete unexpected temp path: $PathFull"
    }

    if (Test-Path $PathFull) {
        Remove-Item -LiteralPath $PathFull -Recurse -Force
    }
}

function Test-OpenSVPFlowPlugins {
    param(
        [string]$PluginDir,
        [string]$Label
    )

    if (-not (Test-Path $VSPipe)) {
        Write-Host "[WARN] vspipe.exe not found. Smoke test skipped: $VSPipe"
        return
    }

    if (-not (Test-Path $CheckScript)) {
        Write-Host "[WARN] Check_OpenSVPFlow.vpy not found. Smoke test skipped: $CheckScript"
        return
    }

    Write-Host "       CPU smoke test..."
    & $VSPipe --arg "plugin_dir=$PluginDir" --arg "gpu=0" --arg "algo=13" --start 0 --end 0 $CheckScript --
    Assert-LastExitCode "$Label CPU smoke test"

    Write-Host "       GPU/OpenCL smoke test..."
    & $VSPipe --arg "plugin_dir=$PluginDir" --arg "gpu=1" --arg "algo=13" --start 0 --end 0 $CheckScript --
    Assert-LastExitCode "$Label GPU/OpenCL smoke test"
}

function Get-CurrentTag {
    if (Test-Path $VersionFile) {
        $Tag = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
        if (-not [string]::IsNullOrWhiteSpace($Tag)) {
            return $Tag
        }
    }

    return "unknown"
}

Write-Host "============================================================"
Write-Host " Film Grain Studio - open-svpflow updater"
Write-Host "============================================================"
Write-Host ""

$TempDir = $null
$BackupDir = $null
$BackupMade = $false
$InstallStarted = $false
$HadOldSVP1 = $false
$HadOldSVP2 = $false
$HadVersionFile = $false

try {
    New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null

    $Headers = @{
        "User-Agent" = $UserAgent
        "Accept" = "application/vnd.github+json"
    }

    Write-Host "[1/7] Checking latest GitHub release..."
    $Release = Invoke-RestMethod -UseBasicParsing -Uri $ApiUrl -Headers $Headers
    $LatestTag = [string]$Release.tag_name

    if ([string]::IsNullOrWhiteSpace($LatestTag)) {
        throw "GitHub latest release did not contain tag_name."
    }

    $CurrentTag = Get-CurrentTag
    Write-Host "       Installed marker: $CurrentTag"
    Write-Host "       Latest release  : $LatestTag"

    if ($CurrentTag -eq $LatestTag) {
        Write-Host ""
        Write-Host "Already up to date. No files changed."
        exit 0
    }

    $Asset = $Release.assets | Where-Object { $_.name -eq $WindowsAssetName } | Select-Object -First 1
    if (-not $Asset) {
        throw "Required Windows asset not found in latest release: $WindowsAssetName"
    }

    $Random = [Guid]::NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()
    $TempDir = Join-Path $Root ("_UpdateTemp_" + $Random)
    $DownloadZip = Join-Path $TempDir $WindowsAssetName
    $ExtractDir = Join-Path $TempDir "Extracted"
    $StagePlugins = Join-Path $TempDir "Plugins"

    New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
    New-Item -ItemType Directory -Force -Path $ExtractDir | Out-Null
    New-Item -ItemType Directory -Force -Path $StagePlugins | Out-Null

    Write-Host "[2/7] Downloading $WindowsAssetName..."
    Invoke-WebRequest -UseBasicParsing -Uri $Asset.browser_download_url -Headers $Headers -OutFile $DownloadZip

    if (-not (Test-Path $DownloadZip)) {
        throw "Download failed: $DownloadZip"
    }

    Write-Host "[3/7] Verifying SHA-256..."
    $ActualHash = (Get-FileHash -LiteralPath $DownloadZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $ExpectedDigest = [string]$Asset.digest

    if (-not [string]::IsNullOrWhiteSpace($ExpectedDigest) -and $ExpectedDigest.StartsWith("sha256:", [System.StringComparison]::OrdinalIgnoreCase)) {
        $ExpectedHash = $ExpectedDigest.Substring(7).ToLowerInvariant()

        if ($ActualHash -ne $ExpectedHash) {
            throw "SHA-256 mismatch. Expected=$ExpectedHash Actual=$ActualHash"
        }

        Write-Host "       SHA-256 OK: $ActualHash"
    } else {
        Write-Host "[WARN] GitHub API did not provide a SHA-256 digest. Download hash: $ActualHash"
    }

    Write-Host "[4/7] Extracting and locating plugin DLLs..."
    Expand-Archive -LiteralPath $DownloadZip -DestinationPath $ExtractDir -Force

    $NewSVP1 = Get-ChildItem -LiteralPath $ExtractDir -Recurse -File -Filter "svpflow1_vs.dll" | Select-Object -First 1
    $NewSVP2 = Get-ChildItem -LiteralPath $ExtractDir -Recurse -File -Filter "svpflow2_vs.dll" | Select-Object -First 1

    if (-not $NewSVP1) {
        throw "svpflow1_vs.dll was not found inside the downloaded archive."
    }

    if (-not $NewSVP2) {
        throw "svpflow2_vs.dll was not found inside the downloaded archive."
    }

    Copy-Item -LiteralPath $NewSVP1.FullName -Destination (Join-Path $StagePlugins "svpflow1_vs.dll") -Force
    Copy-Item -LiteralPath $NewSVP2.FullName -Destination (Join-Path $StagePlugins "svpflow2_vs.dll") -Force

    Write-Host "[5/7] Testing downloaded plugins before installation..."
    Test-OpenSVPFlowPlugins -PluginDir $StagePlugins -Label "Downloaded open-svpflow"

    $OldSVP1 = Join-Path $PluginsDir "svpflow1_vs.dll"
    $OldSVP2 = Join-Path $PluginsDir "svpflow2_vs.dll"

    $HadOldSVP1 = Test-Path $OldSVP1
    $HadOldSVP2 = Test-Path $OldSVP2
    $HadVersionFile = Test-Path $VersionFile
    $HasOldFiles = $HadOldSVP1 -or $HadOldSVP2 -or $HadVersionFile

    if ($HasOldFiles) {
        $Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $SafeOldTag = Get-SafeName $CurrentTag
        $BackupDir = Join-Path $BackupRoot ($Stamp + "_" + $SafeOldTag)
        New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null

        Write-Host "[6/7] Backing up current plugins..."
        if (Test-Path $OldSVP1) {
            Copy-Item -LiteralPath $OldSVP1 -Destination (Join-Path $BackupDir "svpflow1_vs.dll") -Force
        }
        if (Test-Path $OldSVP2) {
            Copy-Item -LiteralPath $OldSVP2 -Destination (Join-Path $BackupDir "svpflow2_vs.dll") -Force
        }
        if (Test-Path $VersionFile) {
            Copy-Item -LiteralPath $VersionFile -Destination (Join-Path $BackupDir "open-svpflow-version.txt") -Force
        }

        $BackupInfo = @(
            "BackupTime=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            "PreviousTag=$CurrentTag"
            "TargetTag=$LatestTag"
        )
        [System.IO.File]::WriteAllLines(
            (Join-Path $BackupDir "backup-info.txt"),
            $BackupInfo,
            (New-Object System.Text.UTF8Encoding($false))
        )

        $BackupMade = $true
        Write-Host "       Backup: $BackupDir"
    } else {
        Write-Host "[6/7] No existing plugin files found. Backup not required."
    }

    Write-Host "[7/7] Installing and verifying latest plugins..."
    $InstallStarted = $true
    Copy-Item -LiteralPath (Join-Path $StagePlugins "svpflow1_vs.dll") -Destination $OldSVP1 -Force
    Copy-Item -LiteralPath (Join-Path $StagePlugins "svpflow2_vs.dll") -Destination $OldSVP2 -Force

    [System.IO.File]::WriteAllText(
        $VersionFile,
        $LatestTag + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false))
    )

    Test-OpenSVPFlowPlugins -PluginDir $PluginsDir -Label "Installed open-svpflow"

    Write-Host ""
    Write-Host "============================================================"
    Write-Host " Update OK"
    Write-Host "============================================================"
    Write-Host "Previous : $CurrentTag"
    Write-Host "Installed: $LatestTag"
    if ($BackupMade) {
        Write-Host "Backup   : $BackupDir"
    }
    Write-Host ""
    Write-Host "Restart Film Grain Studio before using OpenSVPFlow."
    Write-Host ""
}
catch {
    $OriginalError = $_
    Write-Host ""
    Write-Host "[ERROR] $($OriginalError.Exception.Message)"

    if ($InstallStarted) {
        Write-Host "[ROLLBACK] Restoring the pre-update plugin state..."

        $OldSVP1 = Join-Path $PluginsDir "svpflow1_vs.dll"
        $OldSVP2 = Join-Path $PluginsDir "svpflow2_vs.dll"

        try {
            if ($BackupMade -and $BackupDir -and (Test-Path $BackupDir)) {
                $BackupSVP1 = Join-Path $BackupDir "svpflow1_vs.dll"
                $BackupSVP2 = Join-Path $BackupDir "svpflow2_vs.dll"
                $BackupVersion = Join-Path $BackupDir "open-svpflow-version.txt"

                if ($HadOldSVP1 -and (Test-Path $BackupSVP1)) {
                    Copy-Item -LiteralPath $BackupSVP1 -Destination $OldSVP1 -Force
                } elseif (-not $HadOldSVP1 -and (Test-Path $OldSVP1)) {
                    Remove-Item -LiteralPath $OldSVP1 -Force
                }

                if ($HadOldSVP2 -and (Test-Path $BackupSVP2)) {
                    Copy-Item -LiteralPath $BackupSVP2 -Destination $OldSVP2 -Force
                } elseif (-not $HadOldSVP2 -and (Test-Path $OldSVP2)) {
                    Remove-Item -LiteralPath $OldSVP2 -Force
                }

                if ($HadVersionFile -and (Test-Path $BackupVersion)) {
                    Copy-Item -LiteralPath $BackupVersion -Destination $VersionFile -Force
                } elseif (-not $HadVersionFile -and (Test-Path $VersionFile)) {
                    Remove-Item -LiteralPath $VersionFile -Force
                }
            } else {
                if (-not $HadOldSVP1 -and (Test-Path $OldSVP1)) {
                    Remove-Item -LiteralPath $OldSVP1 -Force
                }
                if (-not $HadOldSVP2 -and (Test-Path $OldSVP2)) {
                    Remove-Item -LiteralPath $OldSVP2 -Force
                }
                if (-not $HadVersionFile -and (Test-Path $VersionFile)) {
                    Remove-Item -LiteralPath $VersionFile -Force
                }
            }

            Write-Host "[ROLLBACK] Previous state restored."
        }
        catch {
            Write-Host "[ROLLBACK ERROR] $($_.Exception.Message)"
            if ($BackupDir) {
                Write-Host "Manual backup location: $BackupDir"
            }
        }
    }

    exit 1
}
finally {
    if ($TempDir) {
        try {
            Remove-SafeTemp -Path $TempDir
        }
        catch {
            Write-Host "[WARN] Temp cleanup failed: $($_.Exception.Message)"
        }
    }
}
