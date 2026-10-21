# CQ 本轮审计与验证边界

基线：本次上传 FilmGrain_Studio_v5.0.19_Stable.zip 与 FilmGrain_addup.v5.0.19.zip 合并后的完整目录。未从 GitHub 拉取生产文件。产品/程序集/文件版本保持 5.0.19 / 5.0.19.0；本包是开发验证包，不是正式 Release。

## 真实调用链及修改位置

| 生产文件 | 入口 / 本轮变更 |
| --- | --- |
| dotnet/FilmGrain_Studio_NET_Preview.cs | BuildEncodeGroup 创建 CQ 复选框，LoadBitrateStateForCodec 恢复每编码器 CQ 与 VBR 显示，UpdateBitrateDisplays 对 CQ 跳过推荐码率；原 StartIndependentQueue / StartSharedQueue / coordinator 执行入口保留 |
| dotnet/FilmGrain_ConfigCore.cs | Advanced.RateControl 的六个新字段；沿用现有 IsTaskKey、UseTaskValues、Save 内存覆盖，不反复写全局 INI 应用逐文件配置 |
| dotnet/FilmGrain_BridgeStateCapture.cs | CaptureBridgeEnvironmentSnapshot 捕获模式与质量；FileEditorControls 包含 CQ 复选框；CaptureFileEditor 不把 CQ 文本误存为手动码率；ApplyFileEditor 从恢复的配置覆盖重建显示 |
| dotnet/FilmGrain_BridgeEnvironmentCore.cs | BridgeEnvironmentSnapshot / BuildEncode 新增 FG_NVENC_RATE_CONTROL、FG_NVENC_CQ；CQ 的码率、峰值、缓冲字段置零，不解析自动码率；NvencRateControlCore 校验质量、实际 FFmpeg 参数能力、最终参数与命名 |
| dotnet/FilmGrain_NativeAv1Core.cs | TryPrepare 的自动码率、输出名、真实 FFmpeg 能力核对与最终 encode 参数接入同一 CQ 核心 |
| dotnet/FilmGrain_NativeHevcCore.cs | HEVC / H.264 共用 TryPrepareEngine 与 BuildHevcArgs 接入同一 CQ 核心；包括直接、FRUC 和 SVP 进入该构造器的路线 |
| dotnet/FilmGrain_BridgeRequestPreparationCore.cs | NativeExecutionPreparationCore 准备成功时添加实际模式 / 用户质量日志；预览后续准备继续走该入口 |

链路：主窗口事件 → CaptureBridgeEnvironmentSnapshot → BridgeEnvironmentCore.Build → FileEditorSettings.Config 内存覆盖与 Copy 深复制 → StartIndependentQueue / StartSharedQueue 冻结状态 → NativeExecutionPreparationCore.TryPrepare → NativeAv1Core / NativeHevcCore → NvencRateControlCore.Apply → BridgeTaskCoordinator.StartQueue → BridgeExecutionCore 实际执行。

FGSIM 的 TryPrepareHook、TrySelectDevice、真实 shader 注册/像素变化检查、设备选择、取消包装均未修改。LUT 临时参考图、预览时间字段、队列选择跟随、高级页布局、音频和主题核心也未改动。Legacy 文件仅作为上传资源保留；当前 .NET 生产入口不会调用 Legacy BAT。独立 AV1 像素烘焙窗口保持 VBR。

## VBR 基线

原版与新源码分别编译同一验证夹具；BASELINE 宏仅跳过不存在的新功能，不重新实现编码算法。三个编码器都调用真实 BridgeEnvironmentCore.Build 和 NativeExecutionPreparationCore.TryPrepare。原码率 4321k / 12963k / 25926k 与原高级参数逐项一致，编码参数签名相同。本地原版 9 项、新版 18 项生产准备/命令/命名检查通过。实际 Windows 主窗口捕获还由双击入口分别跑原版、新版，并再次对照签名。

## CQ 语义与 FFmpeg 证据

上传包没有 FFmpeg / ffprobe 二进制，README 标注 9.0.1，不能据此宣称用户实际安装版本。本地工具是 Linux FFmpeg 6.1.1，其三路帮助均提供 CQ，但 AV1 此版本上限仍是 51。Windows 验证器读取真实 FgsConfig 的工具路径，记录 -version 和三路 -h encoder 帮助，不另装或替换 FFmpeg。

