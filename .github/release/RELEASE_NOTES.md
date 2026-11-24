# Film Grain Studio v5.0.94

- R08a：符合条件的单遍隔行 HDR→SDR 编码直接串联反交错与 Hable，去掉整片 HEVC 工作文件。
- R08b：符合条件的逐行 HDR→SDR → OpenSVPFlow 或 NVEncC AI → AV1 NVENC 走全程管道，去掉 HEVC 和 FFV1 两份整片工作文件。原有多遍、像素颗粒和不兼容路线继续使用工作文件。
- 随包 OpenSVPFlow Windows DLL 成对更新至 `nightly-20261123-badc0debd33f`。该 nightly 修复 [OpenSVPFlow #16](https://github.com/Z1xus/open-svpflow/issues/16) 的 HDR 插帧问题，已由用户实测确认。
- RTX 4080 已通过三种反交错路线、NVEncC AI NUT 直连和 OpenSVPFlow Y4M 直连的预览验收。
