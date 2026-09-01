Universal Film Grain Toolkit - FINAL v32
=========================================

目录结构
--------
FilmGrain_Universal_HEVC_AV1_v32_Grav1synth_LUTGallery.bat
AV1_FilmGrain_Bake_for_Social_Upload_v1.3.bat
AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode_v1.1.bat
LUT_Preview_Batch_v2.3_Gallery.bat
_LUT_Tools\
    LUT_Gallery_Selector.ps1
    LUT_Preview_Batch_v2.3_Gallery.ps1
    LUT_Reference_Default.jpg

请保持以上 BAT、README 和 _LUT_Tools 文件夹的相对位置不变。

固定依赖与默认路径
------------------
FFmpeg：E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe
FFprobe：E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs
目标显卡：RTX 4080（B-frames 与 Temporal AQ 默认开启）

1. 整合主脚本 v32
-----------------
将一个或多个视频拖到：
FilmGrain_Universal_HEVC_AV1_v32_Grav1synth_LUTGallery.bat

功能：
- AV1 Main10：NVENC 编码后由 grav1synth 写入 Film Grain metadata。
- HEVC Main10：使用扫描 Grain plate、Vulkan overlay 和 NVENC 编码。
- 共享速度、画幅、帧率、容器、LUT Gallery、码率和批量处理菜单。
- 默认编码方式：AV1。
- 默认输出容器：MP4（音频转 AAC 320k，不兼容的字幕、附件和数据流不写入）。
- MKV 模式仍可保留原始音频、字幕、附件和数据流。
- Cinematic style 画幅约为 2.39:1。

LUT 选择中选择 Gallery 后，可搜索、分页、双击选择，或使用 Enter、
PageUp、PageDown 和 Esc。Resolve CUBE 兼容转换、tetrahedral 插值与
LUT 强度选择均保留。

2. 社交网站转码工具 v1.3
-----------------------
将一个或多个带 AV1 Film Grain metadata 的视频拖到：
AV1_FilmGrain_Bake_for_Social_Upload_v1.3.bat

脚本通过 libdav1d 将 Film Grain 烘焙为实际像素，再编码为兼容性较高的
H.264/AAC MP4。输出文件名带 _UPLOAD_H264_GRAIN。

3. AV1 免重编码胶片颗粒工具 v1.1
--------------------------------
将一个或多个已有 AV1 视频拖到：
AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode_v1.1.bat

脚本不重新编码视频，只将 AV1 视频流复制到 IVF，使用 grav1synth 添加或
替换 Film Grain metadata，再封装为 MKV 或 MP4，并检查最终 Film Grain。
默认输出 MKV；MP4 模式会将音频转换为 AAC 320k。

4. LUT 缩略图生成器 v2.3
-----------------------
运行 LUT_Preview_Batch_v2.3_Gallery.bat。
LUT 根目录和参考图片/视频均有默认值，直接回车采用默认值，也可临时输入
其他路径。保留 Resolve CUBE 兼容、Junction/Symlink、防循环、1920 预览
及 Gallery index 功能。

注意事项
--------
- RTX T600 Laptop 不支持当前 RTX 4080 默认参数时，请在整合主脚本中将
  ENABLE_BF 和 ENABLE_TEMPORAL_AQ 都设为 0。
- 输出文件已存在时，脚本会跳过，避免覆盖现有结果。
- AV1 免重编码工具失败时默认保留临时目录，便于查看日志。
