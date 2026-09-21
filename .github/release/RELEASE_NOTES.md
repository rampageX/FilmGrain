# Film Grain Studio v4.8.8

本次版本主要统一 AV1 / HEVC NVENC Standard 默认编码参数，并调整 NVENC AQ 默认策略，同时修复 FGSIM 在特殊字符路径下的 shader 路径解析问题。

## 更新内容

- 统一 AV1 / HEVC NVENC Standard 默认编码参数：P7、VBR、10-bit、Full Resolution Multipass、B-frames 4。
- Spatial AQ 默认 Strength 8，Temporal AQ 默认关闭，并统一作用于 AV1 / HEVC。
- NVENC Lookahead 调整为 27。
- 精简高级编码设置中的 AQ 与 H.264 High10 说明。
- 修复 FGSIM 在包含空格、单引号等特殊字符路径下的 shader 路径解析问题。
- Benchmark README 增加 LG OLED DAYDREAMS 演示视频链接。