# Film Grain Studio v5.0.77

- 正式集成 GPU 胶片边框：Dark 35mm、FGS Classic Black，支持静态和动态效果、边印文字及自定义 PNG 导入/删除。
- 独立 GPU 边框预览新增视频构图：画窗内拖动、鼠标锚点缩放、X/Y 精确定位、时间跳转与随机帧；构图参数参与正式编码。
- 动态边框开放晃动幅度、速度、随机频率，三项参数随配置保存。
- 主界面「添加边框」按钮显示样式名称、启用高亮及右键清空。影院样式首次默认关闭，并记忆上一次开关状态。
- 前处理剪裁显示预设名称或「自定义」，修复剪裁预设跳回原始画幅和参数校验异常；同步优化剪裁与 GPU 预览布局。
- 保留已验收的 Vulkan/libplacebo → CUDA → NVENC 全 GPU 直通，不增加 hwdownload 中转；主 FFmpeg 使用已验证 Stable，FRUC Vulkan/SVT-HDR 继续使用单独配置的特殊版。
- 内置 Dark 35mm 原版 PNG 与 RAW 资源校验、Windows GPU 预览构建和回归检查纳入发布流程。

未在本版开放：边框与插帧组合、AV1 边框输出和 HDR→SDR GPU 边框预览。
