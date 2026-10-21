# Film Grain Studio — Windows 构建

当前发布目标为 v5.0.18 / Stable。产品版本为 5.0.18，文件和程序集版本为 5.0.18.0；由 `.github/release/sync_native_version.ps1` 同步。

## 本机构建

在 Windows 运行 `dotnet\build_NET_Preview.bat`。脚本使用 Windows .NET Framework 的 C# 编译器，构建所有产品源码；Framework API 审计通过后更新根目录 `FilmGrain_Studio.exe` 与 `dotnet/build/FilmGrain_Studio_NET_Preview.exe`。两处 EXE 必须一致。构建工具不参与应用运行时。

## 界面检查

构建后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File dotnet\BuildTools\Test_UI_Startup.ps1
```

独立 STA 进程加载实际应用，检查主窗口构造、四种编码切换、首次显示和高级设置三页的控件边界、说明顺序、推荐提示。测试以取消关闭，不保存设置、不启动媒体编码或依赖安装。结果和临时文件在 `dotnet/BuildTools/UiAudit`，发布流程会排除。

## 正式发布

先运行 `release.bat --dry-run 5.0.18`，通过后运行 `release.bat 5.0.18`。本机和 Windows runner 共用 `.github/release/build_release.ps1` 与 `release_layout.ps1`，从源码重建、审计并校验版本资源。准备包和 Dry Run 均不代表已经发布。

## 编译基线与 API 审计

非 Windows 环境只能以 Microsoft .NET Framework 参考程序集检查 C# 语义和 API；不能用 Mono 或现代 .NET 默认程序集代替 Windows Framework 引用。成品的类型、方法、字段和泛型引用由 `BuildTools/FrameworkApiAudit.cs` 与 `Test_Framework_Api.ps1` 检查。审计使用固定版本 Mono.Cecil，许可与完整性检查随工具保留；失败时不跳过门禁。Windows GUI/GPU 实测仍需 Windows 环境。
