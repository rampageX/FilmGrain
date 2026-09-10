# Film Grain Studio v4.6.0

Based on the user-validated v4.5.2.2 stable line.

## Highlights

- Adds **AV1 NVENC multi-engine parallel encoding / Split Frame Encoding (SFE)**.
- Adds **多引擎并行 ×N** directly below Speed / Quality in the main GUI.
- Detects the available NVENC engine count dynamically; no GPU-model hardcoding.
- Enables SFE only for **AV1 Standard** and **AV1 UHQ**.
- Requires **grav1synth 0.2.2 or newer** before SFE can be enabled.
- Keeps AV1 FAST, HEVC, x264, and AV1 no-reencode SFE disabled by policy based on real pipeline testing.
- Fixes interlaced input with Interpolation enabled: progressive files still use OpenSVPFlow 60 fps, while interlaced files automatically bypass OpenSVPFlow and use the existing field-rate deinterlace path.
- Preserves the v4.5.2.2 Batch Summary timing compatibility fix and existing AAC 256k / Grain / LUT / subtitle / Cinematic / upload / MPEG-TS synchronization behavior.

## grav1synth

Use **grav1synth 0.2.2+** for the AV1 SFE path:

https://github.com/rampageX/grav1synth/releases/latest

The Film Grain Studio release package does not bundle grav1synth.
