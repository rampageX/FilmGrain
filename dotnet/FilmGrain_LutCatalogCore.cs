using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    // Main-window lifetime only; never changes the gallery reference or configuration.
    internal sealed class LutSessionReference : IDisposable
    {
        private readonly string directory=Path.Combine(Path.GetTempPath(),"FilmGrain_Studio","LutReference_"+Guid.NewGuid().ToString("N"));
        internal string PathValue {get;private set;}
        internal void Set(byte[] png)
        {
            if(png==null||png.Length==0)throw new ArgumentException("Missing LUT reference frame.");
            Directory.CreateDirectory(directory);
            string next=Path.Combine(directory,Guid.NewGuid().ToString("N")+".png");
            File.WriteAllBytes(next,png);PathValue=next;
        }
        public void Dispose(){PathValue=null;try{if(Directory.Exists(directory))Directory.Delete(directory,true);}catch{}}
    }

    internal static class LutStrengthCore
    {
        internal static string Caption(string relative)
        {
            string text=(relative??"").Replace('\\','-').Replace('/','-');
            string extension=Path.GetExtension(text);return extension.Length>0?text.Substring(0,text.Length-extension.Length):text;
        }
        internal static bool IsCompoundFile(string path)
        {
            bool one=false,three=false;
            foreach(string line in File.ReadLines(path)) {
                string[] parts=line.Split('#')[0].Trim().Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length==0)continue;
                if(parts[0].Equals("LUT_1D_SIZE",StringComparison.OrdinalIgnoreCase))one=true;
                if(parts[0].Equals("LUT_3D_SIZE",StringComparison.OrdinalIgnoreCase))three=true;
                if(one&&three)return true;
            }
            return false;
        }
        internal static string Normalize(string text)
        {
            var lines=new List<string>();
            foreach(string line in text.Replace("\r\n","\n").Split('\n')) {
                string[] p=line.Trim().Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                if(p.Length==3 && p[0].Equals("LUT_3D_INPUT_RANGE",StringComparison.OrdinalIgnoreCase)) {
                    lines.Add("DOMAIN_MIN "+p[1]+" "+p[1]+" "+p[1]);lines.Add("DOMAIN_MAX "+p[2]+" "+p[2]+" "+p[2]);
                }else lines.Add(line);
            }
            return string.Join("\n",lines.ToArray());
        }
        internal static string Bake(string text,int percent,out bool baked)
        {
            if(percent<1||percent>100)throw new ArgumentOutOfRangeException("percent");
            baked=true;if(percent==100)return text;
            string[] lines=text.Replace("\r\n","\n").Split('\n');int size=0;
            double[] min={0,0,0},max={1,1,1};
            foreach(string line in lines) {
                string raw=line.Split('#')[0].Trim();string[] p=raw.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                if(p.Length==0)continue;
                if(p[0].Equals("LUT_1D_SIZE",StringComparison.OrdinalIgnoreCase)) {baked=false;return text;}
                if(p[0].Equals("LUT_3D_SIZE",StringComparison.OrdinalIgnoreCase)) {if(p.Length!=2||!int.TryParse(p[1],out size)||size<2||size>256)throw new InvalidDataException("Invalid CUBE size");}
                if(p[0].Equals("DOMAIN_MIN",StringComparison.OrdinalIgnoreCase)||p[0].Equals("DOMAIN_MAX",StringComparison.OrdinalIgnoreCase)) {
                    if(p.Length!=4)throw new InvalidDataException("Invalid CUBE domain");
                    var target=p[0].Equals("DOMAIN_MIN",StringComparison.OrdinalIgnoreCase)?min:max;
                    for(int c=0;c<3;c++)target[c]=Number(p[c+1]);
                }
            }
            if(size==0)throw new InvalidDataException("Missing LUT_3D_SIZE");
            for(int c=0;c<3;c++)if(max[c]<=min[c])throw new InvalidDataException("Invalid CUBE domain range");
            int index=0;double amount=percent/100.0;
            for(int i=0;i<lines.Length;i++) {
                string raw=lines[i].Split('#')[0].Trim();string[] p=raw.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);double first;
                if(p.Length!=3||!double.TryParse(p[0],NumberStyles.Float,CultureInfo.InvariantCulture,out first))continue;
                if(index>=size*size*size)throw new InvalidDataException("Too many CUBE rows");
                int[] cell={index%size,(index/size)%size,index/(size*size)};var row=new string[3];
                for(int c=0;c<3;c++) {
                    double identity=min[c]+(max[c]-min[c])*cell[c]/(size-1.0);
                    row[c]=(identity*(1-amount)+Number(p[c])*amount).ToString("G17",CultureInfo.InvariantCulture);
                }
                lines[i]=string.Join(" ",row);index++;
            }
            if(index!=size*size*size)throw new InvalidDataException("Incomplete CUBE rows");
            return string.Join("\n",lines);
        }
        private static double Number(string text) {double v;if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out v)||double.IsNaN(v)||double.IsInfinity(v))throw new InvalidDataException("Invalid CUBE value");return v;}
        internal static bool BakeFile(string path,int percent)
        {
            bool baked;string output=Bake(File.ReadAllText(path),percent,out baked);
            if(baked&&percent!=100)File.WriteAllText(path,output,new UTF8Encoding(false));return baked;
        }
        internal static string CreateFile(string source,string target,int percent)
        {
            bool baked;string output=Bake(Normalize(File.ReadAllText(source)),percent,out baked);File.WriteAllText(target,output,new UTF8Encoding(false));
            return baked?"1.00":(percent/100.0).ToString("0.00",CultureInfo.InvariantCulture);
        }
        internal static string OptimizeGraph(string graph)
        {
            if(string.IsNullOrEmpty(graph))return graph;
            return System.Text.RegularExpressions.Regex.Replace(graph,
                @"split=2\[lutorig\]\[lutsrc\];\[lutsrc\](?<lut>lut3d=.*?)\[lutgraded\];\[lutgraded\]\[lutorig\]blend=all_mode=normal:all_opacity=1(?:\.0+)?(?=,|\[|;|$)",
                delegate(System.Text.RegularExpressions.Match m){return m.Groups["lut"].Value;});
        }
    }

    // G04c: measured adapter and ordinary HEVC Main10 only. Never alter global Vulkan policy.
    internal static class G04cLutCore
    {
        internal const string ConfigKey="LUT_PROCESSING_MODE", StateKey="FG_LUT_PROCESSING_MODE";
        internal const string Signal="setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
        internal const string Download=",hwdownload,format=p010le", Direct=",hwupload=derive_device=cuda";
        internal const string DirectDeviceFlags=",disable_multiplane=1,disable_host_image_copy=1";
        private static string Get(IDictionary<string,string> s,string k)
        {string v;return s!=null&&s.TryGetValue(k,out v)?v??"":"";}
        internal static bool WantsGpu(IDictionary<string,string> s)
        {string v=Get(s,StateKey);return v==""||v.Equals("GPU",StringComparison.OrdinalIgnoreCase);}
        private static bool Known(string v,string expected)
        {return string.IsNullOrEmpty(v)||v=="unknown"||v=="unspecified"||v==expected;}
        internal static bool MediaEligible(MediaProbeInfo m)
        {
            return m!=null&&m.HasVideo&&!m.IsHdr&&!m.IsInterlaced&&Known(m.FieldOrder,"progressive")&&
                m.ColorRange=="tv"&&m.ColorSpace=="bt709"&&Known(m.ColorPrimaries,"bt709")&&Known(m.ColorTransfer,"bt709")&&
                (m.PixelFormat=="yuv420p"||m.PixelFormat=="yuv420p10le"||m.PixelFormat=="nv12"||m.PixelFormat=="p010le")&&
                (string.IsNullOrEmpty(m.SampleAspectRatio)||m.SampleAspectRatio=="1:1"||m.SampleAspectRatio=="0:1"||m.SampleAspectRatio=="N/A")&&
                m.Width>=8&&m.Height>=8&&((m.Width|m.Height)&1)==0;
        }
        internal static bool RouteEligible(MediaProbeInfo m,HardwareCapabilitySnapshot c,IDictionary<string,string> s)
        {
            if(!MediaEligible(m)||c==null||!c.Ready||!c.HevcAvailable||
                !string.Equals(c.GpuName,"NVIDIA GeForce RTX 4080",StringComparison.OrdinalIgnoreCase)||Get(s,"FG_MODE")!="HEVC")return false;
            string fps=Get(s,"FG_FPS_MODE"),border=Get(s,"FG_FILM_BORDER_MODE");
            return Get(s,"FG_GPU_DECODE")!="1"&&Get(s,"FG_HWD_BACKEND")==""&&
                Get(s,"FG_SVP_INTERPOLATE")!="1"&&(fps=="SOURCE"||fps=="KEEP")&&
                (!SharedGrainCore.IsEnabled(s)||SharedGrainCore.Engine(s)=="NATIVE")&&
                !PreprocessingCore.Enabled(s)&&!ResizeCore.Enabled(s)&&Get(s,"FG_CINEMATIC_FRAME")!="1"&&
                Get(s,"FG_SUBTITLE")!="1"&&(border==""||border=="OFF")&&Get(s,"FG_COLOR_ENABLED")!="1"&&
                Get(s,ColorGradeCore.OriginalLutKey)==""&&!ColorGrade.Parse(Get(s,ColorGrade.StateKey)).IsActive;
        }
        private static bool ValueIs(string text,double expected)
        {double n;return double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&n==expected;}
        // Canonical [0,1] standalone 3D table; same node mixing as M9K4. Do not clip unsupported LUTs.
        internal static bool TryCanonicalCube(string path,int percent,out string text)
        {
            text="";if(percent<1||percent>100||string.IsNullOrEmpty(path)||!File.Exists(path)||new FileInfo(path).Length>32L*1024*1024)return false;
            int size=0,index=0;var headers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nodes=new StringBuilder();double a=percent/100.0;
            foreach(string line in File.ReadLines(path)){
                string[] p=line.Split('#')[0].Trim().Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                if(p.Length==0)continue;string key=p[0].ToUpperInvariant();
                if(key=="TITLE")continue;
                if(key=="LUT_3D_SIZE"||key=="DOMAIN_MIN"||key=="DOMAIN_MAX"||key=="LUT_3D_INPUT_RANGE"){
                    if(index!=0||!headers.Add(key))return false;
                    if(key=="LUT_3D_SIZE"){
                        if(p.Length!=2||!int.TryParse(p[1],NumberStyles.None,CultureInfo.InvariantCulture,out size)||size<2||size>65)return false;
                    }else if(key=="LUT_3D_INPUT_RANGE"){
                        if(p.Length!=3||!ValueIs(p[1],0)||!ValueIs(p[2],1)||headers.Contains("DOMAIN_MIN")||headers.Contains("DOMAIN_MAX"))return false;
                    }else{
                        if(p.Length!=4||headers.Contains("LUT_3D_INPUT_RANGE"))return false;
                        for(int c=1;c<4;c++)if(!ValueIs(p[c],key=="DOMAIN_MIN"?0:1))return false;
                    }
                    continue;
                }
                if(size==0||p.Length!=3||index>=size*size*size)return false;
                int[] cell={index%size,index/size%size,index/(size*size)};
                for(int c=0;c<3;c++){
                    double v;if(!double.TryParse(p[c],NumberStyles.Float,CultureInfo.InvariantCulture,out v)||double.IsNaN(v)||double.IsInfinity(v)||v<0||v>1)return false;
                    if(c>0)nodes.Append(' ');
                    nodes.Append(((1-a)*cell[c]/(size-1)+a*v).ToString("F10",CultureInfo.InvariantCulture));
                }
                nodes.Append('\n');index++;
            }
            if(size==0||index!=size*size*size)return false;
            text="LUT_3D_SIZE "+size.ToString(CultureInfo.InvariantCulture)+"\n"+nodes.ToString();return true;
        }
        internal static string NormalizedFilter(string simpleName)
        {
            if(string.IsNullOrEmpty(simpleName)||simpleName.IndexOfAny(new[]{'\\','/',':','\'','[',']',',',';','\r','\n'})>=0)
                throw new ArgumentException("G04c requires a staged relative CUBE name.");
            return "format=p010le,"+Signal+",setpts=PTS-STARTPTS,hwupload,libplacebo=lut="+simpleName+
                ":lut_type=normalized:format=p010le:colorspace=bt709:range=limited:color_primaries=bt709:color_trc=bt709:peak_detect=0:dithering=none";
        }
        internal static string PlateDirectGraph(string graph)
        {
            const string tail=",hwdownload,format=p010le,format=p010le[vout]";
            if(!graph.EndsWith(tail,StringComparison.Ordinal))throw new InvalidOperationException("G04c unexpected Plate graph tail.");
            return graph.Substring(0,graph.Length-tail.Length)+Direct+"[vout]";
        }
    }

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
