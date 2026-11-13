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
        internal const string BasicKeys="NVENC_AI_MODE RESIZE_MODE RESIZE_WIDTH RESIZE_HEIGHT RESIZE_KEEP_ASPECT RESIZE_ALGORITHM RESIZE_FACTOR PRE_BILATERAL_ENABLED PRE_BILATERAL_SIGMA_S PRE_BILATERAL_SIGMA_R PRE_BILATERAL_PLANES GRAIN_ENABLED AV1_GRAIN_STRENGTH HDR_POLICY AUDIO_MODE AUDIO_BITRATE INTERPOLATION_TARGET_FPS CINEMATIC_CROP_PER_SIDE CINEMATIC_CROP_HORIZONTAL CINEMATIC_NO_CROP COLOR_CORRECTION_ENABLED COLOR_CONTRAST COLOR_BRIGHTNESS COLOR_SATURATION COLOR_GAMMA COLOR_BLACK_WHITE NVENC_AV1_RATE_CONTROL NVENC_AV1_CQ NVENC_AV1_CQ_AUTO NVENC_HEVC_RATE_CONTROL NVENC_HEVC_CQ NVENC_HEVC_CQ_AUTO NVENC_H264_RATE_CONTROL NVENC_H264_CQ NVENC_H264_CQ_AUTO";
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
            case "RESIZE_MODE":Choice(v,"OFF 720 1080 1440 2160 2x 4x 1/2 1/4 CUSTOM",k);return;
            case "RESIZE_WIDTH":case "RESIZE_HEIGHT":Integer(v,2,16384,k);return;
            case "RESIZE_ALGORITHM":Choice(v,"LANCZOS EWA_SHARP EWA_SHARP_HQ",k);return;
            case "RESIZE_FACTOR":
                double factor;
                Require(v.Length==0||ResizeCore.TryParseFactor(v,out factor),"无效缩放倍数。示例：1.5x、3/2、1/3。");return;
            case "RESIZE_KEEP_ASPECT":Choice(v,"true false",k);return;
            case "PRE_BILATERAL_ENABLED":Choice(v,"true false",k);return;
            case "PRE_BILATERAL_SIGMA_S":Number(v,0,512,k);return;
            case "PRE_BILATERAL_SIGMA_R":Number(v,0,1,k);return;
            case "PRE_BILATERAL_PLANES":Integer(v,0,15,k);return;
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
            case "NVENC_AI_MODE":Choice(v,"low medium high",k);return;
            case "FRUC_PERF":Choice(v,"slow medium fast",k);return;
            case "FRUC_GRID":Choice(v,"auto 1 2 4 8",k);return;
            case "SVP_ALGO":Choice(v,"1 2 11 13 21 22 23",k);return;
            case "SVP_ANALYSE":Choice(v,"BASE ENCODEGUI",k);return;
            case "SVP_SPEED":Choice(v,"FGS medium fast faster fastest",k);return;
            case "SVP_TUNING":Choice(v,"FGS weak film smooth animation",k);return;
            case "SVP_BLOCK_SIZE":Choice(v,"auto 8 16 32",k);return;
            case "SVP_HDR_BACKEND":Choice(v,"GPU16 CPU10",k);return;
            case "SVP_DEDUP":Number(v,0,10,k);return;
            case "SVP_MASK_AREA":Integer(v,0,100,k);return;
            case "SVP_STILL_LIMIT":Number(v,0,100,k);return;
            case "SVP_STILL_EDGE":Number(v,0,100,k);return;
            case "SVP_STILL_TOLERANCE":Number(v,0,100,k);return;
            case "SVP_PEEL_LIMIT":Number(v,0,100,k);return;
            case "AV1_GRAIN_STRENGTH":Integer(v,1,100,k);return;
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
            Require(p!=null,"预设为空");Require(p.FormatVersion==1||p.FormatVersion==2,"不支持的预设格式版本（不会覆盖原文件）");
            Guid id;Require(Guid.TryParse(p.Id,out id)&&id!=Guid.Empty&&p.Id==id.ToString("D"),"无效预设 ID（需要标准小写 GUID）");
            SafeText(p.Name,"名称",48);Require(!string.IsNullOrWhiteSpace(p.Name),"名称不能为空");
            Require(p.Description!=null&&p.Description.Length<=4000,"说明过长");Choice(p.Kind,"Simple Full","类型");Choice(p.SubtitlePolicy,"OFF AUTO TARGET","字幕策略");
            Require(p.Config!=null&&p.Fields!=null&&p.Controls!=null&&p.SubtitleStyle!=null,"缺少配置对象");
            foreach(string key in BasicKeys.Split(' '))if(key!="NVENC_AI_MODE"&&!key.StartsWith("PRE_BILATERAL_")&&!key.StartsWith("RESIZE_"))Require(p.Config.ContainsKey(key),"缺少基础参数: "+key);
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
                int max=k=="av1IsoValue"?1000000:k=="av1FormatIndex"?4:k=="av1StockIndex"||k=="fgsimPresetIndex"?3:k=="av1GrainModeIndex"?4:k=="pixelGrainModeIndex"?5:100;
                Integer(v,k=="av1IsoValue"||k=="scannedGrainStrengthPercent"?1:k=="proceduralStrength"?10:0,max,k);
            }
            foreach(string key in Controls.Split(' '))Require(p.Controls.ContainsKey(key),"缺少控件字段: "+key);
            foreach(var pair in p.Controls) {
                string k=pair.Key,v=pair.Value;Require(Contains(Controls,k),"不允许的控件: "+k);SafeText(v,k,64);
                if(k.StartsWith("chk"))Choice(v,"0 1",k);
                else if(k=="cmbInterpolationFps"){InterpolationFrameRate r;string e;Require(InterpolationFrameRateCore.TryParse(v,"24",out r,out e),"无效帧率: "+e);}
                else if(k=="cmbBitrate")Number(v,1,500000000,k);
                else Integer(v,0,k=="cmbCodec"||k=="cmbInterpolationMode"?4:k=="cmbSpeed"||k=="cmbDeintMethod"?2:k=="trackLutStrength"?(p.FormatVersion==1?4:100):1,k);
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
            if(p.Config.ContainsKey("CPU_CODEC"))codecs[2]=p.Config["CPU_CODEC"]=="SVT"?"AV1 SVT-AV1-HDR":p.Config["CPU_CO