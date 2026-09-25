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
