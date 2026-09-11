OpenSVPFlow runtime for Film Grain Studio

Run 00_Setup.bat once before enabling interpolation.

Run 01_Update_OpenSVPFlow.bat only when you intentionally want to update
the open-svpflow plugin DLLs to the latest upstream release.

Updater behavior:
- Queries the latest Z1xus/open-svpflow GitHub Release.
- Downloads the Windows x64 x86_64-pc-windows-msvc.zip asset.
- Verifies the GitHub SHA-256 digest when the release API provides one.
- Smoke-tests staged CPU and GPU/OpenCL plugins before touching Plugins.
- Backs up the current plugin DLLs under _PluginBackup.
- Verifies the installed plugins again and rolls back automatically on failure.
- Records Plugins\open-svpflow-version.txt for later update checks.

Pinned first-install runtime:
- VapourSynth R79
- BestSource 21.0
- open-svpflow nightly-20260804-5ef4260

The setup creates a local .venv in this folder and writes VapourSynth's
normal user-level %APPDATA%\vapoursynth\vapoursynth.toml configuration.

Film Grain Studio defaults:
- GPU/OpenCL
- 60 fps
- SmoothFps Algo 13
- Super pel=1 / gpu=1 / full=true
- EncodeGUI-inspired Analyse profile
- Smooth / Uniform scene mode

Main GUI interpolation modes:
- 平滑 = Uniform / scene.mode 0
- 自动平衡 = Adaptive / scene.mode 3

Advanced settings currently expose:
- SmoothFps Algo
- Analyse Profile
- Artifact Mask Area

Current limitations:
- OpenSVPFlow itself processes progressive input only; interlaced inputs are automatically routed to the existing field-rate deinterlace path.
- OpenSVPFlow owns the output frame rate at 60 fps for progressive interpolation jobs.
- H.264 upload copy remains available while interpolation is enabled and reuses the completed interpolated result where applicable.
- The verified interpolation path normalizes internally to YUV420P8 before returning to the normal Film Grain Studio encode chain. Do not use this path when HDR / end-to-end 10-bit preservation is required.
