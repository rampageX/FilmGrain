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
    internal sealed class LanguagePack
    {
        private readonly Dictionary<string, string> fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Code { get; private set; }
        public string Preference { get; private set; }
        public string Root { get; private set; }

        public LanguagePack(string appRoot)
        {
            Root = Path.Combine(appRoot, "Lang");
            LoadFile(Path.Combine(Root, "zh-CN.ini"), fallback);
            Preference = NormalizePreference(ReadLanguagePreference(appRoot));
            Code = ResolveCode(Preference, CultureInfo.CurrentUICulture);
            string selected = Path.Combine(Root, Code + ".ini");
            if (!File.Exists(selected))
            {
                Code = "zh-CN";
                selected = Path.Combine(Root, "zh-CN.ini");
            }
            LoadFile(selected, current);
        }

        public string T(string key)
        {
            string value;
            if (current.TryGetValue(key, out value)) return value;
            if (fallback.TryGetValue(key, out value)) return value;
            return key;
        }

        public List<LanguageChoice> GetChoices()
        {
            return new List<LanguageChoice> {
                new LanguageChoice("AUTO", Code=="zh-CN"?"自动":"Auto"),
                new LanguageChoice("zh-CN","中文"),
                new LanguageChoice("en-US","English")
            };
        }

        internal static string NormalizePreference(string preference)
        {
            if(string.Equals(preference,"zh-CN",StringComparison.OrdinalIgnoreCase))return "zh-CN";
            if(string.Equals(preference,"en-US",StringComparison.OrdinalIgnoreCase))return "en-US";
            return "AUTO";
        }
        internal static string ResolveCode(string preference,CultureInfo systemUiCulture)
        {
            string normalized=NormalizePreference(preference);
            if(normalized!="AUTO")return normalized;
            return systemUiCulture!=null&&string.Equals(systemUiCulture.TwoLetterISOLanguageName,"zh",StringComparison.OrdinalIgnoreCase)?"zh-CN":"en-US";
        }
        internal static string LogFontName(string code)
        {
            return string.Equals(code,"zh-CN",StringComparison.OrdinalIgnoreCase)?"Microsoft YaHei UI":"Consolas";
        }

        private static string ReadLanguagePreference(string appRoot)
        {
            string config = Path.Combine(appRoot, "FilmGrain_Config.ini");
            if (!File.Exists(config)) config = Path.Combine(appRoot, "FilmGrain_Config.default.ini");
            if (!File.Exists(config)) return "AUTO";
            try
            {
                string[] lines = File.ReadAllLines(config, new UTF8Encoding(false, true));
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int p = raw.IndexOf('=');
                    if (p <= 0) continue;
                    string key = raw.Substring(0, p).Trim();
                    if (!string.Equals(key, "LANGUAGE", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = raw.Substring(p + 1).Trim().Trim('"');
                    if (value.Length > 0) return value;
                }
            }
            catch { }
            return "AUTO";
        }

        private static void LoadFile(string path, Dictionary<string, string> target)
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
            foreach (string raw in lines)
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                int p = raw.IndexOf('=');
                if (p <= 0) continue;
                string key = raw.Substring(0, p).Trim();
                if (key.Length == 0) continue;
                string value = raw.Substring(p + 1).Replace("\\n", Environment.NewLine);
                target[key] = value;
            }
        }
    }

    internal sealed class LanguageChoice
    {
        public string Code { get; private set; }
        public string DisplayName { get; private set; }
        public LanguageChoice(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }
        public override string ToString() { return DisplayName; }
    }


}
