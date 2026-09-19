# Film Grain Studio v4.8.5

本版正式加入 HEVC AQ 高级控制，便于按素材需要手动调整 NVENC 的 Spatial AQ 与 Temporal AQ，同时保持原有默认行为不变。

## HEVC AQ

- `高级设置 → 编码 → HEVC Spatial AQ` 提供：`0 / 4 / 8 / 10 / 12 / 15`。
- 默认仍为 `8`；`0` 表示关闭 Spatial AQ。
- `HEVC Temporal AQ` 保持独立复选框，默认开启。
- 两项设置仍受硬件能力检测约束；不支持时不会强行追加对应 NVENC 参数。

## 兼容性

- 仅调整 HEVC 主编码 AQ 控制；AV1、x264 与 H.264 上传副本继续沿用既有 AQ 逻辑。
- 不修改自动/手动码率、FGSIM、Digital Grain、Grain Plate、HDR、LUT、OpenSVPFlow、反交错、字幕、容器或 AAC 256k。
- 基于 `v4.8.4_AQTEST_Q8N4R` 已完成的实际编码/截图比较收口；正式版新增 4 与 15 两档，并恢复干净的 `v4.8.5` 版本标识。

正式包继续由 Windows Server 2022 runner 构建，并执行 PowerShell、语言资源、BOM、CRLF、必需文件与最终 ZIP 检查。
