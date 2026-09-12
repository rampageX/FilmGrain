# Film Grain Studio — CHANGELOG

## v4.6.3 — 2026-09-12

- LUT Gallery 新增 **“更新预览图”**，直接在图库中同步 LUT 预览，不再需要手动进入 Utils 运行批处理工具。
- 更新模式只补建新增或缺失的 LUT 预览；现有“更换参考图”继续保持选择新参考图并全量重建的原有行为。
- 删除同步采用保守安全策略：仅删除旧 Gallery 索引中明确记录、且源 LUT 已确认不存在的单个 `*_preview.jpg`；对应 Gallery 缩略图按完整预览路径哈希精确删除。
- 禁止递归删除预览目录；父目录仅在确认已经为空时使用非递归 `Directory.Delete(path, false)` 删除，`_LUT_PREVIEWS` 根目录与缩略图缓存树均受保护。
- 更新完成后 Gallery 立即重新加载，顶部 `匹配 / 共` 数量无需重新打开即可刷新；左下角显示本次 `新增 X / 删除 Y / 当前共 Z`。
- LUT Gallery UI 调整：`禁用 LUT` 移至底部并放在“使用选中的 LUT”之后；“更新预览图”位于“更换参考图”之后；分页控件整体右对齐；文件夹下拉框间距修正；智能过滤状态简化为 `智能过滤 X`。
- 本次功能经新增 LUT、删除 LUT 与 UI 刷新实际测试通过；AV1 / HEVC / x264 编码参数、HDR Preserve、Grain、OpenSVPFlow、字幕、AAC 256k 与 H.264 上传版逻辑均未修改。

## v4.6.2.1 — 2026-09-12

- 修复 AV1 / HEVC “同时生成 H.264 上传版”在手动码率模式下可能错误提示 `Invalid Studio H.264 upload bitrate` 的问题。
- Studio GUI 已完成整数范围校验后，StudioBridge 不再对 `FG_UPLOAD_BITRATE` 重复调用第二套 PowerShell/CMD 校验，而是直接使用同一组已验证的 `FG_UPLOAD_BITRATE / FG_UPLOAD_MAXRATE / FG_UPLOAD_BUFSIZE`。
- 手动上传码率继续统一采用平均码率、`maxrate = 3×`、`bufsize = 6×`；例如 10000 kbps 对应 10000k / 30000k / 60000k。
- AV1 与 HEVC 的 H.264 上传副本共用同一修复分支；x264 主输出不创建重复的 H.264 上传副本。
- AV1 / HEVC / x264 三条主编码线现有手动码率仍走共享 `FG_BITRATE / FG_MAXRATE / FG_BUFSIZE` 路径，本次不修改其编码参数、自动码率、HDR、Grain、LUT、OpenSVPFlow、字幕或 AAC 256k 行为。
- 修复已由用户在 v4.6.2_H264UploadBitrate_TEST_K7M4Q 测试包上验证通过。
- 正式发布恢复干净版本号与文件名，不包含 TEST_K7M4Q 测试标记。

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

- Film Grain Studio 正式加入专用 FGS 应用图标；主窗口标题栏与 Windows 任务栏统一显示红色 FGS 图标，不再使用 PowerShell 默认图标。
- 任务栏图标通过独立 `AppUserModelID`、`RelaunchIconResource` 与窗体 Icon 协同设置，继续沿用现有 VBS 隐藏 PowerShell 启动方式，不引入额外 Launcher EXE。
- `_OpenSVPFlow` 新增正式 `01_Update_OpenSVPFlow.bat` / `Update_OpenSVPFlow.ps1`：自动查询 `Z1xus/open-svpflow` 最新 Release，下载 Windows x64 ZIP，并在替换前后执行 CPU 与 GPU/OpenCL smoke test。
- 更新器在替换 `svpflow1_vs.dll` / `svpflow2_vs.dll` 前自动备份当前插件到 `_OpenSVPFlow\_PluginBackup`；安装后验证失败会自动回滚。下载临时目录使用随机后缀并限制安全清理范围。
- 更新器记录 `Plugins\open-svpflow-version.txt`，后续再次运行时若已是最新 Release 则不修改文件。
- `00_Setup.bat` 继续保留已验证的固定版本首次安装职责，不改既有插帧参数与编码链；v4.6.1 不修改 AV1 / HEVC / x264、SFE、Grain、LUT、字幕、反交错、码率、Batch Summary 或 MPEG-TS 同步逻辑。
- 正式版恢复干净文件名和 `v4.6.1` 标识，不包含图标 TEST、SVPFlow Benchmark 或 OpenSVPFlow TEST 更新器；README / CHANGELOG 继续分离维护。

## v4.6.0 — 2026-09-11

