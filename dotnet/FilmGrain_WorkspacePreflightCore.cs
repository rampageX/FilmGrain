using System;
using System.Collections.Generic;
using System.IO;

namespace FilmGrainStudioPreview
{
    internal sealed class WorkspacePreflightIssue
    {
        internal string Key;
        internal string Path;
        internal string Detail;
    }

    internal sealed class WorkspaceSpaceWarning
    {
        internal bool IsOutput;
        internal string Path;
        internal double Available;
        internal double Required;
        internal double Reserve;
    }

    internal static class WorkspacePreflightCore
    {
        // R05: consume the preparation result, not the interpolation checkbox.
        // All callers (single, shared queue, independent queue, or CLI) pass the
        // exact BridgeTaskRequest objects that will be executed.
        internal static bool CheckPlanned(IList<string> inputs, IList<BridgeTaskRequest> tasks,
            IDictionary<string, MediaProbeInfo> mediaCache, Func<WorkspaceSpaceWarning, bool> confirm,
            out WorkspacePreflightIssue issue)
        {
            issue = null;
            if (inputs == null || tasks == null || inputs.Count != tasks.Count || confirm == null)
            { issue = new WorkspacePreflightIssue { Key = "error.temp_unavailable", Detail = "Workspace plan/input count mismatch." }; return false; }
            var tempRequirements = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var outputRequirements = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < inputs.Count; index++)
            {
                string input = inputs[index], tempRoot = "", outputRoot = "";
                try
                {
                    BridgeTaskRequest task = tasks[index];
                    BridgeExecutionRequest plan = task == null ? null : task.ExecutionRequest;
                    if (plan == null || plan.Environment == null)
                        throw new IOException("Native workspace plan is unavailable.");
                    var state = plan.Environment;
                    string tempMode = Get(state, "FG_TEMP_MODE").ToUpperInvariant();
                    tempRoot = tempMode == "SYSTEM" ? Path.Combine(Path.GetTempPath(), "FilmGrain_Studio") :
                        tempMode == "CUSTOM" ? Path.Combine(Get(state, "FG_TEMP_CUSTOM_DIR"), "FilmGrain_Studio") : Path.GetDirectoryName(input);
                    VerifyWritable(tempRoot);
                    MediaProbeInfo info = null;
                    if (mediaCache != null) mediaCache.TryGetValue(input, out info);
                    string mode = Get(state, "FG_MODE");
                    double factor = task.NoReencode ? 2.2 : string.Equals(mode, "AV1", StringComparison.OrdinalIgnoreCase) ? 2.6 : 1.0;
                    if (info != null && info.IsHdr) factor += 4.0;
                    double fileSize = new FileInfo(input).Length;
                    double required = Math.Max(512.0 * 1024 * 1024, Math.Ceiling(fileSize * factor));
                    // Only a registered, executable FFV1 producer stage can add the
                    // full-frame interpolation budget. IVF, grain tables and final
                    // output remain covered by the existing baseline/output factors.
                    foreach (string workfile in plan.TemporaryFiles ?? new string[0])
                    {
                        if (!IsPlannedFrucWorkfile(plan, workfile)) continue;
                        if (info == null) throw new IOException("FRUC workfile size estimate requires media probe information.");
                        required += EstimateFrucWorkfileBytes(info, Get(state, "FG_INTERPOLATION_TARGET_FPS"));
                    }
                    // Preview initially holds only a keyframe-clip stage. Its actual
                    // encoder plan is constructed later by ContinueAfterStages;
                    // until then do not assume a direct route and risk underbudget.
                    if (plan.ContinueAfterStages != null && !task.NoReencode &&
                        string.Equals(mode, "AV1", StringComparison.OrdinalIgnoreCase) &&
                        Get(state, "FG_SVP_INTERPOLATE") == "1")
                    {
                        if (info == null) throw new IOException("Deferred preview workspace estimate requires media probe information.");
                        double requestedSeconds;
                        if (!double.TryParse(Get(state, "FG_PREVIEW_SECONDS"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out requestedSeconds) || requestedSeconds <= 0)
                            throw new IOException("Deferred preview duration is invalid.");
                        // The preview clip starts on a keyframe, not necessarily the
                        // requested timestamp; allow a small buffer for that boundary.
                        required += EstimateFrucWorkfileBytes(info, Get(state, "FG_INTERPOLATION_TARGET_FPS"), requestedSeconds + 2.0);
                    }
                    double previous;
                    if (!tempRequirements.TryGetValue(tempRoot, out previous) || required > previous) tempRequirements[tempRoot] = required;
                    // Use the actual planned output path, rather than assuming that
                    // a queued file uses the current global output-directory setting.
                    // A few workflows (e.g. AV1 bake) register their final
                    // output during PostRunCommit. Keep their old output reserve.
                    IList<string> outputs = plan.OutputFiles == null || plan.OutputFiles.Count == 0
                        ? new string[] { Path.Combine(Get(state, "FG_OUTPUT_MODE").Equals("CUSTOM", StringComparison.OrdinalIgnoreCase)
                            ? Get(state, "FG_OUTPUT_CUSTOM_DIR") : Path.GetDirectoryName(input), "__FGS_PLANNED_OUTPUT__") }
                        : plan.OutputFiles;
                    foreach (string output in outputs)
                    {
                        outputRoot = Path.GetDirectoryName(output);
                        VerifyWritable(outputRoot);
                        double outputRequired = Math.Max(512.0 * 1024 * 1024, Math.Ceiling(fileSize * 1.2));
                        outputRequirements.TryGetValue(outputRoot, out previous);
                        outputRequirements[outputRoot] = previous + outputRequired;
                    }
                }
                catch (Exception ex)
                {
                    issue = new WorkspacePreflightIssue { Key = string.IsNullOrEmpty(outputRoot) ? "error.temp_unavailable" : "error.output_unavailable",
                        Path = string.IsNullOrEmpty(outputRoot) ? tempRoot : outputRoot, Detail = ex.Message };
                    return false;
                }
            }
            foreach (var pair in tempRequirements)
            {
                WorkspaceSpaceWarning warning;
                if (!CheckDrive(pair.Key, pair.Value, false, out warning, out issue)) return false;
                if (warning != null && !confirm(warning)) return false;
            }
            foreach (var pair in outputRequirements)
            {
                WorkspaceSpaceWarning warning;
                if (!CheckDrive(pair.Key, pair.Value, true, out warning, out issue)) return false;
                if (warning != null && !confirm(warning)) return false;
            }
            return true;
        }

