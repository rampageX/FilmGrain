# Film Grain Studio v4.8.0

v4.8.0 adds a lightweight, file-based multilingual GUI layer while keeping the validated v4.7.5 encoding core unchanged.

## Multilingual GUI

- Added **Simplified Chinese** and **English** interface languages.
- UI text is stored separately in:
  - `Lang/zh-CN.ini`
  - `Lang/en-US.ini`
- `Utils/FilmGrain_Language.ps1` handles UTF-8 language loading, fallback, available-language enumeration, and preference persistence.
- The selected language is stored in `Lang/FilmGrain_Language.ini` and takes effect after reopening Film Grain Studio.
- Simplified Chinese remains the default and fallback language.

## UI localization coverage

- Main window and Advanced Settings.
- Encoder, FPS, framing, container, grain and other dynamically updated selectors.
- Media information and AV1 Film Grain status display.
- LUT, subtitle, path configuration, tooltips, common dialogs and runtime status text.
- Dynamic text is translated at display time without changing validated internal state/protocol values.

## Layout

- The existing Simplified Chinese layout remains unchanged at the validated ~1320 px client width.
- English uses a wider 1500 px default window and 1420 px minimum width to prevent long labels from squeezing the HEVC/LUT panel or wrapping controls.
- Window width can be defined per language file for future localizations.

## Compatibility fixes

- Fixed Windows PowerShell 5.1 language enumeration failure caused by returning `List[object]` through `return @($items)`; the loader now uses native PowerShell arrays.
- Release validation includes a real WinPS 5.1 language-loader smoke test and verifies both `zh-CN` and `en-US` are available.
- All language keys used by the GUI must exist in both language files.

## Encoding core unchanged

v4.8.0 does **not** change:

- StudioBridge encoding logic.
- AV1 / HEVC / x264 parameters or bitrate policy.
- GPU Film Grain (FGSIM), Digital Grain, or Grain Plate algorithms.
- HDR Preserve / HDR-to-SDR fallback.
- LUT, OpenSVPFlow, deinterlace, subtitles, containers, or AAC 256k behavior.
- The v4.7.5 HEVC + FGSIM VBR/CQ behavior and shared Vulkan filter-device fix.

## Validation

- Based on the user-verified `v4.7.5_I18N_TEST_P7L4N` build.
- Formal package is built on a **Windows Server 2022 runner**.
- PS1 files are packaged as UTF-8 BOM + CRLF.
- Language INI files are UTF-8 without BOM + CRLF.
- Windows batch safety, PowerShell parsing, curly-quote checks, language loading, and required-resource checks are enforced before release.