- 正式加入 **AV1 NVENC 多引擎并行 / Split Frame Encoding (SFE)**。主界面“速度 / 质量”下方新增 **“多引擎并行 ×N”**，Tooltip 显示 `NVENC: Split Frame Encoding (SFE)`。
- `×N` 由 NVIDIA NVENC 能力检测决定，不按 GPU 型号硬编码；RTX 4080 实测为 ×2。StudioBridge 仅在能力满足时追加 `-split_encode_mode N`，GUI / CLI 继续共用同一编码核心。
- SFE 仅在 **AV1 Standard / AV1 UHQ** 允许启用；AV1 FAST、HEVC、H.264 x264 Grain 与 AV1 不重编码模式自动灰显并取消勾选。RTX 4080 实测 Standard / UHQ 完整流程有实际加速，FAST 与 HEVC Grain 路线不纳入启用范围。
- SFE 新增 **grav1synth 0.2.2+** 依赖检测。低于 0.2.2、版本无法识别或路径不可用时，“多引擎并行”自动禁用；Hardware Caps 缓存签名同时记录 grav1synth 路径、文件状态与版本。
- 配合 grav1synth v0.2.2 修复 NVENC SFE 输出中的 standalone `OBU_FRAME_HEADER / OBU_TILE_GROUP` 兼容问题，AV1 FAST / Standard / UHQ 的 SFE 测试均可完成 Film Grain 注入、转封装与最终 Header 验证。
- 修复 **隔行素材 + OpenSVPFlow 插帧** 的运行错误。OpenSVPFlow 仍保持 progressive-only；逐行输入继续使用 OpenSVPFlow 60 fps，隔行输入则按文件自动旁路 OpenSVPFlow，并使用现有 Field-rate 反交错路线，例如 29.97i → 59.94p、25i → 50p。
- 混合批量任务支持逐文件判断 progressive / interlaced；不增加额外中间视频，也不增加第二次转码。反交错算法仍可使用 BWDIF Vulkan、BWDIF CUDA 或 W3FDIF Complex。
- 保持 v4.5.2.2 的 Batch Summary `Started / Completed / Elapsed` 兼容性修复、AAC 256k 统一策略、LUT / Grain / 字幕 / Cinematic / H.264 上传版、MPEG-TS 时间戳同步与其它已验证编码参数不变。
- 正式版恢复干净版本标识 `v4.6.0`，不包含 TEST 构建文件；README / CHANGELOG 继续分离维护。

## v4.5.2.2 — 2026-09-10

- 修复 v4.5.2 在部分 Windows / PowerShell 环境中 Batch Summary 的 `Started`、`Completed`、`Elapsed` 同时显示 `Unavailable` 的兼容性问题。
- 计时实现改为分别获取开始 UTC ticks、开始本地时间、结束本地时间与总秒数，不再把多个返回值拼接到同一行后交给 `for /f` 二次拆分，降低 CMD / PowerShell 多层引号与分隔解析差异带来的失败概率。
- `Elapsed` 只从 PowerShell 获取纯整数秒数，再由 BAT 统一格式化为累计 `HH:mm:ss`；开始 / 完成时间仍显示本机本地时间。
- 新增 `%DATE% / %TIME%` 纯 BAT 后备计时路径：当 PowerShell 时间命令无法正常返回时，仍可给出开始时间、完成时间与批次耗时，避免直接回落为三项 `Unavailable`。
- 修复继续位于 GUI / CLI 共用的 `FilmGrain_Universal_HEVC_AV1_StudioBridge.bat`，两种入口统计口径一致。该修正版已在用户的笔记本与台式机上完成实际测试。
- 除 Batch Summary 计时兼容性外，不修改 AV1 / HEVC / x264 编码参数、Grain、LUT、字幕、反交错、OpenSVPFlow、H.264 上传副本、AAC 256k 或其他已验证处理逻辑。
- 正式发布恢复干净版本标识 `v4.5.2.2`，不包含测试构建标识、测试更新器或临时备份文件；README 与 CHANGELOG 继续分离维护。

## v4.5.2 — 2026-09-10

- GUI / CLI 共用的 **Batch Summary** 新增 `Started`、`Completed`、`Elapsed` 三项，批处理结束后可直接查看本批任务的开始时间、完成时间与实际总耗时。
- 计时起点位于全部编码参数确认完成、正式进入 Batch 处理之前；终点位于最终 `:FINISHED` 汇总入口，因此主编码以及任务实际启用的 OpenSVPFlow、x264 2-Pass、H.264 上传副本等后续阶段均计入同一总耗时。
- `Started / Completed` 使用本机本地时间显示为 `yyyy-MM-dd HH:mm:ss`；`Elapsed` 使用 UTC ticks 计算时间差并显示为 `HH:mm:ss`，跨午夜不会导致耗时统计错误，超过 24 小时时小时数继续累计。
- 计时逻辑只加入 GUI / CLI 共用的 `FilmGrain_Universal_HEVC_AV1_StudioBridge.bat`，两种入口保持同一统计口径，没有复制第二套编码核心。
- 本功能已完成用户侧实际测试；v4.5.1 的 AV1 / HEVC / x264、码率策略、Grain、LUT、字幕、反交错、OpenSVPFlow、上传副本及音频处理逻辑均未修改。
- 正式发布恢复干净版本标识 `v4.5.2`，不包含 BatchTime 测试更新器、测试构建名或临时备份文件；README 与 CHANGELOG 继续分离维护。

## v4.5.1 — 2026-09-09

