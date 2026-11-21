# FGS · FGB-02 · PC Full Range 插帧＋胶片边框 TODO

- **状态**：待研究；本轮不修复、不解锁限制。
- **基准**：v5.0.81（FGB-02 M8Q7/N7C2/3PG3）；SDR／PQ／HLG × OpenSVPFlow／NVEncC AI 已由用户实机确认通过。
- **影响条件**：输入明确标记 `color_range=pc`／Full Range（包含需要单独识别的 `jpeg`）；在插帧＋GPU 边框组合链中触发现有色域/范围保护。
- **现状**：`dotnet/FilmGrain_NativeHevcCore.cs` 的 FGB-02 SDR GPU 分支要求 BT.709 Limited；HDR 边框创建要求 BT.2020 PQ／HLG Limited。不得简单将 Full Range `setparams` 标记成 Limited。
- **既往尝试**：2026-11-16 R9D6 的 NVEncC HDR VFG `--vpp-colorspace range=full:limited` 产生 VFG 无法接受的 `yuv444(16bit)` 中间格式；未获得成功实测的 PC HDR 转换路线。
- **不适用的已验证成果**：`grav1synth v0.2.3` 修复过 AV1 Film Grain Full Range 保留，但不是像素范围转换，不能用来证明 FGB-02 GPU 边框可直接支持 PC Full Range。
- **恢复研究的条件**：有经过验证、与当前生产者/设备格式兼容的 Full→Limited GPU 像素转换（或直接 PC→PC 合成）路径，且无需引入 CPU 中转、大型临时文件。
- **验收要求**：SDR PC、PQ PC、HLG PC 分开测试；校验黑白电平、范围标记、10-bit、色彩矩阵和 PTS/音频同步；对照 Limited 基线，SVP 与 NVEncC AI 各覆盖静态/动态边框；检查速度不明显劣化。
- **边界**：保留当前主动拒绝策略；不得静默转换范围、降位深或回退 CPU 合成；不影响已验证的 Limited Range 六组组合。

此条仅为跟踪记录，不表示已支持 Full Range。
