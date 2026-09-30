using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FilmGrainStudioPreview
{
    internal static class NativeSubtitlePrepareCore
    {
        internal static bool TryPrepare(string ffmpeg, string ffprobe, string inputVideo, IDictionary<string,string> state, int playResX, int playResY, out string assPath, out string reason)
        {
            assPath = "";
            reason = "";
            if (state == null || Get(state, "FG_SUBTITLE") != "1") return true;
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found for subtitle preparation"; return false; }
            string filterError;
            if (!HasSubtitleFilter(ffmpeg, out filterError)) { reason = "FFmpeg subtitles/libass filter is unavailable" + (string.IsNullOrWhiteSpace(filterError) ? "" : ": " + filterError); return false; }

            string mode = Get(state, "FG_SUB_MODE").ToUpperInvariant();
            if (mode != "AUTO" && mode != "EMBEDDED" && mode != "EXTERNAL") { reason = "subtitle mode is OFF or invalid"; return false; }

            string tempDir = Path.GetDirectoryName(inputVideo);
            if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir)) { reason = "input directory is unavailable for subtitle preparation"; return false; }
            string temp = Path.Combine(tempDir, "__FGSUB_" + Guid.NewGuid().ToString("N") + ".ass");

            try
            {
                string sourcePath = "";
                int? embeddedOrdinal = null;
                if (mode == "EXTERNAL")
                {
                    sourcePath = Get(state, "FG_SUB_PATH");
                    if (string.IsNullOrWhiteSpace(sourcePath)) sourcePath = SubtitleCore.FindSameName(inputVideo);
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) { reason = "external subtitle file was not found"; return false; }
                }
                else if (mode == "EMBEDDED")
                {
                    int index;
                    if (!int.TryParse(Get(state, "FG_SUB_INDEX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0) index = 0;
                    sourcePath = inputVideo;
                    embeddedOrdinal = index;
                }
                else
                {
                    sourcePath = SubtitleCore.FindSameName(inputVideo);
                    if (string.IsNullOrWhiteSpace(sourcePath))
                    {
                        List<SubtitleTrack> tracks = SubtitleCore.Probe(inputVideo, ffprobe);
                        if (tracks == null || tracks.Count == 0) { reason = "no same-name external subtitle or embedded text subtitle was found"; return false; }
                        sourcePath = inputVideo;
                        embeddedOrdinal = tracks[0].Ordinal;
                    }
                }

                string convertError;
                if (!ConvertToAss(ffmpeg, sourcePath, temp, embeddedOrdinal, out convertError))
                {
                    reason = convertError;
                    TryDelete(temp);
                    return false;
                }

                string font = Get(state, "FG_SUB_FONT");
                if (string.IsNullOrWhiteSpace(font)) font = "huiwen-mincho";
                int fontSize;
                if (!int.TryParse(Get(state, "FG_SUB_FONT_SIZE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out fontSize) || fontSize < 6 || fontSize > 300) fontSize = 69;
                string primary = NormalizeHex(Get(state, "FG_SUB_PRIMARY_HEX"), "FFFFFF");
                string border = NormalizeHex(Get(state, "FG_SUB_BORDER_HEX"), "000000");
                double outline;
                if (!double.TryParse(Get(state, "FG_SUB_OUTLINE"), NumberStyles.Float, CultureInfo.InvariantCulture, out outline)) outline = 1.0;
                double shadow;
                if (!double.TryParse(Get(state, "FG_SUB_SHADOW"), NumberStyles.Float, CultureInfo.InvariantCulture, out shadow)) shadow = 1.0;
                int marginV;
                if (!int.TryParse(Get(state, "FG_SUB_MARGINV"), NumberStyles.Integer, CultureInfo.InvariantCulture, out marginV)) marginV = 5;
                if (playResX <= 0) playResX = 1920;
                if (playResY <= 0) playResY = 1080;

                double scale = playResX / 1920.0;
                if (scale <= 0.0) scale = 1.0;
                int renderFont = Math.Max(6, (int)Math.Round(fontSize * scale));
                int renderMargin = Math.Max(0, (int)Math.Round(marginV * scale));
                double renderOutline = outline * scale;
                double renderShadow = shadow * scale;

                string styleError;
                if (!RewriteAssStyle(temp, font, renderFont, primary, border, renderOutline, renderShadow, 2, renderMargin, playResX, playResY, out styleError))
                {
                    reason = styleError;
                    TryDelete(temp);
                    return false;
                }

                assPath = temp;
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                reason = "subtitle preparation failed: " + ex.Message;
                return false;
            }
        }


        private static bool HasSubtitleFilter(string ffmpeg, out string error)
        {
            error = "";
            string stderr;
            int code = Run(ffmpeg, new List<string>{"-hide_banner","-h","filter=subtitles"}, Path.GetDirectoryName(ffmpeg), out stderr);
            if (code == 0) return true;
            error = FirstLine(stderr);
            return false;
        }

        private static bool ConvertToAss(string ffmpeg, string inputPath, string outputAss, int? subtitleOrdinal, out string error)
        {
            error = "";
            List<string> args = new List<string>();
            args.Add("-hide_banner"); args.Add("-loglevel"); args.Add("error"); args.Add("-y");
            if (!subtitleOrdinal.HasValue)
            {
                string bom = DetectBom(inputPath);
                if (string.IsNullOrEmpty(bom) && !IsStrictUtf8(inputPath)) { args.Add("-sub_charenc"); args.Add("GB18030"); }
            }
            args.Add("-i"); args.Add(inputPath);
            args.Add("-map"); args.Add(subtitleOrdinal.HasValue ? "0:s:" + subtitleOrdinal.Value.ToString(CultureInfo.InvariantCulture) : "0:s:0");
            args.Add("-c:s"); args.Add("ass"); args.Add(outputAss);

            string stderr;
            int code = Run(ffmpeg, args, Path.GetDirectoryName(inputPath), out stderr);
            if (code == 0 && File.Exists(outputAss)) return true;
            error = "subtitle conversion to ASS failed" + (string.IsNullOrWhiteSpace(stderr) ? "" : ": " + FirstLine(stderr));
            return false;
        }

        private static bool RewriteAssStyle(string path, string font, int fontSize, string primaryHex, string borderHex, double outline, double shadow, int alignment, int marginV, int playResX, int playResY, out string error)
        {
            error = "";
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { error = "cannot read prepared ASS: " + ex.Message; return false; }

            bool foundX = false, foundY = false, foundScaled = false, inStyles = false;
            string[] format = null;
            int styleCount = 0;
            string primary = ToAssColor(primaryHex), border = ToAssColor(borderHex);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] ?? "";
                string trim = line.Trim();
                if (trim.StartsWith("PlayResX", StringComparison.OrdinalIgnoreCase) && trim.IndexOf(':') >= 0) { lines[i] = "PlayResX: " + playResX.ToString(CultureInfo.InvariantCulture); foundX = true; continue; }
                if (trim.StartsWith("PlayResY", StringComparison.OrdinalIgnoreCase) && trim.IndexOf(':') >= 0) { lines[i] = "PlayResY: " + playResY.ToString(CultureInfo.InvariantCulture); foundY = true; continue; }
                if (trim.StartsWith("ScaledBorderAndShadow", StringComparison.OrdinalIgnoreCase) && trim.IndexOf(':') >= 0) { lines[i] = "ScaledBorderAndShadow: yes"; foundScaled = true; continue; }
                if (trim.StartsWith("[") && trim.EndsWith("]"))
                {
                    inStyles = string.Equals(trim, "[V4+ Styles]", StringComparison.OrdinalIgnoreCase);
                    format = null;
                    continue;
                }
                if (!inStyles) continue;
                if (trim.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = trim.Substring(trim.IndexOf(':') + 1);
                    string[] raw = body.Split(',');
                    format = new string[raw.Length];
                    for (int j = 0; j < raw.Length; j++) format[j] = raw[j].Trim();
                    continue;
                }
                if (!trim.StartsWith("Style:", StringComparison.OrdinalIgnoreCase) || format == null) continue;
                string bodyStyle = trim.Substring(trim.IndexOf(':') + 1).Trim();
                string[] values = bodyStyle.Split(',');
                if (values.Length < format.Length) continue;
                Dictionary<string,int> map = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < format.Length; j++) map[format[j]] = j;
                Set(values, map, "Fontname", font);
                Set(values, map, "Fontsize", fontSize.ToString(CultureInfo.InvariantCulture));
                Set(values, map, "PrimaryColour", primary);
                Set(values, map, "SecondaryColour", primary);
                Set(values, map, "OutlineColour", border);
                Set(values, map, "BackColour", border);
                Set(values, map, "BorderStyle", "1");
                Set(values, map, "Outline", outline.ToString("0.##", CultureInfo.InvariantCulture));
                Set(values, map, "Shadow", shadow.ToString("0.##", CultureInfo.InvariantCulture));
                Set(values, map, "Alignment", alignment.ToString(CultureInfo.InvariantCulture));
                Set(values, map, "MarginV", marginV.ToString(CultureInfo.InvariantCulture));
                Set(values, map, "MarginL", "10");
                Set(values, map, "MarginR", "10");
                lines[i] = "Style: " + string.Join(",", values);
                styleCount++;
            }
            if (styleCount == 0) { error = "no ASS style section was found after subtitle conversion"; return false; }

            int scriptInfo = -1, insertAt = lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                string trim = (lines[i] ?? "").Trim();
                if (string.Equals(trim, "[Script Info]", StringComparison.OrdinalIgnoreCase)) { scriptInfo = i; continue; }
                if (scriptInfo >= 0 && i > scriptInfo && trim.StartsWith("[") && trim.EndsWith("]")) { insertAt = i; break; }
            }
            List<string> finalLines = new List<string>(lines.Length + 3);
            for (int i = 0; i <= lines.Length; i++)
            {
                if (i == insertAt)
                {
                    if (!foundX) finalLines.Add("PlayResX: " + playResX.ToString(CultureInfo.InvariantCulture));
                    if (!foundY) finalLines.Add("PlayResY: " + playResY.ToString(CultureInfo.InvariantCulture));
                    if (!foundScaled) finalLines.Add("ScaledBorderAndShadow: yes");
                }
                if (i < lines.Length) finalLines.Add(lines[i]);
            }
            try { File.WriteAllLines(path, finalLines.ToArray(), new UTF8Encoding(false)); return true; }
            catch (Exception ex) { error = "cannot write styled ASS: " + ex.Message; return false; }
        }

        private static void Set(string[] values, Dictionary<string,int> map, string key, string value)
        {
            int index;
            if (map.TryGetValue(key, out index) && index >= 0 && index < values.Length) values[index] = value;
        }

        private static string NormalizeHex(string value, string fallback)
        {
            string v = (value ?? "").Trim().TrimStart('#');
            if (v.Length != 6) return fallback;
            for (int i = 0; i < v.Length; i++) if (!Uri.IsHexDigit(v[i])) return fallback;
            return v.ToUpperInvariant();
        }

        private static string ToAssColor(string rgb)
        {
            return "&H00" + rgb.Substring(4,2) + rgb.Substring(2,2) + rgb.Substring(0,2);
        }

        private static string DetectBom(string path)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00) return "UTF-32LE";
                if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF) return "UTF-32BE";
                if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return "UTF-8";
                if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return "UTF-16LE";
                if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return "UTF-16BE";
            }
            catch { }
            return "";
        }

        private static bool IsStrictUtf8(string path)
        {
            try { new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)); return true; }
            catch { return false; }
        }

        private static int Run(string exe, IList<string> args, string workingDirectory, out string stderr)
        {
            stderr = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = JoinArgs(args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(exe) : workingDirectory;
                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { stderr = ex.Message; return -1; }
        }

        private static string JoinArgs(IEnumerable<string> args)
        {
            StringBuilder b = new StringBuilder();
            foreach (string x in args) { if (b.Length > 0) b.Append(' '); b.Append(Quote(x ?? "")); }
            return b.ToString();
        }

        private static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new char[]{' ','\t','\n','\v','"'}) < 0) return s;
            StringBuilder b = new StringBuilder(); b.Append('"'); int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { b.Append('\\', bs * 2 + 1); b.Append('"'); bs = 0; continue; }
                if (bs > 0) { b.Append('\\', bs); bs = 0; }
                b.Append(c);
            }
            if (bs > 0) b.Append('\\', bs * 2);
            b.Append('"'); return b.ToString();
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            using (StringReader r = new StringReader(value.Trim())) return r.ReadLine() ?? "";
        }

        private static string Get(IDictionary<string,string> state, string key)
        {
            string value;
            return state != null && state.TryGetValue(key, out value) ? (value ?? "") : "";
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
