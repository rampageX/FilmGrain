# Film Grain Studio — CHANGELOG

## v4.6.2 — 2026-09-11

- 正式加入 **HDR Preserve**，覆盖 HEVC Main10 与 AV1 Main10。
- HDR 输入自动检测 BT.2020 + PQ / HLG，并保持 Primaries / Transfer / Matrix / Range。
- HEVC NVENC 正式强制 `p010le`，修复 Main10 Profile 但实际 8-bit 的问题。
- AV1 继续使用 `p010le + highbitdepth=1`。
- 最终 HDR 输出增加 FFprobe 信号校验；验证失败不保留错误成片。
- 源文件原本存在的 Mastering Display / MaxCLL / MaxFALL 按现有 FFmpeg/NVENC 路径保留；源本来缺失时不伪造。
- 当前 HDR Preserve 安全旁路 OpenSVPFlow YUV420P8、H.264 x264 主输出、H.264 上传版与现有 LUT 处理链。
- GUI 选中单个 HDR 文件并完成媒体探测后，OpenSVPFlow、H.264 上传版与 LUT 区域会视觉灰显；切回 SDR 自动恢复，编码核心仍按文件执行安全旁路。
- HEVC HDR10/PQ 与 AV1 HDR 均完成用户侧实际测试；10-bit、BT.2020、PQ、画面色彩及源静态 HDR10 metadata 保持正常。
- LUT Gallery 新增 **智能过滤**。
- 分类扩展为 `Technical / Combined / Creative / Keep`，仅高置信度纯 `Technical` 被隐藏。
- 新增跨文件 Creative Family 识别：同一创意 Look 存在 S-Log3 / V-Log / LogC / Rec.709 等多输入版本时，优先判为 Combined 并保留。
- Kodak 2383/2393、Film Print / Film Tone / Film Boost / ETERNA 等创意证据优先保留。
- 智能过滤扫描结果写入 `_LUT_PREVIEWS\_LUT_SMART_FILTER_REPORT.csv`。
- LUT Gallery UI 小修：`无 / 禁用 LUT` 缩短为 `禁用 LUT`，状态区域加宽，使隐藏数量更易完整显示。
- 正式发布包保留当前整理的公开 AV1 Grain Table 集合，按 720p / 1080p / 1440p / 2160p 分类，并补充上游来源说明。
- 正式包清理 `_HardwareCaps.json`、`LUT_Reference_Current.jpg`、OpenSVPFlow 本机 DLL/version state/`_PluginBackup` 等运行时状态。
- GUI 底部正式版本标识更新为 `v4.6.2`。
- README / CHANGELOG / README_FilmGrain_Studio / README_Toolkit / STABLE_BASELINE 同步更新。
- 正式版不包含 HDR/LUT Smart Filter 测试 Patch、HOTFIX 文件名或测试标记。

## v4.6.1 — 2026-09-11
- 加入 FGS 专用应用图标。
- `_OpenSVPFlow` 新增最新版更新器及备份/回滚流程。
- 不修改 v4.6.0 编码核心参数。

## v4.6.0 — 2026-09-11
- 正式加入 AV1 NVENC Split Frame Encoding / SFE。
- SFE 仅在 AV1 Standard / UHQ 启用，并要求 grav1synth 0.2.2+。
- 修复隔行素材与 OpenSVPFlow 的组合：隔行按文件自动旁路插帧并改走 Field-rate 反交错。

## v4.5.2.2 — 2026-09-10
- 修复部分 Windows / PowerShell 环境中 Batch Summary `Started / Completed / Elapsed` 显示 `Unavailable`。

## v4.5.2 — 2026-09-10
- Batch Summary 新增 Started / Completed / Elapsed。

## v4.5.1 — 2026-09-09
- x264 Grain 默认改为 `preset faster + tune grain + VBR 单次`，保留 Medium / Slow / VBR 2-Pass。

## v4.5.0 — 2026-09-09
- H.264 x264 Grain 正式成为第三条主编码线。

## v4.4.5 — 2026-09-09
- OpenSVPFlow 60 fps 开启时允许继续生成 H.264 上传版。

## v4.4.3 — 2026-09-07
- 修复 `.ts / .mts / .m2ts + OpenSVPFlow` 音视频同步。

v4.6.2 以 v4.6.1 正式稳定版为直接基线。
