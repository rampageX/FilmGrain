# Film Grain Studio v4.6.2

Based on the user-validated v4.6.1 stable line.

## Highlights

- Adds **HDR Preserve** for HEVC Main10 and AV1 Main10.
- Preserves BT.2020 + PQ/HLG + 10-bit signaling, and preserves source HDR10 static metadata such as Mastering Display / MaxCLL / MaxFALL when it is present in the source.
- Forces HEVC NVENC to actual P010 10-bit output; Main10 profile is no longer allowed to fall back to an 8-bit bitstream.
- Adds final-output HDR signaling verification for color primaries, transfer characteristics, matrix coefficients, and range.
- HDR Preserve safely bypasses paths that are not HDR-safe in the current implementation: OpenSVPFlow YUV420P8 interpolation, x264 main output, H.264 upload copy, and the existing LUT processing path.
- Studio now reflects those HDR compatibility limits visually by disabling incompatible controls for the selected HDR source and restoring them for SDR sources.
- Adds **LUT Gallery Smart Filter**. It conservatively classifies LUTs as Technical / Combined / Creative / Keep and hides only high-confidence pure Technical LUTs.
- Adds cross-file creative-family recognition so multi-input creative LUT families remain visible instead of being mistaken for pure utility transforms.
- Smart Filter never moves, deletes, or renames LUT files; it also writes a CSV classification report for review.
- Refines the LUT Gallery layout so filter counts remain visible and shortens the Disable LUT button.

## HDR validation

HEVC HDR10/PQ and AV1 HDR were both user-tested successfully. HEVC was additionally verified to output a true 10-bit P010 bitstream. BT.2020 / PQ signaling, correct visual color, and source Mastering Display / MaxCLL / MaxFALL preservation were confirmed on test material where those metadata fields existed.

Source files that do not contain HDR10 static mastering metadata are not given fabricated values.

Dolby Vision RPU, HDR10+ dynamic metadata, HDR-to-SDR tone mapping, and HDR OpenSVPFlow interpolation are not added by this release.

## LUT Smart Filter

The accepted filter logic was tested against large real-world LUT libraries. Combined creative families such as camera-log-specific variants of the same creative look are retained, while high-confidence utility/technical transforms can be hidden from the Gallery. The filter is display-only and fully reversible.

## Compatibility

- Existing AV1 / HEVC / x264 encoding behavior outside HDR routing remains unchanged.
- AAC remains standardized at 256k.
- v4.6.1 FGS application icon and OpenSVPFlow updater / backup flow are preserved.
- GUI and CLI continue to share the same StudioBridge encoding core.
- AV1 SFE continues to require **grav1synth 0.2.2+**.

https://github.com/rampageX/grav1synth/releases/latest

The Film Grain Studio release package does not bundle grav1synth.
