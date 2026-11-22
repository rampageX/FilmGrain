# Film Grain Studio v5.0.83

- **LUT 图库文件夹交互**：文件夹选择框加宽至 500 像素；下拉列表按完整文件夹名称测量并在屏幕范围内扩展；鼠标悬停可查看完整选中目录。
- **智能过滤准确性**：不再把 Dehancer CUBE 头部的 `Rec.709 input transform: enabled` 误判为独立技术转换 LUT；支持 `Film Profile` 胶片说明及 `Kodak_2383`、`Teal_Orange` 等下划线连接的创意标记。
- **保持技术 LUT 保护**：Rec.709→LogC、Utility Transform、Gamut Map 等技术用途继续过滤；技术转换与创意标记并存的 LUT 归类为 Combined。
- 用户已确认 LUT 图库补丁实机测试通过；分类器 9 项定向回归测试通过。本版不修改 FFmpeg、FRUC、颗粒、边框或编码执行核心.
