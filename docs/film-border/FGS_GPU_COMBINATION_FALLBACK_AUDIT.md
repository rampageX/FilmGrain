# FGS S4 GPU 边框组合性能审查 · 2026-11-23

## 适用基线

- 当前基线为 v5.0.86。验收源码为 R6K4P9，提交 `4e48529f4830b2d047d4f49e56060374a330bfa7`。
- 核心：`dotnet/FilmGrain_NativeHevcCore.cs` 的 `s4GpuBorder` 选路及 `FilmBorderEncodingCore.AppendGpuFilter`。
- 实机已验收：SDR Grain Plate + 边框，RTX 4080 1080p 约 15→142 fps；L8Q3W7 新增 LUT/色彩纠正＋扫描颗粒＋边框组合，用户反馈测试 OK。

## 确认的无谓 CPU 边框回退

| 组合 | 原因 | 修复策略 |
| --- | --- | --- |
| SDR BT.709 TV Grain Plate + LUT + 边框 | `s4GpuBorder` 的 `lutCompatName` 非空被拒；随后 `blend_vulkan` → `hwdownload` → CPU 边框 | 原 LUT 仍在 GPU 上传之前按原 `lut3d` 与 `blend` 处理，之后保留 S4 Vulkan/libplacebo→CUDA |
| SDR BT.709 TV Grain Plate + 色彩纠正 + 边框 | `colorFilter` 非空被拒；色彩纠正本身已有上传前的 CPU 滤镜链 | 允许已存在的上传前色彩滤镜后继续 S4，不修改色彩纠正算法 |
| SDR BT.709 TV Grain Plate + 色彩纠正 + LUT + 边框 | 同时触发上述两个不必要的选路限制 | 共用同一高性能通道，处理顺序依旧为色彩纠正→LUT→扫描颗粒→边框→CUDA/NVENC |

## v5.0.86 已验收的 GPU 组合

| 组合 | 处理 |
| --- | --- |
| HDR PQ／HLG + LUT／色彩纠正 + 扫描颗粒 + 边框 | 复用原高位深 CPU 调色前缀，以上传前的 BT.2020 Limited 标签把 P010 交给 Vulkan 颗粒与边框。HLG 沿用两阶段 GPU 色彩转换。 |
| SDR Grain Plate + Resize／剪裁 + 边框 | Resize 在颗粒前完成，按输出画布合成边框。单独剪裁在源尺寸混合颗粒，再由边框滤镜应用裁剪坐标。奇数起点的单独剪裁沿用原路线。 |
| Grain Plate + 字幕烧录 + 边框 | 保留现有烧录与边框顺序，关闭研究项。透明字幕层原型的半透明描边和阴影与原烧录输出有偏差。 |

## 其它高开销组合与限制

| 组合或条件 | 源码发现 | 处理 |
| --- | --- | --- |
| FGSIM + 边框（含 LUT） | `(!grainEnabled || plateMode)` 排除 FGSIM；现有 Shader 通常 `hwdownload`，再进 CPU 边框 | 需完整 Vulkan Shader→GPU 边框复用设计，暂不改 |
| Digital Grain + 边框 | 数字颗粒使用 CPU `noise`/mask/overlay 链；不等同扫描 Grain Plate 的 GPU 滤镜 | 不可直接套用 Grain Plate GPU 通道，暂不改 |
| 插帧 + LUT/调色/字幕 + 边框 | FGB-02 有明确不支持条件，触发保护性拒绝而非静默 CPU 回退 | 需独立组合验证；保持现有保护 |
| HDR→SDR 默认 CPU 色调映射、CPU Bilateral、SVP HDR CPU10 | 明确选择/回退的 CPU 处理，不是选路漏判；可能大幅降低 FPS | 保持现有阶段橙色高成本提示 |
| HWD requested=on 实际软解 | 多数路线为了输入格式、外部插帧工具或 Vulkan/CUDA 兼容而显式软解 | 单独软解不预设严重掉速，不误报 |
| 单边框 SDR、单独 LUT、单独 Grain Plate | 已有独立有效路线 | 无需更改 |

## 验证

- R6K4P9 的 HDR 调色及 SDR Resize／剪裁组合已在 RTX 4080 实机通过。
- Windows CI 通过完整构建、S4 边框图、PQ／HLG 图、构图及缩略图回归。47 条本地实际 FFmpeg 滤镜图通过，合成样本保留原有 83 ms 音画偏移。
