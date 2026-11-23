# Film Grain Studio v5.0.92

- R05：工作空间预检依据实际准备的执行计划和阶段表收费；AV1 + FRUC Vulkan、OpenSVPFlow Y4M、NVEncC AI NUT 等直连路径不再被无条件计入整片 FFV1 文件。确实产生 FFV1 的回退路线仍按实际计划计费。
- GUI 单任务、统一队列、独立配置队列共用同一执行计划预算核心；临时工作盘按任务峰值，输出盘按任务累计估算。保留成品／预览／颗粒缓存与至少 2 GB 安全预留，缺失或延迟计划仍保守处理。
- Windows 2022 R05 CI #45174501386 success（.NET Framework 4.8、7/7 阶段识别、3/3 CLR、5 组 Windows 磁盘预算边界及 R01–R04b 回归）。RTX 4080 实测 OpenSVPFlow Y4M → AV1 NVENC 直连无 FFV1，并实测 NVEncC FRUC 长片仍生成 FFV1 工作文件且完成成品；独立配置队列准备 2/2、失败隔离和后续任务执行正常。
- 未实测全过程磁盘峰值、旧路同参加速比例及单独取消清理。OpenSVPFlow HDR 59.94→60 GPU16 初始化问题与 NVEncC 9.41 FRUC raw/NUT 尾帧异常均独立于 R05，未在本版变更；R04c/R04 保持暂缓未完成。
