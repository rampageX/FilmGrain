using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FilmGrainStudioPreview
{
    internal sealed class NativeStartupOptions
    {
        internal bool SetupTest;
        internal bool CheckOnly;
        internal readonly List<string> Inputs = new List<string>();
        internal readonly List<string> Notices = new List<string>();
    }

    // Paths arrive from the CLR argument array, never through a shell or script.
    internal static class NativeStartupCore
    {
        // A WinExe can inherit valid pipe handles without having a console.
        // Write UTF-8 directly; Console.OutputEncoding can call SetConsoleOutputCP
        // and fail with ERROR_INVALID_HANDLE in this exact launch mode.
        internal static void WriteProtocolLine(string value)
        {
            WriteProtocolLine(Console.OpenStandardOutput(), value);
        }

        internal static void WriteProtocolLine(Stream output, string value)
        {
            using (var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true))
            {
                writer.WriteLine(value);
                writer.Flush();
            }
        }

        internal static string FindAppRoot(string executableDirectory)
        {
            DirectoryInfo dir = new DirectoryInfo(Path.GetFullPath(executableDirectory));
            for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FilmGrain_Config.default.ini")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "Lang"))) return dir.FullName;
            }
            throw new AppRootNotFoundException(executableDirectory);
        }

        internal static NativeStartupOptions Parse(IEnumerable<string> arguments, string launchDirectory)
        {
            var options = new NativeStartupOptions();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool filesOnly = false;
            foreach (string argument in arguments ?? new string[0])
            {
                if (argument == null || argument.Length == 0) continue;
                if (!filesOnly && argument == "--") { filesOnly = true; continue; }
                if (!filesOnly && string.Equals(argument, "--setup-test=missing", StringComparison.OrdinalIgnoreCase))
                { options.SetupTest = true; continue; }
                if (!filesOnly && string.Equals(argument, "--startup-check", StringComparison.OrdinalIgnoreCase))
                { options.CheckOnly = true; continue; }
                if (!filesOnly && argument.StartsWith("--", StringComparison.Ordinal))
                { options.Notices.Add("未知启动选项 / Unknown startup option: " + argument); continue; }
                try
                {
                    string path = Path.GetFullPath(Path.IsPathRooted(argument) ? argument : Path.Combine(launchDirectory, argument));
                    if (!File.Exists(path))
                    { options.Notices.Add("输入文件不存在或为目录 / Input file missing or directory: " + argument); continue; }
                    if (seen.Add(path)) options.Inputs.Add(path);
                }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException) && !(ex is NotSupportedException) && !(ex is PathTooLongException)) throw;
                    options.Notices.Add("输入路径无效 / Invalid input path: " + argument);
                }
            }
            return options;
        }
    }
}
