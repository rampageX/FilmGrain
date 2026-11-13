using System;
using System.Collections.Generic;
using System.IO;

namespace FilmGrainStudioPreview
{
    internal static class SharedInterpolationBypassCore
    {
        // PQ and HLG share the validated high-depth 4:2:0 path.
        internal static bool SupportsHighDepthSvpHdr(MediaProbeInfo info)
        {
            return info != null && info.IsHdr && !info.IsInterlaced &&
                info.PixelFormat == "yuv420p10le" && info.ColorPrimaries == "bt2020" &&
                (info.ColorTransfer == "smpte2084" || info.ColorTransfer == "arib-std-b67") &&
                info.ColorSpace == "bt2020nc";
        }
        internal static bool AutoHdrSvpReady(MediaProbeInfo info, IDictionary<string,string> state)
        {
            if (!SupportsHighDepthSvpHdr(info) || state == null) return false;
            string enabled, engine;
            state.TryGetValue("FG_SVP_INTERPOLATE", out enabled);
            state.TryGetValue("FG_INTERPOLATION_ENGINE", out engine);
            return enabled == "1" && (string.IsNullOrEmpty(engine) ||
                string.Equals(engine, "SVP", StringComparison.OrdinalIgnoreCase));
        }

        private static string Get(IDictionary<string,string> state, string key)
        { string value; return state.TryGetValue(key, out value) ? value ?? "" : ""; }

        internal static bool BypassesInterlacedInput(MediaProbeInfo info, IDictionary<string,string> state)
        {
            return info != null && info.IsInterlaced && state != null && Get(state,"FG_SVP_INTERPOLATE") == "1";
        }

        private static bool AutoSvpPreservesHdr(MediaProbeInfo info, IDictionary<string,string> state)
        {
            string mode = Get(state,"FG_MODE").ToUpperInvariant();
            return AutoHdrSvpReady(info,state) && mode != "H264" && mode != "X264" &&
                SharedGrainCore.Engine(state) != "FGSIM";
        }

        internal static string SdrFallbackReason(MediaProbeInfo info, IDictionary<string,string> state)
        {
            if(info == null || !info.IsHdr || state == null) return "";
            string mode=Get(state,"FG_MODE").ToUpperInvariant();
            if(mode=="H264" || mode=="X264") return "8-bit H.264/x264 输出不保留 HDR";
            if((mode=="SVT" || mode=="X265") && SharedGrainCore.IsEnabled(state) && SharedGrainCore.Engine(state)=="FGSIM")
                return "CPU FGSIM 当前使用 SDR 路线";
            if(Get(state,"FG_SVP_INTERPOLATE")!="1") return "";
            string engine=Get(state,"FG_INTERPOLATION_ENGINE").ToUpperInvariant();
            if(engine=="NVENC_FRUC" || engine=="NVENC_AI") return "NVEncC 插帧使用 SDR 路线";
            if((engine=="SVP" || engine=="") && !SupportsHighDepthSvpHdr(info))
                return "此 HDR 采样/传递格式未接入 OpenSVPFlow 高位深路线";
            return "";
        }