官方来源：

- https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/ffmpeg-with-nvidia-gpu/index.html （6.1.3：CONSTQP、VBR、VBR-CQ 的区别）
- https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc_av1.c （当前 CQ 上限 63）
- https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc_h264.c （CQ 上限 51）
- https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html （Target quality 使用 VBR 的 targetQuality；Multipass、AQ、Lookahead 等独立功能）

CQ 采用 VBR + 数值 target quality，不采用 constqp 或 -qp，不引入 qmin/qmax。0 的语义是自动，不作为本 UI 数值质量目标；FGS 提供 1–51 / AV1 1–63，可输入小数。23 是 FGS 的初始参考值，不是 FFmpeg 默认值，不与 FGSIM 绑定。实际所用工具（包括独立 FRUC FFmpeg）按文件身份缓存编码器帮助；范围不符明确拒绝，不回退 VBR。

## 兼容关系

| 项目 | CQ 行为 |
| --- | --- |
| 手动 / 自动码率 | 保留 VBR 编辑值与自动开关；CQ 不推荐、不解析、不命名为码率 |
| 高动态 | 保留已有编码器分析参数选择；不改变 CQ，也不增加码率 |
| Multipass | 保留 FGS / DISABLED / QRES / FULLRES 原能力校验；不是 x264 整片多遍 |
| SAQ / TAQ | 保留 AV1 通用及 HEVC、H.264 独立来源；CQ 不清空 |
| B 帧 / B 参考 / Lookahead | 保留原支持范围、组合校验及 UHQ 特例；不宣称 CQ 扩展了硬件能力 |
| SFE | 保留 AV1 / HEVC 原设备与速度限制；H.264 不支持；CQ 不新增并行任务 |
| 颗粒 / LUT / HDR / 插帧 | 继续走原准备及滤镜链，CQ 只替换最终码率控制；原 HDR/LUT 冲突规则保留 |
| x264 / AV1 不重编码 / 像素烘焙 | 保持原行为，不添加 CRF 或 CQ |

不恢复任何已删除的 FGSIM 质量档位、旧 CQ/QP 配置字段、固定 QP 上下限或联动说明。

## 自动化与证据

双击 Tests_CQ_N7V4PK/Run_CQ_N7V4PK.cmd。自动构建原版夹具、记录 VBR 基线、调用原生产 BAT 构建当前应用，再编译当前夹具。测试配置、日志、样本、输出全部在 Tests_CQ_N7V4PK/Runs 内。只读取真实配置的依赖路径，不向其保存。独立文件覆盖仍调用正常控件事件和生产配置 Save/Load。

夹具包含：真实 MainForm 首次显示/硬件未加载、三编码器模式数值保留、x264/AV1 copy 禁用、实际高级窗口打开关闭、760x520/分编码器页检查、A→B→A、模式切换、预览编辑状态保留、CQ/VBR 命令与命名、独立 SAQ/TAQ、Multipass/高动态组合、可用硬件短编码/探测/解码/非黑、Digital Grain + LUT、HDR 保留/转 SDR、FRUC（能力允许时）、FGSIM 真实像素证据、实际主窗口统一/独立队列冻结与顺序、取消/失败/新请求的生命周期检查。

路径夹具包含中文、空格、单引号、方括号、逗号、分号、&、%。进程参数不交给 CMD 解析；CMD 启动入口关闭延迟扩展，PS1 不使用中文弯引号。安全复制跳过目录链接，不清理或遍历用户依赖环境。每次结果保留独立时间戳目录，失败也打包；要求成功标记与必需签名，不能仅凭退出码视为通过。

结构测试使用明确标记的 capability flags，仅验证参数与状态，绝不作为硬件支持证据；实际编码另用 NativeHardwareProbe 真实检测结果。能力不足记 UNCOVERED；能力检测通过后编码失败记 FAIL。

## 本地验证与尚未覆盖

