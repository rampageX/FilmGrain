# Film Grain Studio v4.8.2

本版正式集成已实测通过的 **FGS Benchmark v1.0**，提供 HEVC / AV1 / x264 颗粒路线的统一测试入口，便于比较处理速度、输出体积与同帧画面。

## Benchmark

- B01–B09、Q01–Q03 共 12 项矩阵，含 HEVC FGSIM VBR / CQ27 / CQ23 对照。
- 自动记录环境与源信息，生成 Benchmark.csv、Benchmark.md 和指定帧号的 PNG 截图。
- 将视频拖到 `Utils/FGS_Benchmark.cmd` 即可运行；配套 PS1 已包含在正式包中。
- Grain Plate 请填原始 MOV 的完整路径，现有无损缓存由 StudioBridge 自动复用。

## 实测样本与文档

仓库 [benchmark](https://github.com/rampageX/FilmGrain/tree/master/benchmark) 保存两组 12/12 成功的实测结果与截图：Taylor Swift - Look What You Made Me Do 片段第 313 帧，以及 LG OLED DAYDREAMS 片段第 543 帧。测试记录来自 v4.8.1，保留其原始环境信息；单帧不代表整段平均画质。

README 维护当前用法，CHANGELOG 维护版本历史。**正式 ZIP 排除整个 benchmark 目录**，仅随包提供测试脚本。

## 兼容性与构建

保留用户实测通过的 Benchmark 脚本原样，不调整 GUI/CLI 共用编码核心、颗粒算法、码率策略或 AAC 256k 设置。

正式包由 Windows Server 2022 runner 构建，执行 PowerShell 语法、语言资源、BOM、CRLF 与必需文件检查，并验证最终 ZIP 包含两个 Benchmark 脚本且不包含 benchmark 目录。
