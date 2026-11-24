# Film Grain Studio v5.0.98

- SDR → TrueHDR 自动对隔行 SDR 启用 CUDA YADIF Bob，每场输出一帧。1080i 29.97 帧／59.94 场可输出 1080p59.94 HDR10。
- 反交错、TrueHDR 和 HEVC／AV1 编码由同一个 NVEncC 进程完成，无需中间视频文件。对比预览两侧也使用 Bob。
