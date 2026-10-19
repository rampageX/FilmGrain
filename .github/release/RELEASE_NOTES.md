# Film Grain Studio v5.0.13

- CPU 监控优先采用与任务管理器对应的 Processor Utility 统计口径，修正原时间占用口径在部分系统上明显偏低的问题；保留不可用时的回退。
- OpenSVPFlow 调校预设 weak 中文显示为“低伪影优先”，补充效果取舍说明；底层参数保持 weak。
- FRUC 光流网格 AUTO 改为选择设备支持的最大网格；补齐支持集合识别、选项过滤和实际选值日志，手动网格保持原值。
- 默认反交错算法改为 BWDIF CUDA，Vulkan 和 W3FDIF 仍可手选。对于无需 HDR→SDR 的 HEVC Grain Plate / FGSIM 组合，CUDA 可避免 Vulkan 路径的独立反交错中间文件。
- 每项任务结束显示总耗时，涵盖任务准备、I/O、所有处理阶段、验证及清理；失败或取消同样显示。队列按项独立统计，不计排队等待。
