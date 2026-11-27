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
            using(var image=FilmBorderExpandCanvasCore.RenderTexture(src,1680,748,g))
            {
                Check(image.Width==2048 && image.Height==1208,"Texture dimensions");
                Check(image.GetPixel(64,64).ToArgb()==Color.Black.ToArgb(),"Protected top-left");
                Check(image.GetPixel(1983,1143).ToArgb()==Color.Black.ToArgb(),"Protected bottom-right");
                Check(image.GetPixel(1024,604).ToArgb()==Color.Black.ToArgb(),"Protected center");
                Check(image.GetPixel(32,32).B>128,"Corner texture preserved");
                for(int y=0;y<image.Height;y+=7) for(int x=0;x<image.Width;x+=7)
                    Check(image.GetPixel(x,y).R==0,"Source aperture contamination");
            }
        }
        foreach(string trc in new[]{"bt709","smpte2084","arib-std-b67"})
        {
            string tx=FilmBorderExpandCanvasCore.TextureFilter(trc,false,true);
            Check(tx.StartsWith("trim=end_frame=1,setpts=0"),"Texture clock");
            Check(tx.EndsWith("format=p010le,hwupload"),"Texture upload");
            foreach(bool moving in new[]{false,true})
            {
                string x=moving?"if(gt(t,1),2,0)":"", y=moving?"-2":"";
                string gpu=FilmBorderExpandCanvasCore.GpuGraph(g,16,15,trc,false,x,y,true);
                Check(gpu.StartsWith("[fgsfilm_gpu_video]split=2[fgsexact][fgsclock]"),"Source-owned clock");
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
        Console.WriteLine("PASS Expand Canvas geometry, texture isolation, source-clock graph contracts");
        return 0;
    }
}