- 本地完整生产源码、原版/新版验证夹具编译通过；使用 Microsoft .NET Framework v4.8 参考程序集及项目 FrameworkApiAudit 核验：2251 项解析、0 项失败。两个交付 EXE 一致，未使用未审计 Mono 框架 API 产物替换。
- LocalBaseline / LocalResults 记录原版及新源码的真实配置解析、媒体身份、任务准备、原始命令和输出命名。三路 VBR 编码签名一致。
- 当前执行环境没有 NVIDIA GPU，不能完成 NVENC / FGSIM 实际编码验收；没有 Windows 桌面，不能把 Linux 编译或结构证据说成 WinForms 人工验收。Windows 夹具需用户机器实际运行，尚未运行的项目不记为通过。
- Windows 夹具已加入合成 HDR 标签素材的保留/转 SDR、H.264 保留拒绝，以及真实能力允许时的 FRUC + CQ；这些组合与设备触发回退/重试目前尚未完成硬件实测覆盖；不要将本轮描述为所有边界已验收。既有处理代码未改，但仍需后续专用验证。
- 未提供完整每版本兼容 GPU 矩阵，也未宣称相同 CQ 数值跨编码器具有相同视觉质量。当前 FFmpeg 帮助核对只证明选项存在，运行支持仍由实际编码结果决定。

README 仅增加最终用法；本记录承载测试过程和边界。未修改 .gitignore、未升级版本、未提交、未建分支/PR、未打 Tag 或发布。补丁相对本次上传基线累计生成。

补充本地验证：Linux WinForms 兼容环境完成 120 项检查，包括真实控件事件、A→B→A、配置与命令矩阵，以及 x264 实际队列取消、源身份变化拒绝、新队列成功编码和可解码非黑输出。Linux 原生 Windows 绘制入口不可用的诊断保留在 NativeLimitations 日志；不作为 Windows 绘制验收。

## R2_P6M8TX：Windows 验证启动修复

2026-10-21 回传 Return_CQ_N7V4PK_20261021_190437_407.zip 仅含 FAILURE.txt，错误位于验证脚本第 67 行的 Activator.CreateInstance，未开始 VBR 基线构建或功能测试。

替换为 FgsConfig(string, string) 精确签名选择及显式 System.String / null 参数数组，消除 PowerShell 自动构造器匹配。新增随机后缀入口；旧入口保留转发。新增构造器/参数类型诊断，未修改编码生产代码、版本或 EXE。

回归验证使用交付 EXE 的真实构造器、项目内含特殊字符的隔离配置，验证显式签名可实例化、读取正确且不写入配置。当前环境仍不能执行 Windows PowerShell 5.1 本身；该平台入口是否完整通过，以 R2 回传为准。此前 120 项本地证据不能代表这次 Windows 运行已经通过。

## R3_H8Q4VN：LUT 测试夹具刻度修复

回传 Return_CQ_N7V4PK_R2_P6M8TX_20261021_191025_939.zip 证明 Windows PowerShell 构造器问题已解决，Windows 构建/API 审计通过，三路 NVENC 的 VBR/CQ 六次真实编码均完成输出探测、解码、非黑检查；三路 VBR 参数签名与基线相同。实际工具版本为 FFmpeg 9.0.2 full。

失败发生在 RealCombinations，尚未开始 Digital Grain + LUT 组合准备：夹具误将百分比 50 写入 0..4 的滑杆刻度。修复为刻度 2，并增加真实 GetLutStrength=50 和生产状态 FG_LUT_STRENGTH=50 两项断言。前置 UI 验证也增加该映射检查。

局部回归通过真实 MainForm 控件复现非法刻度被拒绝，验证正确刻度及 BuildBridgeEnvironment 状态捕获；当前夹具编译通过。未修改生产代码、EXE、版本、用户配置、资源或 .gitignore。R3 的 Windows 组合编码及后续任务生命周期仍需实际运行，不预先记为通过。

## R4_T9K6WM：HDR 样本修复

R3 回传共 178 项通过。三路 CQ Digital Grain + 50% LUT、三路 CQ FGSIM 均完成真编码、输出探测、解码及非黑检查；FGSIM 每路有真实 shader 像素验证成功证据。VBR 签名继续与基线一致。

失败的原 FFV1/MKV probe JSON 没有 color_transfer 或 color_primaries，仅保留 bt2020nc 矩阵。生产 IsHdr 依据 PQ / HLG 传递函数判定，正确返回 false；没有放宽生产判定或伪造 MediaProbeInfo。

改用 10-bit libx265 / MP4 样本，并通过 setparams、输出颜色选项及 x265 VUI 的 colorprim=9:transfer=16:colormatrix=9 写入标记。进入测试前检查实际解码可见的 PQ、BT.2020、矩阵与 10-bit 像素格式。该样本只验证标记和处理路线，不声称为校准的 HDR 视觉参考。

