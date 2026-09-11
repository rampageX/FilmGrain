# Film Grain Studio v4.6.1

Based on the user-validated v4.6.0 stable line.

## Highlights

- Adds a dedicated **FGS application icon** for both the WinForms title bar and the Windows taskbar.
- Adds `_OpenSVPFlow\01_Update_OpenSVPFlow.bat` and `Update_OpenSVPFlow.ps1` for intentional updates to the latest upstream `Z1xus/open-svpflow` release.
- The updater downloads and stages the latest Windows x64 build, verifies the GitHub SHA-256 digest when provided, smoke-tests CPU and GPU/OpenCL before installation, backs up the current plugin DLLs, verifies again after installation, and automatically rolls back on failure.
- Keeps `00_Setup.bat` as the pinned, verified first-install path.
- Does not change the encoding core: AV1 / HEVC / x264 parameters, SFE, Grain, LUT, subtitles, Cinematic framing, interpolation settings, upload copy, Batch Summary, and MPEG-TS synchronization remain unchanged.

## OpenSVPFlow maintenance

Use `00_Setup.bat` for first installation.

Run `01_Update_OpenSVPFlow.bat` only when you intentionally want to update the plugin DLLs to the latest upstream release. Existing DLLs are backed up under `_OpenSVPFlow\_PluginBackup`, and a failed post-install validation automatically restores the previous state.

## grav1synth

AV1 SFE continues to require **grav1synth 0.2.2+**:

https://github.com/rampageX/grav1synth/releases/latest

The Film Grain Studio release package does not bundle grav1synth.
