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
        internal string Mode, StyleId, Label, PngPath, RawPath, UserName;
        internal bool User, Weave;
        internal int ApertureWidth1080, ApertureHeight1080;
    }

    // Style catalog is independent of the projection motion model. User designs
    // are copied into a dedicated subdirectory; built-ins are never writable.
    internal static class FilmBorderCatalogCore
    {
        internal const string Off = "OFF";
        private const string Dark = "DARK";
        private const string Classic = "CLASSIC";
        internal const int ClassicApertureHeight1080 = 748;
        private const string ClassicArtRevision = "FGS_CLASSIC_PERFORATIONS_V1";
        private static string ClassicRevisionPath(string appRoot) {
            return Path.Combine(Root(appRoot),"fgs_classic_black_135.version");
        }
        internal static string Root(string appRoot) { return Path.Combine(appRoot, "Utils", "_FilmBorders"); }
        private static string UserRoot(string appRoot) { return Path.Combine(Root(appRoot), "User"); }
        private static FilmBorderChoice Choice(string id, string label, string path, bool weave, bool user, string userName=null)
        {
            int apertureWidth=ImportApertureWidth;
            int apertureHeight=id==Dark?990:ImportApertureHeight;
            if(user)ReadUserAperture(path,out apertureWidth,out apertureHeight);
            return new FilmBorderChoice { StyleId=id, Mode=id+(weave?"_WEAVE":"_STATIC"),
                Label=label+(weave?" · 动态":" · 静态"), PngPath=path, UserName=user?userName:null,
                RawPath=id==Dark ? Path.Combine(Path.GetDirectoryName(path),
                    "cinema35_dark_1920x1080_rgba.raw") : Path.ChangeExtension(path,".raw"),
                Weave=weave, User=user,
                ApertureWidth1080=apertureWidth,ApertureHeight1080=apertureHeight };
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
        private static System.Drawing.Drawing2D.GraphicsPath RoundedPerforation(float x,float y,float w,float h,float radius)
        {
            var path=new System.Drawing.Drawing2D.GraphicsPath();
            float d=2*radius;
            path.AddArc(x,y,d,d,180,90);
            path.AddArc(x+w-d,y,d,d,270,90);
            path.AddArc(x+w-d,y+h-d,d,d,0,90);
            path.AddArc(x,y+h-d,d,d,90,90);
            path.CloseFigure();
            return path;
        }
        private static void DrawPerforation(Graphics g,int x,int y)
        {
            // Opaque warm stock behind the sprocket aperture: like backlit 135 film.
            // Draw at native texture resolution to keep the edge soft at any output size.
            using(var shadow=new SolidBrush(Color.FromArgb(255,31,27,25)))
            using(var rim=new SolidBrush(Color.FromArgb(255,135,132,124)))
            using(var paper=new SolidBrush(Color.FromArgb(255,239,235,224)))
            using(var innerEdge=new Pen(Color.FromArgb(145,109,106,99),1.7f))
            using(var shadowPath=RoundedPerforation(x-5,y-5,100,78,12))
            using(var rimPath=RoundedPerforation(x-2,y-2,94,72,10))
            using(var cutout=RoundedPerforation(x,y,90,68,9))
            {
                g.FillPath(shadow,shadowPath);
                g.FillPath(rim,rimPath);
                g.FillPath(paper,cutout);
                g.DrawPath(innerEdge,cutout);
            }
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
                const int top=166;
                const int bottom=914;
                g.FillRectangle(black,0,0,1920,top);
                g.FillRectangle(black,0,bottom,1920,1080-bottom);
                g.FillRectangle(black,0,top,120,bottom-top);
                g.FillRectangle(black,1800,top,120,bottom-top);
                for(int i=0;i<8;i++) {
                    int x=116+i*230;
                    DrawPerforation(g,x,90);
                    DrawPerforation(g,x,934);
                }
                g.DrawString(brand,topFont,amber,new PointF(128,29),StringFormat.GenericTypographic);
                g.DrawString(serial,smallFont,faint,new PointF(130,1016),StringFormat.GenericTypographic);
                g.DrawString("35",smallFont,amber,new PointF(1750,1016),StringFormat.GenericTypographic);
                g.DrawString("●",smallFont,amber,new PointF(1810,1016),StringFormat.GenericTypographic);
                img.Save(tmp,ImageFormat.Png);
            }
            if(File.Exists(png))File.Delete(png);
            File.Move(tmp,png);
            BuildRaw(png,raw);
            File.WriteAllText(ClassicRevisionPath(appRoot),ClassicArtRevision,new UTF8Encoding(false));
        }
        private static void EnsureClassic(string appRoot)
        {
            string png=Path.Combine(Root(appRoot),"fgs_classic_black_135.png");
            string raw=Path.ChangeExtension(png,".raw");
            string text=ClassicTextPath(appRoot);
            string revision=ClassicRevisionPath(appRoot);
            if(File.Exists(png) && File.Exists(raw) && new FileInfo(raw).Length==1920L*1080*4 &&
                File.Exists(revision) && File.ReadAllText(revision,Encoding.UTF8).Trim()==ClassicArtRevision &&
                (!File.Exists(text)||File.GetLastWriteTimeUtc(png)>=File.GetLastWriteTimeUtc(text)))return;
            string brand,serial;ReadClassicText(appRoot,out brand,out serial);
            GenerateClassic(appRoot,brand,serial);
        }
        private static string ReadUserName(string path)
        {
            string id=Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            string label=id.Substring(0,8);
            string labelPath=Path.ChangeExtension(path,".label");
            if(File.Exists(labelPath))
            {
                string stored=File.ReadAllText(labelPath,Encoding.UTF8).Trim();
                if(stored.Length>0)label=stored.Length>40?stored.Substring(0,40):stored;
            }
            return label;
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
                    string label=ReadUserName(path);
                    result.Add(Choice("USER_"+id,"自定义 · "+label,path,false,true,label));
                    result.Add(Choice("USER_"+id,"自定义 · "+label,path,true,true,label));
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
        // User-import v1.1: user-adjustable centered video aperture.
        // Existing v1.0 user imports without .aperture metadata keep 1680x748.
        // Built-in Dark/Classic are unchanged.
        internal const int ImportWidth=1920, ImportHeight=1080;
        internal const int ImportApertureWidth=1680, ImportApertureHeight=748;
        // New-import dialog preference only; existing PNG metadata and built-in apertures remain unchanged.
        internal const int ImportDialogDefaultWidth=1650, ImportDialogDefaultHeight=720;
        internal const string ImportLastWidthKey="FILM_BORDER_IMPORT_LAST_WIDTH";
        internal const string ImportLastHeightKey="FILM_BORDER_IMPORT_LAST_HEIGHT";
        internal const int ImportApertureTolerance=50;
        // Import profiles are explicitly selected by perforation direction.
        // The legacy 1680x748 gate remains unchanged for old imports and callers.
        internal const string ImportLayoutSide="SIDE";
        internal const string ImportLayoutTopBottom="TOP_BOTTOM";
        internal const string ImportLastLayoutKey="FILM_BORDER_IMPORT_LAYOUT";
        internal const string ImportSideLastWidthKey="FILM_BORDER_SIDE_LAST_WIDTH";
        internal const string ImportSideLastHeightKey="FILM_BORDER_SIDE_LAST_HEIGHT";
        internal const string ImportTopBottomLastWidthKey="FILM_BORDER_TB_LAST_WIDTH";
        internal const string ImportTopBottomLastHeightKey="FILM_BORDER_TB_LAST_HEIGHT";
        internal const int ImportSideDefaultWidth=1688, ImportSideDefaultHeight=970;
        internal const int ImportTopBottomDefaultWidth=1880, ImportTopBottomDefaultHeight=780;
        internal const int MaxImportBytes=32*1024*1024;
        private static string AperturePath(string png) {return Path.ChangeExtension(png,".aperture");}
        internal static void ValidateAperture(int width,int height)
        {
            if(width<ImportApertureWidth-ImportApertureTolerance || width>ImportApertureWidth+ImportApertureTolerance ||
               height<ImportApertureHeight-ImportApertureTolerance || height>ImportApertureHeight+ImportApertureTolerance)
                throw new InvalidDataException("中心画窗宽必须在 1630–1730px，高必须在 698–798px。");
        }
        internal static void ValidateApertureForLayout(int width,int height,string layout)
        {
            if(string.IsNullOrEmpty(layout) || layout=="LEGACY") {ValidateAperture(width,height);return;}
            if(layout==ImportLayoutSide)
            {
                if(width<1600 || width>1780 || height<900 || height>1020)
                    throw new InvalidDataException("左右齿孔画窗宽必须在1600–1780px，高必须在900–1020px。");
                return;
            }
            if(layout==ImportLayoutTopBottom)
            {
                if(width<1600 || width>1910 || height<700 || height>860)
                    throw new InvalidDataException("上下齿孔画窗宽必须在1600–1910px，高必须在700–860px。");
                return;
            }
            throw new InvalidDataException("未知的胶片边框齿孔方向："+layout);
        }
        internal static void ReadUserAperture(string png,out int width,out int height)
        {
            width=ImportApertureWidth;height=ImportApertureHeight;
            string metadata=AperturePath(png);
            if(!File.Exists(metadata))return; // backwards compatibility for v1.0 imports
            string[] parts=File.ReadAllText(metadata,Encoding.UTF8).Trim().Split('\t');
            if((parts.Length!=2 && parts.Length!=3) ||
                !int.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out width) ||
                !int.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out height))
                throw new InvalidDataException("用户边框的画窗尺寸记录损坏："+metadata);
            ValidateApertureForLayout(width,height,parts.Length==3?parts[2]:"LEGACY");
        }
        private static readonly byte[] PngSignature={137,80,78,71,13,10,26,10};

        private static uint ReadPngUInt(BinaryReader reader)
        {
            uint a=reader.ReadByte(),b=reader.ReadByte(),c=reader.ReadByte(),d=reader.ReadByte();
            return (a<<24)|(b<<16)|(c<<8)|d;
        }
        private static void ValidatePngContainer(string filename)
        {
            using(var stream=new FileStream(filename,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var reader=new BinaryReader(stream))
            {
                if(stream.Length>MaxImportBytes)throw new InvalidDataException("PNG 文件不得超过 32 MiB。");
                if(stream.Length<67)throw new InvalidDataException("PNG 文件不完整。");
                byte[] signature=reader.ReadBytes(8);
                for(int i=0;i<PngSignature.Length;i++)
                    if(signature[i]!=PngSignature[i])
                        throw new InvalidDataException("只能导入真正的 PNG 文件，扩展名不能代替格式。");
                bool first=true,hasIdat=false,ended=false;
                while(stream.Position<stream.Length)
                {
                    if(stream.Length-stream.Position<12)throw new InvalidDataException("PNG 块结构不完整。");
                    uint length=ReadPngUInt(reader);
                    string name=Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if(length>(ulong)(stream.Length-stream.Position-4))
                        throw new InvalidDataException("PNG 块长度不合法。");
                    if(first)
                    {
                        if(name!="IHDR" || length!=13)throw new InvalidDataException("PNG 文件头 IHDR 不合法。");
                        uint w=ReadPngUInt(reader),h=ReadPngUInt(reader);
                        int depth=reader.ReadByte(),kind=reader.ReadByte();
                        int compression=reader.ReadByte(),filter=reader.ReadByte(),interlace=reader.ReadByte();
                        if(w!=ImportWidth || h!=ImportHeight)
                            throw new InvalidDataException("胶片边框必须为 1920 × 1080 像素，不接受自动拉伸。");
                        if(depth!=8 || kind!=6)
                            throw new InvalidDataException("必须是 8-bit RGBA PNG（每通道 8-bit，PNG color type=6），不接受 RGB、索引色或 16-bit。");
                        if(compression!=0 || filter!=0 || interlace!=0)
                            throw new InvalidDataException("PNG 必须使用标准压缩/滤波和非隔行扫描。");
                    }
                    else
                    {
                        if(name=="acTL" || name=="fcTL" || name=="fdAT")
                            throw new InvalidDataException("不接受 APNG 动画，只能导入单帧静态 PNG。");
                        if(name=="IHDR")throw new InvalidDataException("PNG 重复 IHDR 块。");
                        if(name=="IDAT")hasIdat=true;
                        if(name=="IEND")
                        {
                            if(length!=0)throw new InvalidDataException("PNG IEND 块不合法。");
                            ended=true;
                        }
                        stream.Seek(length,SeekOrigin.Current);
                    }
                    reader.ReadUInt32(); // PNG CRC is processed by image decoder below.
                    first=false;
                    if(ended)break;
                }
                if(!ended || !hasIdat || stream.Position!=stream.Length)
                    throw new InvalidDataException("PNG 缺少图像数据、结束块，或存在多余内容。");
            }
        }
        // Preserve all RGBA pixels, including semi-transparent color overlays
        // and film-grain layers over the video image. Reject only a practically
        // fully opaque gate, so an opaque scan can never silently hide the movie.
        internal static void ValidatePng(string filename)
        { ValidatePngWithAperture(filename,ImportApertureWidth,ImportApertureHeight); }

        internal static void ValidatePngWithAperture(string filename,int apertureWidth,int apertureHeight)
        { ValidatePngInternal(filename,apertureWidth,apertureHeight,"LEGACY"); }

        internal static void ValidatePngForLayout(string filename,int apertureWidth,int apertureHeight,string layout)
        { ValidatePngInternal(filename,apertureWidth,apertureHeight,layout); }

        private static void ValidatePngInternal(string filename,int apertureWidth,int apertureHeight,string layout)
        {
            ValidateApertureForLayout(apertureWidth,apertureHeight,layout);
            ValidatePngContainer(filename);
            int x0=(ImportWidth-apertureWidth)/2;
            int y0=(ImportHeight-apertureHeight)/2;
            try
            {
                using(var source=new Bitmap(filename))
                using(var normalized=new Bitmap(ImportWidth,ImportHeight,PixelFormat.Format32bppArgb))
                {
                    if(source.Width!=ImportWidth || source.Height!=ImportHeight)
                        throw new InvalidDataException("解码后的胶片边框尺寸不正确。");
                    using(var g=Graphics.FromImage(normalized))
                    {
                        g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.DrawImage(source,new Rectangle(0,0,ImportWidth,ImportHeight));
                    }
                    var area=new Rectangle(0,0,ImportWidth,ImportHeight);
                    BitmapData pixels=normalized.LockBits(area,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] row=new byte[ImportWidth*4];
                        int visibleBorder=0,opaqueCenter=0;
                        for(int y=0;y<ImportHeight;y++)
                        {
                            Marshal.Copy(IntPtr.Add(pixels.Scan0,y*pixels.Stride),row,0,row.Length);
                            for(int x=0;x<ImportWidth;x++)
                            {
                                int alpha=row[x*4+3];
                                bool inAperture=x>=x0 && x<x0+apertureWidth &&
                                    y>=y0 && y<y0+apertureHeight;
                                if(inAperture)
                                {
                                    if(alpha==255)opaqueCenter++;
                                }
                                else if(alpha>=64)visibleBorder++;
                            }
                        }
                        // Allow fully opaque scratches, marks and corner detail,
                        // but not a film overlay that hides essentially all source video.
                        if(opaqueCenter > (long)apertureWidth*apertureHeight*95/100)
                            throw new InvalidDataException("中心画窗超过95%完全不透明，视频会被遮挡；请保留半透明或透明区域。");
                        if(visibleBorder<1024)
                            throw new InvalidDataException("边框区域可见像素不足（需至少 1024 个 Alpha≥64 的像素）。");
                    }
                    finally {normalized.UnlockBits(pixels);}
                }
            }
            catch(ArgumentException ex){throw new InvalidDataException("PNG 图像无法解码。",ex);}
            catch(OutOfMemoryException ex){throw new InvalidDataException("PNG 图像无效或内存不足。",ex);}
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
        internal static string ValidateDisplayName(string value)
        {
            string name=(value??"").Trim();
            if(name.Length==0)throw new InvalidDataException("请输入边框名称，不能为空。");
            if(name.Length>40)throw new InvalidDataException("边框名称最多40个字符。");
            foreach(char c in name)
                if(char.IsControl(c) || c=='\u2028' || c=='\u2029')
                    throw new InvalidDataException("边框名称不能包含换行、Tab 或控制字符。");
            return name;
        }

        // Output-name normalization is shared by import validation and encoding.
        internal static string SafeUserFileToken(string name)
        {
            if(string.IsNullOrWhiteSpace(name))return "";
            var safe=new StringBuilder();
            foreach(char c in name.Trim().Normalize(NormalizationForm.FormC))
            {
                if(char.IsControl(c) || c=='\u2028' || c=='\u2029' ||
                   "\\/:*?\"<>|".IndexOf(c)>=0)
                    safe.Append('_');
                else safe.Append(c);
            }
            return safe.ToString().Trim(' ','.','_');
        }

        // The dialog validates names early; Import repeats the guard before
        // creating files. Existing old imports are checked on encode as well.
        internal static string ValidateUniqueDisplayName(string appRoot,string value,string exceptStyleId=null)
        {
            string name=ValidateDisplayName(value);
            string token=SafeUserFileToken(name);
            if(token.Length==0)
                throw new InvalidDataException("边框名称不能仅包含文件名非法字符。");
            string folder=UserRoot(appRoot);
            if(!Directory.Exists(folder))return name;
            foreach(string path in Directory.GetFiles(folder,"*.png",SearchOption.TopDirectoryOnly))
            {
                string id=Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
                Guid parsed;
                if(id.Length!=32 || !Guid.TryParseExact(id,"N",out parsed))continue;
                if(string.Equals("USER_"+id,exceptStyleId,StringComparison.OrdinalIgnoreCase))continue;
                if(string.Equals(token,SafeUserFileToken(ReadUserName(path)),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("边框名称已存在，请输入其他名称。");
            }
            return name;
        }

        internal static string Import(string appRoot,string source,string displayName)
        {return Import(appRoot,source,displayName,ImportApertureWidth,ImportApertureHeight);}

        internal static string Import(string appRoot,string source,string displayName,int apertureWidth,int apertureHeight)
        {return ImportWithLayout(appRoot,source,displayName,apertureWidth,apertureHeight,"LEGACY");}

        internal static string ImportWithLayout(string appRoot,string source,string displayName,int apertureWidth,int apertureHeight,string layout)
        {
            displayName=ValidateUniqueDisplayName(appRoot,displayName);
            ValidateApertureForLayout(apertureWidth,apertureHeight,layout);
            if(!File.Exists(source))throw new FileNotFoundException("PNG 不存在",source);
            ValidatePngForLayout(source,apertureWidth,apertureHeight,layout);
            string id=Guid.NewGuid().ToString("N").ToUpperInvariant();
            string folder=UserRoot(appRoot);
            Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,id+".png");
            string raw=Path.Combine(folder,id+".raw");
            try {
                File.Copy(source,path,false);
                BuildRaw(path,raw);
                File.WriteAllText(Path.ChangeExtension(path,".label"),displayName,new UTF8Encoding(false));
                File.WriteAllText(AperturePath(path),
                    apertureWidth.ToString(CultureInfo.InvariantCulture)+"\t"+
                    apertureHeight.ToString(CultureInfo.InvariantCulture)+
                    (layout=="LEGACY"?"\n":"\t"+layout+"\n"),new UTF8Encoding(false));
                return "USER_"+id+"_STATIC";
            } catch {
                foreach(string f in new[]{path,raw,Path.ChangeExtension(path,".label"),AperturePath(path)})
                    try {if(File.Exists(f))File.Delete(f);}catch{}
                throw;
            }
        }
        internal static void Delete(string appRoot,string mode)
        {
            FilmBorderChoice choice;string reason;
            if(!TryResolve(appRoot,mode,out choice,out reason) || !choice.User)
                throw new InvalidOperationException("只能删除用户导入的边框，不能删除内置素材。");
            foreach(string file in new[]{choice.PngPath,choice.RawPath,Path.ChangeExtension(choice.PngPath,".label"),AperturePath(choice.PngPath)})
                if(File.Exists(file))File.Delete(file);
        }
        // Reuse LUT Gallery's persistent, timestamp-checked thumbnail approach.
        // The native preview uses Windows' existing owner-draw LISTBOX and LoadImageW,
        // so the thumbnails are cached as opaque BMPs (not PNG/GDI+ dependencies).
        private const int BorderThumbWidth=128,BorderThumbHeight=72;
        private static string ThumbnailRoot(string appRoot)
        { return Path.Combine(Root(appRoot),"_Thumbnails_v1_128x72"); }

        internal static void RebuildPreviewThumbnails(string appRoot)
        {
            string folder=ThumbnailRoot(appRoot);
            if(!Directory.Exists(folder))return;
            // Delete only the generated BMP cache, never original film frames or User data.
            foreach(string path in Directory.GetFiles(folder,"*.bmp",SearchOption.TopDirectoryOnly))
                File.Delete(path);
        }

        private static string EnsurePreviewThumbnail(string appRoot,FilmBorderChoice choice)
        {
            string folder=ThumbnailRoot(appRoot);
            string thumb=Path.Combine(folder,choice.StyleId+".bmp");
            try {
                if(File.Exists(thumb) && new FileInfo(thumb).Length>54 &&
                   File.GetLastWriteTimeUtc(thumb)>=File.GetLastWriteTimeUtc(choice.PngPath))
                    return thumb;
                Directory.CreateDirectory(folder);
                string temporary=thumb+".tmp_"+Guid.NewGuid().ToString("N");
                try {
                    using(var original=new Bitmap(choice.PngPath))
                    using(var small=new Bitmap(BorderThumbWidth,BorderThumbHeight,PixelFormat.Format24bppRgb))
                    using(var g=Graphics.FromImage(small)) {
                        // Light neutral background reveals transparent sprocket holes.
                        // Keep the actual PNG texture/colors; do not add synthetic grading.
                        g.Clear(Color.FromArgb(93,97,102));
                        g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(original,new Rectangle(0,0,BorderThumbWidth,BorderThumbHeight),
                            0,0,original.Width,original.Height,GraphicsUnit.Pixel);
                        small.Save(temporary,ImageFormat.Bmp);
                    }
                    if(File.Exists(thumb))File.Delete(thumb);
                    File.Move(temporary,thumb);
                } finally {if(File.Exists(temporary))File.Delete(temporary);}
                return thumb;
            } catch(IOException) {return "";}
              catch(UnauthorizedAccessException) {return "";}
              catch(ArgumentException) {return "";}
              catch(OutOfMemoryException) {return "";}
        }

        internal static string CreatePreviewChoices(string appRoot,string ffmpeg,string temporary,
            bool hdr,string transfer,CancellationToken token,bool expand=false)
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
                    string previewStock=expand?FilmBorderExpandCanvasCore.WritePreviewStock(actual.PngPath,temporary):actual.PngPath;
                    var args=new List<string>{"-hide_banner","-nostdin","-v","error","-y","-i",previewStock,"-frames:v","1"};
                    if(hdr)args.AddRange(new[]{"-vf","format=gbrap16le,zscale=matrixin=gbr:primariesin=709:transferin=iec61966-2-1:rangein=full"+
                        ":matrix=gbr:primaries=2020:transfer="+transfer+":range=full:npl=100:threads=1"});
                    args.AddRange(new[]{"-pix_fmt",hdr?"rgba64le":"rgba","-f","rawvideo",raw});
                    int rc=NativeLibraryTools.RunProcessEnvironment(ffmpeg,args,temporary,null,30000,token,null);
                    if(rc!=0 || !File.Exists(raw) || new FileInfo(raw).Length!=1920L*1080*(hdr?8:4))
                        throw new InvalidDataException("边框纹理转换失败："+actual.Label);
                    if(expand)File.Delete(previewStock);
                    cache.Add(actual.StyleId,raw);
                }
                // Share one BMP thumbnail for the STATIC/WEAVE entries of each style.
                // This also handles a newly imported border when the dialog reopens.
                string thumb=EnsurePreviewThumbnail(appRoot,actual);
                lines.Add(choice.Mode+"\t"+choice.Label+"\t"+raw+"\t"+
                    actual.ApertureWidth1080.ToString(CultureInfo.InvariantCulture)+"\t"+
                    actual.ApertureHeight1080.ToString(CultureInfo.InvariantCulture)+"\t"+thumb);
            }
            string output=Path.Combine(temporary,"border_choices.txt");
            File.WriteAllText(output,string.Join("\r\n",lines)+"\r\n",Encoding.Unicode);
            return output;
        }
    }
}