本地已真实生成新样本，FFprobe 与生产 MediaProbeInfo.Parse / IsHdr 均验证通过；新夹具编译通过。未修改生产源码、EXE、版本、.gitignore 或用户配置。R4 的 Windows HDR/FRUC/队列实际运行仍未覆盖，不能预先记为通过。

R4 同时为验证子进程单独设置项目内的 TEMP / TMP（_ProcessTemp），使 NVDEC 等硬件探针的临时样本也留在当前 Runs 目录；不修改用户或系统环境变量。此前回传的 Z:\TEMP 路径作为历史证据保留。

## R4 Windows acceptance evidence

Return_CQ_N7V4PK_R4_T9K6WM_20261021_193031_371.zip: PASS 238 checks, 0 failures, 3 uncovered. RTX 4080 / driver 617.14 / FFmpeg 9.0.2. Original/current VBR signatures match; all three NVENC encoders passed short VBR/CQ encoding, decoding/nonblack, LUT/Digital Grain and real FGSIM shader-pixel evidence, HDR and queue lifecycle checks. Uncovered: configured FFmpeg lacks fruc_vulkan; private SVP environment excluded from sandbox; driver-specific retry trigger not forced.

## R5_U6M9QP — CQ recommendations and optional peak rate

Production changes: ConfigCore registers six new per-codec CQ limit keys; StateCapture freezes selected AUTO/manual target and limit; EnvironmentCore carries these to native arguments, validates 1..2000 Mbps, removes old VBR rate arguments and adds maxrate / twice-maxrate VBV buffer only for enabled CQ. Main window replaces the exhaustive range with eight editable entries per codec. SettingsDialogs adds independent limits alongside each encoder's first row, keeps 760x520 and both pages, and resets only current-page limits. Existing numeric CQ values remain unchanged; new defaults use AUTO, rate-control mode still defaults to VBR. No FGSIM-specific profile linkage or fixed QP bounds.

AUTO heuristic (not a measured perceptual guarantee): AV1 25 / HEVC 22 / H264 20; subtract one each for high-motion and enabled grain. No inference from source bitrate, resolution, scene complexity or FGSIM preset. Numeric targets are never adjusted. Presets are documented in README by quality/size intent. H264's lower initial number does not assert cross-codec equivalence.

CQ caps default enabled: AV1 80 / HEVC 100 / H264 120 Mbps. They are generous guardrails for ordinary up-to-4K use, not device certifications or a total-size guarantee. VBV buffer is 2 seconds. Disabled omits custom maxrate and bufsize; encoder/level internal limits can still apply. NVIDIA's Target quality documentation explicitly permits maxBitRate and warns that an insufficient cap may prevent attaining the target quality:
https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/#rate-control

User-provided article was reference only. No changes to existing speed/preset, bit depth, AQ, multipass, SFE or FGSIM shader code.

Local verification: production-source CqAudit with actual MainForm on Mono/Xvfb PASS 147, with one hardware-encoding limitation. Includes eight choices, AUTO and manual semantics, custom 37 Mbps / 74 Mb VBV and disabled limit production commands for all three, per-file A/B limit isolation, frozen capture, codec/mode retention, advanced page/control bounds, old VBR and prior UI matrix. This is not Windows native-rendering or NVENC execution proof. Microsoft .NET Framework API audit: 2257 resolved / 0 failures, both delivered EXEs identical, metadata unchanged 5.0.19 / 5.0.19.0.

The first local limit test exposed that AudioOutputCore.Get uppercases values; production limit parsing now compares TRUE rather than lowercase true. Subsequent full local run passed. Other initial local failures were fixture state/order issues, corrected without weakening assertions.

Windows entry: Tests_CQ_N7V4PK\Run_CQ_N7V4PK_R5_U6M9QP.cmd. It uses isolated project INIs, rebuilds production and compares baseline VBR, runs existing short hardware/queue/shader checks with new default caps and new recommendation/limit assertions, and packages results even on failure. R5 Windows/NVENC validation is pending; R4 results are prior evidence, not a pass for newly capped commands. Existing FRUC/SVP/driver-retry limits remain.

R5 baseline compile check: the unchanged original-source ZIP plus current harness with BASELINE defined compiles successfully (8 existing warnings, no errors).


## R5 Windows result / R6_B4T8NZ UI revision

