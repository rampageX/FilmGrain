using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed class SubtitleState
    {
        internal bool Enabled;
        internal string Mode = "OFF";
        internal int EmbeddedIndex;
        internal string ExternalPath = "";
        internal string FontName = "huiwen-mincho";
        internal int FontSize = 69;
        internal string PrimaryHex = "FFFFFF";
        internal string BorderHex = "000000";
        internal decimal Outline = 1m;
        internal decimal Shadow = 1m;
        internal int MarginV = 5;
        internal string Label = "";

        internal void WriteEnvironment(SortedDictionary<string, string> state)
        {
            state["FG_SUBTITLE"] = Enabled ? "1" : "0";
            state["FG_UPLOAD_SUBTITLE"] = state["FG_SUBTITLE"];
            state["FG_SUB_MODE"] = Mode;
            state["FG_SUB_INDEX"] = EmbeddedIndex.ToString(CultureInfo.InvariantCulture);
            state["FG_SUB_PATH"] = ExternalPath;
            state["FG_SUB_FONT"] = FontName;
            state["FG_SUB_FONT_SIZE"] = FontSize.ToString(CultureInfo.InvariantCulture);
            state["FG_SUB_PRIMARY_HEX"] = PrimaryHex;
            state["FG_SUB_BORDER_HEX"] = BorderHex;
            state["FG_SUB_OUTLINE"] = Outline.ToString("0.##", CultureInfo.InvariantCulture);
            state["FG_SUB_SHADOW"] = Shadow.ToString("0.##", CultureInfo.InvariantCulture);
            state["FG_SUB_MARGINV"] = MarginV.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class SubtitleTrack
    {
        internal int Ordinal;
        internal string Codec;
        internal string Language;
        internal string Title;
    }

    internal static class SubtitleCore
    {
        private static readonly HashSet<string> TextCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "subrip", "ass", "ssa", "webvtt", "mov_text", "text", "sami", "microdvd", "jacosub", "realtext", "subviewer", "subviewer1", "vplayer" };

        internal static List<SubtitleTrack> Probe(string videoPath, string ffprobe)
        {
            List<SubtitleTrack> tracks = new List<SubtitleTrack>();
            if (!File.Exists(videoPath) || !File.Exists(ffprobe)) return tracks;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffprobe);
                psi.Arguments = "-v error -select_streams s -show_entries stream=index,codec_name:stream_tags=language,title -of json " + Quote(videoPath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process process = Process.Start(psi))
                {
                    string json = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0) return tracks;
                    object parsed = new JavaScriptSerializer().DeserializeObject(json);
                    var root = parsed as Dictionary<string, object>;
                    object raw;
                    if (root == null || !root.TryGetValue("streams", out raw)) return tracks;
                    var streams = raw as object[];
                    if (streams == null) return tracks;
                    for (int ordinal = 0; ordinal < streams.Length; ordinal++)
                    {
                        var stream = streams[ordinal] as Dictionary<string, object>;
                        if (stream == null) continue;
                        string codec = Get(stream, "codec_name").ToLowerInvariant();
                        if (!TextCodecs.Contains(codec)) continue;
                        object tagsRaw;
                        var tags = stream.TryGetValue("tags", out tagsRaw) ? tagsRaw as Dictionary<string, object> : null;
                        tracks.Add(new SubtitleTrack { Ordinal = ordinal, Codec = codec,
                            Language = tags == null || Get(tags, "language") == "" ? "und" : Get(tags, "language"),
                            Title = tags == null ? "" : Get(tags, "title") });
                    }
                }
            }
            catch { }
            return tracks;
        }

        private static string Get(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        internal static string FindSameName(string videoPath)
        {
            if (string.IsNullOrEmpty(videoPath)) return null;
            string directory = Path.GetDirectoryName(videoPath);
            string basename = Path.GetFileNameWithoutExtension(videoPath);
            foreach (string ext in new[] { ".ass", ".srt", ".ssa", ".vtt" })
            {
                string path = Path.Combine(directory, basename + ext);
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}
