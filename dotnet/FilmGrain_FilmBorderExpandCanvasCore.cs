using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

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
        internal const string ThicknessKey="FILM_BORDER_EXPAND_PX";
        internal const int DefaultThickness=64;
        internal const int MaximumThickness=512;
        internal static bool TryParseThickness(string value,out int pixels)
        {
            pixels=0;
            return String.IsNullOrEmpty(value) || (Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out pixels)
                && pixels>=0 && pixels<=MaximumThickness && (pixels&1)==0);
        }
        // The old test-only numeric value now serves as an enable flag.
        internal static FilmBorderExpandGeometry NaturalGeometry(int width,int height,int apertureWidth,int apertureHeight)
        {
            if(apertureWidth<=0 || apertureHeight<=0 || apertureWidth>=1920 || apertureHeight>=1080)
                throw new ArgumentException("Invalid film border aperture.");
            double scale=Math.Min((double)width/apertureWidth,(double)height/apertureHeight);
            int x=(1920-apertureWidth)/2,y=(1080-apertureHeight)/2;
            return new FilmBorderExpandGeometry(width,height,EvenMargin(x,scale),EvenMargin(y,scale),
                EvenMargin(1920-x-apertureWidth,scale),EvenMargin(1080-y-apertureHeight,scale));
        }
        private static int EvenMargin(int margin,double scale)
        {return Math.Max(2,2*(int)Math.Floor(margin*scale/2+0.5));}

        internal static string WriteTexture(string source,int apertureWidth,int apertureHeight,FilmBorderExpandGeometry geometry,string directory)
        {
            string path=Path.Combine(directory,"__FGS_EXPAND_"+Guid.NewGuid().ToString("N")+".png");
            try
            {
                using(var input=new Bitmap(source))
                using(var texture=RenderTexture(input,apertureWidth,apertureHeight,geometry))
                    texture.Save(path,ImageFormat.Png);
                return path;
            }
            catch { try { if(File.Exists(path)) File.Delete(path); } catch {} throw; }
        }
        private static string N(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        // Choose the quietest shared location on both opposing rails. Average
        // local derivative over 13 pixels and mildly prefer solid bright stock.
        // Only samples outside the declared original aperture participate.
        private static int QuietSeam(byte[] pixels,int length,int start,int end,
            int railStart,int railEnd,int across,bool horizontal)
        {
            const int stride=1920*4;
            double[] delta=new double[length],mean=new double[length];
            int samples=(railStart+across-railEnd)*3;
            for(int pos=1;pos<length;pos++)
            {
                long diff=0,sum=0;
                for(int v=0;v<across;v++)
                {
                    if(v>=railStart && v<railEnd)continue;
                    int i=horizontal?v*stride+4*pos:pos*stride+4*v;
                    int prev=i-(horizontal?4:stride);
                    for(int ch=0;ch<3;ch++)
                    {diff+=Math.Abs((int)pixels[i+ch]-pixels[prev+ch]);sum+=pixels[i+ch];}
                }
                delta[pos]=(double)diff/samples;
                mean[pos]=(double)sum/samples;
            }
            int low=Math.Max(start+1,Math.Max(7,start+(end-start)/10));
            int high=Math.Min(end-1,Math.Min(length-7,end-(end-start)/10));
            if(low>=high)return (start+end)/2;
            double bright=0;for(int i=low;i<high;i++)bright=Math.Max(bright,mean[i]);
            double best=Double.MaxValue;int chosen=low;
            for(int i=low;i<high;i++)
            {
                double variation=0;for(int j=-6;j<=6;j++)variation+=delta[i+j];
                double score=variation/13+0.03*(bright-mean[i])+0.1*Math.Abs(i-(start+end)/2.0)/(end-start);
                if(score<best){best=score;chosen=i;}
            }
            return chosen;
        }
        private sealed class Axis
        {
            internal int[] Source,Target;
            internal int Insert=-1;
            internal bool IsPicture(int i){return i>0 && i<Source.Length-2;}
        }
        private static Axis MapAxis(int total,int start,int end,int seam,int first,int content,int last,double scale)
        {
            int fitted=Math.Min(content,Math.Max(1,(int)Math.Floor((end-start)*scale+0.5)));
            int extra=content-fitted;
            if(extra==0 || fitted<3)return new Axis{Source=new[]{0,start,end,total},
                Target=new[]{0,first,first+content,first+content+last}};
            int one=Math.Min(fitted-2,Math.Max(1,(int)Math.Floor(scale+0.5)));
            int before=Math.Max(1,Math.Min(fitted-one-1,(int)Math.Floor((seam-start)*scale+0.5)));
            return new Axis{Source=new[]{0,start,seam,seam+1,end,total},
                Target=new[]{0,first,first+before,first+before+one+extra,first+content,first+content+last},Insert=2};
        }
        // Isotropic scale of the stock; only a narrow quiet seam is extended
        // when the aperture aspect differs. Corners, lettering and sprockets
        // outside that seam retain shape, apart from integer pixel rounding.
        // Preview and production consume the same rendered texture.
        internal static Bitmap RenderTexture(Bitmap source,int apertureWidth,int apertureHeight,FilmBorderExpandGeometry g)
        {
            if(source==null || source.Width!=1920 || source.Height!=1080)
                throw new ArgumentException("Invalid film border source.");
            FilmBorderExpandGeometry expected=NaturalGeometry(g.Width,g.Height,apertureWidth,apertureHeight);
            if(g.Left!=expected.Left || g.Top!=expected.Top || g.Right!=expected.Right || g.Bottom!=expected.Bottom)
                throw new ArgumentException("Natural film border geometry mismatch.");
            int sx=(1920-apertureWidth)/2,sy=(1080-apertureHeight)/2,ex=sx+apertureWidth,ey=sy+apertureHeight;
            double scale=Math.Min((double)g.Width/apertureWidth,(double)g.Height/apertureHeight);
            using(var stock=new Bitmap(1920,1080,PixelFormat.Format32bppPArgb))
            {
                using(Graphics draw=Graphics.FromImage(stock))
                {draw.Clear(Color.Black);draw.DrawImageUnscaled(source,0,0);}
                byte[] pixels=new byte[1920*1080*4];
                BitmapData data=stock.LockBits(new Rectangle(0,0,1920,1080),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                try
                {
                    for(int y=0;y<1080;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*1920*4,1920*4);
                }
                finally {stock.UnlockBits(data);}
                int seamX=QuietSeam(pixels,1920,sx,ex,sy,ey,1080,true);
                int seamY=QuietSeam(pixels,1080,sy,ey,sx,ex,1920,false);
                Axis xMap=MapAxis(1920,sx,ex,seamX,g.Left,g.Width,g.Right,scale);
                Axis yMap=MapAxis(1080,sy,ey,seamY,g.Top,g.Height,g.Bottom,scale);
                Bitmap canvas=new Bitmap(g.CanvasWidth,g.CanvasHeight,PixelFormat.Format32bppPArgb);
                try
                {
                    using(Graphics target=Graphics.FromImage(canvas))
                    {
                        target.Clear(Color.Black);
                        target.CompositingMode=CompositingMode.SourceCopy;
                        target.PixelOffsetMode=PixelOffsetMode.Half;
                        for(int y=0;y<yMap.Source.Length-1;y++)for(int x=0;x<xMap.Source.Length-1;x++)
                        {
                            if(xMap.IsPicture(x) && yMap.IsPicture(y))continue;
                            Rectangle src=Rectangle.FromLTRB(xMap.Source[x],yMap.Source[y],xMap.Source[x+1],yMap.Source[y+1]);
                            Rectangle dst=Rectangle.FromLTRB(xMap.Target[x],yMap.Target[y],xMap.Target[x+1],yMap.Target[y+1]);
                            if(src.Width<=0 || src.Height<=0 || dst.Width<=0 || dst.Height<=0)continue;
                            target.InterpolationMode=(x==xMap.Insert || y==yMap.Insert)?
                                InterpolationMode.NearestNeighbor:InterpolationMode.Bilinear;
                            using(Bitmap tile=stock.Clone(src,PixelFormat.Format32bppPArgb))
                            using(ImageAttributes attrs=new ImageAttributes())
                            {
                                attrs.SetWrapMode(WrapMode.TileFlipXY);
                                GraphicsState saved=target.Save();
                                target.SetClip(dst);
                                target.DrawImage(tile,dst,0,0,tile.Width,tile.Height,GraphicsUnit.Pixel,attrs);
                                target.Restore(saved);
                            }
                        }
                    }
                    return canvas;
                }
                catch {canvas.Dispose();throw;}
            }
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
