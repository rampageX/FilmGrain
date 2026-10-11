# .NET Framework 成品构建要求

目标是 Windows .NET Framework，不能用 Mono 的默认运行库作为发布 EXE 的 API 引用基线。即使 C# 5 编译成功、Mono 自动检查通过，Mono 的 String 单字符重载、参数为空的 TrimStart/TrimEnd、Array.Empty 或基础类型程序集归属也可能与 Framework 不同。

Windows：现有 build_NET_Preview.bat 使用 Windows 自带 Framework csc，更新 dotnet/build 与根目录两处成品。请先关闭程序，检查脚本返回码。

非 Windows 预构建：本轮使用 Microsoft.NETFramework.ReferenceAssemblies.net45 1.0.3；编译启用 -nostdlib，并显式引用该包中的 mscorlib、System、System.Core、System.Drawing、System.Windows.Forms、System.Web.Extensions。所有产品 C# 源文件都使用同一引用集。不要混入 Mono 默认的同名 DLL。

交付前必须对实际成品进行微软参考程序集元数据校验。ReferenceApiAudit_8BBMST.cs 是开发审计源码，需要 Mono.Cecil 0.11.6；不能仅在 Mono 运行时调用 ResolveMember 来代替此项，因为 Mono 会统一到自身 mscorlib。本轮报告位于 Tests_Shared/reference_new_8BBMST.log。

Windows 无需上述开发审计依赖：Run_StartupFix_Checks_4GP6Z6.cmd（旧 8BBMST 入口已转发）首先对包内两处预编译成品做当前 Windows Framework API 解析与配置/语言读取检查，再运行启动检查与既有回归。此检查不替代 Windows GUI/GPU 实测。

检查工具 4GP6Z6 直接解析泛型元数据，使用 Tests_Shared 内的 Mono.Cecil net40 DLL；不再逐个无上下文调用 Module.ResolveMember。Windows 模式读取当前 Framework 的物理 DLL/GAC，开发模式可指定微软参考程序集目录；不能把 Mono 当前运行库解析成功当成 Windows API 校验。依赖、许可和哈希见 Tests_Shared/ThirdPartyAudit_4GP6Z6.md。

4FH3JQ：WinExe 的启动诊断及硬件查询协议通过 Console.OpenStandardOutput 和无 BOM UTF-8 StreamWriter 写入继承管道，不设置 Console.OutputEncoding，避免无控制台进程调用控制台代码页 API 引发句柄无效。新检查入口 Run_StartupFix_Checks_4FH3JQ.cmd，旧 8BBMST / 4GP6Z6 入口转发。正式版本仍 v4.8.13，产品测试标识为 4FH3JQ。

v4.8.14 发布准备：Program.ReleaseVersion 是 .NET 版本唯一显示来源；正式 TestBuild 为 Stable。发布核心的 sync_native_version.ps1 同步版本，build_release.ps1 校验 Native 与 Legacy 版本一致。Windows 编译清单覆盖全部 39 个 C# 文件，两处 EXE 必须 SHA256 相同。Tests_Shared 仅供本地检查，正式 Stable ZIP 排除；本地使用 Run_ReleaseReady_Checks_HKW0ES.cmd，发布 Dry Run 使用 release.bat --dry-run 4.8.14。
