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

        private static int RailSize(int size,double scale)
        {return Math.Max(1,(int)Math.Floor(size*scale+0.5));}

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

        // Keep an even guard band so subsampled CPU overlay rounding stays inside it.
        internal static int MotionGuard(int first,int last)
        {return Math.Min(10,2*(Math.Min(first,last)/12));}

        private static string LimitMotion(string expression,int guard)
        {return String.IsNullOrEmpty(expression)?"0":"clip("+expression+",-"+N(guard)+","+N(guard)+")";}

        // Find the first open scanline towards the centre. The middle half of
        // the opposite aperture axis excludes rounded corners and side rails.
        // Require a run of open lines so isolated defects cannot move the split.
        private static int StockEdge(byte[] alpha,int start,int direction,int crossStart,int crossLength,bool horizontal)
        {
            int open=0;
            int length=horizontal?1080:1920;
            for(int offset=0;offset<64;offset++)
            {
                int position=start+offset*direction;
                if(position<0 || position>=length)break;
                int transparent=0;
                for(int i=crossStart;i<crossStart+crossLength;i++)
                {
                    int index=horizontal?position*1920+i:i*1920+position;
                    if(alpha[index*4+3]<128)transparent++;
                }
                open=transparent>crossLength/2?open+1:0;
                if(open==3)return position-2*direction+(direction<0?1:0);
            }
            return direction<0?start+1:start;
        }
        internal static Rectangle ArtworkAperture(Bitmap source,int width,int height)
        {
            int x=(1920-width)/2,y=(1080-height)/2;
            int crossX=x+width/4,crossY=y+height/4;
            using(var rgba=source.Clone(new Rectangle(0,0,1920,1080),PixelFormat.Format32bppArgb))
            {
                byte[] pixels=ReadPixels(rgba,PixelFormat.Format32bppArgb);
                return Rectangle.FromLTRB(
                    StockEdge(pixels,x,1,crossY,height/2,false),
                    StockEdge(pixels,y,1,crossX,width/2,true),
                    StockEdge(pixels,x+width-1,-1,crossY,height/2,false),
                    StockEdge(pixels,y+height-1,-1,crossX,width/2,true));
            }
        }

        private static byte[] ReadPixels(Bitmap bitmap,PixelFormat format)
        {
            const int stride=1920*4;
            byte[] pixels=new byte[stride*1080];
            BitmapData data=bitmap.LockBits(new Rectangle(0,0,1920,1080),ImageLockMode.ReadOnly,format);
            try
            {
                for(int y=0;y<1080;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*stride,stride);
            }
            finally {bitmap.UnlockBits(data);}
            return pixels;
        }

        // Compare full rail cross-sections. A repeat adds a complete stock
        // segment between matching positions instead of enlarging one pixel.
        private static double RailDifference(byte[] pixels,int a,int b,int railStart,int railEnd,int across,bool horizontal)
        {
            long difference=0;
            int samples=0;
            for(int v=0;v<across;v+=4)
            {
                if(v>=railStart && v<railEnd)continue;
                int i=horizontal?(v*1920+a)*4:(a*1920+v)*4;
                int j=horizontal?(v*1920+b)*4:(b*1920+v)*4;
                for(int channel=0;channel<3;channel++)
                    difference+=Math.Abs((int)pixels[i+channel]-pixels[j+channel]);
                samples+=3;
            }
            return samples==0?0:(double)difference/samples;
        }

        private static Rectangle[] MapAxis(byte[] pixels,int total,int start,int end,int railStart,int railEnd,
            int across,int first,int content,int last,double scale,bool horizontal)
        {
            int length=end-start;
            int extra=content-(int)Math.Round(length*scale);
            int pitch=0,begin=start;
            if(extra>0 && length>=150)
            {
                int low=start+length/5, high=end-length/5;
                double best=Double.MaxValue;
                for(int lag=36;lag<=Math.Min(280,(high-low)/2);lag++)
                {
                    double difference=0;
                    int count=0;
                    for(int pos=low;pos+lag<high;pos+=11)
                    {
                        difference+=RailDifference(pixels,pos,pos+lag,railStart,railEnd,across,horizontal);
                        count++;
                    }
                    double score=difference/count+0.008*lag;
                    if(score<best){best=score;pitch=lag;}
                }
                best=Double.MaxValue;
                for(int pos=low;pos+pitch<high;pos++)
                {
                    double score=0;
                    for(int offset=-4;offset<=4;offset+=2)
                    {
                        score+=RailDifference(pixels,pos+offset,pos+pitch+offset,railStart,railEnd,across,horizontal);
                        score+=RailDifference(pixels,pos+offset,pos+offset+1,railStart,railEnd,across,horizontal);
                        score+=RailDifference(pixels,pos+pitch+offset,pos+pitch+offset+1,railStart,railEnd,across,horizontal);
                    }
                    score+=0.003*Math.Abs(pos+pitch/2-(low+high)/2);
                    if(score<best){best=score;begin=pos;}
                }
            }
            int repeats=pitch==0?0:(int)Math.Round((double)extra/Math.Max(1,pitch*scale));
            repeats=Math.Max(0,Math.Min(repeats,content/2));
            var slices=new System.Collections.Generic.List<Rectangle>();
            slices.Add(new Rectangle(0,0,start,first));
            if(repeats==0)
                slices.Add(new Rectangle(start,first,length,content));
            else
            {
                int effectiveLength=length+repeats*pitch;
                int sourceOffset=begin-start;
                int destOffset=(int)Math.Round((double)sourceOffset*content/effectiveLength);
                slices.Add(new Rectangle(start,first,sourceOffset,destOffset));
                for(int repeat=0;repeat<repeats;repeat++)
                {
                    sourceOffset+=pitch;
                    int next=(int)Math.Round((double)sourceOffset*content/effectiveLength);
                    slices.Add(new Rectangle(begin,first+destOffset,pitch,next-destOffset));
                    destOffset=next;
                }
                slices.Add(new Rectangle(begin,first+destOffset,end-begin,content-destOffset));
            }
            slices.Add(new Rectangle(end,first+content,total-end,last));
            return slices.ToArray();
        }

        internal static Bitmap RenderTexture(Bitmap source,int apertureWidth,int apertureHeight,FilmBorderExpandGeometry g)
        {
            if(source==null || source.Width!=1920 || source.Height!=1080)
                throw new ArgumentException("Invalid film border source.");
            FilmBorderExpandGeometry expected=NaturalGeometry(g.Width,g.Height,apertureWidth,apertureHeight);
            if(g.Left!=expected.Left || g.Top!=expected.Top || g.Right!=expected.Right || g.Bottom!=expected.Bottom)
                throw new ArgumentException("Natural film border geometry mismatch.");
            Rectangle aperture=ArtworkAperture(source,apertureWidth,apertureHeight);
            int sx=aperture.Left,sy=aperture.Top,ex=aperture.Right,ey=aperture.Bottom;
            int guardX=MotionGuard(g.Left,g.Right),guardY=MotionGuard(g.Top,g.Bottom);
            double scale=Math.Min((double)g.Width/aperture.Width,(double)g.Height/aperture.Height);
            scale=Math.Min(scale,Math.Min((double)(g.Left-2*guardX)/sx,(double)(g.Right-2*guardX)/(1920-ex)));
            scale=Math.Min(scale,Math.Min((double)(g.Top-2*guardY)/sy,(double)(g.Bottom-2*guardY)/(1080-ey)));
            int left=RailSize(sx,scale),right=RailSize(1920-ex,scale);
            int top=RailSize(sy,scale),bottom=RailSize(1080-ey,scale);
            using(var stock=new Bitmap(1920,1080,PixelFormat.Format32bppPArgb))
            {
                using(Graphics draw=Graphics.FromImage(stock))
                {
                    draw.Clear(Color.Black);
                    // Film aperture coordinates are pixels. Ignore PNG print DPI.
                    draw.DrawImage(source,new Rectangle(0,0,stock.Width,stock.Height),
                        0,0,source.Width,source.Height,GraphicsUnit.Pixel);
                }
                byte[] pixels=ReadPixels(stock,PixelFormat.Format32bppPArgb);
                Rectangle[] xMap=MapAxis(pixels,1920,sx,ex,sy,ey,1080,left,g.Width+2*guardX,right,scale,true);
                Rectangle[] yMap=MapAxis(pixels,1080,sy,ey,sx,ex,1920,top,g.Height+2*guardY,bottom,scale,false);
                int originX=g.Left-guardX-left,originY=g.Top-guardY-top;
                Bitmap canvas=new Bitmap(g.CanvasWidth,g.CanvasHeight,PixelFormat.Format32bppPArgb);
                try
                {
                    using(Graphics target=Graphics.FromImage(canvas))
                    {
                        target.Clear(Color.Black);
                        target.CompositingMode=CompositingMode.SourceCopy;
                        target.PixelOffsetMode=PixelOffsetMode.Half;
                        target.InterpolationMode=InterpolationMode.Bilinear;
                        for(int y=0;y<yMap.Length;y++)for(int x=0;x<xMap.Length;x++)
                        {
                            if(x>0 && x<xMap.Length-1 && y>0 && y<yMap.Length-1)continue;
                            Rectangle xSlice=xMap[x],ySlice=yMap[y];
                            var src=new Rectangle(xSlice.X,ySlice.X,xSlice.Width,ySlice.Width);
                            var dst=new Rectangle(xSlice.Y,ySlice.Y,xSlice.Height,ySlice.Height);
                            if(src.Width<=0 || src.Height<=0 || dst.Width<=0 || dst.Height<=0)continue;
                            using(Bitmap tile=stock.Clone(src,PixelFormat.Format32bppPArgb))
                            using(ImageAttributes attrs=new ImageAttributes())
                            {
                                attrs.SetWrapMode(WrapMode.TileFlipXY);
                                GraphicsState saved=target.Save();
                                dst.Offset(originX,originY);
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
            return "trim=end_frame=1,"+(gpu?"settb=AVTB,":"")+"setpts=0,format=gbrp16le,zscale=matrixin=gbr:primariesin=709:"+
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
            // overlay_vulkan in the current Stable build copies framesync PTS
            // without rescaling to the output link time base. Align both inputs
            // to AVTB before framesync; this does not force a CFR or change N.
            // T600 R9D2: 720/720 PTS match, 418/471 MiB static/dynamic NVENC.
            string graph="[fgsfilm_gpu_video]settb=AVTB,split=2[fgsexact][fgsclock];"+
                "[fgsclock]scale_vulkan=w="+w+":h="+h+":scaler=nearest[fgsbase];"+
                "[fgsbase][fgstexture]overlay_vulkan=x=0:y=0[fgsart]";
            string art="fgsart";
            if(!String.IsNullOrEmpty(moveX) || !String.IsNullOrEmpty(moveY))
            {
                string p=hdr?"bt2020":"bt709",m=hdr?"bt2020nc":"bt709";
                graph+=";[fgsart]libplacebo=inputs=1:w="+w+":h="+h+
                    ":format=p010le:color_primaries="+p+":colorspace="+m+":color_trc="+transfer+
                    ":range="+(fullRange?"full":"limited")+":crop_x=0:crop_y=0:crop_w=iw:crop_h=ih"+
                    ":pos_w=iw:pos_h=ih:pos_x='"+LimitMotion(moveX,MotionGuard(g.Left,g.Right))+
                    "':pos_y='"+LimitMotion(moveY,MotionGuard(g.Top,g.Bottom))+
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
            string x=LimitMotion(moveX,MotionGuard(g.Left,g.Right));
            string y=LimitMotion(moveY,MotionGuard(g.Top,g.Bottom));
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
