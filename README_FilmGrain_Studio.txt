Film Grain Studio - 使用说明
============================

当前产品版本：v4.8.20；构建 Stable。

运行：双击 FilmGrain_Studio.exe，或把视频拖到 EXE；启动只导入，不自动编码。
请保持完整应用目录。首次运行按提示指定或安装 FFmpeg/FFprobe 与需要的依赖。
主编码选择 AV1、HEVC 或 x264；启用颗粒默认开启，可关闭后只做插帧、调色等处理。

AV1 不重编码模式仅添加/替换 Film Grain metadata，不重新编码视频；MP4 音频转 AAC 256k。
符合条件的 MP4 静态表和内置预设自动使用 Fast；ISO 核对色彩后选择 Fast 或 compatibility。
MKV、动态外部颗粒表、复杂裁剪时间轴等保留 compatibility。这是处理路径，不是失败。
预览按钮左键打开同帧原图/颗粒对照，右键设置自动快速路径及完整检查。
预览默认跳过开头近黑帧，最多八张采样图；HDR 显示转换不改正式输出。

完整日志位于 Logs；失败时保留 Task 日志和参数。日志目录按钮右键可清理 FGS 日志。
用户配置 FilmGrain_Config.ini 使用时生成，正式包不含个人设置。
功能说明见 README.md；版本变化见 CHANGELOG.md。
