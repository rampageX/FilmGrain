# Film Grain Studio v5.0.85

- **SDR 多效果 GPU 快速通道修复**：HEVC/H.264 NVENC 的 BT.709 TV Limited 扫描 Grain Plate 与胶片边框组合，在新增 LUT、色彩纠正或同时开启两项时继续使用 S4 Vulkan/libplacebo→CUDA/NVENC，不再因前置 CPU 滤镜而无谓退回 CPU 边框。
- **保持效果算法与顺序**：原有色彩纠正→3D LUT（含强度与混合透明度）→扫描颗粒→静态／动态边框的顺序不变；H.264 继续在 CUDA 内转 NV12，HEVC 维持 P010。未更改 PQ/HLG、插帧、字幕、缩放或其它尚未验证的组合保护条件。
- **验收**：用户已确认 L8Q3W7 测试版正常。Windows CI 完整编译、SDR LUT/色彩纠正＋Grain Plate＋S4 边框图回归、PQ/HLG GPU 图、S4 现有路线及 Native UI 构图／缩略图回归通过。
- **性能后续研究**：HDR PQ/HLG＋LUT/色彩纠正＋扫描颗粒＋边框、SDR Grain Plate＋Resize/剪裁＋边框、Grain Plate＋字幕烧录＋边框继续保留既有保护条件；记录于 `docs/film-border/FGS_GPU_COMBINATION_FALLBACK_AUDIT.md`，本版不擅自放开。
