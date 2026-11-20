using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FilmGrainStudioPreview
{
    internal sealed class FilmBorderChoice
    {
        internal string Mode, StyleId, Label, PngPath, RawPath;
        internal bool User, Weave;
    }

    // Style catalog is independent of the projection motion model. User designs
    // are copied into a dedicated subdirectory; built-ins are never writable.
    internal static class FilmBorderCatalogCore
    {
        internal const string Off = "OFF";
        private const string Dark = "DARK";
        private const string Classic = "CLASSIC";
        internal static string Root(string appRoot) { return Path.Combine(appRoot, "Utils", "_FilmBorders"); }
        private static string UserRoot(string appRoot) { return Path.Combine(Root(appRoot), "User"); }
        private static FilmBorderChoice Choice(string id, string label, string path, bool weave, bool user)
        {
            return new FilmBorderChoice { StyleId=id, Mode=id+(weave?"_WEAVE":"_STATIC"),
                Label=label+(weave?" · 动态":" · 静态"), PngPath=path,
                RawPath=Path.ChangeExtension(path,".raw"), Weave=weave, User=user };
        }

        // Genuine editable, original FGS classic-black optical print. No third-party
        // film scans or PNGs. Regenerate only when the two editable labels change.
        internal static string ClassicTextPath(string appRoot) {return Path.Combine(Root(appRoot),"classic-text.txt");}
        internal static void ReadClassicText(string appRoot,out string brand,out string serial)
        {
            brand="FGS MOTION PICTURE";
            serial="35 MM  •  400D  •  FGS 01029A";
            string path=ClassicTextPath(appRoot);
            if(!File.Exists(path))return;
            string[] lines=File.ReadAllLines(path,Encoding.UTF8);
            if(lines.Length>0 && lines[0].Trim().Length>0) brand=lines[0].Trim();
            if(lines.Length>1 && lines[1].Trim().Length>0) serial=lines[1].Trim();
        }
        private static string SafeText(string value,int max)
        {
            if(value==null)return "";
            var text=new StringBuilder();
            foreach(char c in value)
                if(!char.IsControl(c) && c!='\u2028' && c!='\u2029')text.Append(c);
            string clean=text.ToString().Trim();return clean.Substring(0,Math.Min(max,clean.Length));
        }
        internal static void SaveClassicText(string appRoot,string brand,string serial)
        {
            brand=SafeText(brand,36);serial=SafeText(serial,62);
            if(brand.Length==0 || serial.Length==0)
                throw new InvalidDataException("胶片品牌和编号均不能留空。");
            Directory.CreateDirectory(Root(appRoot));
            File.WriteAllText(ClassicTextPath(appRoot),brand+"\n"+serial+"\n",new UTF8Encoding(false));
            // Regenerate immediately before returning to the GPU preview.
            GenerateClassic(appRoot,brand,serial);
        }
        private static Font FilmPrintFont(float size)
        {
            foreach(string name in new[]{"Bahnschrift SemiCondensed","Bahnschrift Condensed","Agency FB","Arial Narrow","Segoe UI"})
            {
                try {
                    Font f=new Font(name,size,FontStyle.Bold,GraphicsUnit.Pixel);
                    if(f.FontFamily.Name.Equals(name,StringComparison.OrdinalIgnoreCase))return f;
                    f.Dispose();
                } catch(ArgumentException){}
            }
            return new Font(FontFamily.GenericSansSerif,size,FontStyle.Bold,GraphicsUnit.Pixel);
        }
        private static void GenerateClassic(string appRoot,string brand,string serial)
        {
            string png=Path.Combine(Root(appRoot),"fgs_classic_black_135.png");
            string raw=Path.ChangeExtension(png,".raw");
            Directory.CreateDirectory(Root(appRoot));
            string tmp=png+".new";
            using(var img=new Bitmap(1920,1080,PixelFormat.Format32bppArgb))
            using(var g=Graphics.FromImage(img))
            using(var amber=new SolidBrush(Color.FromArgb(232,201,139)))
            using(var faint=new SolidBrush(Color.FromArgb(208,169,124)))
            using(var black=new SolidBrush(Color.FromArgb(255,5,5,6)))
            using(var topFont=FilmPrintFont(36))
            using(var smallFont=FilmPrintFont(27))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.FillRectangle(black,0,0,1920,116);
                g.FillRectangle(black,0,980,1920,100);
                g.FillRectangle(black,0,116,60,864);
                g.FillRectangle(black,1860,116,60,864);
                g.DrawString(brand,topFont,amber,new PointF(128,40),StringFormat.GenericTypographic);
                g.DrawString(serial,smallFont,faint,new PointF(130,1010),StringFormat.GenericTypographic);
                g.DrawString("35",smallFont,amber,new PointF(1750,1010),StringFormat.GenericTypographic);
                g.DrawString("●",smallFont,amber,new PointF(1810,1010),StringFormat.GenericTypographic);
                img.Save(tmp,ImageFormat.Png);
            }
            if(File.Exists(png))File.Delete(png);
            File.Move(tmp,png);
            BuildRaw(png,raw);
        }
        private static void EnsureClassic(string appRoot)
        {
            string png=Path.Combine(Root(appRoot),"fgs_classic_black_135.png");
            string raw=Path.ChangeExtension(png,".raw");
            string text=ClassicTextPath(appRoot);
            if(File.Exists(png) && File.Exists(raw) && new FileInfo(raw).Length==1920L*1080*4 &&
                (!File.Exists(text)||File.GetLastWriteTimeUtc(png)>=File.GetLastWriteTimeUtc(text)))return;
            string brand,serial;ReadClassicText(appRoot,out brand,out serial);
            GenerateClassic(appRoot,brand,serial);
        }
        internal static List<FilmBorderChoice> List(string appRoot)
        {
            // A read-only deployment may retain the original Dark style even
            // when no new built-in texture can be generated on first launch.
            try { EnsureClassic(appRoot); } catch(IOException){} catch(UnauthorizedAccessException){}
            var result=new List<FilmBorderChoice>();
            result.Add(new FilmBorderChoice{Mode=Off,StyleId=Off,Label="关闭"});
            string root=Root(appRoot);
            foreach(var entry in new[] {
                new KeyValuePair<string,string>(Dark,"Dark 35mm"),
                new KeyValuePair<string,string>(Classic,"FGS Classic Black") })
            {
                string filename=entry.Key==Dark?"cinema35_dark_1920x1080.png":"fgs_classic_black_135.png";
                string path=Path.Combine(root,filename);
                if(!File.Exists(path))continue;
                result.Add(Choice(entry.Key,"内置 · "+entry.Value,path,false,false));
                result.Add(Choice(entry.Key,"内置 · "+entry.Value,path,true,false));
            }
            string folder=UserRoot(appRoot);
            if(Directory.Exists(folder))
            {
                foreach(string path in Directory.GetFiles(folder,"*.png",SearchOption.TopDirectoryOnly))
                {
                    string id=Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
                    Guid ignored;
                    if(id.Length!=32 || !Guid.TryParseExact(id,"N",out ignored))continue;
                    string label=id.Substring(0,8);
                    string labelPath=Path.ChangeExtension(path,".label");
                    if(File.Exists(labelPath))
                    {
                        string stored=File.ReadAllText(labelPath,Encoding.UTF8).Trim();
                        if(stored.Length>0)label=stored.Length>40?stored.Substring(0,40):stored;
                    }
                    result.Add(Choice("USER_"+id,"自定义 · "+label,path,false,true));
                    result.Add(Choice("USER_"+id,"自定义 · "+label,path,true,true));
                }
            }
            return result;
        }
        internal static bool TryResolve(string appRoot,string value,out FilmBorderChoice choice,out string reason)
        {
            choice=null;reason="";
            string mode=(value??"").Trim().ToUpperInvariant();
            if(mode.Length==0)mode=Off;
            foreach(FilmBorderChoice item in List(appRoot))
                if(item.Mode==mode) {choice=item;return true;}
            reason="Unknown or missing film border style: "+mode;return false;
        }
        private static void ValidatePng(string filename)
        {
            using(var image=new Bitmap(filename))
            {
                if(image.Width!=1920 || image.Height!=1080)
                    throw new InvalidDataException("边框 PNG 必须为 1920 × 1080。");
                if((Image.GetPixelFormatSize(image.PixelFormat)!=32 && Image.GetPixelFormatSize(image.PixelFormat)!=64) ||
                    !Image.IsAlphaPixelFormat(image.PixelFormat))
                    throw new InvalidDataException("边框素材必须是真正带 Alpha 通道的 RGBA PNG。");
                if(image.GetPixel(960,540).A>12)
                    throw new InvalidDataException("中心画面必须透明（Alpha ≤ 12），不能覆盖原视频。");
            }
        }
        // Generates the exact FFmpeg raw RGBA byte order for Vulkan input.
        private static void BuildRaw(string png,string raw)
        {
            using(var input=new Bitmap(png))
            using(var normalized=new Bitmap(1920,1080,PixelFormat.Format32bppArgb))
            {
                using(var graphics=Graphics.FromImage(normalized))
                {
                    graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.DrawImage(input,new Rectangle(0,0,1920,1080));
                }
                var rect=new Rectangle(0,0,1920,1080);
                BitmapData lockData=normalized.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                try {
                    byte[] row=new byte[1920*4];
                    using(var stream=new FileStream(raw,FileMode.Create,FileAccess.Write,FileShare.None))
                        for(int y=0;y<1080;y++) {
                            Marshal.Copy(IntPtr.Add(lockData.Scan0,y*lockData.Stride),row,0,row.Length);
                            for(int i=0;i<row.Length;i+=4) {
                                byte b=row[i];row[i]=row[i+2];row[i+2]=b;
                            }
                            stream.Write(row,0,row.Length);
                        }
                } finally { normalized.UnlockBits(lockData); }
            }
        }
        internal static string Import(string appRoot,string source)
        {
            if(!File.Exists(source))throw new FileNotFoundException("PNG 不存在",source);
            ValidatePng(source);
            string id=Guid.NewGuid().ToString("N").ToUpperInvariant();
            string folder=UserRoot(appRoot);
            Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,id+".png");
            string raw=Path.Combine(folder,id+".raw");
            try {
                File.Copy(source,path,false);
                BuildRaw(path,raw);
                string label=Path.GetFileNameWithoutExtension(source);
                foreach(char invalid in Path.GetInvalidFileNameChars())label=label.Replace(invalid,'_');
                if(label.Length>40)label=label.Substring(0,40);
                File.WriteAllText(Path.ChangeExtension(path,".label"),label,new UTF8Encoding(false));
                return "USER_"+id+"_STATIC";
            } catch {
                foreach(string f in new[]{path,raw,Path.ChangeExtension(path,".label")})
                    try {if(File.Exists(f))File.Delete(f);}catch{}
                throw;
            }
        }
        internal static void Delete(string appRoot,string mode)
        {
            FilmBorderChoice choice;string reason;
            if(!TryResolve(appRoot,mode,out choice,out reason) || !choice.User)
                throw new InvalidOperationException("只能删除用户导入的边框，不能删除内置素材。");
            foreach(string file in new[]{choice.PngPath,choice.RawPath,Path.ChangeExtension(choice.PngPath,".label")})
                if(File.Exists(file))File.Delete(file);
        }
        internal static string CreatePreviewChoices(string appRoot,string ffmpeg,string temporary,
            bool hdr,string transfer,CancellationToken token)
        {
            var choices=List(appRoot);
            if(choices.Count<3)throw new InvalidDataException("没有可用的胶片边框素材。");
            var cache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var lines=new List<string>();
            foreach(FilmBorderChoice choice in choices)
            {
                FilmBorderChoice actual=choice.Mode==Off?choices[1]:choice;
                string raw;
                if(!cache.TryGetValue(actual.StyleId,out raw))
                {
                    raw=Path.Combine(temporary,"border_"+cache.Count.ToString(CultureInfo.InvariantCulture)+".rgba");
                    var args=new List<string>{"-hide_banner","-nostdin","-v","error","-y","-i",actual.PngPath,"-frames:v","1"};
                    if(hdr)args.AddRange(new[]{"-vf","format=gbrap16le,zscale=matrixin=gbr:primariesin=709:transferin=iec61966-2-1:rangein=full"+
                        ":matrix=gbr:primaries=2020:transfer="+transfer+":range=full:npl=100:threads=1"});
                    args.AddRange(new[]{"-pix_fmt",hdr?"rgba64le":"rgba","-f","rawvideo",raw});
                    int rc=NativeLibraryTools.RunProcessEnvironment(ffmpeg,args,temporary,null,30000,token,null);
                    if(rc!=0 || !File.Exists(raw) || new FileInfo(raw).Length!=1920L*1080*(hdr?8:4))
                        throw new InvalidDataException("边框纹理转换失败："+actual.Label);
                    cache.Add(actual.StyleId,raw);
                }
                lines.Add(choice.Mode+"\t"+choice.Label+"\t"+raw);
            }
            string output=Path.Combine(temporary,"border_choices.txt");
            File.WriteAllText(output,string.Join("\r\n",lines)+"\r\n",Encoding.Unicode);
            return output;
        }
    }
}
