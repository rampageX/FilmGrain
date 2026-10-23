# v5.0.30 验收记录

## 基线与范围

以本次上传的 Working_Source（含未提交修改）为原始基线，保留已发布 v5.0.29 时间轴修复；在 Q6M8VN → V9N4QK → P7X4MN 累积工作副本上整理发布。未从 GitHub 或旧包覆盖源码。版本 5.0.30 / 5.0.30.0 / Stable 同步。

## Windows 回传证据

- Return_MediaQueue_Q6M8VN_20261023_140433.zip：20 文件串行去重读取、快速切换仅 20 次 FFprobe、多选缓存、移除重加复用、失败无热重试、清空取消、共享预览范围及用户配置哈希保持，全部通过。
- Return_MediaQueue_V9N4QK_20261023_142157.zip：SVT-HDR 正反例、缓存/刷新、配置特殊版真实 10-bit tune=6 编码通过；独立配置连续继承、回访、显式配置保护及字幕绑定隔离，全部通过。
- Return_MediaQueue_P7X4MN_20261023_150947.zip：真实 FFmpeg 进程的遇错停止/继续、连续失败、首项启动失败、取消不继续、失败汇总及临时文件清理通过；真实 MainForm 方法的已完成项跳过、移除失败后续跑、移除重加及清空状态通过。
- P7X4MN：真实 FFmpeg + grav1synth apply → inspect → 80% → apply → 解码通过；事件/种子/有效 AR、禁用事件与 CRLF、100% 原样保留检查通过；全部既有 Media Probe、SVT-HDR 及独立配置自动检查通过，用户配置哈希不变。
- 用户同意发布。队列故障注入为自动化实测；不将其表述为每个 GUI 动作和全部编码组合的人工验收。

## 语义与边界

跳过失败为全局设置，默认关闭，队列启动时冻结；运行失败可继续，取消仍停止，启动前媒体/配置/空间校验仍须通过。再次开始按当前列表顺序仅处理未完成项；已完成状态仅限当前会话，移除重加明确请求重做。

仅对无色度 scaling points、非 chroma-from-luma 的单零 AR 系数补齐零项，兼容 grav1synth 的 inspect 回读；有效 AR、时间轴及 100% 路线不改。SVT 检测仅声明相关微型编码/参数通过，不扩大 HDR 元数据承诺。

## 发布整理

发布候选生产 C# 与 P7X4MN 实测包除 Program 版本常量外相同；编码、插帧、时间轴、硬件检测没有额外发布修改。README 写用法，CHANGELOG/Release Notes 写变化。临时测试入口与源码移出候选，白名单仅要求本版验收记录，不依赖测试或清理工具。

保留既有 Windows runner 正式构建。GitHub 仅发布 Stable ZIP 与 SHA256；本地正式构建生成 Stable/Source 及各自 SHA256。当前环境未运行最终 Windows Dry Run，维护者在当前完整工作树（保留本地 images）执行 release.bat --dry-run 5.0.30，再正式发布。
