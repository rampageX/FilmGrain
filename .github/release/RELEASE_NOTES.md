# Film Grain Studio v5.1.07

- HDR → SDR 新增 Vulkan Spline 和 BT.2390，主流程、独立转换及同帧预览共用色调映射参数。
- Spline 曲线对比度、BT.2390 高光拐点适配当前 FFmpeg/libplacebo 参数；不支持独立 Spline 斜率时禁用该滑条。支持亮度、饱和度和 Vulkan 对比度恢复调节，独立预览滑条支持单项重置。
- SDR → TrueHDR 队列仅在勾选 AI 插帧 2× 时对符合条件的逐行素材插帧；隔行素材仍自动 Bob 2×，未勾选时逐行保持原帧率。
- CB8F27 候选版已由用户实机验收通过。
