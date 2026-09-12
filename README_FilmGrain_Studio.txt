Film Grain Studio - 使用说明
============================

当前正式稳定版：v4.6.2.1

这是 Universal Film Grain Toolkit 的图形前端。
CLI 与 GUI Bridge 共享同一套编码逻辑，并分别提供交互菜单和图形界面。
所有脚本采用固定文件名；项目版本只体现在发布压缩包文件名中。

启动方式
--------
双击：FilmGrain_Universal_HEVC_AV1_GUI.bat

也可以把一个或多个视频直接拖到 FilmGrain_Universal_HEVC_AV1_GUI.bat，
Studio 打开后会自动加入文件列表。

初版已实现
----------
- 多视频添加、拖放、移除和清空。
- 选中单个输入视频时显示视频/音频编码、码率、分辨率、帧率与时长。
- v4.6.2 新增 HDR Preserve：HEVC Main10 / AV1 Main10 对 HDR 输入保持 10-bit、BT.2020、PQ/HLG 与源本来存在的 HDR10 静态元数据；HEVC 显式强制 P010 10-bit，并在最终输出检查 HDR Primaries / Transfer / Matrix / Range。
- 源文件没有 Mastering Display / MaxCLL / MaxFALL 时不会伪造；YouTube 等只有 BT.2020 + PQ + 10-bit 的 HDR 输入可正常保持现有信号。
- 当前 HDR 输入自动旁路 OpenSVPFlow YUV420P8、H.264 x264 HDR 主输出、H.264 上传版和现有 SDR/BT.709 LUT；GUI 选中单个 HDR 文件后会同步灰显这些不兼容区域，切回 SDR 自动恢复。
- 编码方式：AV1 · grav1synth 胶片颗粒（默认）、HEVC · 扫描胶片颗粒、H.264 · x264 Grain（CPU / VBR 单次）；单个 AV1 还可选择不重编码添加/替换胶片颗粒。
- MP4（默认）与 MKV。
- FAST（默认）、Standard；硬件探测通过时 AV1 还可选 UHQ。
- AV1 / HEVC / H.264 共用自动码率策略：按分辨率 + 最终 FPS + 高动态实时推荐并直接显示具体 kbps；取消“自动”后可手动输入且不被覆盖。
- 自动反交错（默认）：BWDIF Vulkan；可选 BWDIF CUDA、W3FDIF Complex 或关闭。
- 隔行素材自动 Field-rate：29.97i → 59.94p、25i → 50p；逐行素材自动旁路。
- 逐行素材使用自动电影帧率或保持源帧率。
- Cinematic Style（默认开启）：HEVC / AV1 均可选择加黑边保留原分辨率，
  或裁剪为约 2.39:1 有效画面；默认加黑边，适合后期字幕。
- NVIDIA GPU / 驱动 / FFmpeg 能力自动探测；界面同时显示驱动与 FFmpeg 版本；能力状态显示“配置：已适配 / 已缓存”；已验证 RTX 4080 与 T600 Laptop。
- `_OpenSVPFlow\00_Setup.bat` 继续负责首次安装；`01_Update_OpenSVPFlow.bat` 可主动更新上游最新版，更新前后执行 CPU/GPU smoke test并支持自动备份、失败回滚。
- 右上角“配置…”统一管理 FFmpeg 目录、grav1synth、Grain 与 LUT 路径，并写入根目录 FilmGrain_Config.ini。
- 四项路径均提供“浏览…”与 ↻ 刷新：浏览选择后立即检测；手工输入后点击 ↻ 检测。
- FFmpeg 显示 ffmpeg.exe / ffprobe.exe 版本；grav1synth 显示版本；Grain 同时统计原始 MOV、原分辨率/1080p Cache，并可补齐缺失缓存；LUT 同时统计 .cube 与 Gallery 预览图，并可创建缺失缩略图。
- 保存时只做快速路径存在性检查，不重新运行版本检测或递归扫描。
- AV1 胶片颗粒元数据支持胶片预设 / 感光度 ISO、胶片格式、胶片型号，以及亮度 + 色度。
- AV1 另支持现成 .tbl / .txt Grain Table；_AV1_Grain_Tables 按 720p / 1080p / 1440p / 2160p 分类，GUI 默认只显示与源视频最接近的档位，勾选右侧无文字复选框可显示全部。
- v4.6.2 正式包保留当前整理的一组公开 Grain Table；主要来源为 Boulder08/chunknorris 与 nekotrix/AV1-Photon-Noise-Tables，来源说明见 `_AV1_Grain_Tables\README.txt`。
- AV1 / HEVC 均可选“同时生成 H.264 上传版”：上传副本固定使用 x264 Grain / High 8-bit，默认 Faster + tune grain + VBR 单次，并与主线共享 Preset / Pass 设置，拥有独立自动/手动码率；OpenSVPFlow 60 fps 插帧开启时同样可用，且不会重复运行插帧。v4.6.2.1 修复手动上传码率被 StudioBridge 二次校验后误拒绝的问题，继续统一使用平均码率 / 3× maxrate / 6× bufsize。
- x264 主线与上传副本共用分辨率 + 最终 FPS + 高动态码率策略，VBV 固定为 maxrate=平均×3、bufsize=平均×6；全局“高动态”还会按能力启用 NVENC 运动优化。
- 高级设置“编码”页提供 H.264 High10（实验），默认关闭；仅作用于 H.264 x264 Grain 主输出，不作用于兼容性优先的上传副本。
- 字幕功能已独立于 H.264 上传版：可直接烧写进主 AV1 / HEVC / H.264；如同时生成 H.264 上传副本，
  H.264 版本也会继承同一套字幕。
