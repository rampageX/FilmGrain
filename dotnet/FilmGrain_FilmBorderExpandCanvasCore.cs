using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace FilmGrainStudioPreview
{
    // Expand-only geometry. Do not use the film aperture to resize the picture.
    // This is a candidate core; routing and GUI must remain gated until real graph tests pass.
    internal sealed class FilmBorderExpandGeometry
    {
        internal readonly int Width, Height, Left, Top, Right, Bottom, CanvasWidth, CanvasHeight;
        internal FilmBorderExpandGeometry(int width,int height,int left,int top,int right,int bottom)
        {
            if(width<32 || height<32 || left<2 || top<2 || right<2 || bottom<2 ||
                ((width|height|left|top|right|bottom)&1)!=0)
                throw new ArgumentException("Expand Canvas requires even picture and positive even border sizes.");
            long w=(long)width+left+right, h=(long)height+top+bottom;
            if(w>16384 || h>16384) throw new ArgumentException("Expand Canvas exceeds geometry limit 16384.");
            Width=width; Height=height; Left=left; Top=top; Right=right; Bottom=bottom;
            CanvasWidth=(int)w; CanvasHeight=(int)h;
        }
        internal Rectangle Picture { get { return new Rectangle(Left,Top,Width,Height); } }
    }

    internal static class FilmBorderExpandCanvasCore
    {
        private static string N(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        // Eight isolated pieces: no samples from the source aperture, including opaque scratches.
        // Aspect-fit each piece; black film stock fills unused area. Alpha is resolved against black.
        internal static Bitmap RenderTexture(Bitmap source,int apertureWidth,int apertureHeight,FilmBorderExpandGeometry g)
        {
            if(source==null || source.Width!=1920 || source.Height!=1080 ||
                apertureWidth<=0 || apertureHeight<=0 || apertureWidth>=1920 || apertureHeight>=1080)
                throw new ArgumentException("Invalid film border source/aperture.");
            int sx=(source.Width-apertureWidth)/2, sy=(source.Height-apertureHeight)/2;
            int[] xs={0,sx,sx+apertureWidth,source.Width};
            int[] ys={0,sy,sy+apertureHeight,source.Height};
            int[] xd={0,g.Left,g.Left+g.Width,g.CanvasWidth};
            int[] yd={0,g.Top,g.Top+g.Height,g.CanvasHeight};
            Bitmap canvas=new Bitmap(g.CanvasWidth,g.CanvasHeight,PixelFormat.Format32bppPArgb);
            try
            {
                using(Graphics target=Graphics.FromImage(canvas))
                {
                    target.Clear(Color.Black);
                    target.CompositingMode=CompositingMode.SourceOver;
                    target.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    target.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    for(int y=0;y<3;y++) for(int x=0;x<3;x++)
                    {
                        if(x==1 && y==1) continue;
                        Rectangle src=new Rectangle(xs[x],ys[y],xs[x+1]-xs[x],ys[y+1]-ys[y]);
                        Rectangle dst=new Rectangle(xd[x],yd[y],xd[x+1]-xd[x],yd[y+1]-yd[y]);
                        if(src.Width==0 || src.Height==0) continue;
                        double scale=Math.Min((double)dst.Width/src.Width,(double)dst.Height/src.Height);
                        int w=Math.Min(dst.Width,Math.Max(1,(int)Math.Round(src.Width*scale)));
                        int h=Math.Min(dst.Height,Math.Max(1,(int)Math.Round(src.Height*scale)));
                        Rectangle fit=new Rectangle(dst.X+(dst.Width-w)/2,dst.Y+(dst.Height-h)/2,w,h);
                        // Isolate the tile before interpolation. Clip the destination as a second guard.
                        using(Bitmap tile=source.Clone(src,PixelFormat.Format32bppPArgb))
                        using(ImageAttributes attrs=new ImageAttributes())
                        {
                            attrs.SetWrapMode(WrapMode.TileFlipXY);
                            GraphicsState saved=target.Save();
                            target.SetClip(dst);
                            target.DrawImage(tile,fit,0,0,tile.Width,tile.Height,GraphicsUnit.Pixel,attrs);
                            target.Restore(saved);
                        }
                    }
                }
                return canvas;
            }
            catch { canvas.Dispose(); throw; }
        }

        // Convert only the artwork, once. The selected range/transfer are a property
        // of the incoming content, never inferred from the artwork.
        internal static string TextureFilter(string transfer,bool fullRange,bool gpu)
        {
            if(transfer!="bt709" && transfer!="smpte2084" && transfer!="arib-std-b67")
                throw new ArgumentException("Unvalidated Expand Canvas transfer.");
            bool hdr=transfer!="bt709";
            if(hdr && fullRange) throw new ArgumentException("Full-range HDR border is not validated.");
            string p=hdr?"2020":"709", m=hdr?"2020_ncl":"709", r=fullRange?"full":"limited";
            return "trim=end_frame=1,setpts=0,format=gbrp16le,zscale=matrixin=gbr:primariesin=709:"+
                "transferin=iec61966-2-1:rangein=full:matrix="+m+":primaries="+p+
                ":transfer="+transfer+":range="+r+":npl=100:threads=1,format=yuv420p10le"+
                (gpu?",format=p010le,hwupload":"");
        }

        private static string Sar(int num,int den)
        {
            if(num<=0 || den<=0) throw new ArgumentException("Effective SAR must be known.");
            return "setsar=sar="+N(num)+"/"+N(den)+":max=65535";
        }

        // The background's main input is a branch of the source, NOT a looping CFR
        // texture. overlay_vulkan copies its main input's frame properties. The
        // scaled clock branch is entirely overwritten before the actual picture
        // is copied into the canvas by the final, non-alpha overlay.
        // Both video and texture inputs are P010 Vulkan frames; texture is single-frame.
        // Inputs must start on the source's normalized nonnegative timeline.
        internal static string GpuGraph(FilmBorderExpandGeometry g,int sarNum,int sarDen,
            string transfer,bool fullRange,string moveX,string moveY,bool cuda)
        {
            if(transfer!="bt709" && transfer!="smpte2084" && transfer!="arib-std-b67")
                throw new ArgumentException("Unvalidated Expand Canvas transfer.");
            bool hdr=transfer!="bt709";
            if(hdr && fullRange) throw new ArgumentException("Full-range HDR border is not validated.");
            string w=N(g.CanvasWidth),h=N(g.CanvasHeight);
            string graph="[fgsfilm_gpu_video]split=2[fgsexact][fgsclock];"+
                "[fgsclock]scale_vulkan=w="+w+":h="+h+":scaler=nearest[fgsbase];"+
                "[fgsbase][fgstexture]overlay_vulkan=x=0:y=0[fgsart]";
            string art="fgsart";
            if(!String.IsNullOrEmpty(moveX) || !String.IsNullOrEmpty(moveY))
            {
                string p=hdr?"bt2020":"bt709",m=hdr?"bt2020nc":"bt709";
                graph+=";[fgsart]libplacebo=inputs=1:w="+w+":h="+h+
                    ":format=p010le:color_primaries="+p+":colorspace="+m+":color_trc="+transfer+
                    ":range="+(fullRange?"full":"limited")+":crop_x=0:crop_y=0:crop_w=iw:crop_h=ih"+
                    ":pos_w=iw:pos_h=ih:pos_x='"+(String.IsNullOrEmpty(moveX)?"0":moveX)+
                    "':pos_y='"+(String.IsNullOrEmpty(moveY)?"0":moveY)+
                    "':tonemapping=clip:peak_detect=false:dithering=none:fillcolor=black[fgsmoved]";
                art="fgsmoved";
            }
            return graph+";["+art+"][fgsexact]overlay_vulkan=x="+N(g.Left)+":y="+N(g.Top)+
                ","+Sar(sarNum,sarDen)+(cuda?",hwupload=derive_device=cuda":"")+"[vout]";
        }

        // Both inputs are planar yuv420p10le. The padded clock branch can be altered
        // freely by overlay: only its four non-overlapping strips enter xstack.
        // xstack copies the unchanged source plane samples, not an alpha blend.
        internal static string CpuGraph(FilmBorderExpandGeometry g,int sarNum,int sarDen,
            string transfer,bool fullRange,string moveX,string moveY)
        {
            if(transfer!="bt709" && transfer!="smpte2084" && transfer!="arib-std-b67")
                throw new ArgumentException("Unvalidated Expand Canvas transfer.");
            bool hdr=transfer!="bt709";
            if(hdr && fullRange) throw new ArgumentException("Full-range HDR border is not validated.");
            string w=N(g.CanvasWidth),h=N(g.CanvasHeight),l=N(g.Left),t=N(g.Top);
            string bottom=N(g.Top+g.Height),right=N(g.Left+g.Width);
            string x=String.IsNullOrEmpty(moveX)?"0":moveX,y=String.IsNullOrEmpty(moveY)?"0":moveY;
            return "[fgsfilm_src]split=2[fgsexact][fgsclock];"+
                "[fgsclock]pad="+w+":"+h+":"+l+":"+t+":color=black[fgsbase];"+
                "[fgsbase][fgstexture]overlay=x='"+x+"':y='"+y+
                "':eval=frame:eof_action=repeat:format=yuv420p10,format=yuv420p10le,split=4[fgst][fgsb][fgsl][fgsr];"+
                "[fgst]crop="+w+":"+t+":0:0[fgstop];"+
                "[fgsb]crop="+w+":"+N(g.Bottom)+":0:"+bottom+"[fgsbottom];"+
                "[fgsl]crop="+l+":"+N(g.Height)+":0:"+t+"[fgsleft];"+
                "[fgsr]crop="+N(g.Right)+":"+N(g.Height)+":"+right+":"+t+"[fgsright];"+
                "[fgsexact][fgstop][fgsbottom][fgsleft][fgsright]xstack=inputs=5:layout="+
                l+"_"+t+"|0_0|0_"+bottom+"|0_"+t+"|"+right+"_"+t+":shortest=1,"+
                Sar(sarNum,sarDen)+",setparams=range="+(fullRange?"full":"limited")+
                ":color_primaries="+(hdr?"bt2020":"bt709")+":color_trc="+transfer+
                ":colorspace="+(hdr?"bt2020nc":"bt709")+"[vout]";
        }
    }
}
