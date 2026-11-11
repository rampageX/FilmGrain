# Film Grain Studio v5.0.64

- SVP-GPU、FRUC 和 SVT-HDR 状态栏按钮各自只复检对应项目。OpenSVPFlow 插件更新成功后只刷新 SVP 能力。
- NVEncC 插帧检测缓存迁入 `Utils`。匹配当前工具的旧根目录缓存会自动迁移。
- 插帧下拉列表跟随控件宽度。OpenSVP 两项改为「平滑 · HDR」和「平衡 · HDR」。
- 修正 AV1 插帧队列的 FFV1 临时空间估算。高位深 HDR 按 16-bit 样本存储估算，只有实际绕过插帧的隔行源才跳过这项估算。
- 修正 HDR AUTO 转 SDR 时传入 SVP 的渲染标记。编码预览按裁剪后重新探测的输入判定路线。
