# FGS Benchmark v1.0

Source: H:\Tester\Benchmark\LG OLED  DAYDREAMS 1080p_CUT_00-00-00_DUR_00-00-30.mp4
Started: 2026-09-19 02:31:16; report updated: 2026-09-19 02:35:07
Matrix: 12 tests; recorded: 12; OK: 12; failed: 0

Elapsed = complete StudioBridge wall time in seconds (includes detection, preparation, encoding and muxing; excludes probing, file relocation and screenshots). FPS = Frames / Elapsed; MPix/s = Width * Height * FPS / 1e6; Speed = output video duration / Elapsed. Output Size is bytes; Bitrate is measured whole-file average in kbit/s, including audio/container. Rates are not FFmpeg instantaneous FPS.

FAST / source FPS / MP4 / AUTO bitrate / Digital Grain 55 / FGSIM Medium / x264 faster + tune grain + single-pass / no LUT, subtitles, interpolation or deinterlacing. Grain Plate strength 85%. B06: Classic35, Fujifilm Eterna 250D. B02 and Q01 deliberately repeat the same VBR settings. One pass per test, fixed order, no warm-up; plate-cache state may affect timing. Hardware support is checked by StudioBridge, never inferred from GPU model.
Grain plate: D:\Film_Grain\CT 35mm Grain 4K DCI\CT 35mm Grain 4K DCI.mov

| Test | Encoder | Grain | Status | Elapsed | Resolution | FPS | Frames | MPix/s | Output Size | Bitrate | Speed | Output File | Error |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| B01 | HEVC | Digital Grain | OK | 20.01 | 1920x1080 | 45.027 | 901 | 93.368 | 17040677 | 4539.137 | 1.501 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B01.mp4 |  |
| B02 | HEVC | FGSIM VBR | OK | 13.845 | 1920x1080 | 65.078 | 901 | 134.945 | 16769495 | 4466.902 | 2.169 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B02.mp4 |  |
| B03 | HEVC | Grain Plate | OK | 15.574 | 1920x1080 | 57.853 | 901 | 119.965 | 16880138 | 4496.374 | 1.928 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B03.mp4 |  |
| B04 | AV1 | Digital Grain | OK | 20.491 | 1920x1080 | 43.97 | 901 | 91.176 | 15526647 | 4135.844 | 1.466 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B04.mp4 |  |
| B05 | AV1 | FGSIM VBR | OK | 14.125 | 1920x1080 | 63.789 | 901 | 132.272 | 15298353 | 4075.033 | 2.126 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B05.mp4 |  |
| B06 | AV1 | Native Film Grain | OK | 10.611 | 1920x1080 | 84.909 | 901 | 176.068 | 16307836 | 4343.93 | 2.83 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B06.mp4 |  |
| B07 | X264 | Digital Grain | OK | 22.279 | 1920x1080 | 40.441 | 901 | 83.859 | 23938788 | 6376.592 | 1.348 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B07.mp4 |  |
| B08 | X264 | FGSIM VBR | OK | 18.58 | 1920x1080 | 48.492 | 901 | 100.553 | 23065147 | 6143.879 | 1.616 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B08.mp4 |  |
| B09 | X264 | Grain Plate | OK | 18.477 | 1920x1080 | 48.764 | 901 | 101.117 | 23335912 | 6216.003 | 1.625 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\B09.mp4 |  |
| Q01 | HEVC | FGSIM VBR | OK | 15.357 | 1920x1080 | 58.669 | 901 | 121.655 | 16769495 | 4466.902 | 1.956 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\Q01.mp4 |  |
| Q02 | HEVC | FGSIM STANDARD | OK | 13.904 | 1920x1080 | 64.8 | 901 | 134.37 | 238723100 | 63588.84 | 2.16 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\Q02.mp4 |  |
| Q03 | HEVC | FGSIM HIGH | OK | 13.944 | 1920x1080 | 64.616 | 901 | 133.987 | 325474197 | 86696.79 | 2.154 | H:\Tester\Benchmark\FGS_Benchmark_24877_3093\Outputs\Q03.mp4 |  |

Successful tests total wall time: 197.197 seconds.

## Screenshots
Frame numbers are zero-based decoded frame indices. PNG is lossless after RGB conversion. AV1 uses libdav1d with film grain enabled. HDR screenshots are raw RGB conversions, not a common SDR tone-map; compare HDR/SDR cases with care. Out-of-range or failed frames are reported, never replaced with another frame.

### Frame 543
[SOURCE](Screenshots/Frame_543/SOURCE.png)
[B01](Screenshots/Frame_543/B01.png)
[B02](Screenshots/Frame_543/B02.png)
[B03](Screenshots/Frame_543/B03.png)
[B04](Screenshots/Frame_543/B04.png)
[B05](Screenshots/Frame_543/B05.png)
[B06](Screenshots/Frame_543/B06.png)
[B07](Screenshots/Frame_543/B07.png)
[B08](Screenshots/Frame_543/B08.png)
[B09](Screenshots/Frame_543/B09.png)
[Q01](Screenshots/Frame_543/Q01.png)
[Q02](Screenshots/Frame_543/Q02.png)
[Q03](Screenshots/Frame_543/Q03.png)