- H.264 x264 Grain 的默认编码策略由 `preset slow + tune grain + true 2-pass` 调整为 **`preset faster + tune grain + VBR 单次`**；该组合已完成用户侧实际测试，显著降低日常 x264 Grain 编码耗时。
- 高级设置 → 编码新增 **x264 码率模式**：`VBR 单次（默认 / 推荐）` 与 `VBR 2-Pass`。2-Pass 完整保留，供需要更精确平均码率 / 文件大小分配的任务主动选择，不再作为 Film Grain 保留的强制条件。
- 高级设置 → 编码新增 **x264 Preset**：`Faster（默认 / 推荐）`、`Medium`、`Slow`；三档均继续固定使用 `tune grain`，自动码率、高动态、`maxrate = 3×`、`bufsize = 6×`、10-bit 前处理 / 8-bit dither 与 High10 逻辑保持不变。
- H.264 x264 Grain 主线与 AV1 / HEVC 的“同时生成 H.264 上传版”统一读取同一组 x264 Preset / Pass 设置；上传副本继续固定 High 8-bit，High10 仍只作用于 H.264 主输出。
- GUI 与 CLI 同步支持新的 x264 Preset / Pass 选择；默认 Faster + VBR 单次输出保持干净正式文件名，仅在选择 Medium / Slow 或 2-Pass 时追加参数识别后缀，避免非默认参数成片与默认成片互相误判为已存在。
- 独立 `Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat` 不在本次改动范围内，继续保留其已验证的 `slow + tune grain + 2-pass` 独立工具策略。
- 正式发布基于用户验证通过的 v4.5.1 测试版，移除测试构建标识与测试说明；AV1 / HEVC 编码参数和既有处理链未作修改。

## v4.5.0 — 2026-09-09

- **H.264 x264 Grain 正式升级为第三条主编码线**，与 AV1 NVENC、HEVC NVENC 同级；复用扫描 Grain / LUT / Cinematic / 字幕 / 反交错 / OpenSVPFlow 前处理，最终使用 `libx264 / preset slow / tune grain / true 2-pass`。
- H.264 默认保持 10-bit 前处理到最终编码边界，再输出兼容性更好的 High Profile 8-bit；能力探测通过时使用 error-diffusion dither。高级设置新增 **H.264 High10（实验）**，默认关闭，启用后使用 `yuv420p10le / High 10 Profile`。
- AV1 / HEVC / H.264 三条主线统一加入**可见的自动码率策略**：按编码器 + 输出分辨率 + 最终 FPS + 高动态状态计算，主界面直接显示实际 kbps，任务日志同步打印分辨率档位、60 fps 基准、FPS 系数、`b:v`、`3× maxrate` 与 `6× bufsize`；取消自动后尊重用户手动码率。
- 统一 60 fps 自动码率基准：普通动态 AV1/HEVC/x264 分别为 720p `3.5/4/5M`、1080p `5/6/7.5M`、1440p `7/8/10M`、2160p `10/12/15M`；高动态分别为 `6/7/10M`、`9/11/15M`、`12/15/20M`、`18/22/30M`。
- FPS 系数采用适合颗粒素材的非线性联动：24≈0.60、25≈0.62、30≈0.70、50≈0.90、60=1.00、120≈1.65，中间值插值；Cinematic 裁剪、Field-rate 与 OpenSVPFlow 最终尺寸/FPS 均参与实际计算。
- 全局 **“高动态”** 统一控制三条主线：除提高自动码率外，AV1 / HEVC NVENC 在实际能力允许时使用 Lookahead 32、Fullres Multipass、adaptive B 与 scene-cut；x264 在探测支持时使用 `b_strategy 2`，并继续保留 `slow + tune grain + 2-pass`。
- HEVC / AV1 的 **“同时生成 H.264 上传版”** 淘汰旧 NVENC 固定码率和 x264 Tier 选择，统一固定为 x264 Grain 8-bit；上传副本拥有独立的自动/手动码率框，但与主线 x264 Grain 共用分辨率 / 最终 FPS / 高动态策略，VBV 继续使用 3× / 6×。High10 不作用于上传副本。
- H.264 主线、High10、HEVC、AV1、OpenSVPFlow 插帧及相关组合功能已完成实际用户侧测试；v4.4.3 的 MPEG-TS OpenSVPFlow A/V 同步修复和 v4.4.5 的插帧 + H.264 上传复用逻辑保持不变。
- 正式发布包恢复干净版本标识 `v4.5.0`，移除测试说明 / TEST 构建标签；README 与 CHANGELOG 继续分离维护。

## v4.4.5 — 2026-09-09

- OpenSVPFlow 60 fps 插帧开启时，GUI / CLI 不再禁用 **“同时生成 H.264 上传版”**，插帧与上传母版可以在同一任务中完成。
- AV1 + OpenSVPFlow 上传路线继续复用最终 AV1 主成片，由 `libdav1d` 将 AV1 Film Grain metadata 合成为真实颗粒像素后再生成 H.264，不重复运行插帧。
- HEVC + OpenSVPFlow 新增专用上传分支：直接复用已经完成 60 fps 插帧、扫描 Grain、LUT、Cinematic 与字幕处理的最终 HEVC 主成片，再生成 H.264；不再次运行 OpenSVPFlow，也不重复渲染 Grain / LUT / 画幅 / 字幕。
- 未启用 OpenSVPFlow 的 HEVC 上传路线保持 v4.4.4 既有逻辑，仍从原始视频直接重走 Grain / LUT / 反交错 / Cinematic 处理链，避免普通流程从主 HEVC 成片二次转码。
- H.264 上传质量与码率规则保持不变：NVENC P7 三档，以及 `libx264 / preset slow / tune grain / 2-pass` 三档；x264 继续按 FPS + 分辨率 + 动态系数联动，VBV 为 `maxrate = 平均码率 × 3`、`bufsize = 平均码率 × 6`。
- H.264 上传音频继续统一为 **AAC 256 kbps / stereo / 48 kHz**；v4.4.3 已验证的 `.ts / .mts / .m2ts + OpenSVPFlow` A/V 时间戳同步修复保持不变。