- 默认字幕为 huiwen-mincho、1080p 基准字号 69、白字黑边阴影；统一以最终输出画面底边定位，
  默认 MarginV=5 px（1920 宽基准），按输出宽度自动缩放，并支持 GB18030 中文字幕。
- HEVC Grain 根目录递归扫描：界面仅显示电脑上实际存在的 .mov 颗粒片。
- Grain 列表支持选择目录后自动刷新，也可点击 ↻ 手动刷新。
- HEVC Grain 4 档强度，并自动匹配同名 1080p / 原分辨率 Lossless Cache；配置界面可直接生成缺失 Cache。
- CLI 与 GUI 均支持输入文件名或目录名包含 & 等 CMD 特殊字符。
- 拖放视频启动时，CMD 与 PowerShell 控制台均不驻留任务栏。
- 主界面直接读取 Gallery 最近使用与我的最爱，各显示最多 25 个 LUT，并复用缩略图即时预览。
- 直接调用现有 LUT Gallery，支持 25% / 50% / 75% / 100% LUT 强度；配置界面可直接补齐缺失缩略图。
- v4.6.2 LUT Gallery 新增“智能过滤”：将 LUT 分类为 Technical / Combined / Creative / Keep，只隐藏高置信度纯 Technical；同一创意 Look 的多种 Log 输入适配版本通过 Creative Family 识别为 Combined 并保留。
- 智能过滤只改变 Gallery 显示，不移动、不删除、不改名 `.cube`；扫描报告写入 `<LUT_ROOT>\_LUT_PREVIEWS\_LUT_SMART_FILTER_REPORT.csv`。
- Gallery“更换参考图”后会将当前参考图保存为 _LUT_Tools\LUT_Reference_Current.jpg；
  Gallery 全量重建、配置界面补建缺失缩略图及独立预览生成器均优先复用该图片，
  不存在时才回退 LUT_Reference_Default.jpg。Current 文件由用户首次更换参考图后自动生成。
- 内置任务日志、当前阶段、当前编码阶段进度、fps / speed / ETA 与取消任务。
- Studio 模式使用 FFmpeg 结构化实时进度，并兼容原有 -stats 文本解析。

默认设置
--------
编码：AV1 Main10 + grav1synth
容器：MP4 / AAC 256k
速度：FAST（p5 / qres / lookahead 16）
码率：自动推荐并显示；按编码器 / 分辨率 / 最终 FPS / 高动态联动
画幅：Cinematic Style 开启 / 默认加黑边保留原分辨率
反交错：自动 / BWDIF Vulkan
帧率：隔行 Field-rate ×2；逐行自动电影帧率
GPU：自动检测
AV1 Grain：Classic35 / Fujifilm Eterna 250D
LUT：关闭

统一路径配置
------------
路径统一保存在根目录 FilmGrain_Config.ini，可由 GUI 右上角“配置…”修改。
FFmpeg 目录：E:\EnCoder\FFMpeg\x64\bin（目录内同时使用 ffmpeg.exe 与 ffprobe.exe）
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs
INI 使用 UTF-8 无 BOM；GUI、CLI、StudioBridge 与相关 Utils 工具读取同一配置。

文件关系
--------
FilmGrain_Universal_HEVC_AV1_GUI.bat
    GUI 入口；解决 PowerShell 执行策略与 STA 启动问题。

FilmGrain_Config.ini
    全项目共享的外部路径配置。

Utils\FilmGrain_Config.ps1 / FilmGrain_Config_Load.bat
    PowerShell / CMD 共用的配置读取层。

Utils\FilmGrain_Studio.ps1
    WinForms 图形界面。

Utils\FilmGrain_Studio_Launcher.vbs
    无控制台启动器；异步启动 WinForms 后立即退出，不保留 CMD 任务栏窗口。

Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
    GUI / CLI 共用编码核心；GUI 通过 FG_* 参数调用，CLI 使用交互模式。

Utils\FilmGrain_Subtitle_Prepare.ps1
    H.264 上传版文本字幕识别、字符集处理、ASS 样式与位置准备。

FilmGrain_Universal_HEVC_AV1_CLI.bat
    CLI 入口，直接进入同一 StudioBridge 核心的交互模式，可独立拖放使用。

请保持两个入口 BAT、Utils、_LUT_Tools 与 _AV1_Grain_Tables 文件夹的相对位置不变。

历史变更
--------
正式发布历史统一记录在根目录 CHANGELOG.md。
