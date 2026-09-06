Universal Film Grain Toolkit
=============================

版本与命名
----------
所有独立脚本采用固定文件名，不再包含组件版本号。项目版本只体现在发布压缩包
文件名中；升级时请整体替换，避免新旧脚本混用。

目录结构
--------
FilmGrain_Universal_HEVC_AV1_CLI.bat
FilmGrain_Universal_HEVC_AV1_GUI.bat
FilmGrain_Config.ini
Utils\
    FilmGrain_Config.ps1
    FilmGrain_Config_Load.bat
    FilmGrain_Studio.ps1
    FilmGrain_Studio_Launcher.vbs
    FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
    FilmGrain_Subtitle_Prepare.ps1
    AV1_FilmGrain_Bake_for_Social_Upload.bat
    AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
    LUT_Preview_Batch_Gallery.bat
    Collect_BT709_LUTs_Conservative.bat
    FilmGrain_MOV_to_HEVC_Lossless_Cache.bat
_LUT_Tools\
    LUT_Gallery_Selector.ps1
    LUT_Preview_Batch_Gallery.ps1
    LUT_Reference_Default.jpg
    LUT_Reference_Current.jpg  （用户更换参考图后自动生成；发布包默认不存在）

根目录保留 CLI / GUI 两个 BAT 入口以及 FilmGrain_Config.ini。请保持 Utils、README 和
_LUT_Tools 文件夹的相对位置不变。

统一依赖与默认路径
------------------
以下路径统一保存在根目录 FilmGrain_Config.ini，并可通过 GUI“配置…”修改：
FFmpeg 目录：E:\EnCoder\FFMpeg\x64\bin（目录内同时使用 ffmpeg.exe 与 ffprobe.exe）
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs
GPU：NVIDIA GPU 自动探测（已验证 RTX 4080 与 T600 Laptop）

1. 整合主脚本
-------------
将一个或多个视频拖到：
FilmGrain_Universal_HEVC_AV1_CLI.bat

功能：
- AV1 Main10：NVENC 编码后由 grav1synth 写入 Film Grain metadata。
- HEVC Main10：使用扫描 Grain plate、Vulkan overlay 和 NVENC 编码。
- 共享速度、画幅、反交错、帧率、容器、LUT Gallery、码率和批量处理菜单。
- 默认编码方式：AV1。
- 默认输出容器：MP4（音频转 AAC 256k，不兼容的字幕、附件和数据流不写入）。
- MKV 模式仍可保留原始音频、字幕、附件和数据流。
- Cinematic Style 约 2.39:1；HEVC / AV1 均可选择“加黑边保留原分辨率”或“裁剪有效画面”。
- 自动反交错默认使用 BWDIF Vulkan；隔行 29.97i → 59.94p、25i → 50p。
- AV1 / HEVC 均可同时生成 H.264 上传版；NVENC P7 提供 6000 / 8000 / 15000 kbps。
- x264 Slow + tune grain + 2-pass 提供推荐 / 高质量 / 极高三档，并按实际输出 FPS + 分辨率
  自动联动码率；普通动态默认 0.5×，勾选“高动态视频”后为 1.0×。
- 字幕功能独立开关，可烧写进主 HEVC / AV1；同时生成 H.264 上传副本时也会继承字幕。
- 支持内嵌 / 同名外部 / 浏览外部文本字幕；默认 huiwen-mincho、69 号 1080p 基准，
  统一以最终输出底边定位，MarginV=5 px，并按输出宽度等比缩放。

LUT 选择中选择 Gallery 后，可搜索、分页、双击选择，或使用 Enter、
PageUp、PageDown 和 Esc。Resolve CUBE 兼容转换、tetrahedral 插值与
LUT 强度选择均保留。

Recent 与 Favorites 仅由 LUT Gallery 写入。Gallery 双击或确认 LUT 时立即
登记 Recent；Studio 的“最近使用”和“我的最爱”下拉列表平时只读。只有从
Studio“我的最爱”选择 LUT 并点击“开始编码”时，Studio 才调用 Gallery 的
无界面入口，由 Gallery 登记一次 Recent。Recent 保持去重、25 条上限及
同目录原子替换。

