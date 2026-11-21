# Film Grain Studio v5.0.80

- HLG HDR10-bit 胶片边框改用两阶段 Vulkan/libplacebo 全 GPU 合成：先在 GPU 合成，再转换为 HLG P010 并直通 CUDA/NVENC；修复原 HLG GPU 路线输出绿色块的问题。静态／动态边框与构图功能均保留，不增加 NVEncC 依赖，也不使用 CPU `zscale`／`hwdownload`。
- PQ HDR 保留已验证的单阶段 GPU 路线，不改既有编码、色彩和 HDR 信令逻辑。
- 胶片边框预览采用已验收的缩略图图库和暗黑导航交互，保留现有静态／动态切换、滚轮缩放、拖动与 X/Y 构图操作。
- 正式 Stable 首次预装 15 款已验收的 1920×1080 RGBA 胶片边框（9 款左右齿孔、6 款上下齿孔）。每款含 PNG、RAW、名称及画窗元数据，共 60 个文件；Windows 构建以固定清单逐文件校验 SHA256，并检查 Stable ZIP 不缺项。
- 15 款边框来自固定源包，打包到运行目录 `Utils/_FilmBorders/User`；不使用开发者个人 User 目录作为发布源。两款原内置边框和旧用户导入记录继续兼容。
- 三份胶片边框设计文档移至 `docs/film-border/`，根目录保持整洁；新增项目根目录 `AGENTS.md` 作为开发、测试和发布约定。
- 维持 AV1 边框、边框与插帧、HDR→SDR 边框等现有不支持组合的限制；本版不纳入 Windows HDR/MPO 拖动问题的实验代码。

