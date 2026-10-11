using System;
using System.Collections.Generic;
using System.IO;

namespace FilmGrainStudioPreview
{
    internal enum BridgeRequestPreparationFailureKind
    {
        None,
        MissingTool,
        NoInputs,
        MissingInput
    }

    internal sealed class BridgeRequestPreparationFailure
    {
        public BridgeRequestPreparationFailureKind Kind { get; private set; }
        public string Path { get; private set; }

        public BridgeRequestPreparationFailure(BridgeRequestPreparationFailureKind kind, string path)
        {
            Kind = kind;
            Path = path ?? "";
        }
    }

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
            if (info.IsHdr)
            {
                // AUTO can require tonemapping because interpolation was requested. Keep that Legacy route.
                if (info.IsInterlaced || !string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase)) return true;
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
            notice = "[Native .NET] OpenSVPFlow 已绕过：隔行输入使用所选反交错方式的 2x 场率输出，目标插帧 FPS 不参与。 / OpenSVPFlow bypassed for interlaced input; selected field-rate deinterlace used.";
            return true;
        }
        internal static void Attach(BridgePreparedExecution prepared,string notice)
        {
            if (prepared == null || string.IsNullOrEmpty(notice)) return;
            prepared.ExecutionRequest.ExecutionNotice = notice;
            if (prepared.ExecutionRequest.DirectStages != null && prepared.ExecutionRequest.DirectStages.Count > 0)
                prepared.ExecutionRequest.DirectStages[0].StageText += " / OpenSVP 已绕过";
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
            NoReencode = noReencode;
            ToolPath = toolPath ?? "";
            InputFiles = inputFiles;
        }
    }

    // Phase 3.5.5 request-preparation core.
    // Resolves the verified BAT entry point, validates input accessibility,
    // freezes the input list, and creates BridgeExecutionRequest/BridgeTaskRequest.
    // UI validation, localized messages and logging remain in MainForm.
    internal static class BridgeRequestPreparationCore
    {
        public static bool TryPrepareExecution(
            string appRoot,
            IEnumerable<string> inputFiles,
            IDictionary<string, string> environment,
            bool noReencode,
            out BridgePreparedExecution prepared,
            out BridgeRequestPreparationFailure failure)
        {
            prepared = null;
            failure = null;

            string root = appRoot ?? "";
            string toolName = noReencode
                ? "AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat"
                : "FilmGrain_Universal_HEVC_AV1_StudioBridge.bat";
            string toolPath = Path.Combine(root, "Utils", toolName);
            if (!File.Exists(toolPath))
            {
                failure = new BridgeRequestPreparationFailure(BridgeRequestPreparationFailureKind.MissingTool, toolPath);
                return false;
            }

            List<string> inputs = new List<string>();
            if (inputFiles != null)
            {
                foreach (string inputPath in inputFiles)
                {
                    if (!string.IsNullOrWhiteSpace(inputPath)) inputs.Add(inputPath);
                }
            }
            if (inputs.Count == 0)
            {
                failure = new BridgeRequestPreparationFailure(BridgeRequestPreparationFailureKind.NoInputs, "");
                return false;
            }

            foreach (string inputPath in inputs)
            {
                if (!File.Exists(inputPath))
                {
                    failure = new BridgeRequestPreparationFailure(BridgeRequestPreparationFailureKind.MissingInput, inputPath);
                    return false;
                }
            }

            BridgeExecutionRequest executionRequest = new BridgeExecutionRequest();
            executionRequest.ToolPath = toolPath;
            executionRequest.WorkingDirectory = root;
            executionRequest.InputFiles = inputs;
            executionRequest.Environment = environment;

            prepared = new BridgePreparedExecution(executionRequest, noReencode, toolPath, inputs);
            return true;
        }

        public static BridgeTaskRequest CreateTask(BridgePreparedExecution prepared, double recommendedOutputFps)
        {
            if (prepared == null) throw new ArgumentNullException("prepared");
            if (prepared.ExecutionRequest == null) throw new ArgumentException("ExecutionRequest is required.", "prepared");

            BridgeTaskRequest taskRequest = new BridgeTaskRequest();
            taskRequest.ExecutionRequest = prepared.ExecutionRequest;
            taskRequest.NoReencode = prepared.NoReencode;
            taskRequest.RecommendedOutputFps = recommendedOutputFps;
            return taskRequest;
        }
    }
}
