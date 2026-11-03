# Film Grain Studio v5.0.49

- SVT 与 x265 的 AUTO 默认保留 HDR；移除仅因源编码、位深/采样、硬解、帧率、缩放、画幅或静态信息不全而拒绝保留的限制。GUI、CLI、统一/独立队列共用判断，界面提示同步。
- 显式转 SDR 和已知 SDR 调色/LUT、CPU FGSIM shader、OpenSVPFlow 路线仍按实际能力转换或说明冲突；保留既有 FRUC P010 精度校验与失败停止。x264 的 HDR 策略不变。
- HEVC PQ 源已有 MDCV/CLLI 按项传入 SVT mastering-display/content-light 或 x265 master-display/max-cll；分别采用各编码器要求的单位，不编造缺失值。x265 缺少 CLLI 时关闭其默认零值占位。读取不到静态信息继续保留 HDR，不把颜色标签完整等同于所有 metadata 已保留。
- 修复 SVT 参数颗粒中间文件重复继承长源名称导致的 PathTooLongException；内部改用唯一任务目录下的短名，正式输出命名不变。更正颗粒前校验日志，不再声称已核验完整静态 OBU。
- 不增加视频编码或整片媒体文件；沿用 NVENC HDR 修复、颗粒强度优化、不重编码颗粒功能和硬件缓存。共用预览裁剪、最终输出及极深临时目录的长路径边界仍存在，README 已明确。
- Windows/RTX4080 用户正常使用确认 SVT HDR 及路径修复通过；x265 实测输出为 720p Main10、BT.2020/PQ，保留 Display P3、0.0001–1000 cd/m²、MaxCLL 1505/MaxFALL 1012，用户确认画面正常。
- 本地完成 46 模块 C#5/.NET Framework 4.8 参考编译、SVT/x265 策略与命令计划、颗粒阶段/强度回归、源静态值提取和参数合并，以及 x265 四组短片的实际 HEVC SEI/颜色/帧数检查。正式 Windows runner 构建结果以发布工作流为准；上述检查不扩展为 DV/HDR10+ 动态信息或任意组合验证。
- 发布排除规则覆盖本轮临时说明、清单、路径报告及交付ZIP，不混入正式运行包或源码发布包。