        // All interpolation engines bypass interlaced fields; incompatible HDR processing gets SDR.
        internal static bool TryResolve(MediaProbeInfo info, IDictionary<string,string> state,
            out IDictionary<string,string> normalized, out string notice, out string reason)
        {
            normalized = null; notice = ""; reason = "";
            if (info == null || state == null) return true;
            string enabled=Get(state,"FG_SVP_INTERPOLATE"),engine=Get(state,"FG_INTERPOLATION_ENGINE");
            string policy=Get(state,"FG_HDR_POLICY");
            if(BypassesInterlacedInput(info,state))
            {
                normalized=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                normalized["FG_SVP_INTERPOLATE"]="0";
                normalized["FG_FPS_MODE"]="AUTO";
                normalized["FG_DEINTERLACE"]="AUTO";
                notice="[Native .NET] " + (engine=="SVP" || engine=="" ? "OpenSVPFlow" : engine) +
                    " 已绕过：隔行输入改用所选反交错算法的 2x 场率输出；不会向插帧器传递交错帧。";
                state=normalized;
                enabled="0";
            }
            string fallback=SdrFallbackReason(info,state);
            if(string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase) && fallback.Length>0)
            {
                if(normalized==null)normalized=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                normalized["FG_HDR_POLICY"]="SDR";
                notice+=(notice.Length>0?Environment.NewLine:"")+"[HDR route] HDR 保留请求自动改走 SDR："+fallback;
                state=normalized;
                policy="SDR";
            }
            if(enabled!="1" || (engine!="" && !string.Equals(engine,"SVP",StringComparison.OrdinalIgnoreCase)))return true;
            if (info.IsHdr && !info.IsInterlaced &&
                (string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase) ||
                 (string.Equals(policy,"AUTO",StringComparison.OrdinalIgnoreCase) && AutoSvpPreservesHdr(info,state))))
            {
                string backend;
                if(!state.TryGetValue("FG_SVP_HDR_BACKEND",out backend)||string.IsNullOrEmpty(backend))backend="GPU16";
                if(backend!="GPU16" && backend!="CPU10")
                {reason="OpenSVPFlow HDR10 renderer must be GPU16 or CPU10";return false;}
                string hdrFlag;
                if(!state.TryGetValue("FG_SVP_HDR10",out hdrFlag) || hdrFlag!="1")
                {
                    if(normalized==null)normalized = new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                    normalized["FG_SVP_HDR10"]="1";
                }
                notice += (notice.Length>0?Environment.NewLine:"") + "[Native .NET] OpenSVPFlow " + (info.ColorTransfer == "arib-std-b67" ? "HLG experimental" : "HDR10") + ": 8-bit motion analysis + " +
                    (backend == "CPU10" ? "10-bit CPU render" : "16-bit GPU render") + " + 10-bit output; no SDR conversion.";
                return true;
            }
            return true;
        }
        internal static void Attach(BridgePreparedExecution prepared,string notice)
        {
            if (prepared == null || string.IsNullOrEmpty(notice)) return;
            prepared.ExecutionRequest.ExecutionNotice = (prepared.ExecutionRequest.ExecutionNotice ?? "") + Environment.NewLine + notice;
            if (prepared.ExecutionRequest.DirectStages != null && prepared.ExecutionRequest.DirectStages.Count > 0)
            {
                if (notice.IndexOf(" 已绕过：隔行输入", StringComparison.Ordinal) >= 0)
                    prepared.ExecutionRequest.DirectStages[0].StageText += " / 隔行源插帧已绕过";
                else if (notice.IndexOf("OpenSVPFlow HDR10:", StringComparison.Ordinal) >= 0 ||
                         notice.IndexOf("OpenSVPFlow HLG experimental:", StringComparison.Ordinal) >= 0)
                    prepared.ExecutionRequest.DirectStages[0].StageText += notice.IndexOf("HLG experimental", StringComparison.Ordinal) >= 0
                        ? " / OpenSVP HLG 实验已启用" : " / OpenSVP HDR10 已启用";
            }
        }
    }

    internal static class NativeRuntimeBoundaryCore
    {
        // The .NET application never delegates preparation failures to scripts.
        internal static bool MustStop(string reason) { return true; }
        internal static string FailureReason(string reason)
        { return string.IsNullOrWhiteSpace(reason)?"Native preparation failed without a diagnostic; task stopped.":reason; }
    }

    internal static class NativeExecutionPreparationCore
    {
        // Shared by single-file UI, queue and the local test harness. No compatibility backend.
        internal static bool TryPrepare(string appRoot,string input,MediaProbeInfo info,
            IDictionary<string,string> state,HardwareCapabilitySnapshot caps,bool noReencode,
            out BridgePreparedExecution prepared,out string reason)
        {
            if(state==null){prepared=null;reason="missing execution state";return false;}
            var timer=System.Diagnostics.Stopwatch.StartNew();
            string previewFlag;state.TryGetValue("FG_ENCODE_PREVIEW",out previewFlag);
            string requested;state.TryGetValue("FG_GPU_DECODE",out requested);
            bool planning=requested=="1"&&!noReencode&&previewFlag!="1"&&!NvencInterpolationCore.Enabled(state);
            var initialState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
            initialState.Remove("FG_HWD_PLAN");initialState.Remove("FG_HWD_BACKEND");initialState.Remove("FG_HWD_SOURCE");
            if(planning)initialState["FG_HWD_PLAN"]=Guid.NewGuid().ToString("N");
            bool ok=TryPrepareCore(appRoot,input,info,initialState,caps,noReencode,out prepared,out reason);
            string decodeNotice=noReencode?"[HWD] effective=not-applicable reason=video stream copy":"";
            if(ok && !noReencode && previewFlag!="1") {
                IDictionary<string,string> selected;
                bool hardware=HardwareDecodeCore.Select(appRoot,input,prepared.ExecutionRequest,initialState,out selected,out decodeNotice);
                if(planning) {
                    BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                    if(!hardware)selected=initialState;
                    selected.Remove("FG_HWD_PLAN");
                    ok=TryPrepareCore(appRoot,input,info,selected,caps,noReencode,out prepared,out reason);
                }
            }
            if(ok && prepared!=null && decodeNotice.Length>0)prepared.ExecutionRequest.ExecutionNotice=(prepared.ExecutionRequest.ExecutionNotice??"")+Environment.NewLine+decodeNotice;
            if(ok && prepared!=null && prepared.ExecutionRequest!=null)
            {
                try { SharedInputTimelineCore.Bind(prepared.ExecutionRequest,input,info.Identity.Probe,info,noReencode); }
                catch(Exception ex) {
                    BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                    prepared=null;reason="Shared input timeline binding failed: "+ex.Message;ok=false;
                }
            }
            timer.Stop();
            if(ok && prepared!=null && prepared.ExecutionRequest!=null)
                prepared.ExecutionRequest.PreparationElapsed=timer.Elapsed;
            return ok;
        }

        internal static bool TryPrepareCore(string appRoot,string input,MediaProbeInfo info,
            IDictionary<string,string> state,HardwareCapabilitySnapshot caps,bool noReencode,
            out BridgePreparedExecution prepared,out string reason)
        {
            prepared=null;reason="";
            if(string.IsNullOrWhiteSpace(input)||!File.Exists(input)){reason="input file does not exist or is inaccessible: "+input;return false;}
            if(info==null){reason="media probe cache is not ready: "+input;return false;}
            var probeConfig=new FgsConfig(appRoot,null);probeConfig.Load();
            string currentProbe=Path.Combine(probeConfig.Get("FFMPEG_DIR"),"ffprobe.exe");
            if(info.Identity==null || !info.Identity.IsCurrent(input,currentProbe)) { reason="Input or FFprobe changed; reselect and probe again: "+input;return false; }
            if(!info.HasVideo){reason="input has no usable video stream: "+input;return false;}
            if(state==null){reason="missing execution state";return false;}
            try { AudioOutputCore.EstimateMissing(input,currentProbe,info,state); }
            catch(Exception ex){reason=ex.Message;return false;}

            string preview;
            if (state.TryGetValue("FG_ENCODE_PREVIEW", out preview) && preview == "1")
            {
                bool ok=SharedEncodePreviewCore.TryPrepare(appRoot,input,info,state,caps,noReencode,out prepared,out reason);
                if(ok) { prepared.ExecutionRequest.SourceIdentity=info.Identity;prepared.ExecutionRequest.SourceConfigRoot=appRoot; }
                return ok;
            }

            string routeNotice="";
            if(!noReencode)
            {
                IDictionary<string,string> effective;
                if(!SharedInterpolationBypassCore.TryResolve(info,state,out effective,out routeNotice,out reason))return false;
                if(effective!=null)state=effective;
            }
            if(!noReencode && NvencInterpolationCore.Enabled(state)) {
                string gpu=caps==null?VulkanDevicePolicy.DeviceName:caps.GpuName;
                if(!NvencInterpolationCore.Validate(appRoot,input,info,state,gpu,out reason))return false;
                var nvState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                var nvReport=NvencInterpolationCore.Cached(appRoot,NvencInterpolationCore.Get(state,"FG_NVENC_DIR"));
                nvState["FG_NVENC_GPU_NAME"]=gpu;nvState["FG_NVENC_DEVICES"]=nvReport.Devices;
                nvState["FG_NVENC_SOURCE_FPS"]=info.AvgFrameRate;
                state=nvState;
            }
            string svpEnabled, svpEngine;
            state.TryGetValue("FG_SVP_INTERPOLATE", out svpEnabled); state.TryGetValue("FG_INTERPOLATION_ENGINE", out svpEngine);
            if (!noReencode && svpEnabled == "1" && (string.IsNullOrEmpty(svpEngine) || string.Equals(svpEngine, "SVP", StringComparison.OrdinalIgnoreCase)))
            {
                string speed, tuning, block, analyse;
                if (!state.TryGetValue("FG_SVP_SPEED", out speed)) speed = "FGS";
                if (!state.TryGetValue("FG_SVP_TUNING", out tuning)) tuning = "FGS";
                if (!state.TryGetValue("FG_SVP_BLOCK_SIZE", out block)) block = "auto";
                if (!state.TryGetValue("FG_SVP_ANALYSE", out analyse)) analyse = "ENCODEGUI";
                if (!SharedSvpCore.TryValidateOptions(speed, tuning, block, analyse, out reason)) return false;
            }
            try
            {
                bool success;
                if(noReencode && (PreprocessingCore.Enabled(state)||ResizeCore.Enabled(state))){reason="前处理需要重新编码，不能用于仅替换 AV1 颗粒。";return false;}
                if(noReencode)
                {
                    var copyState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);copyState["FG_MODE"]="AV1";copyState["FG_GRAIN_ENABLED"]="1";
                    success=NativeAv1GrainReplaceCore.TryPrepare(appRoot,input,info,copyState,out prepared,out reason);
                }
                else
                {
                    string mode;state.TryGetValue("FG_MODE",out mode);
                    if(string.Equals(mode,"AV1BAKE",StringComparison.OrdinalIgnoreCase)&&(PreprocessingCore.Enabled(state)||ResizeCore.Enabled(state))){reason="独立 AV1 颗粒烘焙不提供前处理，请使用主编码流程。";return false;}
                    if(string.Equals(mode,"AV1BAKE",StringComparison.OrdinalIgnoreCase))success=NativeAv1BakeCore.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if(string.Equals(mode,"AV1",StringComparison.OrdinalIgnoreCase))success=NativeAv1Core.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if((string.Equals(mode,"HEVC",StringComparison.OrdinalIgnoreCase)||string.Equals(mode,"H264",StringComparison.OrdinalIgnoreCase)))success=NativeHevcCore.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if(string.Equals(mode,"X264",StringComparison.OrdinalIgnoreCase)||string.Equals(mode,"SVT",StringComparison.OrdinalIgnoreCase)||string.Equals(mode,"X265",StringComparison.OrdinalIgnoreCase))success=NativeX264DigitalGrainCore.TryPrepare(appRoot,input,info,state,out prepared,out reason,caps==null?"":caps.GpuName);
                    else {reason="unsupported codec: "+mode;return false;}
                }
                if(success && prepared!=null && prepared.ExecutionRequest!=null)
                {
                    SharedInterpolationBypassCore.Attach(prepared,routeNotice);
                    if(NvencInterpolationCore.Enabled(state))prepared.ExecutionRequest.ExecutionNotice=(prepared.ExecutionRequest.ExecutionNotice??"")+Environment.NewLine+
                        "[NVEncC SDR] "+NvencInterpolationCore.Label(NvencInterpolationCore.Get(state,"FG_IN