# Film Grain Studio v4.8.17

本版增加默认开启的“启用颗粒”总开关，允许只执行插帧、调色、LUT、字幕及其它处理。应用继续使用当前.NET实现；本轮既定测试及累计补测已由用户确认通过，正式Stable由Windows发布流程构建。

- 开关保存到`[General] GRAIN_ENABLED`，旧INI缺项时开启；关闭后颗粒控件灰显但保留参数，重新开启及重启恢复状态。GUI、快照、原生任务和自动测试共用判断。
- 关闭时三线真正跳过Digital Grain、Grain Plate、FGSIM及普通AV1的grav1synth metadata添加/替换；不以强度0代替，不要求未执行的颗粒依赖，不生成仅供颗粒的资源。其它实际功能及依赖正常执行/检查。
- 关闭时取消实际libx264主输出/上传副本自动`tune grain`；NVENC不套用x264参数。保留preset、码率和其它质量设置；无颗粒输出去掉本次颗粒命名标记，日志/状态明确显示“颗粒：关闭”。
- HDR自动判断只依据实际启用的处理，保存的FGSIM选择不再使无颗粒任务转SDR；调色/LUT及明确HDR策略限制保持，不静默跳过处理。
- AV1不重编码专用模式固定颗粒开启并给出tooltip，进出时恢复普通模式开关偏好；颗粒检测/查看保持独立。关闭不是降噪，不清除源像素颗粒，也不承诺普通AV1重编码原样保留输入metadata，不新增清除步骤。
- 修复普通无颗粒x264/HEVC及AV1封装中剪切TS相对音画起点的恢复，上传副本保留主输出时间轴；无颗粒HEVC＋SVP的TS/MTS/M2TS管道接入现有共享时间轴处理，保留前导音频。没有修改FRUC/SVP算法或扩大到启用颗粒的任意时间轴组合。
- 本地测试支持规范样本、ClipOnly、编码线/插帧筛选及实际帧率记录；修复参数默认路径初始化、并发日志写入和PowerShell错误截断，保留完整异常/退出码。技术起点容差仍70ms，画质/HDR/同步仍需人工观察。Tests_Shared只在完整测试包保留，不进入GitHub或正式Stable ZIP。
- 最终341项模拟断言、11种默认开启计划与初始Stable对比、明确Microsoft Framework参考程序集构建及成品API审计通过；软件执行与内容时间匹配记录单独保留。2026-10-12用户确认G7N2VX及累计补测全数通过，包括普通剪切TS、FRUC以及V3K8MS的HEVC/AV1＋SVP；不扩大到未测硬件、HDR、NVENC/Vulkan或任意组合。
- 整理当前.NET使用说明，移除当前README中旧CLI/BAT操作指引；Legacy归档未修改。保留发布工具的UTF-8 Git路径读取、特殊路径、benchmark历史跟踪/Stable排除、同版号失败重试及BuildTools暂存规则。本批不混入性能优化、LUT分辨率、框架升级或额外UI改动。
