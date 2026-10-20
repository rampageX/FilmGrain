# Film Grain Studio v5.0.15

- 逐行、关闭颗粒和插帧的 HEVC HDR→SDR 任务，合并 HDR 转换、调色、LUT 与最终编码，省去独立 HDR 中间视频及其读写和解码。
- 保持原有转换算法、LUT 强度、画幅、字幕和音频时间线。Hable 继续优先采用 Vulkan/libplacebo，实际能力检测失败时回退 CPU；手选其它算法保持原值。
- 合并路径按已检测显卡名称选择 Vulkan 设备，避免多显卡机器上 Vulkan 与 CUDA 序号不同。
- 颗粒、插帧、隔行、HDR 保留及已有主输出复用等组合沿用原处理路径；预览编码复用相同任务实现。
