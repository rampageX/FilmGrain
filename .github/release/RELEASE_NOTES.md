# Film Grain Studio v5.0.62

- HDR10 Preserve + 逐行 PQ / BT.2020 / 10-bit 4:2:0 输入新增 OpenSVPFlow 高位深插帧：GPU16（P8 运动分析 + P16 GPU 渲染 → P10 输出，默认）与 CPU10（P8 分析 + P10 CPU 渲染 → P10 输出），无需整体转 SDR；SDR、FRUC 与隔行旁路路线保持原有行为。
- 修复 CPU10 的 SmoothFps `cubic` 不兼容（固定 `cubic:0`）；完整／快捷高级设置将 CPU10 与实验性高速分析、Lean Super 互斥，旧预设参数在共用执行核心自动归一化。
- 修正 SVP HDR10 实际执行时误显示“OpenSVP 已绕过”的状态；补齐 AV1 插帧中间 FFV1 的 10-bit 输出和四条编码路线的共用传参。
- 带上本轮使用的 `svpflow1_vs.dll`、`svpflow2_vs.dll`，附 SHA-256 清单与 `_OpenSVPFlow/UPGRADE_GUIDE.md`。私有 VapourSynth 统一 R82 / BestSource 21.0，现有 `.venv` 可经 FGS 原生更新入口原地升级；不调用 Legacy 安装脚本。
- 验证边界：T600 上 GPU16 HDR10 29.97→59.94 fps 编码成功，CPU10 参数修复及 GUI 互斥由用户确认；并未据此宣称所有 HDR / 动态元数据 / GPU 组合均通过。
