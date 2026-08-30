Film Grain Toolkit - FINAL
==========================

Files:
1. FilmGrain_HEVC_NVENC_v20_FINAL_AutoCinemaFPS_FIX2.bat
   Scanned film-grain plate -> Vulkan Overlay -> HEVC Main10 NVENC.

2. AV1_NVENC_grav1synth_FilmGrain_FINAL.bat
   AV1 Main10 NVENC -> grav1synth AV1 Film Grain -> remux -> verify.

3. FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat
   Convert 4K ProRes grain MOV to verified 1920x1080 P010 HEVC lossless cache.

4. FilmGrain_MOV_to_HEVC_Lossless_Cache.bat
   Convert original grain MOV to verified P010 HEVC lossless 4K cache.

5. build-grav1synth-windows-v2-FIX.yml
   GitHub Actions workflow for building grav1synth.exe on Windows x64.

Before use, edit the FFmpeg / FFprobe / grav1synth / Film_Grain paths at the top of BAT files.
