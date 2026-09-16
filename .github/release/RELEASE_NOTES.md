# Film Grain Studio v4.7.0

v4.7.0 adds a universal **Digital Grain / Fast Noise** engine and a new **HDR to SDR Tone Mapping fallback** while keeping the existing AV1 Film Grain metadata and scanned-grain workflows intact.

## Added

- Digital Grain is available on all three main lines: **AV1, HEVC, and H.264 x264**.
- Continuous Digital Grain strength slider: **0.10–1.00**, default **0.55**.
- Practical strength references from testing: `0.30` subtle, `0.40` light, `0.55` medium, `0.68` approximately HEVC CT35 85%, `0.75+` pronounced/heavy.
- SDR Digital Grain uses the faster ~1.333x Fast Noise pipeline; 1080p/1440p testing showed roughly a 3x filter-speed advantage over the early GEQ reference implementation.
- AV1/HEVC HDR Preserve now supports a **10-bit luma-only Digital Grain path**; U/V chroma planes are preserved.
- Advanced HDR policy: **Auto / Keep HDR / Force SDR**.
- HDR to SDR Tone Mapping algorithms: **Hable** (default), Mobius, Reinhard, Gamma, Linear, and Clip.
- Auto mode Tone Maps HDR to BT.709 SDR when an SDR-only path is required, including x264, BT.709 LUT, OpenSVPFlow, or the H.264 upload copy.

## Fixed

- Fixed the early HDR Digital Grain path where `alphamerge/overlay` could negotiate the HDR image down to 8-bit, causing a dark picture and ineffective grain.
- Fixed HDR-to-SDR working-file activation issues caused by CMD block expansion and a missing/misplaced batch label.
- HDR-to-SDR temporary files use randomized suffixes to avoid cache collisions and are cleaned up after completion/failure.

## Validation

- AV1 HDR Digital Grain was user-tested successfully.
- HDR-to-SDR + LUT was user-tested successfully.
- A full workflow test completed successfully with AV1 UHQ + Digital Grain + HDR-to-SDR Hable + OpenSVPFlow 60 fps + LUT.
- Formal package is built on a **Windows Server 2022 runner**. BAT/VBS/CMD are normalized to CRLF with no BOM; PS1 files are normalized to UTF-8 BOM + CRLF and checked for forbidden curly quotes and missing BAT labels.

AAC remains standardized at **256 kbps**, and existing AV1 SFE, HDR Preserve, LUT Gallery, OpenSVPFlow, subtitles, Cinematic Style, Batch Summary, bitrate logic, and other validated behavior are retained.
