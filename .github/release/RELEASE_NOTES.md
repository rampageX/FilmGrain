# Film Grain Studio v5.0.60

- 修复 v5.0.59 Stable 运行包漏打包 `_LUT_Tools/FilmLut` 的问题，恢复五套经典胶片 CUBE 文件及 `profiles.json`、许可和来源文件。
- Windows runner 增加最终 Stable ZIP 的经典胶片资源校验；五个 CUBE 缺失则构建失败，不再把源码阶段校验误认为运行包检查。
- 其余 LUT 工坊、GPU 缓存预览、AV1/HEVC/H.264 编码流程、HDR/SDR 行为和 GUI 设置保持 v5.0.59 原状。
