# FGS 待办与兼容性追踪

仅记录尚未验证或暂不支持的技术方案；不作为功能已实现声明。

- [P1：FFmpeg 9.1 HDR GPU 胶片边框兼容性](FGS_FFmpeg91_HDR_Border_GPU_Interop_TODO.md)（主干升级阻断，待定位修复）
- [FGB-02：PC Full Range 插帧＋GPU 胶片边框](FGS_FGB02_Full_Range_TODO.md)（待研究，不开放）

- **HDR→SDR 高级 GPU 预览**（低优先级，暂缓，待验证）
  - 复用现有滑动对比与同一解码帧。左侧显示 HDR 原片，右侧调用当前正式编码路线的 HDR→SDR 转换及参数，先处理再缩放显示。
  - 研究在 Windows HDR 开启时共用一个输出表面，保留左侧 HDR 高光，并按当前屏幕的系统 SDR 白点显示右侧结果。Windows HDR 关闭时，明确标注左侧为 SDR 显示参考。
  - 验证时各取一段 PQ 和 HLG，对照同一时间点的预览右侧与正式编码输出，核对灰阶、高光和饱和色，再切换 Windows HDR 排查显示阶段的亮度差异。
  - 参考 [HDR Blaster 的预览实现](https://github.com/5ymph0en1x/NVIDIA-HDR-Converter-GUI/blob/main/hdr_blaster/preview.py#L136-L265)。
