# Film Grain Studio v5.1.05

- HDR 输入信息旁的快捷按钮打开 HDR → SDR 窗口，支持同帧滑动、并排和单图预览。「确定」将色调映射参数应用于主流程；也可使用窗口当前参数独立批量转换。
- Hable 预览新增「CPU Hable 参照」，左侧使用 CPU 默认 Hable，右侧显示当前路径及参数。Hable 优先使用 Vulkan，其它算法按所选方式处理。
- 独立转换支持 HEVC／AV1 NVENC、MKV／MP4、VBR／CQ 和全队列截取。素材短于设定截取时长时处理整段，起点越界的素材从片头截取。
- RTX 4080 实机验收了 CPU／Vulkan Hable 预览；独立队列以 Mobius＋HEVC NVENC 完成 4 条 PQ／HLG 素材，包含 12-bit 4:4:4 和特殊字符路径，4 条输出均通过成品校验。
