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
        if(args.Length==4)
        {
            Type preview=T("SharedEncodePreviewCore");
            string probeArgs=(string)preview.GetMethod("KeyframeProbeArguments",S).Invoke(null,new object[]{args[3],0.0});
            string json=(string)preview.GetMethod("RunTool",S).Invoke(null,new object[]{args[2],probeArgs,new Func<bool>(delegate{return false;}),30000});
            var parser=new System.Web.Script.Serialization.JavaScriptSerializer();
            var metadata=(Dictionary<string,object>)parser.DeserializeObject(json);
            var format=(Dictionary<string,object>)metadata["format"];
            var packets=(object[])metadata["packets"];
            Check(format.ContainsKey("start_time") && !format.ContainsKey("tags") && packets.Length>0,"Keyframe probe must exclude arbitrary metadata");
            Check(Convert.ToString(((Dictionary<string,object>)packets[0])["flags"]).Contains("K"),"Keyframe preserved");
            string all=(string)preview.GetMethod("RunTool",S).Invoke(null,new object[]{args[2],"-v error -show_entries format_tags=title -of json "+(string)preview.GetMethod("Quote",S).Invoke(null,new object[]{args[3]}),new Func<bool>(delegate{return false;}),30000});
            var titleRoot=(Dictionary<string,object>)parser.DeserializeObject(all);
            var titleFormat=(Dictionary<string,object>)titleRoot["format"];
            var tags=(Dictionary<string,object>)titleFormat["tags"];
            Check(Convert.ToString(tags["title"])=="\u91c7\u7528Blackmagic\u62cd\u6444\u300aNature\u300b","UTF-8 metadata survives process capture");
            Console.WriteLine("PASS UTF-8 keyframe probe and metadata exclusion");
        }
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
        VerifyPictureGeometry(root,work);
        return 0;
    }

    static void VerifyPictureGeometry(string root,string work)
    {
        var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
        foreach(int sourceWidth in new[]{1920,3840})
        foreach(string operation in new[]{"crop","resize","crop_resize","resize_crop"})
        foreach(string trc in new[]{"bt709","smpte2084","arib-std-b67"})
        foreach(string motion in new[]{"STATIC","WEAVE"})
        {
            bool hdr=trc!="bt709";
            int sourceHeight=sourceWidth*9/16;
            bool scaled=operation!="crop", after=operation=="resize_crop";
            bool crop=operation!="resize";
            string id=sourceWidth+"_"+operation+"_"+trc+"_"+motion;
            object media=New("MediaProbeInfo");
            Set(media,"Width",sourceWidth);Set(media,"Height",sourceHeight);Set(media,"SampleAspectRatio","16:15");
            Set(media,"ColorRange","tv");Set(media,"ColorPrimaries",hdr?"bt2020":"bt709");
            Set(media,"ColorSpace",hdr?"bt2020nc":"bt709");Set(media,"ColorTransfer",trc);
            Set(media,"PixelFormat","yuv420p10le");
            var state=new Dictionary<string,string>{
                {"FG_MODE","X265"},
                {"FG_FILM_BORDER_MODE","DARK_"+motion},{"FG_FILM_BORDER_EXPAND_PX","64"},
                {"FG_CINEMATIC_FRAME",crop?"1":"0"},{"FG_FRAME_MODE","CROP"},{"FG_CROP_MARGINS","6,10,18,22"},
                {"FG_RESIZE_MODE",scaled?"1/2":"OFF"},{"FG_RESIZE_KEEP_ASPECT","false"},
                {"FG_RESIZE_CROP_AFTER",after?"true":"false"},{"FG_RESIZE_CROP_MARGINS","5,9,11,15"}
            };
            object[] frameArgs={media,state,null,""};
            Check((bool)T("CinematicFrameCore").GetMethod("TryBuild",S).Invoke(null,frameArgs),"Framing "+id+": "+frameArgs[3]);
            object frame=frameArgs[2];
            object[] resizeArgs={media,state,frame,null,"",hdr};
            Check((bool)T("ResizeCore").GetMethod("TryBuild",S).Invoke(null,resizeArgs),"Resize "+id+": "+resizeArgs[4]);
            object resize=resizeArgs[3];
            int width=(int)Get(frame,"ActiveWidth"),height=(int)Get(frame,"ActiveHeight");
            int expectedWidth=operation=="crop"?sourceWidth-24:operation=="crop_resize"?sourceWidth/2-12:after?sourceWidth/2-16:sourceWidth/2;
            int expectedHeight=operation=="crop"?sourceHeight-32:operation=="crop_resize"?sourceHeight/2-16:after?sourceHeight/2-24:sourceHeight/2;
            Check(width==expectedWidth && height==expectedHeight,"Effective dimensions "+id);
            object[] created={root,media,state,false,hdr,null,"",false};
            Check((bool)Call("TryCreate",created),"Border "+id+": "+created[6]);
            object plan=created[5];
            foreach(string codec in hdr?new[]{"HEVC","X265"}:new[]{"HEVC","H264","X264","X265"})
            {
                object[] configured={plan,frame,resize,media,codec,""};
                int limit=codec=="H264"?4096:codec=="HEVC"?8192:16384;
                bool ok=(bool)Call("ConfigureExpand",configured);
                var candidate=Get(plan,"ExpandGeometry");
                bool fits=(int)Get(candidate,"CanvasWidth")<=limit && (int)Get(candidate,"CanvasHeight")<=limit;
                Check(ok==fits && (ok || ((string)configured[5]).Contains("codec dimension limit")),"Canvas limit "+codec+" "+id);
            }
            Check((int)Get(plan,"ExpandSarNum")== (scaled?1:16) && (int)Get(plan,"ExpandSarDen")== (scaled?1:15),"Effective SAR "+id);
            var canvas=Get(plan,"ExpandGeometry");
            var files=new List<string>();
            Call("StageExpandTexture",plan,work,files);
            string art=Path.Combine(work,id+".png");
            File.Move((string)Get(plan,"ExpandTexture"),art);
            string prefix=(string)Get(resize,"Prefix");
            var graphs=new Dictionary<string,object>{
                {"sourceWidth",sourceWidth},{"sourceHeight",sourceHeight},{"resize",scaled},
                {"width",width},{"height",height},{"left",Get(canvas,"Left")},{"top",Get(canvas,"Top")},
                {"canvasWidth",Get(canvas,"CanvasWidth")},{"canvasHeight",Get(canvas,"CanvasHeight")},
                {"sarNum",Get(plan,"ExpandSarNum")},{"sarDen",Get(plan,"ExpandSarDen")},{"transfer",trc}
            };
            foreach(string pixel in hdr?new[]{"yuv420p10le"}:new[]{"yuv420p","yuv420p10le"})
            {
                string suffix=(string)Get(frame,"Filter")+",format="+pixel;
                string baseline=(string)T("SharedGrainCore").GetMethod("BuildWithoutGrain",S).Invoke(null,new object[]{prefix,"","",suffix});
                graphs["cpuBase_"+pixel]=baseline;
                graphs["cpu_"+pixel]=Call("AppendFilter",baseline,plan,width,height,1,"25/1",media,pixel);
            }
            string signal="setparams=range=limited:color_primaries="+(hdr?"bt2020":"bt709")+
                ":color_trc="+trc+":colorspace="+(hdr?"bt2020nc":"bt709")+",";
            string gpuBase=(string)Call("ExpandGpuPicture",prefix,frame,signal);
            Set(plan,"GpuInputIndex",1);
            string gpu=(string)Call("AppendGpuFilter",gpuBase,plan);
            Check(gpu.Contains("[fgsfilm_gpu_video]settb=AVTB") && gpu.EndsWith(",hwupload=derive_device=cuda[vout]"),
                "Production NVENC handoff "+id);
            if(!hdr)
            {
                Set(plan,"GpuH264",true);
                string h264=(string)Call("AppendGpuFilter",gpuBase,plan);
                Check(h264.EndsWith(",hwupload=derive_device=cuda,scale_cuda=format=nv12[vout]"),"H.264 NVENC handoff "+id);
            }
            graphs["gpuBase"]=gpuBase.Replace("hwupload[fgsfilm_gpu_video]","format=yuv420p10le[vout]");
            graphs["gpu"]=gpu.Replace(",hwupload=derive_device=cuda[vout]",",hwdownload,format=p010le,format=yuv420p10le[vout]");
            File.WriteAllText(Path.Combine(work,id+".geometry.json"),serializer.Serialize(graphs),new UTF8Encoding(false));
            Set(frame,"CinematicBars",true);
            object[] bars={plan,frame,resize,media,"X265",""};
            Check(!(bool)Call("ConfigureExpand",bars) && ((string)bars[5]).Contains("Cinematic bars"),"Independent cinema option "+id);
            Console.WriteLine("PASS processed picture plan "+id);
        }
        foreach(string codec in new[]{"H264","HEVC","X264","X265"})
        {
            int limit=codec=="H264"?4096:codec=="HEVC"?8192:16384;
            object plan=Activator.CreateInstance(T("FilmBorderEncodingCore").GetNestedType("Plan",BindingFlags.NonPublic),true);
            object frame=New("CinematicFramePlan");
            Set(plan,"ExpandThickness",64);
            Set(frame,"ActiveHeight",1080);
            Set(frame,"ActiveWidth",limit-260);
            object[] test={plan,frame,New("ResizePlan"),New("MediaProbeInfo"),codec,""};
            Check((bool)Call("ConfigureExpand",test),"At canvas limit "+codec+": "+test[5]);
            Check((int)Get(Get(plan,"ExpandGeometry"),"CanvasWidth")==limit,"Exact canvas limit "+codec);
            Set(frame,"ActiveWidth",limit-258);
            Check(!(bool)Call("ConfigureExpand",test),"Above canvas limit "+codec);
        }
    }
}