        private static string Get(IDictionary<string, string> state, string key)
        { string value; return state.TryGetValue(key, out value) ? value ?? "" : ""; }

        // _svp.mkv / _fruc.mkv are produced by the legacy AV1 FFV1 stages.
        // A path in cleanup alone is not sufficient: verify a planned stage
        // actually writes it and requests FFV1.
        internal static bool IsPlannedFrucWorkfile(BridgeExecutionRequest plan, string path)
        {
            if (plan == null || string.IsNullOrWhiteSpace(path) || plan.DirectStages == null) return false;
            string name = Path.GetFileName(path);
            if (!name.EndsWith("_svp.mkv", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith("_fruc.mkv", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (BridgeExecutionStage stage in plan.DirectStages)
            {
                string args = stage.DirectArguments ?? "";
                if (args.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    args.IndexOf("ffv1", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        internal static double EstimateFrucWorkfileBytes(MediaProbeInfo info, string rate, double maxDuration = 0.0)
        {
            InterpolationFrameRate target; string reason;
            double duration;
            if (info == null || info.Width <= 0 || info.Height <= 0 ||
                !double.TryParse(info.Duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration) || duration <= 0 ||
                !InterpolationFrameRateCore.TryParse(rate, info.AvgFrameRate, out target, out reason))
                throw new IOException("FRUC workfile size estimate requires known dimensions, duration and FPS.");
            if (maxDuration > 0.0 && maxDuration < duration) duration = maxDuration;
            // Use the 16-bit storage bound for HDR 4:2:0 workfiles. FFV1 compression is content-dependent.
            double bytesPerPixel = info.IsHdr ? 3.0 : 1.5;
            double required = info.Width * (double)info.Height * bytesPerPixel * target.Fps * duration * 1.05;
            if (double.IsNaN(required) || double.IsInfinity(required)) throw new IOException("FRUC workfile size estimate is invalid.");
            return required;
        }

        private static void VerifyWritable(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("Directory is not configured.");
            Directory.CreateDirectory(root);
            string testPath = Path.Combine(root, ".fgs_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(testPath, "FGS");
                File.Delete(testPath);
            }
            finally { if (File.Exists(testPath)) { try { File.Delete(testPath); } catch { } } }
        }

        private static bool CheckDrive(string root, double required, bool output,
            out WorkspaceSpaceWarning warning, out WorkspacePreflightIssue issue)
        {
            warning = null;
            issue = null;
            try
            {
                string driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
                double available = new DriveInfo(driveRoot).AvailableFreeSpace;
                double reserve = Math.Max(2.0 * 1024 * 1024 * 1024, Math.Ceiling(required * 0.2));
                if (available < required + reserve)
                    warning = new WorkspaceSpaceWarning { IsOutput = output, Path = root, Available = available, Required = required, Reserve = reserve };
                return true;
            }
            catch (Exception ex)
            {
                issue = new WorkspacePreflightIssue { Key = output ? "error.output_unavailable" : "error.temp_unavailable", Path = root, Detail = ex.Message };
                return false;
            }
        }
    }
}
