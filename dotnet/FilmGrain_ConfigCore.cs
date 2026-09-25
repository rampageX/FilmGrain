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
            new KeyValuePair<string, string[]>("Paths", new string[] { "FFMPEG_DIR", "GRAV1SYNTH", "GRAIN_ROOT", "LUT_ROOT", "TEMP_MODE", "TEMP_CUSTOM_DIR", "OUTPUT_MODE", "OUTPUT_CUSTOM_DIR" }),
            new KeyValuePair<string, string[]>("General", new string[] { "LANGUAGE" }),
            new KeyValuePair<string, string[]>("LUTGallery", new string[] { "SMART_FILTER_ENABLED" }),
            new KeyValuePair<string, string[]>("Advanced.Encoding", new string[] { "H264_HIGH10", "X264_RATE_MODE", "X264_PRESET", "NVENC_SPATIAL_AQ", "NVENC_TEMPORAL_AQ" }),
            new KeyValuePair<string, string[]>("Advanced.Interpolation", new string[] { "SVP_ALGO", "SVP_ANALYSE", "SVP_MASK_AREA" }),
            new KeyValuePair<string, string[]>("Advanced.HDR", new string[] { "HDR_POLICY", "TONE_MAP_ALGO" }),
            new KeyValuePair<string, string[]>("Advanced.Other", new string[] { "CINEMATIC_CROP_PER_SIDE", "COLOR_PREVIEW_LARGE_UI" }),
            new KeyValuePair<string, string[]>("ColorCorrection", new string[] { "COLOR_CORRECTION_ENABLED", "COLOR_CONTRAST", "COLOR_BRIGHTNESS", "COLOR_SATURATION", "COLOR_GAMMA", "COLOR_BLACK_WHITE" })
        };

        public string ConfigPath { get { return configPath; } }

        public FgsConfig(string appRoot)
        {
            configPath = Path.Combine(appRoot, "FilmGrain_Config.ini");
            defaultPath = Path.Combine(appRoot, "FilmGrain_Config.default.ini");
            BuildDefaults();
            Load();
        }

        private void BuildDefaults()
        {
            defaults["FFMPEG_DIR"] = @"E:\EnCoder\FFMpeg\x64\bin";
            defaults["GRAV1SYNTH"] = @"E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe";
            defaults["GRAIN_ROOT"] = @"D:\Film_Grain";
            defaults["LUT_ROOT"] = @"E:\Adobe Portable\LUTs";
            defaults["TEMP_MODE"] = "VIDEO";
            defaults["TEMP_CUSTOM_DIR"] = "";
            defaults["OUTPUT_MODE"] = "VIDEO";
            defaults["OUTPUT_CUSTOM_DIR"] = "";
            defaults["LANGUAGE"] = "zh-CN";
            defaults["SMART_FILTER_ENABLED"] = "false";
            defaults["H264_HIGH10"] = "false";
            defaults["X264_RATE_MODE"] = "VBR1";
            defaults["X264_PRESET"] = "faster";
            defaults["NVENC_SPATIAL_AQ"] = "8";
            defaults["NVENC_TEMPORAL_AQ"] = "false";
            defaults["SVP_ALGO"] = "13";
            defaults["SVP_ANALYSE"] = "ENCODEGUI";
            defaults["SVP_MASK_AREA"] = "100";
            defaults["HDR_POLICY"] = "AUTO";
            defaults["TONE_MAP_ALGO"] = "hable";
            defaults["CINEMATIC_CROP_PER_SIDE"] = "0";
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
                if (key == "HEVC_SPATIAL_AQ") key = "NVENC_SPATIAL_AQ";
                if (key == "HEVC_TEMPORAL_AQ") key = "NVENC_TEMPORAL_AQ";
                if (!defaults.ContainsKey(key)) continue;

                values[key] = value;
                if (key == "FFMPEG_DIR") hasFfmpegDir = true;
            }

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

        public string Get(string key)
        {
            string value;
            if (values.TryGetValue(key, out value)) return value;
            if (defaults.TryGetValue(key, out value)) return value;
            return "";
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
            if (key == "FFMPEG_DIR" || key == "GRAV1SYNTH" || key == "GRAIN_ROOT" || key == "LUT_ROOT" || key == "TEMP_CUSTOM_DIR" || key == "OUTPUT_CUSTOM_DIR")
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
