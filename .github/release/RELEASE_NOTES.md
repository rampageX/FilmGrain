# Film Grain Studio v4.8.13

### 新增

* .NET Preview 新增独立 Task Log 与完整日志保存，任务执行日志与界面日志分离，便于定位单次编码和媒体检查问题。
* “日志目录”按钮支持右键清理 FGS 日志；只处理 `Logs\FGS_*.log`，不会删除 Logs 目录或其中的其他文件，正在使用而无法删除的日志会保留。
* 配置新增日志条目限制，默认保留最新 100 个 FGS 日志文件；可设置 0–10000，`0` 表示不限制。创建新的 Session / Task 日志时自动清理超出上限的旧日志。

### 改进与修复

* AV1 媒体颗粒检查增加 bitstream 预检查：先通过 FFmpeg `trace_headers` 读取 `film_grain_params_present`；明确为 0 时显示“未发现 AV1 Film Grain metadata”并跳过 grav1synth inspect，避免普通无颗粒 AV1 触发 TagBits 错误。检测到 Film Grain metadata 时仍沿用原 grav1synth inspect 流程。
* AV1 不重编码添加 / 替换 Film Grain 的独立工作流保持不变；本次 AV1 Inspect 修复只调整媒体颗粒信息检查入口。
* 修复 .NET RichTextBox 日志自动滚动在部分更新时可能触发异常的问题；保留完整日志写入，不因界面日志显示失败影响任务执行。
* 更新随包 OpenSVPFlow `svpflow1_vs.dll`、`svpflow2_vs.dll` 及版本记录；更新产生的 `_OpenSVPFlow\_PluginBackup` 继续作为本机备份目录排除，不进入 Git / 正式发布包。
* `.gitignore` 增加根目录 `/Logs` 与 `_OpenSVPFlow/_PluginBackup/` 运行时目录排除，避免日志和插件更新备份进入版本控制。
