# Film Grain Studio v4.7.5

v4.7.5 formalizes the user-tested **GPU Film Grain (FGSIM)** workflow while preserving the validated shader, Vulkan/libplacebo pipeline, and existing AV1/HEVC/x264 behavior.

## Added and unified

- The UI now exposes one **GPU Film Grain (FGSIM)** mode instead of separate Light, Medium, and Heavy entries.
- **Film Grain Strength** is the shared control for Digital Grain, Grain Plate, and FGSIM.
- Internal FGSIM mapping remains unchanged: Light `0.10`, Medium `0.20`, and Heavy `0.30`.
- In **HEVC + FGSIM**, the Video Bitrate selector adds two optional grain-oriented choices:
  - Standard CQ27 / QP18-26
  - High Quality CQ23 / QP18-24
- Original automatic or manual **VBR remains the default**. CQ is intended as an optional remedy for clips that show banding.
- The UI guidance is: `HEVC+FGSIM 模式下, 若画面出现色带，请在视频码率中尝试 Standard CQ27 或 High Quality CQ23 方案。`

## Fixed

- Fixed `Only one filter device can be used` when FGSIM and BWDIF Vulkan are enabled together.
- BWDIF Vulkan and libplacebo now reuse the same `vk` filter device; no second `deintvk` device or `-filter_hw_device` is appended.
- The fix covers the direct FGSIM HEVC, x264, and AV1 paths without rebuilding the validated filter chain.

## Unchanged

- AV1 routes and x264 Grain routes.
- Normal HEVC VBR behavior.
- Digital Grain and Grain Plate algorithms.
- Output containers, AAC 256k, interpolation, LUT, HDR, subtitles, Cinematic Style, AV1 SFE, and Batch Summary behavior.

## Validation

- The final v4.7.5 build was tested successfully by the user.
- The FGSIM shader, preparation script, and fallback-noise resource are preserved from the validated build.
- The formal package is built on a **Windows Server 2022 runner** and rechecked after ZIP extraction.
- BAT/VBS/CMD are CRLF with no BOM; PS1 files are UTF-8 BOM + CRLF. Curly quotes, full-width square brackets, caret trailing whitespace, and missing BAT labels are rejected.

