using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Drawing;
using System.Text;

internal static class ExpandCanvasProductionTests
{
    static Assembly asm;
    const BindingFlags F=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    const BindingFlags S=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static Type T(string n){return asm.GetType("FilmGrainStudioPreview."+n,true);}
    static object New(string n){return Activator.CreateInstance(T(n),true);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Get(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    static object Call(string name,params object[] args){return T("FilmBorderEncodingCore").GetMethod(name,S).Invoke(null,args);}
    public static int Main(string[] args)
    {
        string root=args[0], work=args[1];
        Directory.CreateDirectory(work);
        asm=Assembly.LoadFrom(Path.Combine(root,"FilmGrain_Studio.exe"));
        foreach(string trc in new[]{"bt709","smpte2084","arib-std-b67"})
        foreach(string pix in trc=="bt709"?new[]{"yuv420p","yuv420p10le"}:new[]{"yuv420p10le"})
        foreach(string motion in new[]{"STATIC","WEAVE"})
        {
            bool hdr=trc!="bt709";
            string id=trc+"_"+pix+"_"+motion;
            object media=New("MediaProbeInfo"), frame=New("CinematicFramePlan"), resize=New("ResizePlan");
            Set(media,"Width",320);Set(media,"Height",180);Set(media,"SampleAspectRatio","16:15");
            Set(media,"ColorRange","tv");Set(media,"ColorPrimaries",hdr?"bt2020":"bt709");
            Set(media,"ColorSpace",hdr?"bt2020nc":"bt709");Set(media,"ColorTransfer",trc);Set(media,"PixelFormat",pix);
            Set(frame,"ActiveWidth",320);Set(frame,"ActiveHeight",180);
            var state=new Dictionary<string,string>{{"FG_FILM_BORDER_MODE","DARK_"+motion},{"FG_FILM_BORDER_EXPAND_PX","64"}};
            object[] p={root,media,state,false,hdr,null,"",false};
            Check((bool)Call("TryCreate",p),"TryCreate: "+p[6]);
            object plan=p[5];
            object[] c={plan,frame,resize,media,"X265",""};
            Check((bool)Call("ConfigureExpand",c),"ConfigureExpand: "+c[5]);
            Check((int)Call("FinalWidth",plan,320)==364 && (int)Call("FinalHeight",plan,180)==196,"Canvas size");
            var tmp=new List<string>();Call("StageExpandTexture",plan,work,tmp);
            Check(tmp.Count==1,"Texture cleanup registration");
            string art=(string)Get(plan,"ExpandTexture");
            using(var image=new Bitmap(art)) Check(image.Width==364 && image.Height==196 && image.GetPixel(182,98).ToArgb()==Color.Black.ToArgb(),"Actual staged Dark texture");
            File.Move(art,Path.Combine(work,id+".png"));
            string graph=(string)Call("AppendFilter","[0:v:0]format="+pix+"[vout]",plan,320,180,1,"25/1",media,pix);
            Check(graph.Contains("xstack=inputs=5"),"Missing final region copy");
            File.WriteAllText(Path.Combine(work,id+".graph"),graph,new UTF8Encoding(false));
            Set(plan,"GpuInputIndex",1);
            string gpu=(string)Call("AppendGpuFilter","[0:v:0]format=p010le,hwupload[fgsfilm_gpu_video]",plan);
            Check(gpu.Contains("overlay_vulkan=x=22:y=8") && gpu.Contains("hwupload=derive_device=cuda") && !gpu.Contains("hwdownload"),"GPU region-copy route");
            Check(gpu.Contains("[fgsfilm_gpu_video]settb=AVTB,split=2[fgsexact][fgsclock]") && gpu.Contains("trim=end_frame=1,settb=AVTB,setpts=0"),"Production Vulkan framesync time bases aligned");
            Console.WriteLine("PASS production plan and graph "+id);
        }
        return 0;
    }
}
