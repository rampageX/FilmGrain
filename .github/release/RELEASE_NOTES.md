# Film Grain Studio v5.1.09

- RTX 4080 符合条件的 HEVC Main10 HDR→SDR 路线支持 Vulkan→CUDA 双端 GPU 直通，适用于 Hable、Spline、BT.2390、无 Plate、同尺寸及 GPU 缩放 Plate。保留 bilinear、色调参数、时间轴及两个 Vulkan 标志；不扩大至插帧、边框、LUT 等未经验证的组合。
- 颗粒选择界面显示实际尺寸及当前 GPU 直通／GPU 缩放／常规处理路线，不要求为宽银幕另制 Plate。
- HDR→SDR 快捷高级设置新增 HDR 输入处理，与主高级设置同步；独立转换仍执行 HDR→SDR。
- K8H3 Windows CI 及用户实机验收通过；G04 GPU/CPU LUT、S4 内外边框、FRUC、取消清理、特殊字符路径和音画同步回归通过。
