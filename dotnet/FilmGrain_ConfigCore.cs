using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed class FgsConfig
    {
        private readonly string configPath;
        private readonly string defaultPath;
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly KeyValuePair<string, string[]>[] Sections = new KeyValuePair<string, string[]>[]
        {
            new KeyValuePair<string, string[]>("Advanced.EncoderAQ", new[] { "HEVC_SPATIAL_AQ", "HEVC_TEMPORAL_AQ", "H264_SPATIAL_AQ", "H264_TEMPORAL_AQ" }),
            new KeyValuePair<string, string[]>("Paths", new string[] { "FFMPEG_DIR", "FRUC_FFMPEG_SAME_AS_MAIN", "FRUC_FFMPEG_PATH", "GRAV1SYNTH", "GRAIN_ROOT", "LUT_ROOT", "TEMP_MODE", "TEMP_CUSTOM_DIR", "OUTPUT_MODE", "OUTPUT_CUSTOM_DIR" }),
            new KeyValuePair<string, string[]>("General", new string[] { "LANGUAGE", "UI_THEME", "MAIN_WINDOW", "SETUP_VERSION", "NETWORK_PROXY_MODE", "NETWORK_PROXY_URL", "LOG_ENTRY_LIMIT", "GRAIN_ENABLED", "AV1_GRAIN_STRENGTH", "AV1_COPY_FAST", "AV1_COPY_FULL_VERIFY" }),
            new KeyValuePair<string, string[]>("LUTGallery", new string[] { "SMART_FILTER_ENABLED", "LUT_GALLERY_SIZE", "LUT_GALLERY_FIXED" }),
            new KeyValuePair<string, string[]>("Advanced.Encoding", new string[] { "H264_HIGH10", "X264_RATE_MODE", "X264_PRESET", "NVENC_SPATIAL_AQ", "NVENC_TEMPORAL_AQ", "AUDIO_MODE", "AUDIO_BITRATE", "AV1_MULTIPASS", "HEVC_MULTIPASS", "H264_MULTIPASS", "HEVC_BFRAMES", "H264_BFRAMES", "HEVC_BREF_MODE", "H264_BREF_MODE", "HEVC_LOOKAHEAD", "H264_LOOKAHEAD" }),
            new KeyValuePair<string, string[]>("Advanced.Interpolation", new string[] { "SVP_ALGO", "SVP_ANALYSE", "SVP_MASK_AREA", "SVP_SPEED", "SVP_TUNING", "SVP_BLOCK_SIZE", "SVP_DEDUP", "SVP_STILL", "SVP_STILL_LIMIT", "SVP_STILL_EDGE", "SVP_STILL_TOLERANCE", "SVP_PEEL", "SVP_PEEL_LIMIT", "FRUC_PERF", "FRUC_GRID", "INTERPOLATION_TARGET_FPS" }),
            new KeyValuePair<string, string[]>("Advanced.HDR", new string[] { "HDR_POLICY", "TONE_MAP_ALGO" }),
            new KeyValuePair<string, string[]>("Advanced.Other", new string[] { "CINEMATIC_CROP_PER_SIDE", "CINEMATIC_CROP_HORIZONTAL", "CINEMATIC_NO_CROP", "COLOR_PREVIEW_LARGE_UI" }),
            new KeyValuePair<string, string[]>("ColorCorrection", new string[] { "COLOR_CORRECTION_ENABLED", "COLOR_CONTRAST", "COLOR_BRIGHTNESS", "COLOR_SATURATION", "COLOR_GAMMA", "COLOR_BLACK_WHITE" })
        };

        public string ConfigPath { get { return configPath; } }

        public FgsConfig(string appRoot, string overridePath)
        {
            configPath = overridePath ?? Path.Combine(appRoot, "FilmGrain_Config.ini");
            defaultPath = Path.Combine(appRoot, "FilmGrain_Config.default.ini");
            BuildDefaults();
            Load();
        }

        private void BuildDefaults()
        {
            defaults["FFMPEG_DIR"] = @"E:\EnCoder\FFMpeg\x64\bin";
            defaults["GRAV1SYNTH"] = @"E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe";
            defaults["MAIN_WINDOW"] = "";
            defaults["LUT_GALLERY_SIZE"] = "";
            defaults["LUT_GALLERY_FIXED"] = "false";
            defaults["GRAIN_ENABLED"] = "true";
            defaults["AV1_GRAIN_STRENGTH"] = "100";
            defaults["AV1_COPY_FAST"] = "true";
            defaults["AV1_COPY_FULL_VERIFY"] = "false";
            defaults["GRAIN_ROOT"] = @"D:\Film_Grain";
            defaults["LUT_ROOT"] = @"E:\Adobe Portable\LUTs";
            defaults["TEMP_MODE"] = "VIDEO";
            defaults["TEMP_CUSTOM_DIR"] = "";
            defaults["OUTPUT_MODE"] = "VIDEO";
            defaults["OUTPUT_CUSTOM_DIR"] = "";
            defaults["LANGUAGE"] = "zh-CN";
            defaults["UI_THEME"] = "SYSTEM";
            defaults["SETUP_VERSION"] = "0";
            defaults["NETWORK_PROXY_MODE"] = "SYSTEM";
            defaults["NETWORK_PROXY_URL"] = "";
            defaults["LOG_ENTRY_LIMIT"] = "100";
            defaults["FRUC_FFMPEG_PATH"] = "";
            // Empty means infer from an older configuration: a saved FRUC path implies custom mode.
            defaults["FRUC_FFMPEG_SAME_AS_MAIN"] = "";
            defaults["SMART_FILTER_ENABLED"] = "false";
            defaults["H264_HIGH10"] = "false";
            defaults["X264_RATE_MODE"] = "VBR1";
            defaults["X264_PRESET"] = "faster";
            defaults["AUDIO_MODE"] = "FGS";
            defaults["AUDIO_BITRATE"] = "256";
            defaults["AV1_MULTIPASS"] = "FGS";
            defaults["HEVC_MULTIPASS"] = "FGS";
            defaults["H264_MULTIPASS"] = "FGS";
            defaults["HEVC_BFRAMES"] = "FGS";
            defaults["H264_BFRAMES"] = "FGS";
            defaults["HEVC_BREF_MODE"] = "FGS";
            defaults["H264_BREF_MODE"] = "FGS";
            defaults["HEVC_LOOKAHEAD"] = "FGS";
            defaults["H264_LOOKAHEAD"] = "FGS";

            defaults["HEVC_SPATIAL_AQ"] = "8";
            defaults["HEVC_TEMPORAL_AQ"] = "false";
            defaults["H264_SPATIAL_AQ"] = "8";
            defaults["H264_TEMPORAL_AQ"] = "false";
            defaults["NVENC_SPATIAL_AQ"] = "8";
            defaults["NVENC_TEMPORAL_AQ"] = "false";
            defaults["SVP_ALGO"] = "13";
            defaults["SVP_ANALYSE"] = "ENCODEGUI";
            defaults["SVP_MASK_AREA"] = "100";
            defaults["SVP_SPEED"] = "FGS";
            defaults["SVP_TUNING"] = "FGS";
            defaults["SVP_BLOCK_SIZE"] = "auto";
            defaults["SVP_DEDUP"] = "0";
            defaults["SVP_STILL"] = "false";
            defaults["SVP_STILL_LIMIT"] = "4.5";
            defaults["SVP_STILL_EDGE"] = "5";
            defaults["SVP_STILL_TOLERANCE"] = "30";
            defaults["SVP_PEEL"] = "false";
            defaults["SVP_PEEL_LIMIT"] = "6";

            defaults["FRUC_PERF"] = "medium";
            defaults["FRUC_GRID"] = "auto";
            defaults["INTERPOLATION_TARGET_FPS"] = "60";
            defaults["HDR_POLICY"] = "AUTO";
            defaults["TONE_MAP_ALGO"] = "hable";
            defaults["CINEMATIC_CROP_PER_SIDE"] = "0";
            defaults["CINEMATIC_CROP_HORIZONTAL"] = "0";
            defaults["CINEMATIC_NO_CROP"] = "0";
            defaults["COLOR_PREVIEW_LARGE_UI"] = "false";
            defaults["COLOR_CORRECTION_ENABLED"] = "false";
            defaults["COLOR_CONTRAST"] = "1.00";
            defaults["COLOR_BRIGHTNESS"] = "0.00";
            defaults["COLOR_SATURATION"] = "1.00";
            defaults["COLOR_GAMMA"] = "1.00";
            defaults["COLOR_BLACK_WHITE"] = "false";
        }

        public void Load()
        {
            values.Clear();
            foreach (KeyValuePair<string, string> pair in defaults) values[pair.Key] = pair.Value;

            if (!File.Exists(configPath) && File.Exists(defaultPath))
            {
                try { File.Copy(defaultPath, configPath, false); } catch { }
            }
            if (!File.Exists(configPath)) return;

            string legacyFfmpeg = "";
            string legacyFfprobe = "";
            bool hasFfmpegDir = false;
            var explicitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(configPath, new UTF8Encoding(false, true));
            foreach (string raw in lines)
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";") || (trimmed.StartsWith("[") && trimmed.EndsWith("]"))) continue;
                int p = raw.IndexOf('=');
                if (p <= 0) continue;
                string key = raw.Substring(0, p).Trim().ToUpperInvariant();
                string value = raw.Substring(p + 1).Trim().Trim('"');

                if (key == "FFMPEG") { legacyFfmpeg = value; continue; }
                if (key == "FFPROBE") { legacyFfprobe = value; continue; }
                if (!defaults.ContainsKey(key)) continue;

                explicitKeys.Add(key);
                values[key] = value;
                if (key == "FFMPEG_DIR") hasFfmpegDir = true;
            }

            // Seed missing per-encoder settings from the previous shared value.
            // Explicit settings win regardless of INI ordering; Load never writes the user's file.
            foreach (string encoder in new[] { "HEVC", "H264" })
                foreach (string suffix in new[] { "SPATIAL_AQ", "TEMPORAL_AQ" })
                    if (!explicitKeys.Contains(encoder + "_" + suffix))
                        values[encoder + "_" + suffix] = Get("NVENC_" + suffix);

            if (!hasFfmpegDir)
            {
                string legacyExe = legacyFfmpeg.Length > 0 ? legacyFfmpeg : legacyFfprobe;
                if (legacyExe.Length > 0)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(legacyExe);
                        if (!string.IsNullOrEmpty(dir)) values["FFMPEG_DIR"] = dir.TrimEnd('\\');
                    }
                    catch { }
                }
            }
        }

        internal string EncoderAq(string mode, bool temporal)
        {
            string prefix = mode == "HEVC" ? "HEVC" : mode == "H264" ? "H264" : "NVENC";
            return Get(prefix + (temporal ? "_TEMPORAL_AQ" : "_SPATIAL_AQ"));
        }

        public string Get(string key)
        {
            string value;
            if (values.TryGetValue(key, out value)) return value;
            if (defaults.TryGetValue(key, out value)) return value;
            return "";
        }

        public bool FrucUsesMainFfmpeg()
        {
            string setting = Get("FRUC_FFMPEG_SAME_AS_MAIN");
            if (setting.Equals("true", StringComparison.OrdinalIgnoreCase) || setting == "1") return true;
            if (setting.Equals("false", StringComparison.OrdinalIgnoreCase) || setting == "0") return false;
            return string.IsNullOrWhiteSpace(Get("FRUC_FFMPEG_PATH"));
        }

        public string GetDefault(string key)
        {
            string value;
            return defaults.TryGetValue(key, out value) ? value : "";
        }

        public bool GetBool(string key)
        {
            bool value;
            return bool.TryParse(Get(key), out value) && value;
        }

        public int GetInt(string key, int fallback)
        {
            int value;
            return int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        public double GetDouble(string key, double fallback)
        {
            double value;
            return double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        public void SaveValue(string key, string value)
        {
            Dictionary<string, string> updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            updates[key] = value;
            Save(updates);
        }

        public void Save(IDictionary<string, string> updates)
        {
            Load();
            foreach (KeyValuePair<string, string> pair in updates)
            {
                string key = (pair.Key ?? "").Trim().ToUpperInvariant();
                if (!defaults.ContainsKey(key)) continue;
                values[key] = Normalize(key, pair.Value ?? "");
            }

            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string[]> section in Sections)
            {
                lines.Add("[" + section.Key + "]");
                foreach (string key in section.Value) lines.Add(key + "=" + Get(key));
                lines.Add("");
            }

            string dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temporaryPath = configPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporaryPath, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(configPath)) File.Replace(temporaryPath, configPath, null);
                else File.Move(temporaryPath, configPath);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { /* Preserve the original write error; the old config remains in place. */ }
            }
        }

        private static string Normalize(string key, string value)
        {
            string text = value ?? "";
            if (key == "NETWORK_PROXY_URL") text = text.Trim().Trim('"');
            else if (key == "FFMPEG_DIR" || key == "GRAV1SYNTH" || key == "GRAIN_ROOT" || key == "LUT_ROOT" || key == "TEMP_CUSTOM_DIR" || key == "OUTPUT_CUSTOM_DIR")
            {
                text = text.Trim().Trim('"');
                if (key == "FFMPEG_DIR") text = text.TrimEnd('\\');
            }
            else text = text.Trim();

            if (text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0) throw new InvalidDataException("Invalid FilmGrain_Config value for " + key + ".");
            return text;
        }
    }

}
