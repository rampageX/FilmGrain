# Film Grain Studio v5.0.67

- 高级 GPU 预览新增 FGSIM 四档（Light、Normal、Medium、Heavy）和 Digital Grain（0.15–1.00，步进 0.05），拖动强度时直接重绘当前帧。
- 在保留 HDR 的兼容组合中支持 PQ／HLG。FGSIM 复用原颗粒纹理与计算规则，HDR 合成 10-bit 亮度差分。Digital Grain 在换帧时生成固定种子的噪声底图，调节强度时由 GPU 计算遮罩与混合。
- 两种模式均可与 LUT 一起预览，按 G 切换颗粒，点击「应用到 FGS」回传设置。
