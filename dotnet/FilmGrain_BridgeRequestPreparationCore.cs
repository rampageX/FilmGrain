using System;
using System.Collections.Generic;
using System.IO;

namespace FilmGrainStudioPreview
{
    internal static class SharedInterpolationBypassCore
    {
        // Normalize only proven Legacy bypass rules. Do not mutate the user's captured state.
        internal static bool TryResolve(MediaProbeInfo info, IDictionary<string,string> state,
            out IDictionary<string,string> normalized, out string notice, out string reason)
        {
            normalized = null; notice = ""; reason = "";
            if (info == null || state == null) return true;
            string enabled, engine, policy, deint;
            state.TryGetValue("FG_SVP_INTERPOLATE",out enabled); state.TryGetValue("FG_INTERPOLATION_ENGINE",out engine);
            if (enabled != "1" || (!string.IsNullOrEmpty(engine) && !string.Equals(engine,"SVP",StringComparison.OrdinalIgnoreCase))) return true;
            state.TryGetValue("FG_HDR_POLICY",out policy); state.TryGetValue("FG_DEINTERLACE",out deint);
            if (info.IsHdr && !info.IsInterlaced && string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase))
            {
                normalized = new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                normalized["FG_SVP_INTERPOLATE"] = "0"; normalized["FG_FPS_MODE"] = "SOURCE";
                notice = "[Native .NET] OpenSVPFlow 已绕过：HDR 保留使用原帧率与 10-bit 编码路径，不运行 8-bit 光流。 / OpenSVPFlow bypassed for HDR preservation; source FPS retained.";
                return true;
            }
            if (!info.IsInterlaced) return true;
            if (!string.Equals(deint,"AUTO",StringComparison.OrdinalIgnoreCase))
            { reason = "隔行源启用 OpenSVPFlow 时必须选择 AUTO 反交错；不会向光流传递交错帧。 / Interlaced OpenSVPFlow requires AUTO deinterlace."; return false; }
            normalized = new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
            normalized["FG_SVP_INTERPOLATE"] = "0"; normalized["FG_FPS_MODE"] = "AUTO";
            // Retain the original AUTO tonemap decision even though SVP is bypassed.
            if(info.IsHdr && !string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase))normalized["FG_HDR_POLICY"]="SDR";
            notice = "[Native .NET] OpenSVPFlow 已绕过：隔行输入使用所选反交错方式的 2x 场率输出，目标插帧 FPS 不参与。 / OpenSVPFlow bypassed for interlaced input; selected field-rate deinterlace used.";
            return true;
        }
        internal static void Attach(BridgePreparedExecution prepared,string notice)
        {
            if (prepared == null || string.IsNullOrEmpty(notice)) return;
            prepared.ExecutionRequest.ExecutionNotice = (prepared.ExecutionRequest.ExecutionNotice ?? "") + Environment.NewLine + notice;
            if (prepared.ExecutionRequest.DirectStages != null && prepared.ExecutionRequest.DirectStages.Count > 0)
                prepared.ExecutionRequest.DirectStages[0].StageText += " / OpenSVP 已绕过";
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
            prepared=null;reason="";
            if(string.IsNullOrWhiteSpace(input)||!File.Exists(input)){reason="input file does not exist or is inaccessible: "+input;return false;}
            if(info==null){reason="media probe cache is not ready: "+input;return false;}
            if(!info.HasVideo){reason="input has no usable video stream: "+input;return false;}
            if(state==null){reason="missing execution state";return false;}
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
                if(noReencode)
                {
                    var copyState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);copyState["FG_MODE"]="AV1";copyState["FG_GRAIN_ENABLED"]="1";
                    success=NativeAv1GrainReplaceCore.TryPrepare(appRoot,input,info,copyState,out prepared,out reason);
                }
                else
                {
                    string mode;state.TryGetValue("FG_MODE",out mode);
                    if(string.Equals(mode,"AV1BAKE",StringComparison.OrdinalIgnoreCase))success=NativeAv1BakeCore.TryPrepare(appRoot,input,info,state,out prepared,out reason);
                    else if(string.Equals(mode,"AV1",StringComparison.OrdinalIgnoreCase))success=NativeAv1Core.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if(string.Equals(mode,"HEVC",StringComparison.OrdinalIgnoreCase))success=NativeHevcCore.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if(string.Equals(mode,"X264",StringComparison.OrdinalIgnoreCase))success=NativeX264DigitalGrainCore.TryPrepare(appRoot,input,info,state,out prepared,out reason);
                    else {reason="unsupported codec: "+mode;return false;}
                }
                if(success && prepared!=null && prepared.ExecutionRequest!=null)
                { NativeProcessPolicy.ValidateRequest(prepared.ExecutionRequest);return true; }
                if(success)reason="Native preparation returned no execution request";
            }
            catch(Exception ex){reason="Native preparation failed: "+ex.Message;}
            if(prepared!=null && prepared.ExecutionRequest!=null)BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
            prepared=null;reason=NativeRuntimeBoundaryCore.FailureReason(reason);return false;
        }
    }

    internal sealed class BridgePreparedExecution
    {
        public BridgeExecutionRequest ExecutionRequest { get; private set; }
        public bool NoReencode { get; private set; }
        public string ToolPath { get; private set; }
        public IList<string> InputFiles { get; private set; }

        public BridgePreparedExecution(BridgeExecutionRequest executionRequest, bool noReencode, string toolPath, IList<string> inputFiles)
        {
            ExecutionRequest = executionRequest;
            SharedGrainCore.Attach(executionRequest,noReencode);
            NoReencode = noReencode;
            ToolPath = toolPath ?? "";
            InputFiles = inputFiles;
        }
    }

    // Task construction for direct Native requests. Name retained for source/build compatibility.
    internal static class BridgeRequestPreparationCore
    {
        public static BridgeTaskRequest CreateTask(BridgePreparedExecution prepared, double recommendedOutputFps)
        {
            if (prepared == null) throw new ArgumentNullException("prepared");
            if (prepared.ExecutionRequest == null) throw new ArgumentException("ExecutionRequest is required.", "prepared");

            NativeProcessPolicy.ValidateRequest(prepared.ExecutionRequest);
            BridgeTaskRequest taskRequest = new BridgeTaskRequest();
            taskRequest.NativeBackend = true;
            taskRequest.ExecutionRequest = prepared.ExecutionRequest;
            taskRequest.NoReencode = prepared.NoReencode;
            taskRequest.RecommendedOutputFps = recommendedOutputFps;
            return taskRequest;
        }
    }
}
