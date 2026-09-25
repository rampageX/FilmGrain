# Film Grain Studio v4.8.10

* 正式 ZIP 增加可直接运行的 .NET 图形界面预览版 `dotnet\build\FilmGrain_Studio_NET_Preview.exe`，并附带 C# 源码和本地编译脚本；原 `FilmGrain_Universal_GUI.bat` 仍为正式入口。
* .NET 界面恢复 AV1 / HEVC / x264 编码、AV1 不重编码更换颗粒、媒体信息及 AV1 颗粒检查、字幕、LUT 图库与色彩纠正、路径和高级设置；继续复用已验证的编码 BAT、辅助工具与统一配置。

### 改进与修复

* .NET 界面为 HEVC + FGSIM 提供 VBR、Standard CQ27 / QP18-26、High Quality CQ23 / QP18-24 三种码率选择；选用 CQ 时禁用不适用的自动码率和高动态控件。
* 编码启动前检查临时目录和输出目录的写入权限与可用空间；找不到完整程序目录时显示明确的启动位置提示。
* .NET 保存 `FilmGrain_Config.ini` 时先写同目录临时文件，再替换原文件；配置键继续与正式 GUI 共用。
* Windows 发布构建从已提交的 .NET 源码编译预览 EXE，并在生成 ZIP 前检查文件存在；dry-run 使用同一构建核心，避免复用本机旧的编译产物。