## v4.4.4 — 2026-09-09

- 更新独立 `Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat`：H.264 上传编码由旧 `h264_nvenc` 路线同步为 **libx264 / preset slow / tune grain / 2-pass**，与 Studio 已验证的 x264 Grain 高质量策略保持一致。
- 独立工具新增输入编码探测：AV1 使用 `libdav1d` 将 Film Grain metadata 合成为真实颗粒像素；HEVC 等输入使用 FFmpeg 自动解码，修复 HEVC 成片被强制交给 libdav1d 后持续出现 `Unknown OBU type` 并转换失败的问题。
- x264 独立上传工具同步 Studio 的推荐 / 高质量 / 极高三档，以及普通动态 0.5× / 高动态 1.0× 预算；码率按实际 FPS + 分辨率 + 动态系数联动，VBV 保持 `maxrate = 平均码率 × 3`、`bufsize = 平均码率 × 6`。
- 独立上传工具重新接回项目统一 `FilmGrain_Config.ini` / `FilmGrain_Config_Load.bat` 路径配置，不在正式版中保留测试阶段的 FFmpeg 硬编码路径。
- H.264 独立上传音频恢复并统一为 **AAC 256 kbps / stereo / 48 kHz**。
- 其余 v4.4.3 已验证的 OpenSVPFlow MPEG-TS 时间戳同步、GUI / CLI 编码核心及 Film Grain 功能保持不变。

## v4.4.3 — 2026-09-08

- 修复 **OpenSVPFlow + MPEG-TS 系列输入** 的音画同步问题。
- 支持 `.ts` / `.mts` / `.m2ts` 在启用 OpenSVPFlow 插帧时自动检测源视频与音频起始时间戳，并保留原始 A/V 相对时间关系。
- HEVC + OpenSVPFlow 与 AV1 + OpenSVPFlow 均完成实际测试验证。
- 该时间轴修正仅作用于 MPEG-TS 系列输入且仅在 OpenSVPFlow 启用时生效，MP4 / MKV 等普通容器以及非插帧流程保持原稳定路径。
- 修复源于 VSPipe 重建视频时间轴从 0 开始时，丢失 MPEG-TS 原始 PTS 偏移的问题。

本文件记录正式发布版本的主要变化。`README.md` 只维护当前版本功能与使用说明，不再重复版本更新摘要。

## v4.4.2 — 2026-09-07

- 正式加入 **OpenSVPFlow GPU 60 fps 插帧**：本地 `_OpenSVPFlow` 运行环境由 `00_Setup.bat` 一次性安装，固定 VapourSynth R79、BestSource 21.0 与 `open-svpflow nightly-20260804-5ef4260`；能力检测通过实际 CPU / GPU/OpenCL smoke test 决定是否启用，不按 GPU 型号硬编码。
- 主界面新增 **“平滑 / 自动平衡”** 两种插帧模式：默认平滑对应 `scene.mode=0`，自动平衡对应 `scene.mode=3`；默认继续使用已验证的 Algo 13、`Super pel=1 / gpu=1 / full=true` 与 EncodeGUI-inspired Analyse profile。
- 右上角新增独立 **“高级…”** 设置窗体，按“编码 / 插帧 / 其他”分类；当前插帧高级项支持 SmoothFps Algo、Analyse Profile、Artifact Mask Area，并提供恢复推荐值。常用项继续留在主界面，后续高级参数统一收口到该窗体。
- OpenSVPFlow 处理使用 **VSPipe Y4M → FFmpeg 管道**，HEVC 与 AV1 主链均避免生成巨大中间视频；新增 SVP 路线输出 / AV1 中间文件 FFprobe 验证，避免 Windows CMD 管道错误码掩盖 FFmpeg 失败。HEVC + OpenSVPFlow + LUT + Grain 已完成实际测试。
- OpenSVPFlow 与自动反交错 / 普通电影帧率选择互斥；启用后输出帧率由 OpenSVPFlow 接管为 60 fps。当前版本仅接受逐行输入，H.264 上传副本在插帧启用时暂时关闭；已验证插帧路径内部为 YUV420P8，因此定位为逐行 SDR 插帧，不作为 HDR / 端到端 10-bit 保真链。
- Studio UI 以约 **1320×960** 客户区与 32 / 35 / 33 三列布局作为新基线；GPU、驱动、FFmpeg、配置缓存及 AV1 / UHQ / HEVC-Vulkan / OpenSVPFlow 能力统一移入最底部单行 StatusStrip，最右侧显示版本/构建标识。
- 修复启动后在未勾选插帧时“输出帧率”仍被错误灰化的问题：现在只有当前选中素材被明确检测为隔行时才锁定 Field-rate；逐行 / unknown 素材保持帧率下拉可用。
- 文件列表启用原生 ToolTip；长文件名即使在列宽中被截断，鼠标悬停仍可查看完整文件名。
- 测试 / RC / 临时 Bugfix 构建的右下角版本栏使用完整构建标识和随机防缓存后缀；正式发布恢复干净版本号 **`v4.4.2`**。
- 正式包移除测试期 `FilmGrain_Studio_DEBUG.bat` 与 `README_OpenSVPFlow_TEST.txt`，README / CHANGELOG 分离维护；PS1 继续使用 UTF-8 BOM，并在发布前确认不存在 `“ ” ‘ ’` 四种可能导致 Windows PowerShell 5.1 误解析的中文弯引号。

