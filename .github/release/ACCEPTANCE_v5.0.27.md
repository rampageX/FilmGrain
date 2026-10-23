# v5.0.27 发布验收与交接

## 本轮已验收

沿用已发布 v5.0.26 的当前源码，累计 HDRFRUC R5 和 MultiPreset R1；未从 GitHub 或旧包覆盖当前修改。

- Windows HDR R4 回传 `Return_HDRFRUC_H7M4QX_R4_M8Q2VK_20261023_071435_769.zip`：27 项通过。真实 P010 插值色阶精度；源样片、FRUC FFV1 中间文件、HEVC/AV1 输出的 10bit、PQ、BT.2020；插帧输出 48fps/48帧通过。
- HDR R5 主窗口旧状态提示修正后，维护者确认 BT.2020 / HLG、数字颗粒及 AV1 内置颗粒实际视频观感与音画同步正常。
- Windows 多选 R1 回传 `Return_Presets_R8M3VN_Multi_R1_F8Q3VN_20261023_074856_398.zip`：115 项通过、无 FAIL；维护者人工测试 OK。覆盖批量 Simple 各目标高级参数保留、Full覆盖、未选文件隔离、第二目标校验失败不部分提交、字幕目标绑定、深拷贝/冻结快照隔离、菜单目标数及无选择拒绝。原有暗色 UI、精确布局和快捷提示回归通过，预设操作不运行硬件检测器。
- 维护者在本轮确认此前 CQ+FRUC、HDR→SDR+FRUC、三遍+FGSIM+FRUC、特殊时间轴同步已补做实机测试。依据为用户人工反馈，不等同于全部参数组合自动矩阵。

## 验证边界

静态连续渐变在插值中出现最大3码值偏差，突变图边缘最大54，原帧误差0；作为光流质量诊断保留，不宣称逐像素恒等或无伪影。PQ/HLG色彩与10bit验证不代表母版亮度/CLL、HDR10+或Dolby Vision元数据完整保留。T600 Vulkan FRUC限制、隔行FRUC与各编码器原有限制不变。

## 文档与发布

README依据源码更新H.264 NVENC颗粒方式、10%–100% Grain Table强度、HEVC独立SFE及H.264 NVENC普通/高动态码率列；补充HDR FRUC和多选用法，修正静态元数据过度承诺。版本、TXT标识、CHANGELOG、Release Notes、Stable基线同步5.0.27。

准备包不含预构建EXE、用户INI、UserPresets、quick.json或临时测试。五份初始预设继续按显式白名单发布。升级不覆盖用户修改。Clean_Preset_Tests.cmd沿用先隔离自测再清理流程，现增加Tests_HDRFRUC_H7M4QX及对应Return ZIP，其他清理范围不变；联接只删除链接，不遍历目标。PS1带UTF-8 BOM，BAT/CMD无BOM，无中文弯引号。

维护者步骤：

1. 关闭FGS，覆盖发布准备源码包。
2. 运行 `Clean_Preset_Tests.cmd`，先通过Windows隔离自测再清理本轮预设/HDR测试。
3. 运行 `release.bat --dry-run 5.0.27`，检查Windows编译、元数据和打包。
4. 维护者确认后自行执行 `release.bat 5.0.27`。

本环境未运行Windows正式Dry Run或PowerShell清理自测，未提交、推送、打Tag、创建分支/PR或操作远程发布。正式Release使用现有Windows runner；GitHub仅Stable与SHA256，本地流程生成Stable、Source及各自校验文件。此包为发布准备源码，不是正式Release。

## 本地发布版本核对

Framework API audit: 2442 resolved, 0 failures; Microsoft reference assemblies.；HDR逻辑10项、预设数据66项通过。产品版本5.0.27，文件/程序集5.0.27.0；两份候选EXE一致，SHA256 `e3c7c79f8e6d335c3e4f5b2097bad5b1daf8846afcc17c64bc88ee18cb2b98db`。候选只用于本地核对，不随准备包发布。正式Windows构建重新生成。
