# Film Grain Studio v5.0.81

- FGB-02：HEVC NVENC 正式支持 OpenSVPFlow（SDR、PQ、HLG；HDR GPU16/CPU10）或 NVEncC AI FrameGen（SDR；PQ/HLG 使用已验证的 10-bit 实验版）与胶片边框组合。插帧后直接经过现有 S4 Vulkan/libplacebo GPU 合成，送 CUDA/NVENC，不新增大型中间文件或 CPU 回退。
- 修复组合管线的视频、原始音频、Grain Plate、边框素材流编号；在 GPU 合成前规范插帧帧率，保留现有音视频时间轴、静态／动态运动、构图和预装 15 款边框。
- SDR GPU 输出明确使用 BT.709 Limited；PQ 与 HLG 保留各自色彩及 GPU 合成路线，HLG 沿用两阶段 libplacebo。用户已实机验收 SDR／PQ／HLG × SVP／AI 六组组合。
- 不支持的色彩或功能组合显示中文提示并附带原始错误；组合任务运行中失败会在结束时显示一次警告，日志仍保留详细信息。
- PC Full Range 转换仍不开放，记录到 `docs/todo/FGS_FGB02_Full_Range_TODO.md`；不放开 AV1＋边框、FRUC Vulkan＋边框或 HDR→SDR＋边框。
- 保留 v5.0.80 的全部 HDR 合成、GPU 预览和 15 款预装边框素材；正式构建恢复 `Stable` 标记，由 Windows runner 编译及校验。
