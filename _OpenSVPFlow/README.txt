OpenSVPFlow runtime for Film Grain Studio

Run 00_Setup.bat once before enabling interpolation.

Pinned runtime:
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
- Progressive input only.
- OpenSVPFlow owns the output frame rate at 60 fps.
- Automatic deinterlacing and normal cinematic/source FPS selection are disabled while interpolation is enabled.
- H.264 upload copy is currently disabled while interpolation is enabled.
- The verified interpolation path normalizes internally to YUV420P8 before returning to the normal Film Grain Studio encode chain. Do not use this path when HDR / end-to-end 10-bit preservation is required.