## v4.4.1 — 2026-09-06

- 将 **现成 AV1 Grain Table（影视 / Photon）** 从测试支线正式纳入 Studio：支持递归加载 `.tbl / .txt`，普通 AV1 重编码与“AV1 不重编码 · 添加/替换胶片颗粒”统一复用同一 Grain Table。
- `_AV1_Grain_Tables` 按 `720p / 1080p / 1440p / 2160p` 分辨率分类；GUI 默认只显示与源视频最接近的档位，右侧无文字复选框可切换为显示全部分辨率，ToolTip 显示当前自动匹配档位。
- 同一分辨率档位内支持解析 `3840x1600 / 3840x2160` 等实际宽高并按与源视频尺寸的接近程度优先排序，改善 1440p / 4K 电影画幅 Grain Table 的选择效率。
- 修复 Grain Table 分支 GUI 日志中的中文路径乱码：后台 CMD、临时结果文件与 .NET 日志读取统一按 UTF-8 处理。
- 修复 AV1 不重编码反复添加/替换颗粒时输出文件名继续叠加旧 `_AV1GS_... / _AV1FG_...` 标签的问题；现在只替换 Grain 标签并保留既有编码信息，避免文件名无限增长。
- 全项目凡发生音频重编码的路径统一为 **AAC 256 kbps**；MP4 主输出、AV1 不重编码 MP4 与 H.264 上传版统一使用 256k。MKV 等使用 `-c:a copy` 的原始音频保留路线不变。
- GUI 硬件信息区将能力缓存状态中文化：首次完成探测显示 **`配置：已适配`**，直接读取缓存时显示 **`配置：已缓存`**；底层缓存状态字段继续保持原有英文值，避免影响既有逻辑。
- README 正式加入两个现成 Grain Table 来源：`Boulder08/chunknorris` 与 `nekotrix/AV1-Photon-Noise-Tables`。正式包不重新分发第三方 Grain Table 数据。

## v4.3.1 — 2026-09-06

- 修复 LUT Gallery 更换参考图后，从 GUI 配置页“创建缩略图”补建缺失预览时又使用出厂默认参考图，导致同一 Gallery 中缩略图参考画面不一致的问题。
- 保留 `_LUT_Tools\LUT_Reference_Default.jpg` 作为只读的出厂默认参考图；用户在 Gallery 中执行“更换参考图”后，将所选图片统一转换并保存为 `_LUT_Tools\LUT_Reference_Current.jpg`。
- Gallery 全量重建、GUI 配置页补建缺失缩略图以及独立 LUT 预览生成器统一优先使用 `LUT_Reference_Current.jpg`；当前参考图不存在时才回退 `LUT_Reference_Default.jpg`。
- `LUT_Reference_Current.jpg` 为运行时用户状态文件，不预置在正式发布包中；首次更换参考图后自动生成，因此工具包整体移动后仍可继续复用最近一次参考图，不依赖原始图片外部路径。
- LUT 选择、Recent / Favorites、预览命名、已有缩略图不覆盖策略及编码核心保持 v4.3.0 已验证逻辑不变。


## v4.3.0 — 2026-09-06

- 细化 AV1 不重编码模式的自动命名：源文件没有 Film Grain 时输出 `_AV1FG_<Preset>_ADDED`，源文件已有 Film Grain 时输出 `_AV1FG_<Preset>_REPLACED`。
- 连续替换颗粒时，自动清理文件名末尾由本项目生成的旧 `_AV1FG_..._ADDED/REPLACED` 链，再写入当前颗粒名称，避免测试多次后文件名无限增长。
- 最终文件名整理改为在 AV1 Film Grain 验证成功后执行；使用独立 `FilmGrain_AV1_FinalizeName.ps1` 通过 Literal/.NET 路径操作完成，避免中文路径以及 `& ^ ( ) !` 等 CMD 特殊字符参与二次解析。
- Studio GUI 已经完成源 AV1 颗粒检测时直接复用该结果判断 `ADDED / REPLACED`；独立 BAT 使用时才由 helper 通过 `.NET Process` 进行一次 shell-free `grav1synth inspect`。
- 修复 PowerShell 5.1 将 grav1synth 正常 stderr `INFO` 输出视为 `NativeCommandError` 的问题，以及最终改名结果在 GUI 日志窗口中显示中文乱码的问题。
- AV1 视频流处理、grav1synth 注入、转封装与最终 Film Grain 验证流程保持 v4.2.9 已验证逻辑不变，本次仅收口输出命名与日志显示。


## v4.2.9 — 2026-09-06

