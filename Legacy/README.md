# Legacy 独立工具归档 — J9M4TX

此目录保留旧 CLI、旧 PowerShell GUI、Bridge 编码 BAT、不重编码 BAT、旧探测/准备助手和旧依赖安装脚本，共 29 个脚本。它们不再是根目录 FilmGrain_Studio.exe 的执行后端。

- 旧交互 CLI：Legacy/FilmGrain_Universal_CLI.bat。
- 旧 GUI：Legacy/FilmGrain_Universal_GUI.bat → Utils/FilmGrain_Studio_Launcher.vbs → Utils/FilmGrain_Studio.ps1。
- 这些独立工具仍读取应用根目录的配置、Lang、FGSIM 纹理/模板、LUT 参考图和 OpenSVPFlow 运行资源。路径已适配归档位置；本批未重写这些旧工具，也未声称其 Windows 运行通过。
- .NET 主程序默认且仅允许 Native。准备失败显示原因并停止，不尝试此目录的 BAT/PS1/VBS。可以移走整个 Legacy，不要移走共享资源或真正的外部依赖。
- Python/VapourSynth、SVP .vpy、插件，以及 FGSIM 模板/纹理属于实际算法依赖，继续保留在原路径。
- dotnet/build_NET_Preview.bat 是源码构建工具，Tests_Shared 内脚本是本地测试工具，均不属于应用运行时 Legacy 依赖。

使用 Patch 时，请运行 Tests_Shared/Run_NativeOnly_J9M4TX.bat，它会先整理旧路径脚本；用户修改过的旧副本保存在 Legacy/UserPreserved。只需要整理而不编码时，可运行 Tests_Shared/Arrange_Legacy_J9M4TX.ps1。
