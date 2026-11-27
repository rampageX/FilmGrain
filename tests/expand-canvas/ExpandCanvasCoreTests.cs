using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections;
using System.Reflection;
using System.IO;
using FilmGrainStudioPreview;

internal static class ExpandCanvasCoreTests
{
    static void Check(bool condition,string message)
    {
        if(!condition) throw new Exception(message);
    }
    static void Reject(Action action)
    {
        bool failed=false;
        try { action(); } catch(ArgumentException) { failed=true; }
        Check(failed,"Invalid geometry accepted");
    }
    // A fully opaque ring must fill every output perimeter pixel. A solid source
    // detects gaps introduced by mapping, independently of artistic transparency.
    static void CheckContinuousRing(int apertureWidth,int apertureHeight,FilmBorderExpandGeometry g)
    {
        using(var src=new Bitmap(1920,1080))
        {
            using(var draw=Graphics.FromImage(src))
            {
                draw.Clear(Color.Blue);
                draw.FillRectangle(Brushes.Magenta,(1920-apertureWidth)/2,(1080-apertureHeight)/2,
                    apertureWidth,apertureHeight);
            }
            using(var image=FilmBorderExpandCanvasCore.RenderTexture(src,apertureWidth,apertureHeight,g))
            {
                Rectangle picture=g.Picture;
                for(int y=0;y<image.Height;y++) for(int x=0;x<image.Width;x++)
                {
                    Color actual=image.GetPixel(x,y);
                    if(picture.Contains(x,y))
                        Check(actual.ToArgb()==Color.Black.ToArgb(),"Protected picture at "+x+","+y);
                    else
                        Check(actual.R==0 && actual.G==0 && actual.A==255,
                            "Aperture contamination at "+x+","+y);
                }
            }
        }
    }
    static void RenderCatalog(string root,string destination)
    {
        Assembly app=Assembly.LoadFrom(Path.Combine(root,"FilmGrain_Studio.exe"));
        Type catalog=app.GetType("FilmGrainStudioPreview.FilmBorderCatalogCore",true);
        var choices=(IEnumerable)catalog.GetMethod("List",BindingFlags.Static|BindingFlags.NonPublic)
            .Invoke(null,new object[]{root});
        int styles=0;
        foreach(object choice in choices)
        {
            Type type=choice.GetType();
            Func<string,object> field=delegate(string name)
            {return type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(choice);};
            string mode=(string)field("Mode");
            if(!mode.EndsWith("_STATIC",StringComparison.Ordinal))continue;
            string label=(string)field("UserName") ?? ((string)field("StyleId")=="DARK"?"Dark 35mm":"FGS Classic Black");
            string directory=Path.Combine(destination,label);
            Directory.CreateDirectory(directory);
            int aw=(int)field("ApertureWidth1080"),ah=(int)field("ApertureHeight1080");
            using(var stock=new Bitmap((string)field("PngPath")))
            {
                stock.Save(Path.Combine(directory,"Original.png"),ImageFormat.Png);
                Rectangle aperture=FilmBorderExpandCanvasCore.ArtworkAperture(stock,aw,ah);
                Console.WriteLine(label+" declared "+aw+"x"+ah+" artwork "+aperture);
                if(label=="FGS Lumen Ice 320")Check(aperture.Bottom<=909,"Lumen bottom split must precede complete perforations");
                foreach(Size size in new[]{new Size(1920,1080),new Size(2520,1080),new Size(1440,1080),new Size(1080,1920),
                    new Size(3840,2160),new Size(5040,2160),new Size(2880,2160),new Size(2160,3840)})
                {
                    var g=FilmBorderExpandCanvasCore.NaturalGeometry(size.Width,size.Height,aw,ah);
                    int dx=FilmBorderExpandCanvasCore.MotionGuard(g.Left,g.Right);
                    int dy=FilmBorderExpandCanvasCore.MotionGuard(g.Top,g.Bottom);
                    using(var art=FilmBorderExpandCanvasCore.RenderTexture(stock,aw,ah,g))
                    using(var sheet=new Bitmap(1500,335))
                    using(var draw=Graphics.FromImage(sheet))
                    {
                        Check(art.Width==g.CanvasWidth && art.Height==g.CanvasHeight,"Catalog canvas size");
                        Check(g.Picture.Size==size,"Catalog picture size");
                        Check(art.GetPixel(g.Left-dx,g.Top-dy).ToArgb()==Color.Black.ToArgb(),"Inner motion guard");
                        draw.Clear(Color.FromArgb(28,28,28));
                        int index=0;
                        foreach(Point move in new[]{Point.Empty,new Point(-dx,-dy),new Point(dx,-dy),new Point(-dx,dy),new Point(dx,dy)})
                        {
                            using(var frame=new Bitmap(g.CanvasWidth,g.CanvasHeight))
                            using(var compose=Graphics.FromImage(frame))
                            {
                                compose.Clear(Color.Black);
                                compose.DrawImageUnscaled(art,move.X,move.Y);
                                compose.FillRectangle(Brushes.DimGray,g.Picture);
                                double scale=Math.Min(296.0/frame.Width,296.0/frame.Height);
                                int w=(int)Math.Round(frame.Width*scale),h=(int)Math.Round(frame.Height*scale);
                                draw.DrawImage(frame,index*300+(300-w)/2,27+(296-h)/2,w,h);
                            }
                            string tag=index==0?"Static":String.Format("Weave ({0}, {1})",move.X,move.Y);
                            draw.DrawString(tag,SystemFonts.DefaultFont,Brushes.White,index*300+8,8);
                            index++;
                        }
                        sheet.Save(Path.Combine(directory,size.Width+"x"+size.Height+".png"),ImageFormat.Png);
                        if(label=="FGS Lumen Ice 320" && size.Width==1920 && size.Height==1080)
                            art.Save(Path.Combine(directory,"1920x1080_Artwork.png"),ImageFormat.Png);
                    }
                }
            }
            styles++;
        }
        Check(styles==17,"Expected 17 styles from the production catalog");
    }
    public static int Main(string[] args)
    {
        var g=new FilmBorderExpandGeometry(1920,1080,64,64,64,64);
        Check(g.CanvasWidth==2048 && g.CanvasHeight==1208,"1080p geometry");
        var portrait=new FilmBorderExpandGeometry(1080,1920,32,64,48,80);
        Check(portrait.Picture==new Rectangle(32,64,1080,1920),"Portrait asymmetric geometry");
        Check(portrait.CanvasWidth==1160 && portrait.CanvasHeight==2064,"Portrait canvas");
        Reject(delegate { new FilmBorderExpandGeometry(1921,1080,64,64,64,64); });
        Reject(delegate { new FilmBorderExpandGeometry(1920,1080,63,64,64,64); });
        Reject(delegate { new FilmBorderExpandGeometry(16384,1080,64,64,64,64); });
        Reject(delegate { new FilmBorderExpandGeometry(1920,1080,Int32.MaxValue,64,64,64); });
        using(var src=new Bitmap(1920,1080))
        {
            using(var draw=Graphics.FromImage(src))
            {
                draw.Clear(Color.Blue);
                draw.FillRectangle(Brushes.Magenta,120,166,1680,748);
            }
            var artGeometry=FilmBorderExpandCanvasCore.NaturalGeometry(1920,1080,1680,748);
            using(var image=FilmBorderExpandCanvasCore.RenderTexture(src,1680,748,artGeometry))
            {
                Check(image.Width==artGeometry.CanvasWidth && image.Height==artGeometry.CanvasHeight,"Texture dimensions");
                Check(image.GetPixel(artGeometry.Left,artGeometry.Top).ToArgb()==Color.Black.ToArgb(),"Protected top-left");
                Check(image.GetPixel(artGeometry.Left+1919,artGeometry.Top+1079).ToArgb()==Color.Black.ToArgb(),"Protected bottom-right");
                Check(image.GetPixel(artGeometry.Left+960,artGeometry.Top+540).ToArgb()==Color.Black.ToArgb(),"Protected center");
                Check(image.GetPixel(artGeometry.Left/2,artGeometry.Top/2).B>128,"Corner texture preserved");
                for(int y=0;y<image.Height;y+=7) for(int x=0;x<image.Width;x+=7)
                    Check(image.GetPixel(x,y).R==0,"Source aperture contamination");
            }
        }
        CheckContinuousRing(1680,748,FilmBorderExpandCanvasCore.NaturalGeometry(320,180,1680,748));
        CheckContinuousRing(1680,990,FilmBorderExpandCanvasCore.NaturalGeometry(180,320,1680,990));
        CheckContinuousRing(1680,748,FilmBorderExpandCanvasCore.NaturalGeometry(640,64,1680,748));
        CheckContinuousRing(1651,721,FilmBorderExpandCanvasCore.NaturalGeometry(64,640,1651,721));
        var chroma=FilmBorderExpandCanvasCore.NaturalGeometry(1920,1080,1684,970);
        var vista=FilmBorderExpandCanvasCore.NaturalGeometry(1920,1080,1888,800);
        Check(chroma.Left==132 && chroma.Top==62 && chroma.CanvasWidth==2184 && chroma.CanvasHeight==1204,"Chroma native aspect");
        Check(vista.Left==16 && vista.Top==142 && vista.CanvasWidth==1952 && vista.CanvasHeight==1364,"Vistalux native aspect");
        Console.WriteLine("PASS opaque stock aperture isolation (landscape, portrait, thin and odd aperture)");
        foreach(string trc in new[]{"bt709","smpte2084","arib-std-b67"})
        {
            string tx=FilmBorderExpandCanvasCore.TextureFilter(trc,false,true);
            Check(tx.StartsWith("trim=end_frame=1,settb=AVTB,setpts=0"),"Texture clock");
            Check(FilmBorderExpandCanvasCore.TextureFilter(trc,false,false).StartsWith("trim=end_frame=1,setpts=0"),"CPU texture clock unchanged");
            Check(tx.EndsWith("format=p010le,hwupload"),"Texture upload");
            foreach(bool moving in new[]{false,true})
            {
                string x=moving?"if(gt(t,1),2,0)":"", y=moving?"-2":"";
                string gpu=FilmBorderExpandCanvasCore.GpuGraph(g,16,15,trc,false,x,y,true);
                Check(gpu.StartsWith("[fgsfilm_gpu_video]settb=AVTB,split=2[fgsexact][fgsclock]"),"Source-owned clock aligned before split");
                Check(gpu.Contains("[fgsbase][fgstexture]overlay_vulkan"),"Texture secondary");
                Check(gpu.Contains("[fgsexact]overlay_vulkan=x=64:y=64"),"Final picture copy");
                Check(gpu.EndsWith("setsar=sar=16/15:max=65535,hwupload=derive_device=cuda[vout]"),"SAR/CUDA");
                Check(!gpu.Contains("hwdownload") && !gpu.Contains("fps="),"No transfer or new cadence");
                Check(gpu.Contains("libplacebo=")==moving,"Only move artwork branch");
                string cpu=FilmBorderExpandCanvasCore.CpuGraph(g,16,15,trc,false,x,y);
                Check(cpu.Contains("[fgsexact][fgstop][fgsbottom][fgsleft][fgsright]xstack"),"CPU direct region copy");
                Check(cpu.Contains("layout=64_64|0_0|0_1144|0_64|1984_64:shortest=1"),"CPU layout");
                Check(cpu.Contains("color_trc="+trc),"CPU signal");
            }
        }
        if(args.Length==2)RenderCatalog(args[0],args[1]);
        Console.WriteLine("PASS Expand Canvas geometry, texture isolation, source-clock graph contracts");
        return 0;
    }
}
