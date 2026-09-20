$ErrorActionPreference = 'Stop'

$script:FgLanguageRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'Lang'
$script:FgLanguageConfigPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'FilmGrain_Config.ini'
$script:FgLegacyLanguageSettingsPath = Join-Path $script:FgLanguageRoot 'FilmGrain_Language.ini'
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
    try {
        if (Test-Path -LiteralPath $script:FgLanguageConfigPath -PathType Leaf) {
            $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
            $configText = [System.IO.File]::ReadAllText($script:FgLanguageConfigPath, $utf8Strict)
            if ($configText -match '(?im)^\s*LANGUAGE\s*=') {
                $settings = Get-FilmGrainConfig
                $code = [string]$settings.LANGUAGE
                if ($code) { return $code.Trim() }
            }
        }
    } catch {}

    if (Test-Path -LiteralPath $script:FgLegacyLanguageSettingsPath -PathType Leaf) {
        try {
            $legacy = Import-FgLanguageFile -Path $script:FgLegacyLanguageSettingsPath
            $legacyCode = [string]$legacy['LANGUAGE']
            if ($legacyCode) {
                try {
                    Save-FilmGrainConfig -Values @{ LANGUAGE = $legacyCode.Trim() }
                } catch {}
                return $legacyCode.Trim()
            }
        } catch {}
    }

    return $script:FgDefaultLanguage
}

function Set-FgLanguagePreference {
    param([Parameter(Mandatory=$true)][string]$Language)

    $candidate = Join-Path $script:FgLanguageRoot ($Language + '.ini')
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Language file not found: $candidate"
    }

    Save-FilmGrainConfig -Values @{ LANGUAGE = $Language }
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
