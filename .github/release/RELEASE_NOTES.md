# Film Grain Studio v5.0.93

- R07：HDR→SDR 的 Vulkan/libplacebo Hable 探针用实际源和滤镜链处理三帧，通过 wrapped_avframe 检查滤镜。成品编码器按各路线原有规则检查。
- 仅需要独立 HDR→SDR 中间文件的路线，单独用 1920×1080 P010 检查 HEVC NVENC Main10 无损编码。
- Windows 2022 候选 CI #45208621662 通过。RTX 4080 实机通过 PQ→AV1 NVENC 单遍、HLG→x265 单遍及 1080i29.97 PQ 隔行→HEVC 无损中间文件→AV1 NVENC，音画同步已确认。