- 将 `AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat` 正式接入 Studio GUI：单个 AV1 输入时，“编码方式”新增 **`AV1 不重编码 · 添加/替换胶片颗粒`**，视频流不重新编码，仅添加或替换 AV1 Film Grain metadata。
- 选择 AV1 不重编码模式后，自动禁用码率、速度、反交错、输出帧率、LUT、Cinematic、H.264 上传版等需要重编码的功能；AV1 胶片颗粒参数与 MP4 / MKV 容器选择继续可用。独立 BAT 的多文件能力保持不变。
- 所选 AV1 视频增加 `grav1synth inspect` 异步检测，并在媒体信息独立一行显示 **`AV1 胶片颗粒：无 / 亮度 / 亮度 + 色度`**；媒体信息去除价值较低的容器字段，为颗粒状态留出固定显示空间。
- 编码方式与 AV1 Grain 设置区统一中文界面文案：`AV1 · grav1synth 胶片颗粒（默认）`、`HEVC · 扫描胶片颗粒`、`AV1 · 胶片颗粒元数据`、`颗粒方式`、`胶片预设`、`胶片格式`、`胶片型号`、`感光度 ISO`、`亮度 + 色度` 等；AV1 / HEVC / grav1synth、预设名和胶片型号等专有名词继续保留英文。
- GUI **配置 → Grain 根目录** 的刷新扩展为同时统计原始 MOV、原分辨率 Cache 与 1080p Cache；发现缺失时启用 **“生成高速缓存”**，直接调用现有 `FilmGrain_MOV_to_HEVC_Lossless_Cache.bat` 补齐缺失的两类 Cache，已有文件不覆盖。
- Cache BAT 新增可选 `1 / 2 / 3` 模式参数，GUI 直接传入 `3` 进行非交互生成，避免日志窗口无法向 `set /p` 菜单传递键盘输入；单独双击 BAT 时仍保留原交互菜单。
- GUI **配置 → LUT 根目录** 的刷新扩展为同时统计 `.cube` LUT 与 Gallery 预览图；存在缺失时启用 **“创建缩略图”**，使用默认参考图只生成缺失预览，不覆盖已有缩略图；更换参考图并全部重建仍由 LUT Gallery 的“更换参考图”完成。
- LUT 预览生成脚本新增显式 FFmpeg 路径入口，以便 GUI 使用当前统一配置中的 FFmpeg；AV1 无重编码工具新增 Studio 非交互入口，GUI 与独立 Utils 继续共用同一套已验证处理核心。
- 保持 v4.2.1 的统一路径配置、硬件能力探测以及 HEVC / AV1 主编码核心不变；本次发布重点是将常用零散 Utils 能力收进 GUI，并统一相关状态显示与交互。


## v4.2.1 — 2026-09-06

- 新增根目录统一路径配置 `FilmGrain_Config.ini`；GUI、CLI、StudioBridge 与相关 Utils 工具统一从同一配置读取外部依赖路径，结束各脚本分别维护硬编码路径的方式。
- GUI 右上角新增 **“配置…”**，集中管理 FFmpeg、grav1synth、HEVC Grain 根目录与 LUT 根目录；项目内部脚本仍使用相对路径，保持工具包可整体移动。
- FFmpeg 配置精简为单一 **FFmpeg 目录**，默认 `E:\EnCoder\FFMpeg\x64\bin`；程序自动使用该目录内的 `ffmpeg.exe` 与 `ffprobe.exe`，不再分别配置两个执行文件。
- 配置窗口各路径统一提供“浏览…”与 `↻` 刷新。浏览选择后立即检测；手工输入后不自动扫描，由用户点击刷新明确触发检测。
- FFmpeg 检测同时显示 `ffmpeg.exe` / `ffprobe.exe` 是否可用及各自版本；grav1synth 显示可执行文件检测与版本。
- Grain 根目录刷新时递归统计原始 `.mov` Grain Plate 数量；LUT 根目录刷新时统计可用于 Gallery 的 `.cube` LUT 数量。
- “保存”仅执行快速路径存在性检查并写入 INI，不再启动 FFmpeg / grav1synth 或递归扫描目录，避免保存配置时出现不必要停顿。
- GUI 硬件信息区增加 FFmpeg 版本，与 NVIDIA 驱动版本及能力缓存状态一并显示。
- `FilmGrain_Config.ini` 使用 UTF-8 无 BOM；PS1 / BAT 分别通过统一配置读取层处理编码和 CMD 特殊字符，继续遵循 BAT/VBS 无 BOM、PS1 UTF-8 BOM 的项目规则。
- 保持 v4.1.0 已验证的 HEVC / AV1 / 字幕 / H.264 上传 / 反交错 / Cinematic / LUT / Grain 编码逻辑不变，本次重点仅收口配置与状态显示。
- 正式发布前重新执行脚本编码、BOM、CRLF、CMD `^` 续行尾空白、旧 FFmpeg `13.0\bin` 路径残留、文件结构与 ZIP CRC 审计。


## v4.1.0 — 2026-09-05

- 字幕功能从 H.264 上传副本中解耦，改为**独立开关**；不再要求勾选“同时生成 H.264 上传版”才能烧写字幕。
- 主 HEVC / AV1 输出现在都可直接烧写字幕；如同时生成 H.264 上传副本，副本也会包含同一套字幕。
- AV1 路线中字幕先烧写进 Main10 基础画面，再注入 Film Grain metadata；后续 H.264 上传副本直接继承已烧写字幕，避免重复烧写。
- 带字幕的主输出文件名增加 `_SUB`，避免与无字幕版本覆盖或混淆。
- 字幕定位逻辑简化并统一：不再判断下方 Cinematic 黑边高度，而是始终以**最终输出画面底边**为基准定位。
- 默认字幕底部边距由 v4.0.0 的 25 px 调整为 **5 px（1920 宽基准）**，仍按输出宽度等比缩放。
- 启用上下黑边时，黑边属于最终输出画面，字幕自然位于下黑边；不启用黑边时，字幕直接位于视频底部并允许覆盖少量画面内容。
- 继续保留内嵌文本字幕、同名外部字幕、浏览外部字幕、GB18030 回退、`huiwen-mincho`、1080p 基准字号 69、白字黑色 Outline 1 / Shadow 1。
- 其余 v4.0.0 编码逻辑保持不变：x264 `slow + tune grain + 2-pass`、FPS + 分辨率 + 动态联动码率、VBV 3× / 6×、NVENC P7 三档均未改动。
- 正式发布前重新执行 BAT/VBS BOM、PS1 BOM、BAT 标签引用、CMD `^` 续行、CRLF、Markdown/TXT 编码与 ZIP CRC 审计。

