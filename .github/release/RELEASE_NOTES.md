# Film Grain Studio v4.8.3

本版正式加入已经实测通过的 **x264 VBR 3-Pass**，并统一 AV1 / HEVC / x264 三条主线及 H.264 上传副本的输出命名顺序。

## x264 VBR 3-Pass

- `高级设置 → 编码 → x264 码率模式` 新增 `VBR 3-Pass`。
- 严格执行 `Pass 1 → Pass 3 → Pass 2`，默认仍为 `VBR 1-Pass`。
- GUI / CLI、三类像素颗粒、OpenSVPFlow 和 H.264 上传副本共用同一设置。
- 保持现有 preset、tune grain、自动码率、High Motion、VBV 与 AAC 256k 逻辑；3-Pass 输出追加 `_3PASS`。

## 统一输出命名

- 输出名称按 GUI 设置面板顺序排列：编码方式 → 编码参数 → 颗粒及参数 → LUT → 其它标识。
- AV1、HEVC、x264 主输出和 H.264 上传副本使用一致的组织方式。
- AV1 无重编码 Film Grain 添加 / 替换兼容旧版和当前名称；重复替换只更新颗粒标识，并保留单个 `_ADDED` / `_REPLACED`。

## 验证与构建

3-Pass 和新命名均已完成用户实际测试。正式包由 Windows Server 2022 runner 构建，并执行 PowerShell 语法、语言资源、BOM、CRLF、必需文件、Benchmark 排除与最终 ZIP 内容检查。
