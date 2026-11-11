# OpenSVPFlow 安装、升级与回退（FGS v5.0.64）

## 新安装

1. 解压完整 Stable 包，保持 `FilmGrain_Studio.exe`、`_OpenSVPFlow` 和其它资源目录的相对位置；勿只复制 EXE。正式包自带 `FilmGrain_OpenSVPFlow.vpy`、`Check_OpenSVPFlow.vpy`、两只匹配的 Windows DLL 和版本标记。
2. 启动 FGS，通过原生首次运行 / 配置中的可选依赖安装入口安装 OpenSVPFlow。安装程序创建私有 `_OpenSVPFlow\.venv`（Python 3.12+）、私有 `_UserConfig`，**首次就直接安装 VapourSynth R82 / BestSource 21.0**，并做 CPU/GPU 自检；不需要安装完成后再执行一次插件在线更新。已有可用 DLL 优先复用，不联网强制替换。
3. 在「高级 → 插帧」选择 OpenSVPFlow，普通 SDR 保持原路径。逐行 PQ/BT.2020 10-bit 4:2:0 在「自动」或「保持 HDR」下均可使用 GPU16（默认）或 CPU10 高位深渲染；同规格 HLG 已获用户反馈 GPU16/CPU10 测试通过，仍需按实际输出检查 ARIB STD-B67 标签。CPU10 自动取消并禁用「实验性高速分析」「精简 Super」。隔行 HDR/HLG 使用反交错，插帧器会按场率规则自动绕过。

## 已有 FGS 升级（推荐）

1. 停止任务并关闭 FGS、`vspipe.exe` 及可能占用 DLL 的其它播放器/工具。先备份原目录里的 `FilmGrain_Config.ini`、`Presets/User`、`_OpenSVPFlow/_UserConfig` 和 `_OpenSVPFlow/.venv`；不要删除原目录。
2. 推荐把 v5.0.64 Stable 解压到**新的独立目录**，避免 Windows 占用与旧版本脚本混用。按需迁移个人配置；需沿用已有私有 VapourSynth 环境时，请连同 `_UserConfig` 一起迁移 `.venv`，并注意迁移后 `.venv` 中的绝对路径可能失效，若失效则在新位置使用原生依赖安装重建。
3. 如必须原目录覆盖升级，先确认进程已退出，再以新包内 `.vpy`、`Check_OpenSVPFlow.vpy`、`Plugins` 中的两只 DLL 和版本标记**成套更新**；保留 `.venv`、`_UserConfig` 和个人配置，不用 Legacy 下的旧安装脚本替换它们。
4. 启动新 FGS，在「高级 → 插帧 → 更新 OpenSVPFlow 插件」可选择今后升级至上游新 DLL；该操作**不是使用随包 DLL 的前提**。本版已携带用户更新并验证 HLG GPU16/CPU10 的 `nightly-20261106-badc0de392ac` 配对，不必立即再点更新。后续升级属于新组合，建议先用短 HDR10 与 HLG 样本验证再跑长片。

## 原生在线更新做了什么

- 在同一个 `_OpenSVPFlow/.venv` 中用 pip 安装/升级 `vapoursynth==82`、`vapoursynth-bestsource==21.0`，不删除整个虚拟环境；pip 升级步骤与 DLL 回滚独立。
- 查询上游最新 Release，下载 `x86_64-pc-windows-msvc.zip`，检查资产大小、ZIP 条目、可用时核对 GitHub SHA-256 digest；在临时目录测试 CPU、GPU/OpenCL、CPU10 和 GPU16 高位深插件能力。
- 通过后备份旧 `svpflow1_vs.dll`、`svpflow2_vs.dll`、`open-svpflow-version.txt` 到 `_OpenSVPFlow/_PluginBackup/UpdateNET_*`，再成套替换并复检。替换失败或取消时尝试恢复这三个原文件；若发生文件占用而回滚不完整，安装日志会指出具体备份目录。
- 成功后 FGS 只刷新一次 SVP 能力。CPU10 使用 `cubic:0` 与 `exact=true/lean=false`；任何其它插件错误以任务日志为准，不会静默降到 SDR。

## 核验 DLL / 环境

在 FGS 根目录打开 PowerShell：

```powershell
Get-Content -LiteralPath '.\_OpenSVPFlow\Plugins\open-svpflow-version.txt'
Get-FileHash -LiteralPath '.\_OpenSVPFlow\Plugins\svpflow1_vs.dll' -Algorithm SHA256
Get-FileHash -LiteralPath '.\_OpenSVPFlow\Plugins\svpflow2_vs.dll' -Algorithm SHA256
& '.\_OpenSVPFlow\.venv\Scripts\vspipe.exe' --version
& '.\_OpenSVPFlow\.venv\Scripts\python.exe' -m pip show vapoursynth vapoursynth-bestsource
```

- 两只 DLL 的哈希与 `Plugins/BUNDLED_DLL_SHA256.txt` 一致：正在使用本版的测试配对。本轮 SHA256 分别是 `975c6503e466e0ad8a6c23d916d1ec7f13b3fc00359586ecab7adc44a5a7415b` 和 `5e302048867c5fe735f699e06269e022e6f733ca00987de31169d25a8c296e69`；若后续在线更新，预期不同，以更新日志和新配对的 CPU/GPU/HDR 自检为准。
- `vspipe.exe --version` 正常、`pip show` 显示 VapourSynth 82 与 BestSource 21.0：私有环境版本符合本版安装基线。找不到 `.venv` 时走原生依赖安装；不要把系统别处的 VapourSynth 目录直接覆盖进来。
- CPU10 任务日志出现 `effective=HDR10/P8-analysis/CPU10-output` 和 `cubic:0`：已进入 CPU10 高位深路径；`OpenSVP 已绕过` 表示该任务实际未进入插帧路径，需同时核对视频是否逐行及 HDR 策略。
- GUI 切换 CPU10 后两个高速选项应取消并变灰，切回 GPU16 后恢复手动选择；旧预设冲突值会在生产端归一化。

## 失败回退

1. 首先退出 FGS / vspipe 等占用进程。若在线更新失败，查该次日志中的 `_OpenSVPFlow/_PluginBackup/UpdateNET_*`，确认两只 DLL 与版本文件是否已自动恢复。
2. 如安装日志提示回滚不完整，**只从对应的一次备份**恢复成套的两个 DLL 与 `open-svpflow-version.txt`，不要从多个版本混拷；原 `.venv` 和 `_UserConfig` 不需跟着覆盖。
3. 如果更新后私有环境本身启动失败，备份后通过原生依赖安装入口修复 R82 / BestSource 21.0；插件回滚不会自动撤销 pip 已完成的更改。
4. 要完全回到 v5.0.62，应使用旧版本完整目录/备份运行，不要把旧 EXE 与新 `.vpy`、新 DLL 混装。

旧目录 `Legacy/_OpenSVPFlow` 中的 PowerShell/BAT 属历史兼容归档，包含旧版 R79 安装逻辑；v5.0.64 的更新以 **FGS 原生界面**为准。版本标记仅用于显示与更新决策，不能单独证明 DLL 来自同名的官方发布资产，实际身份请核对 SHA-256。

随包两只 DLL 均与上游 `nightly-20261106-badc0de392ac` 的 `x86_64-pc-windows-msvc.zip` 逐字节一致。
