# Film Grain Studio v4.8.16

本版完善 OpenSVPFlow 高级参数、FRUC Vulkan 能力诊断、高级设置布局及 Framework 成品兼容性校验。应用运行时继续使用既有 .NET 实现。

- OpenSVPFlow 新增 Speed（FGS/medium/fast/faster/fastest）、Tuning（FGS/weak/film/smooth/animation）及 Block Size（auto/8/16/32），共享配置与三线执行入口统一接入；默认 FGS/FGS/auto 保留既有插件参数。film/smooth 当前同义，不宣称具有不同算法效果；BASE 与非默认新预设的组合明确处理，不增加脚本回退。
- 高级设置所有页面的“恢复默认”统一位于确定按钮左侧、等高，并保留只恢复当前页、确定后保存的行为；插帧说明改为控件/标签 tooltip；OpenSVPFlow 插件更新按钮位于底部左侧，仅在插帧页显示。
- FRUC 共享预检核对实际 FFmpeg 的 fps/perf/grid 及性能常量，区分输入错误、依赖缺失、构建/网格组合不支持、设备限制和运行异常，保留实际原因，失败不缓存。固定设备0与128×128 NV12小样本策略保持，成功不代表所有输入/编码组合可用。
- FRUC 任务日志记录构建版本、请求参数及实际设备/网格证据；未报告的信息不猜测。插帧选择与底部硬件状态共用简短 tooltip，完整 verbose 诊断保留在日志中。默认仍 medium/auto；auto 为设备支持的最细网格，不能理解为速度优先。
- 修复发布源码暂存的目录白名单遗漏：BuildTools同时纳入目录识别与源码清单，离线Framework门禁源码/DLL/许可不再被筛选器漏掉；其他本地环境/测试/成品排除规则保持，支持4.8.16已同步未提交状态重试。
- 新增离线 Framework API 成品审计，接入非Windows预构建、Windows本机构建与正式包校验；必须使用明确的 Microsoft Framework API 基准，不能回落至 Mono 默认引用。根/兼容 EXE 要求一致，审计失败停止交付或发布。
- 本轮 Windows 成品与测试程序 API 审计零失败；55项FRUC及429项既有软件断言、三线配对模拟计划、100组SVP参数组合检查通过。软件/模拟结果不代替硬件编码结果。
- 用户确认 OpenSVPFlow 本轮A–E及高级设置布局验收通过；确认 FRUC tooltip 精简效果通过。RTX4080 + 2026-09-24-git-5253641e62 FFmpeg 的1080p、4K、实际剪切TS、目标60fps共18个FRUC观察案例完成，帧数/帧率/音频/起始时间自动检查通过，人工观察未发现异常，TS音画同步正常。测试覆盖共享FRUC滤镜+x264/AAC观察路径，不扩展为所有NVENC/HDR/颗粒/LUT/字幕组合通过。
- 当前实机 auto 选择grid=1，grid=1/2/4可用，grid=8被设备明确拒绝并排除，没有静默改用其他参数。网格变粗的速度结果属于参数取舍，不表示算法或端到端编码实现已优化；未修改默认值。
- 本地测试支持规范命名样本、预热与三次null基准、单独生成带音频观察视频、分开记录预览编码耗时，并采集直接FFmpeg的CPU/工作集。没有采集GPU/OFA/显存；此前SVP启动器CPU/内存列不用于资源结论。Tests_Shared只随完整测试交付保留，继续排除GitHub与正式Stable ZIP。
- 本轮未改变编码过滤顺序、既有位深/HDR策略、帧率解析、输出命名、音画时间轴、取消、队列或临时文件清理。Legacy归档、算法依赖和纹理未顺带修改；发布工具继续保留UTF-8 Git清单读取、特殊路径、benchmark历史跟踪及同版号失败重试规则。正式发布仍由维护者执行，Dry Run不等于发布。