## v4.0.0 — 2026-09-05

- H.264 上传路线正式升级为 v4 核心功能：保留 NVENC P7 固定码率档，同时将 **libx264 / preset slow / tune grain / 2-pass** 作为高质量颗粒保留路线。
- NVENC 上传档精简为 `6000 / 8000 / 15000 kbps`，默认仍为 8000 kbps；NVENC 档不参与自动分辨率联动。
- x264 Grain 继续提供“推荐 / 高质量 / 极高”三档；码率算法由 v3.4.0 的 FPS 联动升级为 **FPS + 实际输出分辨率 + 动态系数三重联动**。
- x264 自动码率公式：`60p 档位基准 × FPS/60 × sqrt(输出像素数 / 1920×1080) × 动态系数`，最终按 500 kbps 步进取整。
- 新增 **“高动态视频”** 复选框，默认不勾选：普通动态使用完整自动码率的 0.5×；勾选后恢复 1.0× 高动态预算。1080p60 推荐档实测对应 7.5M / 15M。
- 分辨率联动采用像素面积平方根：720p≈0.67×、1080p=1×、1440p≈1.33×、4K=2×；4K 不再沿用 1080p 的同一总码率。
- Cinematic 裁剪后的**实际有效输出尺寸**参与 x264 码率计算，因此 1920×804、3840×1608 等电影画幅也可自动获得合理码率。
- x264 Grain 继续使用已验证的 VBV：`maxrate = average × 3`、`bufsize = average × 6`，在保持颗粒的同时限制极端高速镜头中的瞬时码率峰值。
- Studio UI 重新整理：H.264 上传质量行下方单独放置“高动态视频”和“字幕…”；画幅 / 驱动 / 能力等纯信息文字顺势下移，避免 Cinematic 与上传选项区域拥挤换行。
- H.264 硬字幕沿用 v3.4.0 已验证方案：内嵌字幕、同名外部字幕、浏览外部字幕、GB18030 回退、`huiwen-mincho`、1080p 基准字号 69、默认距有效画面下沿 25 px，并按输出宽度缩放。
- GUI / CLI 继续共用 `StudioBridge` 编码核心；CLI 的 x264 Grain 选择同样支持高动态开关与 FPS / 分辨率联动。
- 正式发布前重新执行 BAT/VBS BOM、PS1 BOM、BAT 标签引用、CMD `^` 续行、CRLF、Markdown/TXT 编码与 ZIP CRC 审计。

## v3.4.0 — 2026-09-05

- H.264 上传版新增 **硬字幕烧写**，仅作用于上传副本，不改变主 HEVC / AV1 成片。
- 字幕来源支持内嵌文本字幕下拉选择、同目录同名 `.srt / .ass / .ssa / .vtt` 自动匹配，以及浏览本地字幕文件；多文件任务支持逐文件自动匹配。
- 外部字幕新增字符集兼容：Unicode BOM / 严格 UTF-8 自动识别，非 UTF-8 中文字幕自动回退 **GB18030（兼容常见 GBK / ANSI）**。
- 字幕样式默认改为 `huiwen-mincho`、1080p 基准字号 `69`、白字、黑色 Outline 1 / Shadow 1；默认距有效画面下沿由测试阶段的 20 px 调整为 **25 px**。字号、边距、描边和阴影按输出宽度相对 1920 px 自动等比缩放。
- Studio 的“字幕…”按钮移至 **Cinematic Style 同一行**；H.264 上传版一行重新分配布局空间，避免“同时生成 H.264 上传版”文字换行。
- H.264 NVENC 上传档精简为 `6000 / 8000 / 15000 kbps`，统一升级为 **preset P7**；默认仍为 8000 kbps。
- 新增 CPU 高质量上传路线：**libx264 / preset slow / tune grain / 2-pass**，用于更高效地保留真实胶片颗粒。
- x264 Grain 提供“推荐 / 高质量 / 极高”三档，并按**实际输出 FPS 自动联动平均码率**：60p 基准分别为 15 / 20 / 25 Mbps，24p 约为 6 / 8 / 10 Mbps，最终值按 500 kbps 步进取整。
- x264 Grain 三档统一采用实测稳定的 VBV：`maxrate = average × 3`、`bufsize = average × 6`。60p 推荐档即 15M / 45M / 90M，解决无限制 2-pass 在极高动态镜头中瞬时码率异常飙升导致的播放色块问题。
- AV1 上传路线继续由 `libdav1d` 将 Film Grain metadata 合成为真实颗粒像素后再压制；HEVC 上传路线继续从原始视频重新走 Grain / LUT / 反交错 / Cinematic 处理链，避免从主成片二次转码。
- GUI / CLI 继续共用 `StudioBridge` 编码核心；正式发布前重新检查全部 BAT 标签引用、CMD 特殊字符、`^` 续行、文件编码与 ZIP CRC。

