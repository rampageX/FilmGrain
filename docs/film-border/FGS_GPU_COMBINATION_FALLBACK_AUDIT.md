# FGS S4 GPU 边框组合性能审查 · 2026-11-23

## 适用基线

- 正式发布：v5.0.84，Git commit `6e417650daf8e6f78d5d0a537534d56c5d4d4a14`。
- 测试源码从正式版本继续开发；保留 `v5.0.84` tag，不覆盖 Release。
- 核心：`dotnet/FilmGrain_NativeHevcCore.cs` 的 `s4GpuBorder` 选路及 `FilmBorderEncodingCore.AppendGpuFilter`。
- 实机已验收：SDR Grain Plate + 边框，RTX 4080 1080p 约 15→142 fps；原有 LUT+Grain Plate+边框在三效果叠加时用户报告 FPS 急降。**新增组合补丁未完成 RTX 4080 验收。**

## 确认的无谓 CPU 边框回退

| 组合 | 原因 | 修复策略 |
| --- | --- | --- |
| SDR BT.709 TV Grain Plate + LUT + 边框 | `s4GpuBorder` 的 `lutCompatName` 非空被拒；随后 `blend_vulkan` → `hwdownload` → CPU 边框 | 原 LUT 仍在 GPU 上传之前按原 `lut3d` 与 `blend` 处理，之后保留 S4 Vulkan/libplacebo→CUDA |
| SDR BT.709 TV Grain Plate + 色彩纠正 + 边框 | `colorFilter` 非空被拒；色彩纠正本身已有上传前的 CPU 滤镜链 | 允许已存在的上传前色彩滤镜后继续 S4，不修改色彩纠正算法 |
| SDR BT.709 TV Grain Plate + 色彩纠正 + LUT + 边框 | 同时触发上述两个不必要的选路限制 | 共用同一高性能通道，处理顺序依旧为色彩纠正→LUT→扫描颗粒→边框→CUDA/NVENC |

修改只针对 `s4SdrPlate`（SDR、BT.709 limited、渐进、非插帧、Grain Plate）的资格判断。保留 CPU Bilateral、Resize、字幕、非 100% 像素颗粒等互斥条件。HEVC 为 P010，H.264 在 CUDA 内转 NV12。

## 其它高开销组合与限制（未擅自放开）

| 组合或条件 | 源码发现 | 处理 |
| --- | --- | --- |
| HDR PQ/HLG + LUT/色彩纠正 + 扫描颗粒 + 边框 | HDR LUT 进入 `HdrColorProcessingCore.Lut` 的共享色彩前缀，`colorFilter` 非空阻止 S4；下游回 CPU 边框 | 需要专门核验色彩标签、原有高位深顺序与 Vulkan/CUDA 互操作，暂不改 |
| SDR Grain Plate + Resize/剪裁 + 边框 | `resize.Enabled`、尺寸/画幅限制导致 S4 不适用 | 需要单独验证 GPU 缩放后的画布及边框几何，暂不改 |
| Grain Plate + 字幕烧录 + 边框 | `subtitleFilter` 非空阻止 S4；字幕是与 GPU 边框冲突的后处理阶段 | 不可直接将字幕提前，须核对透明叠加及时间轴，暂不改 |
| FGSIM + 边框（含 LUT） | `(!grainEnabled || plateMode)` 排除 FGSIM；现有 Shader 通常 `hwdownload`，再进 CPU 边框 | 需完整 Vulkan Shader→GPU 边框复用设计，暂不改 |
| Digital Grain + 边框 | 数字颗粒使用 CPU `noise`/mask/overlay 链；不等同扫描 Grain Plate 的 GPU 滤镜 | 不可直接套用 Grain Plate GPU 通道，暂不改 |
| 插帧 + LUT/调色/字幕 + 边框 | FGB-02 有明确不支持条件，触发保护性拒绝而非静默 CPU 回退 | 需独立组合验证；保持现有保护 |
| HDR→SDR 默认 CPU 色调映射、CPU Bilateral、SVP HDR CPU10 | 明确选择/回退的 CPU 处理，不是选路漏判；可能大幅降低 FPS | 保持现有阶段橙色高成本提示 |
| HWD requested=on 实际软解 | 多数路线为了输入格式、外部插帧工具或 Vulkan/CUDA 兼容而显式软解 | 单独软解不预设严重掉速，不误报 |
| 单边框 SDR、单独 LUT、单独 Grain Plate | 已有独立有效路线 | 无需更改 |

## 验证与交接

- Windows CI 编译和 S4 图回归可验证新 LUT 前缀、BT.709 标记、色彩纠正先于 LUT、GPU 边框中没有 `hwdownload`、H.264 NV12。
- 无 NVIDIA GPU 的 Windows runner 不能替代 RTX 4080 的最终画面、速度及音画同步验收。
- 用户次日同源素材测试：LUT + Grain Plate + 边框；再选测色彩纠正 + LUT + Grain Plate + 边框。检查 `S4 Vulkan-libplacebo-CUDA GPU film border` 和 FPS。
