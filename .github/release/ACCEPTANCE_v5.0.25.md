# v5.0.25 发布验收与交接

## 验收基线

以 Working_Source_20261022_191748_637.zip 为唯一源码基线，累计至 R11_T6V3KP。未使用远程源码覆盖当前修改。

Windows 返回 Return_Presets_R8M3VN_R11_T6V3KP_20261022_222811_750.zip：92 项全部通过，无 FAIL；维护者人工验收通过。三种字号下按钮、下拉框和勾选框文字检查通过；六组下拉框上/左/下内框扫描白色像素均为 0。快捷摘要真实可见、位于菜单之上、不抢激活、不关闭菜单。

数据及编辑链路验证覆盖：简单/完整格式读写、自动/手动值、高级参数覆盖范围、统一设置保存/应用、独立文件隔离、深拷贝、快捷绑定的重命名和删除、资源缺失、无效参数、缓存硬件不兼容、应用失败无部分修改、损坏文件/版本、冻结快照隔离。打开/应用预设不运行硬件检测器。

本轮不是全编码矩阵或不同机器的所有能力实测。新功能复用原执行链路，现有执行前校验仍保留。未重新宣称 Windows 混合 DPI、所有驱动和全部视频组合均已覆盖。

## 发布准备

- 版本常量、版本文件、README 版本标识、TXT 使用说明、CHANGELOG、Release Notes、Stable 基线同步为 5.0.25；生产功能代码与 R11 一致，仅版本常量更新。
- README 仅写使用方式、边界和资源存储；验收记录留在本文。
- 本轮测试目录/返回包及用户预设从源码 staging 和正式打包排除。随附初始预设仅五份显式白名单；种子临时目录也排除。
- Clean_Preset_Tests.cmd 先运行隔离清理自测再删除 Tests_Presets_R8M3VN、Tests_Cleanup_Presets_R8M3VN 和本轮 Return ZIP；不删除个人预设、快捷绑定、初始预设、INI、依赖、资源或其他项目测试。底层 PS1 不带参数时只列计划。Windows junction 自测由 CMD 在用户机器执行，本环境未运行 PowerShell。
- 清理脚本使用 LiteralPath/IO API，路径参数整体引用，沿用不跟随目录联接的删除实现；PS1 UTF-8 BOM，CMD 无 BOM。
- GitHub Release 继续仅附 Stable ZIP 和对应 SHA256；Windows runner 与本地正式发布仍生成 Stable、Source 及各自 SHA256。

## 维护者操作

1. 关闭 FGS，覆盖本发布准备包（不含本机配置、用户预设及 Linux 预构建 EXE）。
2. 双击 Clean_Preset_Tests.cmd 清理本轮旧测试文件；按需单独备份 UserPresets。
3. 运行 `release.bat --dry-run 5.0.25`，核对 Windows 编译、元数据及打包检查。
4. Dry Run 成功后，由维护者运行 `release.bat 5.0.25` 完成正式发布。

本环境未运行 Windows 正式 Dry Run，未提交、创建分支/PR、推送、打 Tag 或操作远程发布。准备包不是正式 Stable/Source Release；正式产物须由既有 Windows 发布流程生成。

## 本地版本构建记录

产品版本 5.0.25，文件/程序集版本 5.0.25.0。Microsoft Framework API 审计 2424 项解析、0 失败，两份 EXE 字节一致。候选 EXE SHA256：`6e7fbfff2b8304bc85c83791ba0c404b6eecef2acab4839f53d29b68c42ca2f9`。此为本地验证用候选，未作为正式 Release 或交付 EXE。

静态核对通过：初始预设白名单、用户预设/种子目录/测试返回包排除、清理范围和保护哨兵、版本同步、GitHub Release 附件及本地 Stable/Source 校验产物规则。PowerShell 清理自测和正式发布 Dry Run 仍由 Windows 流程执行。