## v3.3.1 — 2026-09-04

- H.264 上传母版由 AV1 专属功能改为 **HEVC / AV1 共用**，GUI 与 CLI 同步开放。
- H.264 上传版新增码率选择：`6000 / 8000 / 10000 / 12000 / 15000 / 18000 / 20000 / 30000 kbps`；默认 8000 kbps，输出文件名包含所选码率，便于多档测试。
- HEVC 上传版直接从原始视频重新走同一套 Grain / LUT / 反交错 / Cinematic 处理链后编码 H.264，避免先经过主 HEVC 成片再二次转码。
- 合并 `FilmGrain_MOV_to_HEVC_Lossless_Cache.bat` 与旧 1080p Cache 工具；统一脚本可生成原始分辨率、1080p 或两种 Cache，旧 `FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat` 删除。
- Cache 校验改为 **实际 10-bit sample-exact**：参考 SHA-256 来自与 NVENC 相同的同一 P010 帧流，并规范化为 `yuv420p10le` 后比较，修复 T600 上因 P010 低 6 位填充差异导致的假校验失败；RTX 4080 与 T600 Laptop 均实测通过。
- 修复 `StudioBridge.bat` 被误保存为 UTF-8 BOM 后导致 `@echo off` 失效、GUI 日志窗口回显 BAT 调试命令的问题。
- GUI / Studio 模式不再显示单文件 `Actual commands` 调试输出；CLI 单文件模式继续保留，便于排错。
- 发布文件编码规则固化并在打包前检查：**BAT/VBS 使用无 BOM 的 ASCII 兼容编码；Windows PowerShell 5.1 的 PS1 使用 UTF-8 BOM；Markdown 使用 UTF-8；中文 TXT 使用 UTF-8 BOM。**

## v3.3 — 2026-09-04

- 新增自动反交错与 Field-rate 输出。
  - 默认：**BWDIF Vulkan**。
  - 备选：**BWDIF CUDA**。
  - 高质量对照：**W3FDIF Complex**。
  - 自动读取 FFprobe `field_order`；`tt / bb / tb / bt` 视为隔行，progressive / unknown 自动旁路。
  - 29.97i 自动输出 59.94p；25i 自动输出 50p。
- Film Grain Studio 的输出帧率提示会针对已识别的隔行素材显示类似 `自动（29.97i → 59.94p）`。
- HEVC 与 AV1 统一 Cinematic Style 画幅处理：
  - **加黑边 · 保留原分辨率**（默认，适合后期在黑边区域添加字幕）；
  - **裁剪 · 输出有效约 2.39:1 画面**。
- HEVC 扫描 Grain 路线的黑边继续在 Grain 合成之后添加，保持纯黑区域不叠加扫描颗粒。
- AV1 普通、AV1 + LUT 与 AV1 UHQ 共用相同的 Cinematic 画幅选择逻辑。
- CLI 正式入口改为直接调用 `Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat` 的交互模式；GUI 与 CLI 从此共用同一编码核心，避免核心功能两套脚本分别维护。
- CLI 同步加入反交错方式选择，并与 GUI 使用相同的 Field-rate、Cinematic、LUT、Grain 与编码参数逻辑。
- 修复 Cinematic 摘要中的 CMD `IF /I NOT` 语法顺序错误，避免 Letterbox / Crop 模式在开始编码前报“此时不应有 …”。

## v3.2 — 2026-09-03

- LUT Gallery 新增 **更换参考图**，可直接选择新图片并覆盖生成全部 LUT 预览。
- 生成期间显示独立进度窗口，Gallery 暂时锁定操作，完成后自动刷新缩略图。
- 当前页码由输入框改为只读下拉菜单，可显示当前页并直接选择任意页面。
- 保留上一页 / 下一页、PageUp / PageDown 首尾循环、搜索、文件夹筛选、Recent 与收藏等既有行为。

## v3.1 — 2026-09-03

- 新增 NVIDIA GPU、驱动与 FFmpeg 实际能力探测，不再依赖固定 GPU 型号写死参数。
- 自动探测 AV1、HEVC、H.264 NVENC、Main10、B-frame、B-reference、Spatial / Temporal AQ、Lookahead、Multipass、NVDEC CUDA 与 Vulkan，并只启用当前环境实际支持的参数。
- 新增 **AV1 UHQ**；只有微型编码测试通过的 GPU / 驱动 / FFmpeg 组合才显示。
- 新增 `Utils\_HardwareCaps.json` 能力缓存；环境未变化时直接读取缓存。
- H.264 社交平台上传母版改为按有效分辨率自动选择 6 / 8 / 10 / 12 Mbps。
- 修复 T600 / RTX 4080 能力探测与缓存状态显示相关问题。

## v3.0 — 2026-09-02

- 将 Film Grain 项目整理为正式稳定发布结构，统一 GUI / CLI 入口与 `Utils`、`_LUT_Tools` 目录。
- 清理各脚本文件名中历史遗留的独立版本号；组件固定文件名，项目版本号只体现在完整发布压缩包上。
- GUI Studio、CLI、HEVC 扫描 Grain、AV1 + grav1synth、LUT Gallery、Cinematic Style、自动电影帧率、MP4 / MKV、多文件处理等整合为统一工具包。
- `README.md` 作为 GitHub 项目的当前功能与使用说明基准。
