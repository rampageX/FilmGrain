using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    // Phase 3.7.4: existing LUT record and preview path rules without WinForms.
    internal static class LutCatalogCore
    {
        private static string JsonString(IDictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static IEnumerable<object> EnumerateJsonRows(object parsed)
        {
            if (parsed == null) yield break;
            object[] array = parsed as object[];
            if (array != null)
            {
                foreach (object item in array) yield return item;
                yield break;
            }
            ArrayList list = parsed as ArrayList;
            if (list != null)
            {
                foreach (object item in list) yield return item;
                yield break;
            }
            if (parsed is IDictionary<string, object> || parsed is string)
            {
                yield return parsed;
            }
        }

        internal static List<LutChoice> ReadLutRecordList(string jsonPath, string lutRoot)
        {
            List<LutChoice> result = new List<LutChoice>();
            if (!File.Exists(jsonPath)) return result;
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object parsed = serializer.DeserializeObject(File.ReadAllText(jsonPath, Encoding.UTF8));
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object item in EnumerateJsonRows(parsed))
                {
                    string path = item as string;
                    IDictionary<string, object> row = item as IDictionary<string, object>;
                    if (path == null && row != null) path = JsonString(row, "LutPath");
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    if (!string.Equals(Path.GetExtension(path), ".cube", StringComparison.OrdinalIgnoreCase)) continue;
                    string full = Path.GetFullPath(path);
                    if (!seen.Add(full)) continue;
                    result.Add(new LutChoice(GetLutDisplayName(full, lutRoot, ""), full));
                    if (result.Count >= 25) break;
                }
            }
            return result;
        }

        internal static string GetLutDisplayName(string path, string lutRoot, string emptyText)
        {
            if (string.IsNullOrEmpty(path)) return emptyText;
            try
            {
                string rootFull = Path.GetFullPath(lutRoot).TrimEnd('\\') + "\\";
                string pathFull = Path.GetFullPath(path);
                if (pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return pathFull.Substring(rootFull.Length);
            }
            catch { }
            return Path.GetFileName(path);
        }

        internal static string GetLutPreviewPath(string lutPath, string lutRoot, string lutPreviewRoot)
        {
            string expected = GetExpectedLutPreview(lutPath, lutRoot, lutPreviewRoot);
            if (!string.IsNullOrEmpty(expected) && File.Exists(expected)) return expected;

            string indexPath = Path.Combine(lutPreviewRoot, "_LUT_GALLERY_INDEX.json");
            if (!File.Exists(indexPath)) return "";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object parsed = serializer.DeserializeObject(File.ReadAllText(indexPath, Encoding.UTF8));
                string targetFull = Path.GetFullPath(lutPath);
                foreach (object item in EnumerateJsonRows(parsed))
                {
                    IDictionary<string, object> row = item as IDictionary<string, object>;
                    if (row == null) continue;
                    string indexedLut = JsonString(row, "LutPath");
                    string relative = JsonString(row, "Relative");
                    string candidate = indexedLut;
                    if ((!string.IsNullOrEmpty(candidate) && !File.Exists(candidate)) && !string.IsNullOrEmpty(relative) && !relative.StartsWith("..\\", StringComparison.Ordinal))
                        candidate = Path.Combine(lutRoot, relative);
                    bool match = false;
                    try { match = !string.IsNullOrEmpty(candidate) && string.Equals(Path.GetFullPath(candidate), targetFull, StringComparison.OrdinalIgnoreCase); }
                    catch { }
                    if (!match) continue;
                    string preview = JsonString(row, "PreviewPath");
                    if (!string.IsNullOrEmpty(preview) && File.Exists(preview)) return preview;
                }
            }
            catch { }
            return "";
        }

        private static string GetExpectedLutPreview(string lutPath, string lutRoot, string lutPreviewRoot)
        {
            if (string.IsNullOrEmpty(lutPath)) return "";
            try
            {
                string rootFull = Path.GetFullPath(lutRoot);
                string childFull = Path.GetFullPath(lutPath);
                Uri rootUri = new Uri(rootFull.TrimEnd('\\') + "\\");
                Uri childUri = new Uri(childFull);
                string relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(childUri).ToString()).Replace('/', '\\');
                string relativeDirectory;
                if (relative.StartsWith("..\\", StringComparison.Ordinal))
                    relativeDirectory = Path.Combine("_JUNCTIONS", new FileInfo(childFull).Directory.Name);
                else
                    relativeDirectory = Path.GetDirectoryName(relative);
                string outputDir = string.IsNullOrEmpty(relativeDirectory) || relativeDirectory == "." ? lutPreviewRoot : Path.Combine(lutPreviewRoot, relativeDirectory);
                return Path.Combine(outputDir, Path.GetFileNameWithoutExtension(childFull) + "_preview.jpg");
            }
            catch { return ""; }
        }

    }
}
