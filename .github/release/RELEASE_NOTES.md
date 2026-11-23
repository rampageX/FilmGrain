# Film Grain Studio v5.0.87

- **R01 慢路径优化**：BWDIF Vulkan＋真实扫描 Grain Plate 在适用 HEVC/H.264 NVENC 及共用 CPU 编码路线中直接串接同一 GPU 过滤图，不再写入并重新读取整片 FFV1 中间视频。
- **兼容性保持**：TFF/BFF、2× Field-rate 与分数时间戳量化、源音画起点、PQ/HLG 10-bit 信令保留；FGSIM、插帧 Plate、HDR→SDR 和 HDR Preserve＋边框隔行限制不变。
- **验收**：Windows CI 编译与场序/PTS/色彩/边框回归通过。T600 完成 SDR TFF/BFF、HDR PQ TFF 的 Vulkan/CUDA 六组编码，用户确认画质以及补测音频同步正常。
