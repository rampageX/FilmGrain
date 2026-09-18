# FGS Benchmark v1.0

Source: H:\Tester\Benchmark\Taylor Swift - Look What You Made Me Do SDR 1440p.mp4
Started: 2026-09-19 00:58:31; report updated: 2026-09-19 01:02:21
Matrix: 12 tests; recorded: 12; OK: 12; failed: 0

Elapsed = complete StudioBridge wall time in seconds (includes detection, preparation, encoding and muxing; excludes probing, file relocation and screenshots). FPS = Frames / Elapsed; MPix/s = Width * Height * FPS / 1e6; Speed = output video duration / Elapsed. Output Size is bytes; Bitrate is measured whole-file average in kbit/s, including audio/container. Rates are not FFmpeg instantaneous FPS.

FAST / source FPS / MP4 / AUTO bitrate / Digital Grain 55 / FGSIM Medium / x264 faster + tune grain + single-pass / no LUT, subtitles, interpolation or deinterlacing. Grain Plate strength 85%. B06: Classic35, Fujifilm Eterna 250D. B02 and Q01 deliberately repeat the same VBR settings. One pass per test, fixed order, no warm-up; plate-cache state may affect timing. Hardware support is checked by StudioBridge, never inferred from GPU model.
Grain plate: D:\Film_Grain\CT 35mm Grain 4K DCI\CT 35mm Grain 4K DCI.mov

| Test | Encoder | Grain | Status | Elapsed | Resolution | FPS | Frames | MPix/s | Output Size | Bitrate | Speed | Output File | Error |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| B01 | HEVC | Digital Grain | OK | 18.351 | 2560x1278 | 26.647 | 489 | 87.18 | 14290359 | 5605.333 | 1.111 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B01.mp4 |  |
| B02 | HEVC | FGSIM VBR | OK | 12.814 | 2560x1278 | 38.16 | 489 | 124.849 | 14487329 | 5682.594 | 1.592 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B02.mp4 |  |
| B03 | HEVC | Grain Plate | OK | 25.384 | 2560x1278 | 19.264 | 489 | 63.027 | 14945236 | 5862.206 | 0.803 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B03.mp4 |  |
| B04 | AV1 | Digital Grain | OK | 18.523 | 2560x1278 | 26.4 | 489 | 86.373 | 11938281 | 4682.74 | 1.101 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B04.mp4 |  |
| B05 | AV1 | FGSIM VBR | OK | 13.251 | 2560x1278 | 36.903 | 489 | 120.736 | 11795457 | 4626.718 | 1.539 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B05.mp4 |  |
| B06 | AV1 | Native Film Grain | OK | 10.781 | 2560x1278 | 45.356 | 489 | 148.39 | 12170033 | 4773.644 | 1.892 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B06.mp4 |  |
| B07 | X264 | Digital Grain | OK | 19.643 | 2560x1278 | 24.895 | 489 | 81.447 | 16232721 | 6367.216 | 1.038 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B07.mp4 |  |
| B08 | X264 | FGSIM VBR | OK | 15.31 | 2560x1278 | 31.94 | 489 | 104.497 | 16225173 | 6364.256 | 1.332 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B08.mp4 |  |
| B09 | X264 | Grain Plate | OK | 25.698 | 2560x1278 | 19.029 | 489 | 62.256 | 16316787 | 6400.191 | 0.794 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\B09.mp4 |  |
| Q01 | HEVC | FGSIM VBR | OK | 12.968 | 2560x1278 | 37.709 | 489 | 123.373 | 14487329 | 5682.594 | 1.573 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\Q01.mp4 |  |
| Q02 | HEVC | FGSIM STANDARD | OK | 13.06 | 2560x1278 | 37.444 | 489 | 122.504 | 105788962 | 41495.275 | 1.562 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\Q02.mp4 |  |
| Q03 | HEVC | FGSIM HIGH | OK | 13.435 | 2560x1278 | 36.398 | 489 | 119.084 | 158939256 | 62343.254 | 1.518 | H:\Tester\Benchmark\FGS_Benchmark_6697_1083\Outputs\Q03.mp4 |  |

Successful tests total wall time: 199.218 seconds.

## Screenshots
Frame numbers are zero-based decoded frame indices. PNG is lossless after RGB conversion. AV1 uses libdav1d with film grain enabled. HDR screenshots are raw RGB conversions, not a common SDR tone-map; compare HDR/SDR cases with care. Out-of-range or failed frames are reported, never replaced with another frame.

### Frame 313
[SOURCE](Screenshots/Frame_313/SOURCE.png)
[B01](Screenshots/Frame_313/B01.png)
[B02](Screenshots/Frame_313/B02.png)
[B03](Screenshots/Frame_313/B03.png)
[B04](Screenshots/Frame_313/B04.png)
[B05](Screenshots/Frame_313/B05.png)
[B06](Screenshots/Frame_313/B06.png)
[B07](Screenshots/Frame_313/B07.png)
[B08](Screenshots/Frame_313/B08.png)
[B09](Screenshots/Frame_313/B09.png)
[Q01](Screenshots/Frame_313/Q01.png)
[Q02](Screenshots/Frame_313/Q02.png)
[Q03](Screenshots/Frame_313/Q03.png)