CLI 入口与 GUI 直接共用 Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
编码核心，因此 HEVC、AV1、反交错、画幅、帧率、LUT 与 Grain 逻辑同步。
输入文件名或目录名可包含 & 等 CMD 特殊字符。逐行素材的自动电影帧率继续
使用数值归一化；隔行素材启用自动反交错时优先使用 Field-rate ×2 输出。

Recent 写入发生在 Gallery 选择 LUT 的当下，与之后编码成功或失败无关；
写入失败时 Gallery 会显示具体错误。

Gallery 已统一为中文界面。页码使用只读下拉菜单，可显示当前页并直接选择
任意页面；上一页/下一页以及 PageUp/PageDown 均可在第一页与最后一页之间循环。

2. 社交网站转码工具
-------------------
将一个或多个带 AV1 Film Grain metadata 的视频拖到：
Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat

脚本通过 libdav1d 将 Film Grain 烘焙为实际像素，再编码为兼容性较高的
H.264/AAC MP4。输出文件名带 _UPLOAD_H264_GRAIN。

3. AV1 免重编码胶片颗粒工具
---------------------------
将一个或多个已有 AV1 视频拖到：
Utils\AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat

脚本不重新编码视频，只将 AV1 视频流复制到 IVF，使用 grav1synth 添加或
替换 Film Grain metadata，再封装为 MKV 或 MP4，并检查最终 Film Grain。
默认输出 MKV；MP4 模式会将音频转换为 AAC 320k。
Studio GUI 在单个 AV1 输入时也会提供“AV1 不重编码 · 添加/替换胶片颗粒”，
并自动禁用需要重新编码的视频处理功能；所选 AV1 会显示胶片颗粒为无、亮度或亮度 + 色度。

4. LUT 缩略图生成器
-------------------
运行 Utils\LUT_Preview_Batch_Gallery.bat。
LUT 根目录和参考图片/视频均有默认值，直接回车采用默认值，也可临时输入
其他路径。保留 Resolve CUBE 兼容、Junction/Symlink、防循环、1920 预览
及 Gallery index 功能。GUI“配置…”中的 LUT 根目录刷新还会统计 Gallery
预览完整度；存在缺失时可直接点击“创建缩略图”，只补缺失文件。
Gallery“更换参考图”后会将所选图片统一保存为 _LUT_Tools\LUT_Reference_Current.jpg。
之后 Gallery 全量重建、GUI 补建缺失预览以及独立预览生成器均优先使用 Current；
若 Current 尚不存在，则使用出厂 LUT_Reference_Default.jpg。这样删除部分预览后再次
补建，也会继续沿用最近一次选择的参考图。

5. 其他 Utils 工具
------------------
Collect_BT709_LUTs_Conservative.bat：保守筛选明确标注 BT.709/Rec.709
输入的 CUBE LUT，复制到 LUT 根目录的 BT.709 子目录并生成 CSV 报告。

FilmGrain_MOV_to_HEVC_Lossless_Cache.bat：统一 Cache 生成器。递归扫描
D:\Film_Grain，可选择生成原始分辨率、Vulkan bilinear 1920×1080，
或同时生成两种 HEVC Main10 Lossless Cache。校验统一比较实际 10-bit
YUV 样本；参考哈希来自与 NVENC 相同的同一帧流，避免 P010 低 6 位
填充差异造成假失败。已验证 RTX 4080 与 T600 Laptop。GUI“配置…”中的
Grain 根目录刷新会同时统计两类 Cache，缺失时可直接点击“生成高速缓存”；
GUI 以非交互模式调用同一 BAT，单独双击 BAT 时仍保留 1 / 2 / 3 菜单。

注意事项
--------
- GPU、驱动与 FFmpeg 能力由 FilmGrain_Hardware_Caps.ps1 自动探测，不需要
  为 RTX 4080 / T600 Laptop 手工切换 B-frame 或 Temporal AQ。
- 输出文件已存在时，脚本会跳过，避免覆盖现有结果。
- AV1 免重编码工具失败时默认保留临时目录，便于查看日志。
