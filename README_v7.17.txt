LUT Gallery + AV1 FilmLook - FINAL v7.17
=============================================

目录结构
--------
AV1_NVENC_grav1synth_FilmGrain_FINAL_v7.17_LUTGallery.bat
LUT_Preview_Batch_v2.3_Gallery.bat
_LUT_Tools\
    LUT_Gallery_Selector.ps1
    LUT_Preview_Batch_v2.3_Gallery.ps1
    LUT_Reference_Default.jpg

日常只需要运行前两个 BAT；_LUT_Tools 是内部支持目录，请保持和两个 BAT 放在同一级目录。

当前默认值
----------
LUT 根目录：E:\Adobe Portable\LUTs
FFmpeg：E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe
FFprobe：E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe

缩略图生成器
------------
运行 LUT_Preview_Batch_v2.3_Gallery.bat。
LUT 根目录和参考图片/视频都有默认值；直接回车采用默认值，输入新路径可临时覆盖。
继续保留：Resolve CUBE 兼容处理、Junction/Symlink、防循环、1920 预览、Gallery index。

转码脚本
--------
把视频拖到 v7.17 BAT。
LUT 选择时选 [1] 打开可视化 Gallery。
支持搜索、分页、双击选择、Enter 确认、PageUp/PageDown 翻页、Esc 取消。
选中 LUT 后仍沿用原有 Resolve LUT 兼容转换、tetrahedral 插值和强度选择。

v7.17 清理内容
--------------
- 正式采用 PictureBox Gallery，删除对旧 ListView/ImageList 方案的依赖。
- Gallery 内部组件统一放到 _LUT_Tools。
- 修复双击 LUT 返回 BAT 时控制台偶尔出现 False：Focus() 返回值显式丢弃，同时 BAT 屏蔽 Selector 普通 stdout。
- LUT 根目录统一默认 E:\Adobe Portable\LUTs。
- Gallery 小图缓存使用新的 _GALLERY_THUMBS_v3_240x135，避免复用测试版缓存。
- 已生成的原始 *_preview.jpg 不需要重做；v3 小缓存会在浏览时自动建立。


UI tuning v7.17a:
- Default Gallery window: 1500 x 1000
- 25 LUTs per page (5 x 5 at the default window size)
- Thumbnail/card dimensions unchanged
