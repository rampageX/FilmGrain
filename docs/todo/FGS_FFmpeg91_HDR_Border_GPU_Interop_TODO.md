# FGS · P1 · FFmpeg 9.1 HDR GPU 胶片边框兼容性 TODO

- **状态**：待定位、待修复；主干功能的 FFmpeg 升级阻断项（P1）。不得以永久固定 9.0.2 作为关闭条件。
- **发现时间**：2026-11-21。
- **当前可用基线**：QuickFatHedgehog 2026-11-07 构建的 FFmpeg `n9.0.2-48-gd4070a14ce`；已验证 FGS HDR GPU 边框流程正常。升级前保留该二进制及配置备份。
- **复现版本**：
  - QuickFatHedgehog 2026-11-07 master：`N-128546-g2d7eacee24`，HDR GPU 边框失败。
  - Gyan FFmpeg 9.1 稳定构建：用户实机确认 SDR 胶片边框正常，HDR 胶片边框复现与上述 master 相同的错误；FRUC Vulkan 的 SDR、HDR 插帧均正常。
- **既有错误线索**：Vulkan→CUDA 外部显存映射阶段出现 `CUDA_ERROR_INVALID_VALUE`，涉及 `cuExternalMemoryGetMappedMipmappedArray`，随后硬件帧上传／NVENC 链路失败。这是已有 master 故障的错误记录；Gyan 9.1 尚未单独保存完整诊断日志。
- **影响**：GPU 胶片边框属于 FGS 主干功能，HDR 输入不能因 FFmpeg 升级而丧失该功能；FRUC Vulkan 正常不能证明边框 GPU 互操作正常。
- **调查方向（尚未确认根因）**：比较 FFmpeg 9.0.2 与 9.1 的 `libavutil/hwcontext_vulkan.c`、`hwcontext_cuda.c`，重点检查 HDR 10-bit 帧的 Vulkan 图像格式／平面数量、外部内存导出标志、CUDA 映射描述符以及帧池分配。两个版本存在上述 API 调用，但不能据此认定具体致错 Commit。
- **解决原则**：优先从源码或 GPU 帧互操作参数上修复；尽量保持原有 GPU 直通及速度，不以 CPU 下载／上传、降为 8-bit、HDR 转 SDR 或整片中间文件作为默认解决方式。必要时对新 FFmpeg 做版本兼容分支，不改坏 9.0.2 基线。
- **验收要求**：
  1. 在拟升级的 FFmpeg 正式构建上验证 PQ／HLG HDR 的静态、动态 GPU 边框及 NVENC 输出，过程无 CUDA 映射错误。
  2. 核对输出 10-bit、BT.2020、PQ／HLG 标记及相关 HDR 元数据保留，画面与边框视觉正常，且无无声的 SDR／CPU 路径回退。
  3. 回归 SDR 边框、FRUC Vulkan SDR／HDR，并检查与边框组合后的流程；性能不得明显退化。
  4. 上述条件达到后才允许替换生产 FFmpeg；未通过则维持已验证版本。
- **关闭条件**：FFmpeg 9.1 或更新稳定版在 FGS HDR GPU 边框真实编码中通过上述验收，且代码／配置修复与测试结果有记录。

此项是版本升级兼容性缺陷跟踪，不表示根因已确认或已完成修复。
