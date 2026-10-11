using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal static class NativeLutRecords
    {
        // Read tolerant old singleton / string formats, but refuse damaged records on write.
        internal static List<Dictionary<string,object>> Read(string path)
        {
            var records=new List<Dictionary<string,object>>();
            if(!File.Exists(path) || string.IsNullOrWhiteSpace(File.ReadAllText(path,Encoding.UTF8))) return records;
            object parsed=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path,Encoding.UTF8));
            IEnumerable rows=parsed is string || parsed is IDictionary<string,object> ? new object[]{parsed} : parsed as IEnumerable;
            if(rows==null) throw new IOException("记录文件格式无效："+path);
            foreach(object item in rows) {
                var map=item as Dictionary<string,object>;
                if(item is string) map=new Dictionary<string,object>{{"LutPath",item}};
                if(map==null || !map.ContainsKey("LutPath") || string.IsNullOrWhiteSpace(Convert.ToString(map["LutPath"]))) throw new IOException("记录缺少 LutPath；原文件保留");
                records.Add(map);
            }
            return records;
        }
        internal static string Key(string path) { return Path.GetFullPath(path).ToLowerInvariant(); }
        internal static void Recent(string previewRoot,string lut)
        {
            if(!File.Exists(lut)||!Path.GetExtension(lut).Equals(".cube",StringComparison.OrdinalIgnoreCase)) throw new IOException("所选 CUBE 不存在");
            string path=Path.Combine(previewRoot,"_LUT_GALLERY_RECENT.json");
            var old=Read(path); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Key(lut)};
            var rows=new List<Dictionary<string,object>> {new Dictionary<string,object>{{"LutPath",Path.GetFullPath(lut)},{"LastUsed",DateTime.UtcNow.ToString("o")}}};
            foreach(var row in old) if(seen.Add(Key(Convert.ToString(row["LutPath"])))) { rows.Add(row); if(rows.Count>=25)break; }
            NativeLibraryTools.WriteJson(path,rows);
        }
        internal static bool ToggleFavorite(string previews,string lut)
        {
            string path=Path.Combine(previews,"_LUT_GALLERY_FAVORITES.json");
            var rows=Read(path); bool exists=rows.Any(x=>Key(Convert.ToString(x["LutPath"]))==Key(lut));
            rows=rows.Where(x=>Key(Convert.ToString(x["LutPath"]))!=Key(lut)).ToList();
            if(!exists) rows.Add(new Dictionary<string,object>{{"LutPath",lut}});
            NativeLibraryTools.WriteJson(path,rows); return !exists;
        }
    }
    internal sealed class NativeLutClassification
    {
        public string Type,Reason; public int Score;
        public bool Technical { get { return Type=="Technical"; } }
    }
    internal static class NativeLutSmartFilter
    {
        static bool Match(string text,string pattern) { return Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase); }
        static string Header(string path)
        {
            var lines=new List<string>();
            try { using(var reader=new StreamReader(path,Encoding.UTF8,true)) {
                for(int n=0;n<240;n++) { string line=reader.ReadLine(); if(line==null)break;
                    if(Match(line,@"^\s*[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\s+[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\s+[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?(?:\s|$)"))break;
                    lines.Add(line);
                }
            } } catch { }
            return string.Join("\n",lines.ToArray());
        }
        static Match Family(LutGalleryEntry e) { return Regex.Match(Path.GetFileNameWithoutExtension(e.LutPath),FamilyPattern,RegexOptions.IgnoreCase); }
        static string FamilyKey(LutGalleryEntry e,Match m) { return ((Path.GetDirectoryName(e.Relative)??"")+"|"+m.Groups["family"].Value.Trim(' ','-','_','.')).ToLowerInvariant(); }
        internal static Dictionary<string,NativeLutClassification> Classify(IList<LutGalleryEntry> entries)
        {
            var groups=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach(var e in entries) { var m=Family(e); if(!m.Success)continue; string key=FamilyKey(e,m);
                if(!groups.ContainsKey(key)) groups[key]=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                groups[key].Add(m.Groups["input"].Value+">"+m.Groups["output"].Value);
            }
            var result=new Dictionary<string,NativeLutClassification>(StringComparer.OrdinalIgnoreCase);
            foreach(var e in entries) {
                string header=Header(e.LutPath),probe=e.Relative+"\n"+header; var lines=header.Split('\n');
                var reasons=new List<string>(); int score=0;
                var input=lines.Where(x=>Match(x,@"^\s*#?\s*(?:input|input color ?space|input[_ -]?colorspace|source|source color ?space|source[_ -]?colorspace)\s*[:=]")).ToArray();
                var output=lines.Where(x=>Match(x,@"^\s*#?\s*(?:output|output color ?space|output[_ -]?colorspace|target|target color ?space|destination|destination color ?space)\s*[:=]")).ToArray();
                if(input.Length>0&&output.Length>0) {
                    score+=10; reasons.Add("Explicit Input/Output header fields");
                    if(Match(string.Join(" ",input),Space)&&Match(string.Join(" ",output),Space)) {score+=2;reasons.Add("Recognized source/target color spaces");}
                }
                bool conversion=false;
                foreach(string line in probe.Split('\n')) {
                    if(!Match(line,@"(?:\bto\b|[-=]+>)"))continue;
                    var parts=Regex.Split(line,@"(?:\bto\b|\s*[-=]+>\s*)",RegexOptions.IgnoreCase);
                    for(int n=0;n<parts.Length-1;n++) if(Match(parts[n],Space)&&Match(parts[n+1],Space)) { conversion=true;break; }
                    if(conversion)break;
                }
                if(conversion) {score+=9;reasons.Add("Recognized color-space conversion pair");}
                if(Match(probe,Technical)) {score+=6;reasons.Add("Utility/technical/transform marker");}
                if(Match(probe,Operation)) {score+=7;reasons.Add("Technical range/tone/gamut operation marker");}
                var family=Family(e); bool multi=family.Success&&groups[FamilyKey(e,family)].Count>=3;
                if(multi) reasons.Add("Multi-input creative family: "+family.Groups["family"].Value+" / "+groups[FamilyKey(e,family)].Count+" variants");
                bool creative=Match(probe,Creative); if(creative)reasons.Add("Creative/look marker");
                string type=multi || creative&&score>=6 ? "Combined" : score>=6 ? "Technical" : creative ? "Creative" : "Keep";
                if(reasons.Count==0)reasons.Add("No strong technical evidence");
                result[e.LutPath]=new NativeLutClassification {Type=type,Score=score,Reason=string.Join("; ",reasons.ToArray())};
            }
            return result;
        }
        internal static void Report(string previews,IList<LutGalleryEntry> entries,Dictionary<string,NativeLutClassification> classes)
        {
            Func<string,string> quote=x=>"\""+(x??"").Replace("\"","\"\"")+"\"";
            var lines=new List<string>{"\"Type\",\"Score\",\"RelativePath\",\"Reason\",\"LutPath\""};
            foreach(var e in entries.OrderBy(x=>classes[x.LutPath].Type).ThenBy(x=>x.Relative)) {var c=classes[e.LutPath];lines.Add(string.Join(",",new[]{quote(c.Type),quote(c.Score.ToString()),quote(e.Relative),quote(c.Reason),quote(e.LutPath)}));}
            File.WriteAllText(Path.Combine(previews,"_LUT_SMART_FILTER_REPORT.csv"),string.Join("\r\n",lines.ToArray())+"\r\n",new UTF8Encoding(true));
        }
        // Patterns and scoring are carried over from the accepted V2 selector without tuning.
        const string Space = @"(?i)(?:\b(?:rec|bt|itu[\s._-]*r[\s._-]*bt)?[\s._-]*(?:709|2020)\b|\b(?:pq|st[\s._-]*2084|hlg|arib[\s._-]*std[\s._-]*b67)\b|\b(?:dci[\s._-]*p3|display[\s._-]*p3|p3)\b|\baces(?:cg|cc|cct)?\b|\b(?:logc(?:3|4)?|arri[\s._-]*logc?|s[\s._-]*log(?:1|2|3)?|slog(?:1|2|3)?|v[\s._-]*log|vlog|c[\s._-]*log(?:1|2|3)?|clog(?:1|2|3)?|f[\s._-]*log(?:2c|2)?|flog(?:2c|2)?|d[\s._-]*log|dlog|n[\s._-]*log|nlog|cineon|redlogfilm|log3g10|bmd[\s._-]*film|blackmagic[\s._-]*film)\b|\b(?:redwidegamut|davinci[\s._-]*wide[\s._-]*gamut|blackmagic[\s._-]*wide[\s._-]*gamut|s[\s._-]*gamut(?:3(?:[\s._-]*cine)?)?|v[\s._-]*gamut|v709|lc[\s._-]*709a)\b|\blinear\b)";
        const string Technical = @"(?i)\b(?:utility|utilities|technical|conversion|convert|transform|color[\s._-]*space[\s._-]*transform|colour[\s._-]*space[\s._-]*transform|colorspace[\s._-]*transform|cst|idt|odt)\b";
        const string Operation = @"(?i)(?:tone[\s._-]*map|tonemap|gamut[\s._-]*map|range[\s._-]*conversion|full[\s._-]*(?:range[\s._-]*)?(?:to|[-=]+>)\s*legal|legal[\s._-]*(?:range[\s._-]*)?(?:to|[-=]+>)\s*full|video[\s._-]*levels?[\s._-]*(?:to|[-=]+>)\s*data[\s._-]*levels?|data[\s._-]*levels?[\s._-]*(?:to|[-=]+>)\s*video[\s._-]*levels?|\bshaper\b)";
        const string Creative = @"(?i)(?:\bcreative\b|\blook\b|\bcinematic\b|\bteal\b|\borange\b|\bvintage\b|\bretro\b|\bbleach\b|\bcross[\s._-]*process\b|\bwarm\b|\bcool\b|\bfilm[\s._-]*(?:look|tone|boost|print|emulation)\b|\bprint[\s._-]*film\b|\b(?:kodak[\s._-]*)?(?:2383|2393)\b|\beterna(?:[\s._-]*bb)?\b)";
        const string FamilyPattern = @"^(?<family>.+?)[._ -]+(?<input>(?:ACEScct|ACEScc|ACEScg|DWG|RWG|BMDFilmGen(?:4|5)|BolexLog|CanonLog(?:1|2|3)?|Cineon|DJIDLog|DragonColor2RLF|FLog2C|FLog2|FLog|KineLog3|LLog|LogC(?:3|4)?|NLog|ProTune|Rec709|BT[._-]?709|SLog3CineVenice|SLog3Cine|SLog3Venice|SLog3|SLog2|SLog1|VLog|ZLog2))[._ -]+to[._ -]+(?<output>(?:ACEScct|ACEScc|ACEScg|DWG|RWG|Rec709|BT[._-]?709))(?:[._ -].*)?$";
    }
}
