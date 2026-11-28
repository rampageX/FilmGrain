# Film Grain Studio v5.1.06

- HDR → SDR 的 Hable 预览新增「NVEncC Hable 参照」。配置目录中存在 NVEncC64.exe 时可选，使用 vpp-colorspace 的默认 Hable 参数，只影响左图。
- NVEncC 参照与右图共用同一张无损 HDR 抽帧，避免分割对比时画面错位。NVEncC 参照使用 8-bit Y4M 显示。
- 参数区新增 Hable 和 Mobius 算法说明。Windows 实机已确认 NVEncC 同帧对照及默认色调映射效果。
