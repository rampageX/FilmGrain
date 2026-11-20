# Film Grain Studio v5.0.78

- 自定义胶片边框的输出后缀改为用户指定的边框名称，例如 `_FILMUSER_ILFORD HP5 PLUS 400_D`，不再写入 GUID 或短 ID；原有自定义 PNG、静态／动态配置和内置样式保持兼容。
- 导入边框时立即拒绝重复名称，包含大小写不敏感和 Windows 文件名非法字符替换后的冲突；导入执行前及旧素材编码前也校验名称唯一性。
- NVENC AV1、SVT-AV1 与 AV1 仅替换颗粒路线的「添加边框」按钮灰色禁用并显示暂不可用。切回 HEVC/H.264 NVENC 或 x265/x264 后恢复原保存的边框选择。
- 修复带着已保存胶片边框切换 AV1 编码时，未处理的 NotSupportedException 弹出 WinForms 系统异常窗口的问题；AV1 编码不携带尚未支持的边框配置。
- SVT-AV1 + Film Grain + 胶片边框的联合接入保留为 FGB-04 TODO，本版不开放，也不修改已验证的边框 GPU 合成和编码路径。

本版为 v5.0.77 的兼容性与文件命名修复版。
