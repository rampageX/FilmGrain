# v5.0.26 发布验收与交接

## 已验收范围

以既有 Working_Source 为唯一基线，沿用已发布 v5.0.25，累计应用 R12/R13 界面修正，未从远程或旧包覆盖源码。

Windows 回传 Return_Presets_R8M3VN_R13_P5N8VK_20261022_232639_316.zip：105 项全部 PASS，无 FAIL；维护者目测确认通过。日志有 CS0649 未赋值字段警告，无编译错误。

- 1200/1500/1800 窗口宽度、中文与简短英文标签下：独立配置与添加文件左边缘一致；管理按钮与移除所选左边缘、完整尺寸一致；选择器左边缘与清空一致、右边缘与 AV1 烘焙一致。
- 自动尺寸勾选框在三种字号下文字可容纳且启用/禁用边界稳定；颗粒按钮、下拉框、勾选框文字及下拉框白边检查通过。
- 用途说明原生白边消除、标题间距、深浅主题切换和实际边缘扫描通过。
- 简单/完整预设、统一/独立配置、复制引用隔离、错误应用原子性、资源/硬件/版本校验、任务快照和快捷提示回归通过。预设操作不运行硬件检测器。

这些检查不是全编码矩阵或所有 DPI/驱动实测。保留现有执行前验证。

## 发布准备与维护者操作

版本、README/TXT 标识、CHANGELOG、Release Notes 和 Stable 基线同步到 5.0.26。README 保留使用说明，测试记录留在本文。编码核心不变。

准备包为完整源码，不含预构建 EXE、用户 INI、UserPresets、快捷绑定、临时测试和回传 ZIP。五份初始预设继续按显式白名单随附，升级不覆盖用户预设。

1. 关闭 FGS 后覆盖源码准备包。
2. 如需清理旧测试，运行 Clean_Preset_Tests.cmd；该入口先执行隔离自测，不删除个人预设、绑定、资源和配置。清理逻辑沿用 v5.0.25。
3. 运行 `release.bat --dry-run 5.0.26`，核对 Windows 编译、元数据及打包检查。
4. 维护者确认后自行运行 `release.bat 5.0.26`。

本环境未运行 Windows 正式 Dry Run 或 PowerShell 清理自测，未提交、推送、打 Tag、创建分支/PR 或操作远程发布。正式 Release 使用既有 Windows runner；GitHub Release 仅 Stable 和 SHA256，本地流程生成 Stable、Source 及各自校验文件。准备包不等同正式 Release。

## 本地版本构建验证

Framework API audit: 2437 resolved, 0 failures; Microsoft reference assemblies.；数据检查 66 项通过。两份 EXE 字节一致，版本 5.0.26 / 5.0.26.0。候选 SHA256：`7bf0efcc43a76f3f96c852b8772e90b36ec85c9ab24e98509e32cdeded11f156`。本地候选仅用于验证，不随准备包交付；正式 Windows 构建会重新生成。
