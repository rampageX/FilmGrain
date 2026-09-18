$ErrorActionPreference = 'Stop'
# I18N TEST R4N8C: WinPS 5.1 language enumeration compatibility fix.

$script:FgLanguageRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'Lang'
$script:FgLanguageSettingsPath = Join-Path $script:FgLanguageRoot 'FilmGrain_Language.ini'
$script:FgDefaultLanguage = 'zh-CN'
$script:FgLanguageCode = $script:FgDefaultLanguage
$script:FgLanguageFallback = @{}
$script:FgLanguageText = @{}

function Import-FgLanguageFile {
    param([Parameter(Mandatory=$true)][string]$Path)

    $result = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }

    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    $lines = [System.IO.File]::ReadAllLines($Path, $utf8Strict)
    foreach ($line in $lines) {
        $text = [string]$line
        $trimmed = $text.Trim()
        if (-not $trimmed -or $trimmed.StartsWith(';') -or $trimmed.StartsWith('#')) { continue }

        $eq = $text.IndexOf('=')
        if ($eq -le 0) { continue }

        $key = $text.Substring(0, $eq).Trim()
        $value = $text.Substring($eq + 1)
        if (-not $key) { continue }

        $value = $value.Replace('\n', [Environment]::NewLine)
        $result[$key] = $value
    }
    return $result
}

function Get-FgLanguagePreference {
    if (-not (Test-Path -LiteralPath $script:FgLanguageSettingsPath -PathType Leaf)) {
        return $script:FgDefaultLanguage
    }

    try {
        $settings = Import-FgLanguageFile -Path $script:FgLanguageSettingsPath
        $code = [string]$settings['LANGUAGE']
        if ($code) { return $code.Trim() }
    } catch {}
    return $script:FgDefaultLanguage
}

function Set-FgLanguagePreference {
    param([Parameter(Mandatory=$true)][string]$Language)

    $candidate = Join-Path $script:FgLanguageRoot ($Language + '.ini')
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Language file not found: $candidate"
    }

    if (-not (Test-Path -LiteralPath $script:FgLanguageRoot -PathType Container)) {
        [void](New-Item -ItemType Directory -Force -Path $script:FgLanguageRoot)
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines(
        $script:FgLanguageSettingsPath,
        @('LANGUAGE=' + $Language),
        $utf8NoBom
    )
}

function Initialize-FgLanguage {
    if (-not (Test-Path -LiteralPath $script:FgLanguageRoot -PathType Container)) {
        throw "Film Grain language directory not found: $script:FgLanguageRoot"
    }

    $fallbackPath = Join-Path $script:FgLanguageRoot ($script:FgDefaultLanguage + '.ini')
    $script:FgLanguageFallback = Import-FgLanguageFile -Path $fallbackPath
    if ($script:FgLanguageFallback.Count -eq 0) {
        throw "Default Film Grain language file is missing or empty: $fallbackPath"
    }

    $requested = Get-FgLanguagePreference
    $selectedPath = Join-Path $script:FgLanguageRoot ($requested + '.ini')
    if (-not (Test-Path -LiteralPath $selectedPath -PathType Leaf)) {
        $requested = $script:FgDefaultLanguage
        $selectedPath = $fallbackPath
    }

    $script:FgLanguageCode = $requested
    $script:FgLanguageText = Import-FgLanguageFile -Path $selectedPath
}

function Get-FgText {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [object[]]$Args
    )

    $value = $null
    if ($script:FgLanguageText.ContainsKey($Key)) {
        $value = [string]$script:FgLanguageText[$Key]
    } elseif ($script:FgLanguageFallback.ContainsKey($Key)) {
        $value = [string]$script:FgLanguageFallback[$Key]
    } else {
        return $Key
    }

    if ($Args -and $Args.Count -gt 0) {
        return [string]::Format([Globalization.CultureInfo]::CurrentCulture, $value, $Args)
    }
    return $value
}

function L {
    param(
        [Parameter(Mandatory=$true)][string]$Key,
        [object[]]$Args
    )
    return Get-FgText -Key $Key -Args $Args
}

function Get-FgAvailableLanguages {
    $items = @()
    $files = @(Get-ChildItem -LiteralPath $script:FgLanguageRoot -Filter '*.ini' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'FilmGrain_Language.ini' } |
        Sort-Object Name)

    foreach ($file in $files) {
        try {
            $dict = Import-FgLanguageFile -Path $file.FullName
            $displayName = [string]$dict['meta.display_name']
            if (-not $displayName) { $displayName = $file.BaseName }
            $items += [pscustomobject]@{
                Code = $file.BaseName
                DisplayName = $displayName
            }
        } catch {}
    }
    return $items
}

Initialize-FgLanguage
