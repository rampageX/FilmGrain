# Film Grain Studio — CHANGELOG

本文件记录正式发布版本的主要变化。`README.md` 只维护当前版本功能与使用说明，不再重复版本更新摘要。

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
