using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    // Data only: no polymorphic deserialization, executable text, tool paths or environment keys.
    public sealed class FgsPreset
    {
        public int FormatVersion {get;set;}
        public string Id {get;set;}
        public string Name {get;set;}
        public string Description {get;set;}
        public string Kind {get;set;}
        public string SubtitlePolicy {get;set;}
        public Dictionary<string,string> Config {get;set;}
        public Dictionary<string,string> Fields {get;set;}
        public Dictionary<string,string> Controls {get;set;}
        public Dictionary<string,string> SubtitleStyle {get;set;}
        public string[] Bitrates {get;set;}
        public bool[] Automatic {get;set;}
        public override string ToString() {return Name;}
    }
    internal static class PresetCore
    {
        internal const string Fields="ordinaryGrainEnabled av1GrainModeIndex pixelGrainModeIndex av1FormatIndex av1StockIndex av1IsoValue av1ChromaEnabled selectedAv1GrainTable proceduralStrength fgsimPresetIndex selectedGrainPlatePath scannedGrainStrengthPercent selectedLutPath colorCorrectionEnabled colorContrast colorBrightness colorSaturation colorGamma colorBlackWhite";
        internal const string Controls="cmbCodec cmbContainer cmbSpeed chkSfe chkCq cmbBitrate chkBitrateAuto chkHighMotion cmbFps chkInterpolation cmbInterpolationMode cmbInterpolationFps cmbDeint cmbDeintMethod chkCinematic cmbFrameMode chkGrainEnabled chkLut trackLutStrength";
        internal const string Styles="FontName FontSize PrimaryHex BorderHex Outline Shadow MarginV";
        internal const string BasicKeys="GRAIN_ENABLED AV1_GRAIN_STRENGTH HDR_POLICY AUDIO_MODE AUDIO_BITRATE INTERPOLATION_TARGET_FPS CINEMATIC_CROP_PER_SIDE CINEMATIC_CROP_HORIZONTAL CINEMATIC_NO_CROP COLOR_CORRECTION_ENABLED COLOR_CONTRAST COLOR_BRIGHTNESS COLOR_SATURATION COLOR_GAMMA COLOR_BLACK_WHITE NVENC_AV1_RATE_CONTROL NVENC_AV1_CQ NVENC_AV1_CQ_AUTO NVENC_HEVC_RATE_CONTROL NVENC_HEVC_CQ NVENC_HEVC_CQ_AUTO NVENC_H264_RATE_CONTROL NVENC_H264_CQ NVENC_H264_CQ_AUTO";
        internal static bool Contains(string list,string key) {return Array.IndexOf(list.Split(' '),key)>=0;}
        internal static bool Includes(string kind,string key) {return FgsConfig.IsTaskKey(key)&&(kind=="Full"||key=="GPU_DECODE"||key=="CPU_CODEC"||Contains("CPU_SVT_CQ CPU_SVT_CQ_AUTO",key)||key=="CPU_SVT_RATE_CONTROL"||Contains("CPU_X265_CQ CPU_X265_CQ_AUTO",key)||key=="CPU_X265_RATE_CONTROL"||Contains(BasicKeys,key));}
        internal static JavaScriptSerializer Serializer() {return new JavaScriptSerializer {MaxJsonLength=262144,RecursionLimit=16};}
        internal static FgsPreset Copy(FgsPreset p) {return Serializer().Deserialize<FgsPreset>(Serializer().Serialize(p));}
        internal static void Require(bool valid,string error) {if(!valid)throw new InvalidDataException(error);}
        internal static double Number(string value,double min,double max,string key)
        {double n;Require(double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!double.IsNaN(n)&&!double.IsInfinity(n)&&n>=min&&n<=max,"无效参数: "+key+" = "+value);return n;}
        internal static void Integer(string value,int min,int max,string key)
        {int n;Require(int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out n)&&n>=min&&n<=max,"无效整数: "+key+" = "+value);}
        internal static void Choice(string v,string choices,string key) {Require(Contains(choices,v),"无效选项: "+key+" = "+v);}
        internal static void SafeText(string v,string key,int max)
        {Require(v!=null&&v.Length<=max&&v.IndexOf('\0')<0&&v.IndexOf('\r')<0&&v.IndexOf('\n')<0,"无效文本: "+key);}
        internal static void ValidateConfig(string k,string v)
        {
            SafeText(v,k,128);
            if(k.EndsWith("_CQ_AUTO")){Choice(v," true false",k);return;}
            if(k.EndsWith("_RATE_CONTROL")){Choice(v,"VBR CQ",k);return;}
            if(k.EndsWith("_CQ")){if(v!="AUTO")Number(v,1,k.Contains("AV1")||k.Contains("SVT")?63:51,k);return;}
            if(k.EndsWith("_CQ_MAXRATE")){Number(v,1,1000,k);return;}
            if(k.EndsWith("_CQ_LIMIT_ENABLED")||k.EndsWith("TEMPORAL_AQ")||Contains("GPU_DECODE GRAIN_ENABLED H264_HIGH10 AV1_COPY_FAST AV1_COPY_FULL_VERIFY SVP_STILL SVP_PEEL SVP_EXACT SVP_LEAN COLOR_CORRECTION_ENABLED COLOR_BLACK_WHITE",k)){Choice(v,"true false",k);return;}
            if(k.EndsWith("SPATIAL_AQ")){Choice(v,"0 4 8 10 12 15",k);return;}
            if(k.EndsWith("MULTIPASS")){Choice(v,"FGS DISABLED QRES FULLRES",k);return;}
            if(k.EndsWith("BFRAMES")){Choice(v,"FGS 0 1 2 3 4",k);return;}
            if(k.EndsWith("BREF_MODE")){Choice(v,"FGS disabled middle",k);return;}
            if(k.EndsWith("LOOKAHEAD")){Choice(v,"FGS 0 8 16 24 27 32",k);return;}
            switch(k) {
            case "AUDIO_MODE":Choice(v,"FGS SMART COPY AAC",k);return;
            case "AUDIO_BITRATE":Choice(v,"SOURCE 128 160 192 256 320",k);return;
            case "CPU_SVT_GRAIN_TUNE":case "CPU_X265_GRAIN_TUNE":case "X264_GRAIN_TUNE":Choice(v,"AUTO ON OFF",k);return;
            case "CPU_SVT_TUNING":Choice(v,"FGS HANDBRAKE",k);return;
            case "CPU_CODEC":Choice(v,"SVT X265 X264",k);return;
            case "CPU_SVT_PRESET":Integer(v,0,13,k);return;
            case "CPU_X265_PRESET":Choice(v,"ultrafast superfast veryfast faster fast medium slow slower veryslow placebo",k);return;
            case "CPU_X265_THREADED_ME":Choice(v,"true false",k);return;
            case "X264_RATE_MODE":Choice(v,"VBR1 2PASS 3PASS",k);return;
            case "X264_PRESET":Choice(v,"ultrafast superfast veryfast faster fast medium slow slower veryslow placebo",k);return;
            case "HDR_POLICY":Choice(v,"AUTO PRESERVE SDR",k);return;
            case "TONE_MAP_ALGO":Choice(v,"hable mobius reinhard gamma linear clip",k);return;
            case "FRUC_PERF":Choice(v,"slow medium fast",k);return;
            case "FRUC_GRID":Choice(v,"auto 1 2 4 8",k);return;
            case "SVP_ALGO":Choice(v,"1 2 11 13 21 22 23",k);return;
            case "SVP_ANALYSE":Choice(v,"BASE ENCODEGUI",k);return;
            case "SVP_SPEED":Choice(v,"FGS medium fast faster fastest",k);return;
            case "SVP_TUNING":Choice(v,"FGS weak film smooth animation",k);return;
            case "SVP_BLOCK_SIZE":Choice(v,"auto 8 16 32",k);return;
            case "SVP_DEDUP":Number(v,0,10,k);return;
            case "SVP_MASK_AREA":Integer(v,0,100,k);return;
            case "SVP_STILL_LIMIT":Number(v,0,100,k);return;
            case "SVP_STILL_EDGE":Number(v,0,100,k);return;
            case "SVP_STILL_TOLERANCE":Number(v,0,100,k);return;
            case "SVP_PEEL_LIMIT":Number(v,0,100,k);return;
            case "AV1_GRAIN_STRENGTH":Integer(v,10,100,k);return;
            case "CINEMATIC_CROP_PER_SIDE":case "CINEMATIC_CROP_HORIZONTAL":Integer(v,0,10000,k);return;
            case "CINEMATIC_NO_CROP":Choice(v,"0 1",k);return;
            case "COLOR_CONTRAST":Number(v,-2,2,k);return;
            case "COLOR_BRIGHTNESS":Number(v,-1,1,k);return;
            case "COLOR_SATURATION":Number(v,0,3,k);return;
            case "COLOR_GAMMA":Number(v,.01,3,k);return;
            case "INTERPOLATION_TARGET_FPS":InterpolationFrameRate rate;string error;Require(InterpolationFrameRateCore.TryParse(v,"24",out rate,out error),"无效插帧帧率: "+error);return;
            default:throw new InvalidDataException("未定义的预设参数: "+k);
            }
        }
        internal static void Validate(FgsPreset p)
        {
            Require(p!=null,"预设为空");Require(p.FormatVersion==1,"不支持的预设格式版本（不会覆盖原文件）");
            Guid id;Require(Guid.TryParse(p.Id,out id)&&id!=Guid.Empty&&p.Id==id.ToString("D"),"无效预设 ID（需要标准小写 GUID）");
            SafeText(p.Name,"名称",48);Require(!string.IsNullOrWhiteSpace(p.Name),"名称不能为空");
            Require(p.Description!=null&&p.Description.Length<=4000,"说明过长");Choice(p.Kind,"Simple Full","类型");Choice(p.SubtitlePolicy,"OFF AUTO TARGET","字幕策略");
            Require(p.Config!=null&&p.Fields!=null&&p.Controls!=null&&p.SubtitleStyle!=null,"缺少配置对象");
            foreach(string key in BasicKeys.Split(' '))Require(p.Config.ContainsKey(key),"缺少基础参数: "+key);
            foreach(var pair in p.Config){Require(Includes(p.Kind,pair.Key),"不允许的配置字段: "+pair.Key);ValidateConfig(pair.Key,pair.Value);}
            if(p.Config.ContainsKey("SVP_EXACT")&&p.Config.ContainsKey("SVP_LEAN"))
                Require(p.Config["SVP_EXACT"]!="true"||p.Config["SVP_LEAN"]!="true","精简 Super 必须启用实验性高速分析 (exact=false)");
            // Version 1 requires all editor fields. Missing optional advanced keys retain the target value.
            foreach(string key in Fields.Split(' '))Require(p.Fields.ContainsKey(key),"缺少字段: "+key);
            foreach(var pair in p.Fields) {
                string k=pair.Key,v=pair.Value;Require(Contains(Fields,k),"不允许的字段: "+k);SafeText(v,k,4096);
                if(k.StartsWith("selected")){ValidateResource(v);if(v!=""){string kind=k=="selectedLutPath"?"lut":k=="selectedAv1GrainTable"?"table":"plate";Require(v.StartsWith(kind+":",StringComparison.Ordinal)||v.StartsWith("external:",StringComparison.Ordinal),"资源类型不匹配: "+k);string ext=Path.GetExtension(v).ToLowerInvariant();Require(kind=="lut"?ext==".cube":kind=="table"?ext==".tbl"||ext==".txt":ext==".mov","不支持的资源扩展名: "+k);}continue;}
                if(Contains("ordinaryGrainEnabled av1ChromaEnabled colorCorrectionEnabled colorBlackWhite",k)){Choice(v,"True False",k);continue;}
                if(k.StartsWith("color")){Number(v,k=="colorContrast"?-2:k=="colorBrightness"?-1:k=="colorGamma"?.01:0,k=="colorBrightness"?1:k=="colorContrast"?2:3,k);continue;}
                int max=k=="av1IsoValue"?1000000:k=="av1FormatIndex"?4:k=="av1StockIndex"||k=="fgsimPresetIndex"?3:k=="av1GrainModeIndex"?4:k=="pixelGrainModeIndex"?2:100;
                Integer(v,k=="av1IsoValue"?1:k=="proceduralStrength"||k=="scannedGrainStrengthPercent"?10:0,max,k);
            }
            foreach(string key in Controls.Split(' '))Require(p.Controls.ContainsKey(key),"缺少控件字段: "+key);
            foreach(var pair in p.Controls) {
                string k=pair.Key,v=pair.Value;Require(Contains(Controls,k),"不允许的控件: "+k);SafeText(v,k,64);
                if(k.StartsWith("chk"))Choice(v,"0 1",k);
                else if(k=="cmbInterpolationFps"){InterpolationFrameRate r;string e;Require(InterpolationFrameRateCore.TryParse(v,"24",out r,out e),"无效帧率: "+e);}
                else if(k=="cmbBitrate")Number(v,1,500000000,k);
                else Integer(v,0,k=="cmbCodec"?4:k=="cmbSpeed"||k=="cmbInterpolationMode"||k=="cmbDeintMethod"?2:k=="trackLutStrength"?4:1,k);
            }
            int codec=int.Parse(p.Controls["cmbCodec"],CultureInfo.InvariantCulture);
            bool cpu=codec==2&&p.Config.ContainsKey("CPU_CODEC")&&p.Config["CPU_CODEC"]!="X264";
            if(cpu)foreach(string suffix in new[]{"RATE_CONTROL","CQ","CQ_AUTO"})Require(p.Config.ContainsKey("CPU_"+p.Config["CPU_CODEC"]+"_"+suffix),"CPU 预设缺少参数: "+suffix);
            if(codec==0||codec==1||codec==3||cpu) {
                string prefix=cpu?"CPU_"+p.Config["CPU_CODEC"]:"NVENC_"+(codec==0?"AV1":codec==1?"HEVC":"H264");bool cq=p.Config[prefix+"_RATE_CONTROL"]=="CQ";
                Require((p.Controls["chkCq"]=="1")==cq,"CQ 控件与配置状态不一致");
                if(cq){bool automatic=p.Config[prefix+"_CQ_AUTO"]==""?p.Config[prefix+"_CQ"]=="AUTO":p.Config[prefix+"_CQ_AUTO"]=="true";Require((p.Controls["chkBitrateAuto"]=="1")==automatic,"CQ 自动状态不一致");if(!automatic){Number(p.Controls["cmbBitrate"],1,codec==0||cpu&&p.Config["CPU_CODEC"]=="SVT"?63:51,"手动 CQ");Require(p.Controls["cmbBitrate"]==p.Config[prefix+"_CQ"],"手动 CQ 数值不一致");}}
            }
            Require(p.Bitrates!=null&&p.Bitrates.Length==4&&p.Automatic!=null&&p.Automatic.Length==4,"无效码率状态");
            foreach(string b in p.Bitrates)Integer(b,11,500000000,"码率");
            foreach(string key in Styles.Split(' '))Require(p.SubtitleStyle.ContainsKey(key),"缺少字幕样式: "+key);
            foreach(var pair in p.SubtitleStyle){string k=pair.Key,v=pair.Value;Require(Contains(Styles,k),"不允许的字幕字段: "+k);SafeText(v,k,128);
                if(k=="FontSize")Integer(v,6,200,k);else if(k=="MarginV")Integer(v,0,300,k);else if(k=="Outline"||k=="Shadow")Number(v,0,10,k);
                else if(k.EndsWith("Hex"))Require(System.Text.RegularExpressions.Regex.IsMatch(v,"^[0-9A-Fa-f]{6}$"),"无效字幕颜色");
            }
        }
        internal static void ValidateResource(string v)
        {
            if(v=="")return;int split=v.IndexOf(':');Require(split>0,"无效资源引用: "+v);
            string kind=v.Substring(0,split),path=v.Substring(split+1);Choice(kind,"lut table plate external","资源类型");
            Require(path.Length>0,"空资源引用");
            if(kind!="external")Require(!Path.IsPathRooted(path)&&path.IndexOf(':')<0&&Array.IndexOf(path.Replace('\\','/').Split('/'),"..")<0,"资源路径不能越出资源库");
        }
        internal static FgsPreset Read(string file)
        {
            Require(new FileInfo(file).Length<=262144,"预设文件过大");string text=File.ReadAllText(file,Encoding.UTF8);
            var raw=Serializer().DeserializeObject(text) as Dictionary<string,object>;Require(raw!=null,"预设必须是对象");
            foreach(var pair in raw)Require(Contains("FormatVersion Id Name Description Kind SubtitlePolicy Config Fields Controls SubtitleStyle Bitrates Automatic",pair.Key),"未知预设字段: "+pair.Key);
            var p=Serializer().Deserialize<FgsPreset>(text);Validate(p);return CoherentCopy(p);
        }
        internal static void AtomicText(string path,string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));string temp=path+".tmp."+Guid.NewGuid().ToString("N");
            try {File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
        internal static void Write(string file,FgsPreset p) {var value=CoherentCopy(p);AtomicText(file,Serializer().Serialize(value));}
        internal static string Summary(FgsPreset p)
        {
            string[] codecs={"AV1 NVENC","HEVC NVENC","x264","H.264 NVENC","AV1 不重编码"};
            if(p.Config.ContainsKey("CPU_CODEC"))codecs[2]=p.Config["CPU_CODEC"]=="SVT"?"AV1 SVT-AV1-HDR":p.Config["CPU_CODEC"]=="X265"?"HEVC x265":"H.264 x264";
            int c=int.Parse(p.Controls["cmbCodec"],CultureInfo.InvariantCulture);
            return codecs[c]+" · "+(p.Controls["chkCq"]=="1"?(c==2?"CRF":"CQ"):p.Automatic[Math.Min(3,c)]?"自动码率":"手动码率")+
                (p.Controls["chkInterpolation"]=="1"?" → 插帧":"")+(p.Controls["chkGrainEnabled"]=="1"?" → 颗粒":"")+(p.Controls["chkLut"]=="1"?" → LUT":"");
        }
        internal static string ShortText(string value,int limit)
        {
            string text=(value??"").Replace("\r"," ").Replace("\n"," ").Trim();
            return text.Length<=limit?text:text.Substring(0,limit-1)+"…";
        }
        // Selection aid only. The manager remains the home for all fields and advanced values.
        internal static string BriefDetails(FgsPreset p)
        {
            string[] codecs={"AV1 NVENC","HEVC NVENC","x264","H.264 NVENC","AV1 不重编码"};
            if(p.Config.ContainsKey("CPU_CODEC"))codecs[2]=p.Config["CPU_CODEC"]=="SVT"?"AV1 SVT-AV1-HDR":p.Config["CPU_CODEC"]=="X265"?"HEVC x265":"H.264 x264";
            int c=int.Parse(p.Controls["cmbCodec"],CultureInfo.InvariantCulture);
            var lines=new List<string>();lines.Add(p.Name+" · "+(p.Kind=="Full"?"完整预设":"简单预设"));
            if(!string.IsNullOrWhiteSpace(p.Description))lines.Add(ShortText(p.Description,54));
            string rate=c==4?"":p.Controls["chkCq"]=="1"?(p.Controls["chkBitrateAuto"]=="1"?"自动 CQ":"CQ "+p.Controls["cmbBitrate"]):p.Automatic[Math.Min(3,c)]?"自动码率":p.Bitrates[Math.Min(3,c)]+" kbps";
            if(c==2)rate=rate.Replace("CQ","CRF");
            lines.Add(codecs[c]+(rate==""?"":" · "+rate)+" · "+(p.Controls["cmbContainer"]=="1"?"MKV":"MP4"));
            var effects=new List<string>();
            if(p.Controls["chkInterpolation"]=="1")effects.Add((p.Controls["cmbInterpolationMode"]=="2"?"FRUC":"OpenSVPFlow")+" → "+p.Controls["cmbInterpolationFps"]+" fps");
            if(p.Controls["chkGrainEnabled"]=="1"){
                int index=int.Parse(p.Fields[c==0||c==4?"av1GrainModeIndex":"pixelGrainModeIndex"],CultureInfo.InvariantCulture);
                string[] names=c==0||c==4?new[]{"胶片合成颗粒","ISO 颗粒","颗粒表","Digital Grain","FGSIM"}:new[]{"Digital Grain","扫描颗粒","FGSIM"};effects.Add(names[index]);
            }
            if(effects.Count>0)lines.Add(string.Join(" · ",effects.ToArray()));
            string hdr=p.Config["HDR_POLICY"],audio=p.Config["AUDIO_MODE"];
            lines.Add("HDR "+(hdr=="AUTO"?"自动":hdr=="SDR"?"转 SDR":"保留")+" · 音频 "+(audio=="FGS"?"FGS 推荐":audio=="SMART"?"智能":audio=="COPY"?"复制":"AAC "+p.Config["AUDIO_BITRATE"]+" kbps"));
            effects.Clear();
            if(p.Controls["chkLut"]=="1"&&p.Controls["trackLutStrength"]!="0")effects.Add("LUT "+(int.Parse(p.Controls["trackLutStrength"],CultureInfo.InvariantCulture)*25)+"%");
            if(p.Fields["colorCorrectionEnabled"]=="True")effects.Add("调色");
            if(p.Controls["chkCinematic"]=="1")effects.Add(p.Controls["cmbFrameMode"]=="1"?"电影裁剪":"电影黑边");
            if(p.Controls["cmbDeint"]=="0")effects.Add("自动反交错");
            if(p.SubtitlePolicy=="AUTO")effects.Add("字幕自动匹配");
            if(effects.Count>0)lines.Add(string.Join(" · ",effects.ToArray()));
            lines.Add(p.Kind=="Simple"?"未包含的高级参数保持当前设置":"包含高级处理参数");
            return string.Join(Environment.NewLine,lines.ToArray());
        }
        // Editor fields are authoritative in v1. Repair stale mirrored INI/control values
        // on a copy so reading/applying never rewrites a user's existing preset file.
        internal static FgsPreset CoherentCopy(FgsPreset source)
        {
            Validate(source);var p=Copy(source);
            bool grain=p.Fields["ordinaryGrainEnabled"]=="True";
            p.Config["GRAIN_ENABLED"]=grain?"true":"false";
            p.Controls["chkGrainEnabled"]=grain||p.Controls["cmbCodec"]=="4"?"1":"0";
            string[] fields={"colorCorrectionEnabled","colorContrast","colorBrightness","colorSaturation","colorGamma","colorBlackWhite"};
            string[] keys={"COLOR_CORRECTION_ENABLED","COLOR_CONTRAST","COLOR_BRIGHTNESS","COLOR_SATURATION","COLOR_GAMMA","COLOR_BLACK_WHITE"};
            for(int i=0;i<fields.Length;i++)p.Config[keys[i]]=p.Fields[fields[i]].ToLowerInvariant();
            return p;
        }
        private static bool DetailConfigActive(FgsPreset p,string key,int codec)
        {
            if(key=="GRAIN_ENABLED"||key.StartsWith("COLOR_")||key=="HDR_POLICY"||key=="AUDIO_MODE"||key=="AUDIO_BITRATE"||key=="INTERPOLATION_TARGET_FPS")return false;
            if(key=="AV1_GRAIN_STRENGTH")return p.Controls["chkGrainEnabled"]=="1"&&(codec==0||codec==4)&&int.Parse(p.Fields["av1GrainModeIndex"])<=2;
            if(key.StartsWith("CINEMATIC_"))return p.Controls["chkCinematic"]=="1"&&(key=="CINEMATIC_NO_CROP"||p.Config["CINEMATIC_NO_CROP"]!="1");
            if(key.StartsWith("FRUC_"))return p.Controls["chkInterpolation"]=="1"&&p.Controls["cmbInterpolationMode"]=="2";
            if(key.StartsWith("SVP_")){
                if(p.Controls["chkInterpolation"]!="1"||p.Controls["cmbInterpolationMode"]=="2")return false;
                if(key.StartsWith("SVP_STILL_")&&p.Config.ContainsKey("SVP_STILL")&&p.Config["SVP_STILL"]=="false")return false;
                if(key=="SVP_PEEL_LIMIT"&&p.Config.ContainsKey("SVP_PEEL")&&p.Config["SVP_PEEL"]=="false")return false;
                return true;
            }
            if(key=="TONE_MAP_ALGO")return p.Config["HDR_POLICY"]=="SDR";
            if(key.StartsWith("AV1_COPY_"))return codec==4;
            if(key.StartsWith("CPU_"))return codec==2;
            if(key.StartsWith("X264_")||key=="H264_HIGH10")return codec==2&&(!p.Config.ContainsKey("CPU_CODEC")||p.Config["CPU_CODEC"]=="X264");
            string family=codec==0?"AV1":codec==1?"HEVC":codec==3?"H264":"";
            if(key.StartsWith("NVENC_")||key.StartsWith("AV1_")||key.StartsWith("HEVC_")||key.StartsWith("H264_")){
                if(family==""||!(key.StartsWith("NVENC_"+family+"_")||key.StartsWith(family+"_")))return false;
                if(key.EndsWith("_RATE_CONTROL")||key.EndsWith("_CQ")||key.EndsWith("_CQ_AUTO"))return false;
                if(key.Contains("_CQ_")&&p.Controls["chkCq"]!="1")return false;
                if(key.EndsWith("_CQ_MAXRATE")){string enabled=key.Replace("_CQ_MAXRATE","_CQ_LIMIT_ENABLED");if(p.Config.ContainsKey(enabled)&&p.Config[enabled]=="false")return false;}
                return true;
            }
            return true;
        }
        internal static string Details(FgsPreset source)
        {
            var p=CoherentCopy(source);int codec=int.Parse(p.Controls["cmbCodec"]);var s=new StringBuilder();
            s.AppendLine(p.Name+" · "+(p.Kind=="Full"?"完整预设":"简单预设"));
            if(!string.IsNullOrWhiteSpace(p.Description))s.AppendLine("用途："+p.Description);
            s.AppendLine(p.Kind=="Simple"?"范围：主界面处理流程；未包含的高级参数保持当前设置。不同高级设置可能产生不同输出。":"范围：主界面流程及明确包含的高级处理参数；未包含的字段保持当前设置。");
            string[] codecs={"AV1 NVENC","HEVC NVENC","x264","H.264 NVENC","AV1 不重编码"};
            if(p.Config.ContainsKey("CPU_CODEC"))codecs[2]=p.Config["CPU_CODEC"]=="SVT"?"AV1 SVT-AV1-HDR":p.Config["CPU_CODEC"]=="X265"?"HEVC x265":"H.264 x264";
            s.AppendLine("编码器："+codecs[codec]+"；容器："+(p.Controls["cmbContainer"]=="1"?"MKV":"MP4"));
            if(codec!=4){s.AppendLine("码率／质量："+(p.Controls["chkCq"]=="1"?(p.Controls["chkBitrateAuto"]=="1"?"自动 CQ（执行时计算）":"手动 CQ "+p.Controls["cmbBitrate"]):p.Automatic[codec]?"自动码率（执行时计算）":"手动码率 "+p.Bitrates[codec]+" kbps"));
                s.AppendLine("编码速度："+(codec==2?"CPU preset（见已包含参数；简单预设保留目标高级设置）":new[]{"快速","标准","UHQ"}[int.Parse(p.Controls["cmbSpeed"])])+"；输出帧率："+(p.Controls["cmbFps"]=="0"?"自动":"源帧率"));
                s.AppendLine("高动态策略："+(p.Controls["chkHighMotion"]=="1"?"开启":"关闭")+"；分割编码："+(p.Controls["chkSfe"]=="1"?"开启":"关闭"));}
            s.AppendLine("颗粒："+(p.Controls["chkGrainEnabled"]=="1"?"开启":"关闭"));
            if(p.Controls["chkGrainEnabled"]=="1"){
                bool av1=codec==0||codec==4;int mode=int.Parse(p.Fields[av1?"av1GrainModeIndex":"pixelGrainModeIndex"]);
                s.AppendLine("颗粒类型："+(av1?new[]{"胶片","ISO","颗粒表","Digital Grain","FGSIM"}:new[]{"Digital Grain","扫描颗粒","FGSIM"})[mode]);
                var keys=new List<string>();
                if(av1&&mode==0){s.AppendLine("颗粒画幅："+new[]{"Classic35","Modern35","16mm","Super8","MaxMid"}[int.Parse(p.Fields["av1FormatIndex"])]);s.AppendLine("胶片："+new[]{"Eterna250D","Eterna500T","Vision3 250D","Vision3 200T"}[int.Parse(p.Fields["av1StockIndex"])]);}
                if(av1&&mode==1)keys.Add("av1IsoValue");
                if(av1&&mode<=1)keys.Add("av1ChromaEnabled");
                if(av1&&mode==2)keys.Add("selectedAv1GrainTable");
                if(mode==(av1?3:0))keys.Add("proceduralStrength");
                if(mode==(av1?4:2))s.AppendLine("FGSIM："+new[]{"light","normal","medium","heavy"}[int.Parse(p.Fields["fgsimPresetIndex"])]);
                if(!av1&&mode==1){keys.Add("selectedGrainPlatePath");keys.Add("scannedGrainStrengthPercent");}
                foreach(string k in keys)s.AppendLine(Label(k)+" = "+p.Fields[k]);
            }
            bool interp=p.Controls["chkInterpolation"]=="1";
            s.AppendLine("插帧："+(interp?new[]{"SVP","SVP 场景融合","FRUC"}[int.Parse(p.Controls["cmbInterpolationMode"])]+" → "+p.Controls["cmbInterpolationFps"]+" fps":"关闭"));
            s.AppendLine("HDR 策略："+(p.Config["HDR_POLICY"]=="AUTO"?"自动（根据输入判断）":p.Config["HDR_POLICY"]=="SDR"?"转 SDR":"保留"));
            bool lut=p.Controls["chkLut"]=="1"&&p.Controls["trackLutStrength"]!="0";
            s.AppendLine("LUT："+(lut?p.Fields["selectedLutPath"]+"；强度 "+(int.Parse(p.Controls["trackLutStrength"])*25)+"%":"关闭"));
            bool color=p.Fields["colorCorrectionEnabled"]=="True";s.AppendLine("调色："+(color?"开启":"关闭"));
            if(color)foreach(string k in new[]{"colorContrast","colorBrightness","colorSaturation","colorGamma","colorBlackWhite"})s.AppendLine(Label(k)+" = "+p.Fields[k]);
            s.AppendLine("画幅："+(p.Controls["chkCinematic"]=="1"?(p.Controls["cmbFrameMode"]=="1"?"裁剪":"加黑边"):"关闭"));
            s.AppendLine("反交错："+(p.Controls["cmbDeint"]=="0"?"自动；算法 "+new[]{"Vulkan","CUDA","W3FDIF"}[int.Parse(p.Controls["cmbDeintMethod"])]:"关闭"));
            s.AppendLine("字幕："+(p.SubtitlePolicy=="OFF"?"关闭":p.SubtitlePolicy=="AUTO"?"自动匹配目标视频字幕":"保留目标视频绑定（不覆盖具体字幕文件或轨道）"));
            if(p.SubtitlePolicy!="OFF")foreach(var x in p.SubtitleStyle)s.AppendLine("字幕 "+x.Key+" = "+x.Value);
            string audio=p.Config["AUDIO_MODE"];s.AppendLine("音频："+(audio=="FGS"?"FGS 推荐":audio=="SMART"?"智能":audio=="COPY"?"复制":"AAC；码率 "+(p.Config["AUDIO_BITRATE"]=="SOURCE"?"跟随源音频":p.Config["AUDIO_BITRATE"]+" kbps")));
            foreach(var x in p.Config)if(DetailConfigActive(p,x.Key,codec))s.AppendLine(Label(x.Key)+" = "+(x.Value=="false"?"关闭":x.Value=="true"?"开启":x.Value));
            return s.ToString();
        }
        internal static string Label(string k)
        {
            switch(k) {case "cmbCodec":return "编码器 [0 AV1 / 1 HEVC / 2 x264 / 3 H264 / 4 不重编码]";
            case "cmbContainer":return "容器 [0 MP4 / 1 MKV]";case "cmbSpeed":return "编码速度 [0 快速 / 1 标准 / 2 UHQ]";
            case "chkCq":return "CQ 质量模式";case "cmbBitrate":return "码率或 CQ（自动时显示值不作为手动设置）";
            case "chkBitrateAuto":return "自动码率 / 自动 CQ";case "chkGrainEnabled":return "颗粒开关";case "chkLut":return "LUT 开关";
            case "trackLutStrength":return "LUT 强度 [0 关闭 / 1 25% / 2 50% / 3 75% / 4 100%]";
            case "cmbInterpolationMode":return "插帧算法 [0 SVP / 1 SVP 场景融合 / 2 FRUC]";case "chkInterpolation":return "插帧开关";
            case "cmbInterpolationFps":return "插帧目标帧率";case "cmbDeint":return "反交错 [0 自动 / 1 关闭]";
            case "cmbDeintMethod":return "反交错算法 [0 Vulkan / 1 CUDA / 2 W3FDIF]";case "chkCinematic":return "电影画幅";
            case "GPU_DECODE":return "GPU 硬件解码";case "cmbFrameMode":return "画幅 [0 黑边 / 1 裁剪]";case "chkHighMotion":return "高动态策略";case "chkSfe":return "分割编码";
            case "cmbFps":return "输出帧率 [0 自动 / 1 源帧率]";case "HDR_POLICY":return "HDR 策略";case "AUDIO_MODE":return "音频策略";
            case "AUDIO_BITRATE":return "音频码率";case "selectedLutPath":return "LUT 资源";case "selectedGrainPlatePath":return "扫描颗粒资源";case "selectedAv1GrainTable":return "AV1 颗粒表资源";
            default:
                var labels=new Dictionary<string,string> {
                    {"ordinaryGrainEnabled","颗粒总开关"},{"av1GrainModeIndex","AV1 颗粒类型 [0 胶片 / 1 ISO / 2 表 / 3 Digital / 4 FGSIM]"},
                    {"pixelGrainModeIndex","像素颗粒类型 [0 Digital / 1 扫描 / 2 FGSIM]"},{"av1FormatIndex","颗粒画幅 [0 Classic35 / 1 Modern35 / 2 16mm / 3 Super8 / 4 MaxMid]"},
                    {"av1StockIndex","胶片 [0 Eterna250D / 1 Eterna500T / 2 Vision3 250D / 3 Vision3 200T]"},{"av1IsoValue","ISO"},{"av1ChromaEnabled","彩色颗粒"},
                    {"proceduralStrength","Digital Grain 强度"},{"fgsimPresetIndex","FGSIM [0 light / 1 normal / 2 medium / 3 heavy]"},{"scannedGrainStrengthPercent","扫描颗粒强度 (%)"},
                    {"colorCorrectionEnabled","调色开关"},{"colorContrast","对比度"},{"colorBrightness","亮度"},{"colorSaturation","饱和度"},{"colorGamma","Gamma"},{"colorBlackWhite","黑白"},
                    {"CPU_SVT_TUNING","SVT 调校方案"},{"CPU_SVT_GRAIN_TUNE","SVT Grain 调优（AUTO/ON/OFF）"},{"CPU_X265_GRAIN_TUNE","x265 Grain 调优（AUTO/ON/OFF）"},{"X264_GRAIN_TUNE","x264 Grain 调优（AUTO/ON/OFF）"},{"H264_HIGH10","x264 High10"},{"X264_RATE_MODE","x264 遍数"},{"X264_PRESET","x264 速度"},{"TONE_MAP_ALGO","HDR 色调映射"},{"GRAIN_ENABLED","颗粒总开关"},{"AV1_GRAIN_STRENGTH","AV1 相对强度 (%)"},
                    {"AV1_COPY_FAST","不重编码快速路径"},{"AV1_COPY_FULL_VERIFY","不重编码完整验证"},{"CINEMATIC_CROP_PER_SIDE","纵向单侧裁剪"},{"CINEMATIC_CROP_HORIZONTAL","横向单侧裁剪"},{"CINEMATIC_NO_CROP","恢复完整画幅"},
                    {"INTERPOLATION_TARGET_FPS","插帧目标帧率"},{"FRUC_PERF","FRUC 性能档位"},{"FRUC_GRID","FRUC 网格（auto=最大可用）"},
                    {"SVP_ALGO","SVP 算法"},{"SVP_ANALYSE","SVP 分析配置"},{"SVP_MASK_AREA","SVP 遮罩区域"},{"SVP_SPEED","SVP 速度"},{"SVP_TUNING","SVP 调校"},{"SVP_BLOCK_SIZE","SVP 分块大小"},{"SVP_DEDUP","SVP 重复帧阈值"},{"SVP_EXACT","SVP 精确分析"},{"SVP_LEAN","SVP 精简 Super"},
                    {"SVP_STILL","SVP 静止保护"},{"SVP_STILL_LIMIT","静止保护阈值"},{"SVP_STILL_EDGE","静止边缘"},{"SVP_STILL_TOLERANCE","静止容差"},{"SVP_PEEL","SVP HUD 剥离"},{"SVP_PEEL_LIMIT","HUD 剥离阈值"}
                };
                string label;if(labels.TryGetValue(k,out label))return label;
                if(k.EndsWith("_RATE_CONTROL"))return k+"（码率/质量模式）";
                if(k.EndsWith("_CQ_AUTO"))return k+"（自动 CQ 状态）";
                if(k.EndsWith("_CQ"))return k+"（质量值；AUTO=自动）";
                if(k.EndsWith("_CQ_LIMIT_ENABLED"))return k+"（CQ 码率上限开关）";
                if(k.EndsWith("_CQ_MAXRATE"))return k+"（CQ 上限 Mbps）";
                if(k.EndsWith("_BFRAMES"))return k+"（B 帧数）";
                if(k.EndsWith("_BREF_MODE"))return k+"（B 参考帧）";
                if(k.EndsWith("_LOOKAHEAD"))return k+"（前向分析帧数）";
                if(k.EndsWith("_MULTIPASS"))return k+"（编码多遍分析）";
                if(k.EndsWith("_SPATIAL_AQ"))return k+"（空间 AQ）";
                if(k.EndsWith("_TEMPORAL_AQ"))return k+"（时间 AQ）";
                return k;}
        }
    }
    internal sealed class PresetStore
    {
        internal readonly string Root;
        internal readonly List<FgsPreset> Items=new List<FgsPreset>();
        internal string[] Quick=new string[5];
        internal PresetStore(string root){Root=root;}
        internal static string ResolveUserRoot(string appRoot)
        {
            string legacy=Path.Combine(appRoot,"UserPresets"),parent=Path.Combine(appRoot,"Presets"),target=Path.Combine(parent,"User");
            foreach(string path in new[]{parent,target,legacy}){
                if(File.Exists(path))throw new IOException("预设目录被同名文件占用: "+path);
                if(Directory.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("预设迁移不跟随目录链接: "+path);
            }
            if(!Directory.Exists(legacy)){Directory.CreateDirectory(parent);return target;}
            bool emptyTarget=Directory.Exists(target)&&Directory.GetFileSystemEntries(target).Length==0;
            if(Directory.Exists(target)&&!emptyTarget){
                if(Directory.GetFileSystemEntries(legacy).Length==0){Directory.Delete(legacy);return target;}
                throw new IOException("发现两处用户预设目录，已停止预设操作以避免覆盖。请先备份并移开其中一处目录，重新打开预设管理器后再导入需要合并的预设。\r\n旧目录: "+legacy+"\r\n新目录: "+target);
            }
            // Same-volume rename preserves all bytes, IDs and quick.json together.
            // Validate before moving: damaged/unsupported presets remain in the old location.
            var check=new PresetStore(legacy);check.Load();
            Directory.CreateDirectory(parent);
            if(emptyTarget)Directory.Delete(target);
            try{Directory.Move(legacy,target);}
            catch{if(emptyTarget&&!Directory.Exists(target))Directory.CreateDirectory(target);throw;}
            return target;
        }
        private string FileFor(string id){Guid g;PresetCore.Require(Guid.TryParse(id,out g),"无效 ID");return Path.Combine(Root,g.ToString("D")+".fgspreset.json");}
        internal FgsPreset Find(string id){return Items.Find(delegate(FgsPreset p){return p.Id==id;});}
        internal void Load()
        {
            Items.Clear();if(!Directory.Exists(Root))return;
            var errors=new List<string>();foreach(string file in Directory.GetFiles(Root,"*.fgspreset.json"))try{var p=PresetCore.Read(file);PresetCore.Require(Find(p.Id)==null,"重复 ID: "+p.Id);Items.Add(p);}catch(Exception ex){errors.Add(Path.GetFileName(file)+": "+ex.Message);}
            string quick=Path.Combine(Root,"quick.json");if(File.Exists(quick))try{var slots=PresetCore.Serializer().Deserialize<string[]>(File.ReadAllText(quick));PresetCore.Require(slots!=null&&slots.Length==5,"快捷绑定格式无效");foreach(string id in slots)if(id!=null)PresetCore.Require(Find(id)!=null,"快捷绑定引用不存在的预设: "+id);Quick=slots;}catch(Exception ex){errors.Add("quick.json: "+ex.Message);}
            if(errors.Count>0)throw new InvalidDataException(string.Join(Environment.NewLine,errors.ToArray()));
        }
        internal void Save(FgsPreset p){PresetCore.Validate(p);PresetCore.Require(!Items.Exists(delegate(FgsPreset x){return x.Id!=p.Id&&string.Equals(x.Name,p.Name,StringComparison.OrdinalIgnoreCase);}),"已存在同名预设，请使用不同名称。");string path=FileFor(p.Id);if(File.Exists(path))PresetCore.Read(path);PresetCore.Write(path,p);var old=Find(p.Id);if(old!=null)Items.Remove(old);Items.Add(PresetCore.Copy(p));}
        internal void Bind(int slot,string id){PresetCore.Require(slot>=0&&slot<5&&(id==null||Find(id)!=null),"快捷绑定不存在");var next=(string[])Quick.Clone();next[slot]=id;PresetCore.AtomicText(Path.Combine(Root,"quick.json"),PresetCore.Serializer().Serialize(next));Quick=next;}
        internal void Delete(FgsPreset p){for(int i=0;i<5;i++)if(Quick[i]==p.Id)Bind(i,null);File.Delete(FileFor(p.Id));Items.Remove(p);}
    }
}
