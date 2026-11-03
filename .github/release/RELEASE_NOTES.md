# Film Grain Studio v5.0.47

- SVT-AV1 新增 Film Preset / Photon ISO / Grain Table，复用 grav1synth 0.2.2 和颗粒强度处理；关闭 SVT 内部颗粒，避免重复加颗粒。旧像素颗粒模式继续保留。
- 统一默认 AV1 家族及推荐胶片预设；无 NVENC AV1 时使用 SVT，保留已有预设/队列的显式选择，不新增重复硬件检测。
- 全部颗粒菜单按胶片预设、ISO、扫描颗粒、Grain Table、Digital、GPU Film Grain 的相对顺序排列，仅显示各路线已有能力；保留存储编号和默认值。
- SVT 参数颗粒复用一次 CPU 编码，输出 clean MKV 后应用模型并复制封装；音频按输出策略处理一次。保留 NVENC 的既有落盘优化及严格回退条件。
- 修复 SVT 参数颗粒输出重定向在合法路径/引号形式下误报执行边界改变的问题。
- HEVC 探测缺少颜色字段时，使用有界 VUI 证据补全缺失项；不覆盖明确冲突。
- 有条件开放普通 HEVC HDR10 的 SVT 保留：软件解码、源帧率、无额外画面处理，颗粒 OFF 或外部参数模型；核对输入 MDCV/CLLI 和输出实际 metadata OBU，失败停止，不伪造元数据。
- Windows/T600 短测覆盖 HDR10 颗粒 OFF、Film/ISO/Table 100%与50%、MP4、受控变帧率、复制音频、模型失败及应用前取消。运行中取消、任意长片及所有处理组合未据此验证。最后菜单排序已完成本地编译和编号检查，Windows 构建/实际界面仍需发布前确认。
- 不扩大 HLG、DV、HDR10+ 保留范围；源 AV1 重编码参数颗粒暂不开放；独立不重编码功能不变。
- 已知问题：NVENC AV1 HDR 静态信息丢失与固定帧率问题尚未修复，待 RTX 4080 分别定位。HDR ISO 非100%可能使用原整片强度回退，相关中间文件与 seed 行为见 README。
