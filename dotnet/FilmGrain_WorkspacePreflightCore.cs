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
        internal static bool Check(IEnumerable<string> inputs, string mode, bool noReencode,
            string tempMode, string customTemp, string outputMode, string customOutput,
            IDictionary<string, MediaProbeInfo> mediaCache, Func<WorkspaceSpaceWarning, bool> confirm,
            out WorkspacePreflightIssue issue)
        {
            issue = null;
            Dictionary<string, double> tempRequirements = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> outputRequirements = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in inputs)
            {
                string root = "";
                try
                {
                    root = string.Equals(tempMode, "SYSTEM", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Path.GetTempPath(), "FilmGrain_Studio")
                        : string.Equals(tempMode, "CUSTOM", StringComparison.OrdinalIgnoreCase)
                            ? Path.Combine(customTemp, "FilmGrain_Studio") : Path.GetDirectoryName(input);
                    VerifyWritable(root);
                    double factor = noReencode ? 2.2 : string.Equals(mode, "AV1", StringComparison.OrdinalIgnoreCase) ? 2.6 : 1.0;
                    MediaProbeInfo info;
                    if (mediaCache != null && mediaCache.TryGetValue(input, out info) && info != null && info.IsHdr) factor += 4.0;
                    double required = Math.Max(512.0 * 1024 * 1024, Math.Ceiling(new FileInfo(input).Length * factor));
                    double previous;
                    if (!tempRequirements.TryGetValue(root, out previous) || required > previous) tempRequirements[root] = required;
                }
                catch (Exception ex) { issue = new WorkspacePreflightIssue { Key = "error.temp_unavailable", Path = root, Detail = ex.Message }; return false; }
            }
            foreach (KeyValuePair<string, double> pair in tempRequirements)
            {
                WorkspaceSpaceWarning warning;
                if (!CheckDrive(pair.Key, pair.Value, false, out warning, out issue)) return false;
                if (warning != null && !confirm(warning)) return false;
            }
            foreach (string input in inputs)
            {
                string root = "";
                try
                {
                    root = string.Equals(outputMode, "CUSTOM", StringComparison.OrdinalIgnoreCase)
                        ? customOutput : Path.GetDirectoryName(input);
                    VerifyWritable(root);
                    double required = Math.Max(512.0 * 1024 * 1024, Math.Ceiling(new FileInfo(input).Length * 1.2));
                    double previous;
                    outputRequirements.TryGetValue(root, out previous);
                    outputRequirements[root] = previous + required;
                }
                catch (Exception ex) { issue = new WorkspacePreflightIssue { Key = "error.output_unavailable", Path = root, Detail = ex.Message }; return false; }
            }
            foreach (KeyValuePair<string, double> pair in outputRequirements)
            {
                WorkspaceSpaceWarning warning;
                if (!CheckDrive(pair.Key, pair.Value, true, out warning, out issue)) return false;
                if (warning != null && !confirm(warning)) return false;
            }
            return true;
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
