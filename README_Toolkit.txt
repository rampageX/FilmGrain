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
Utils\
    FilmGrain_Studio.ps1
    FilmGrain_Studio_Launcher.vbs
    FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
    AV1_FilmGrain_Bake_for_Social_Upload.bat
    AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
    LUT_Preview_Batch_Gallery.bat
    Collect_BT709_LUTs_Conservative.bat
    FilmGrain_MOV_to_HEVC_Lossless_Cache.bat
    FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat
_LUT_Tools\
    LUT_Gallery_Selector.ps1
    LUT_Preview_Batch_Gallery.ps1
    LUT_Reference_Default.jpg

根目录只保留 CLI 与 GUI 两个 BAT 入口。请保持 Utils、README 和
_LUT_Tools 文件夹的相对位置不变。

固定依赖与默认路径
------------------
FFmpeg：E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe
FFprobe：E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs
目标显卡：RTX 4080（B-frames 与 Temporal AQ 默认开启）

1. 整合主脚本
-------------
将一个或多个视频拖到：
FilmGrain_Universal_HEVC_AV1_CLI.bat

功能：
- AV1 Main10：NVENC 编码后由 grav1synth 写入 Film Grain metadata。
- HEVC Main10：使用扫描 Grain plate、Vulkan overlay 和 NVENC 编码。
- 共享速度、画幅、帧率、容器、LUT Gallery、码率和批量处理菜单。
- 默认编码方式：AV1。
- 默认输出容器：MP4（音频转 AAC 256k，不兼容的字幕、附件和数据流不写入）。
- MKV 模式仍可保留原始音频、字幕、附件和数据流。
- Cinematic style 画幅约为 2.39:1。

LUT 选择中选择 Gallery 后，可搜索、分页、双击选择，或使用 Enter、
PageUp、PageDown 和 Esc。Resolve CUBE 兼容转换、tetrahedral 插值与
LUT 强度选择均保留。

Recent 与 Favorites 仅由 LUT Gallery 写入。Gallery 双击或确认 LUT 时立即
登记 Recent；Studio 的“最近使用”和“我的最爱”下拉列表平时只读。只有从
Studio“我的最爱”选择 LUT 并点击“开始编码”时，Studio 才调用 Gallery 的
无界面入口，由 Gallery 登记一次 Recent。Recent 保持去重、25 条上限及
同目录原子替换。

CLI 与 GUI 的 HEVC、AV1、grav1synth、重封装和上传命令均直接执行，不把
完整命令存入 BAT 变量后再次展开，因此输入文件名或目录名可包含 & 等 CMD
特殊字符。自动电影帧率也已统一使用数值归一化：约 29.97/59.94 fps 输出
23.976 CFR，约 30/60 fps 输出 24 CFR。

Recent 写入发生在 Gallery 选择 LUT 的当下，与之后编码成功或失败无关；
写入失败时 Gallery 会显示具体错误。

Gallery 已统一为中文界面。页码框支持输入具体页码后按 Enter 跳转；
上一页/下一页以及 PageUp/PageDown 均可在第一页与最后一页之间循环。

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

4. LUT 缩略图生成器
-------------------
运行 Utils\LUT_Preview_Batch_Gallery.bat。
LUT 根目录和参考图片/视频均有默认值，直接回车采用默认值，也可临时输入
其他路径。保留 Resolve CUBE 兼容、Junction/Symlink、防循环、1920 预览
及 Gallery index 功能。

5. 其他 Utils 工具
------------------
Collect_BT709_LUTs_Conservative.bat：保守筛选明确标注 BT.709/Rec.709
输入的 CUBE LUT，复制到 LUT 根目录的 BT.709 子目录并生成 CSV 报告。

FilmGrain_MOV_to_HEVC_Lossless_Cache.bat：递归扫描 D:\Film_Grain，生成
同分辨率 HEVC Main10 无损缓存，并验证解码后的 P010 像素哈希。

FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat：递归扫描 D:\Film_Grain，
经 Vulkan 缩放到 1920×1080 后生成 HEVC Main10 无损缓存并验证。

注意事项
--------
- RTX T600 Laptop 不支持当前 RTX 4080 默认参数时，请在整合主脚本中将
  ENABLE_BF 和 ENABLE_TEMPORAL_AQ 都设为 0。
- 输出文件已存在时，脚本会跳过，避免覆盖现有结果。
- AV1 免重编码工具失败时默认保留临时目录，便于查看日志。
