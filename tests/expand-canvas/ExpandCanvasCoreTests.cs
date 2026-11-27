using System;
using System.Drawing;
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
                        Check(actual.B>=250 && actual.R==0 && actual.G==0 && actual.A==255,
                            "Artificial stock gap or aperture contamination at "+x+","+y);
                }
            }
        }
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
                Check(image.GetPixel(2,2).B>128,"Corner texture preserved");
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
        Console.WriteLine("PASS complete opaque ring coverage (landscape, portrait, thin and odd aperture)");
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
        if(args.Length==2)
        {
            string[] files=Directory.GetFiles(args[0],"*.png");Array.Sort(files,StringComparer.Ordinal);
            Check(files.Length==15,"Expected all 15 stock textures");
            Directory.CreateDirectory(args[1]);
            foreach(string file in files)
            {
                string stem=Path.Combine(Path.GetDirectoryName(file),Path.GetFileNameWithoutExtension(file));
                string[] aperture=File.ReadAllText(stem+".aperture").Trim().Split('\t');
                int aw=Int32.Parse(aperture[0]),ah=Int32.Parse(aperture[1]);
                var geo=FilmBorderExpandCanvasCore.NaturalGeometry(1920,1080,aw,ah);
                using(var stock=new Bitmap(file))
                using(var result=FilmBorderExpandCanvasCore.RenderTexture(stock,aw,ah,geo))
                {
                    Check(result.GetPixel(geo.Left+960,geo.Top+540).ToArgb()==Color.Black.ToArgb(),"Stock aperture isolation");
                    using(Graphics draw=Graphics.FromImage(result))draw.FillRectangle(Brushes.DimGray,geo.Picture);
                    string label=File.ReadAllText(stem+".label").Trim();
                    result.Save(Path.Combine(args[1],label+"_"+geo.CanvasWidth+"x"+geo.CanvasHeight+".png"),System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine("PASS "+label+" "+geo.CanvasWidth+"x"+geo.CanvasHeight);
                }
            }
        }
        Console.WriteLine("PASS Expand Canvas geometry, texture isolation, source-clock graph contracts");
        return 0;
    }
}