R5 return Return_CQ_N7V4PK_R5_U6M9QP_20261021_195835_718.zip: PASS 276 checks, 0 failures, 3 uncovered. Independently verified original/current VBR signatures byte-identical for all three encoders. Actual CQ commands have AV1 80M/160M VBV, HEVC 100M/200M, H264 120M/240M; actual outputs decode nonblack. Custom 37M/74M and disabled caps passed preparation/command assertions (not separate real custom-cap encodes). FGSIM shader pixels, HDR, LUT, queue and lifecycle pass. FRUC/SVP/private environment and driver-specific retry limits remain as described above.

R6 user-requested UI changes:
- CQ uses the existing automatic checkbox. The editable numeric list contains seven numbers, no AUTO entry. Checked displays the resolved recommendation, unchecking restores the stored manual value; typing selects manual. VBR and CQ automatic/manual settings are independent per codec. Three new CQ_AUTO configuration keys participate in existing deep task snapshots. R5 sentinel AUTO remains readable for upgrade compatibility, with numeric fallback 23 when the user first disables automatic. Existing numeric R5 settings without CQ_AUTO remain manual. Fresh default INI checks automatic. Logs retain internal AUTO selection and resolved numeric target; output names use the numeric value.
- Cap editors are ThemeTextBox rather than NumericUpDown, aligned on Shown to the relevant Multipass row's actual native top/height, using the existing border painter. Validation on save requires integer 1..2000 Mbps while enabled; disabled values are retained. Current-page defaults remain scoped to that page.
- ThemeToolTip in existing Theme.cs supplies common owner drawing, current-window color selection, DPI/screen bounded width, multiline layout, and preserves explicit newlines and literal ampersands. Every explicitly instantiated tooltip was replaced. ToolStrip and file-list native item hints are routed through the same component, with handlers detached and components disposed by their theme engine. Caption/background images remain untouched. Tooltips re-evaluate theme on each display, rather than retaining a startup palette.

Production files changed this round: ConfigCore, BridgeStateCapture, Studio_NET_Preview, SettingsDialogs, Theme, SubtitleDialog, ResourceMonitor, NativeLutGalleryForm, default INI and both EXEs. Native encoder/shader code unchanged from R5; no codec preset, bit depth, AQ, FGSIM linkage, queue ordering or preview-time changes.

Local final checks: PASS 175, 2 unverified items (native Windows tooltip popup and NVENC hardware encoding). Real MainForm/production capture/preparation, old VBR, CQ checkbox/manual restoration, old configuration migration, per-file state, advanced text editing/save/reload and renderer multiline/light/dark pixel assertions pass. The original-source baseline harness also remains compiled under BASELINE in the Windows runner. Textbox native metrics on Mono differ by one pixel; exact native row-height equality is asserted on Windows, not claimed from Linux. One intermediate run rejected changed sample identity; a clean sequential final run passed without bypassing the identity guard.

Final production .NET Framework API verification: 2299 resolved, 0 failures. Both shipped EXEs identical, metadata still 5.0.19 / 5.0.19.0.

R6 default entry Tests_CQ_N7V4PK\Run_CQ_N7V4PK_R6_B4T8NZ.cmd rebuilds, compares original VBR, tests affected UI/settings/snapshots and exercises Windows tooltip Popup/Draw across dark/light/dark. It reuses R5 hardware evidence and does not repeat real encodes by default. Optional PowerShell -RunRealEncoding enables the existing short encoding suite. R6 Windows rendering validation is pending; user visual checks should hover long main/config/advanced/subtitle/LUT/status/file hints, inspect wrapping and theme switching, and compare the ordinary cap field borders/alignment.


## Final acceptance / release preparation — 2026-10-21

R6 Windows return Return_CQ_N7V4PK_R6_B4T8NZ_20261021_202112_422.zip: PASS 184, 0 failures. Native tooltip Popup/Draw, multiline layout and dark/light/dark theme switching passed; ordinary maximum-bitrate text fields align and save/reload. Original/current VBR signatures match. No real encodes repeated in R6; R5 PASS276 provides hardware evidence. User explicitly confirmed manual acceptance. Earlier R6-pending statements describe the state before this final return.

v5.0.20 preparation changes only metadata, documentation and cleanup/release layout. No feature code changed after R6 acceptance. Final formal Windows Dry Run and publication are maintainer actions; no commit/tag/push/release performed here.
