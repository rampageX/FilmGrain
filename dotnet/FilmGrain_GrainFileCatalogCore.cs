using System;
using System.Collections.Generic;
using System.IO;

namespace FilmGrainStudioPreview
{
    // Phase 3.7.3: file catalog rules used by the existing grain UI.
    internal static class GrainFileCatalogCore
    {
        internal static List<string> ScanAv1Tables(string tableRoot)
        {
            List<string> found = new List<string>();
            try
            {
                if (Directory.Exists(tableRoot))
                {
                    string[] files = Directory.GetFiles(tableRoot, "*.*", SearchOption.AllDirectories);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string path in files)
                    {
                        string ext = Path.GetExtension(path);
                        string name = Path.GetFileName(path);
                        if (!string.Equals(ext, ".tbl", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add(path);
                    }
                }
            }
            catch { }

            return found;
        }

        internal static List<string> ScanGrainPlates(string grainRoot)
        {
            List<string> found = new List<string>();
            try
            {
                if (Directory.Exists(grainRoot))
                {
                    string[] files = Directory.GetFiles(grainRoot, "*.mov", SearchOption.AllDirectories);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    found.AddRange(files);
                }
            }
            catch { }

            return found;
        }

        internal static string RelativeDisplayPath(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return fullPath.Substring(fullRoot.Length);
            }
            catch { }
            return Path.GetFileName(path);
        }

    }
}
