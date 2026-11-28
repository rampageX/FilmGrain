using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal sealed class NativeGrainPreviewForm : Form
    {
        protected override void OnLoad(EventArgs e) { MainForm.AttachOwnedTheme(this, Owner); base.OnLoad(e); }
        private readonly string ffmpeg, grav1synth, source;
        private readonly List<string> grainArgs;
        private readonly MediaProbeInfo info;
        private readonly string appRoot;
        private readonly IDictionary<string,string> pixelState;
        private readonly IDictionary<string,string> preprocessingState;
        private readonly HardwareCapabilitySnapshot capabilities;
        private readonly string temporary = Path.Combine(Path.GetTempPath(), "FGS_GrainCompare_" + Guid.NewGuid().ToString("N"));
        private readonly object sync = new object();
        private Process active;
        private volatile bool closing;
        private readonly PictureBox[] panes = { new PictureBox(), new PictureBox(), new PictureBox(), new PictureBox() };
        private readonly List<string>[] variants = { new List<string>(), new List<string>(), new List<string>(), new List<string>() };
        private readonly int[] strengths = {15,40,70,100};
        private string pixelKind="";
        private readonly Button[] choose = {new ThemeButton(),new ThemeButton(),new ThemeButton(),new ThemeButton()};
        private readonly Label status = new ThemeLabel();
        private readonly TrackBar frame = new ThemeTrackBar();
        private readonly List<string> cleanFrames = new List<string>();
        private readonly System.Windows.Forms.Timer playback = new System.Windows.Forms.Timer();
        private readonly Button play = new ThemeButton(), render = new ThemeButton();
        private readonly CheckBox actualSize = new ThemeCheckBox(), skipBlack = new ThemeCheckBox(), showClean = new ThemeCheckBox();
        private readonly TextBox start = new ThemeTextBox();
        private readonly NativeSeekStrip timeline = new NativeSeekStrip();
        private volatile bool running;
        private readonly bool isZh;
        private bool initialPreviewQueued, automaticRequest;
        private string renderLocation="";
        private string T(string zh,string en){return isZh?zh:en;}
        internal int SelectedStrength { get; private set; }

        internal NativeGrainPreviewForm(string ffmpegPath, string gravPath, string input, List<string> args, MediaProbeInfo media, string languageCode = "zh-CN", HardwareCapabilitySnapshot metadataCaps = null, IDictionary<string,string> preprocessing = null)
        {
            isZh=string.Equals(languageCode,"zh-CN",StringComparison.OrdinalIgnoreCase);
            preprocessingState=preprocessing==null?new Dictionary<string,string>():new Dictionary<string,string>(preprocessing);
            capabilities=metadataCaps; info=media; ffmpeg=ffmpegPath; grav1synth=gravPath; source=input; grainArgs=new List<string>(args);
            Text=T("颗粒四档对比","Four grain strengths");
            Size=new Size(1180,860); MinimumSize=new Size(1000,700); StartPosition=FormStartPosition.CenterParent;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=4,Padding=new Padding(8)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,50));layout.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,110));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66));
            for(int i=0;i<4;i++)
            {
                int index=i;
                var panel=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute,30));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                choose[i].Text=strengths[i]+T("% · 点击应用","% · Apply");choose[i].Dock=DockStyle.Fill;choose[i].Enabled=false;
                choose[i].Click+=delegate {SelectedStrength=strengths[index]; DialogResult=DialogResult.OK; Close();};
                panes[i].Dock=DockStyle.Fill;panes[i].SizeMode=PictureBoxSizeMode.Zoom;panes[i].BackColor=Color.Black;
                panel.Controls.Add(choose[i],0,0);panel.Controls.Add(panes[i],0,1);layout.Controls.Add(panel,i%2,i/2);
            }
            var bar=new SettingsFlowPanel {Dock=DockStyle.Fill,WrapContents=true};
            start.Text="00:00:00";start.Width=80;
            render.Text=T("生成四档","Render four strengths");render.AutoSize=true;render.Click+=delegate {if(!running)StartRender();};
            play.Text=T("播放","Play");play.AutoSize=true;play.Enabled=false;play.Click+=delegate {playback.Enabled=!playback.Enabled;play.Text=playback.Enabled?T("暂停","Pause"):T("播放","Play");};
            frame.Width=180;frame.Maximum=0;frame.TickStyle=TickStyle.None;frame.Enabled=false;frame.ValueChanged+=delegate {ShowFrame();};
            actualSize.Text=ResizeCore.Enabled(preprocessingState)?T("1:1 输出像素","1:1 output pixels"):T("1:1 居中","1:1 centered");actualSize.AutoSize=true;
            actualSize.CheckedChanged+=delegate {foreach(var box in panes)box.SizeMode=actualSize.Checked?PictureBoxSizeMode.CenterImage:PictureBoxSizeMode.Zoom;};
            actualSize.Checked=true;
            skipBlack.Text=T("跳过黑帧","Skip black frames");skipBlack.Checked=true;skipBlack.AutoSize=true;
            showClean.Text=T("查看原图","Show original");showClean.AutoSize=true;showClean.Enabled=false;showClean.CheckedChanged+=delegate {ShowFrame();};
            bar.Controls.Add(new ThemeLabel {Text=T("起始","Start"),AutoSize=true,Padding=new Padding(0,7,0,0)});bar.Controls.Add(start);bar.Controls.Add(render);bar.Controls.Add(play);bar.Controls.Add(frame);bar.Controls.Add(actualSize);bar.Controls.Add(skipBlack);bar.Controls.Add(showClean);
            double sourceDuration;double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out sourceDuration);
            timeline.Duration=double.IsNaN(sourceDuration)||double.IsInfinity(sourceDuration)?0:Math.Min(359999,Math.Max(0,sourceDuration));
            timeline.AccessibleName=T("视频时间轴","Video timeline");timeline.Dock=DockStyle.Fill;
            timeline.PositionChanged+=delegate {start.Text=NativeSeekStrip.FormatTime(timeline.Position);};
            timeline.SeekCommitted+=delegate {if(!running)StartRender();};
            start.TextChanged+=delegate {double seconds;if(SharedEncodePreviewCore.TryParseStart(start.Text.Trim(),out seconds))timeline.Position=seconds;};
            var seekLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            seekLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));seekLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            seekLayout.Controls.Add(timeline,0,0);seekLayout.Controls.Add(bar,0,1);
            layout.Controls.Add(seekLayout,0,2);layout.SetColumnSpan(seekLayout,2);
            status.Dock=DockStyle.Fill;status.Text=T("窗口显示后将随机选点并自动生成四档预览。","A random position will be selected and four previews rendered automatically.");
            layout.Controls.Add(status,0,3);layout.SetColumnSpan(status,2);Controls.Add(layout);
            playback.Interval=150;playback.Tick+=delegate {if(frame.Maximum>0)frame.Value=(frame.Value+1)%(frame.Maximum+1);};
            FormClosing+=delegate {closing=true;playback.Stop();lock(sync){try {if(active!=null&&!active.HasExited)active.Kill();}catch{}}};
            FormClosed+=delegate {playback.Dispose();ReleaseImages();if(!running)Cleanup();};
        }

        internal NativeGrainPreviewForm(string ffmpegPath,string root,string input,IDictionary<string,string> state,HardwareCapabilitySnapshot caps,MediaProbeInfo media,string languageCode = "zh-CN")
            : this(ffmpegPath,"",input,new List<string>(),media,languageCode)
        {
            appRoot=root;pixelState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);capabilities=caps;
            pixelKind=pixelState["FG_GRAIN_ENGINE"];
            if(ResizeCore.Enabled(pixelState))actualSize.Text=T("1:1 输出像素","1:1 output pixels");
            for(int i=0;i<4;i++) {
                if(pixelKind=="FGSIM")strengths[i]=i+1;
                else if(pixelKind=="NATIVE") {
                    int platePercent=GrainControlCore.PlatePercent(pixelState);
                    if(platePercent<15 || platePercent%5!=0) {
                        int[] plateLevels={1,5,10,15};
                        int nearest=0;
                        for(int j=1;j<plateLevels.Length;j++)
                            if(Math.Abs(plateLevels[j]-platePercent)<Math.Abs(plateLevels[nearest]-platePercent))nearest=j;
                        strengths[i]=i==nearest?platePercent:plateLevels[i];
                    }
                }
                string label=pixelKind=="FGSIM"?GrainControlCore.FgsimPresets[i]+" / "+GrainControlCore.FgsimStrengths[i]:pixelKind=="PROCEDURAL"?(strengths[i]/100.0).ToString("0.00",CultureInfo.InvariantCulture):strengths[i]+"%";
                choose[i].Text=label+T(" · 点击应用"," · Apply");
            }
            Text=T("像素颗粒四档对比","Pixel grain comparison");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if(initialPreviewQueued)return;initialPreviewQueued=true;
            BeginInvoke(new Action(delegate {
                if(closing||IsDisposed||running)return;
                start.Text=NativeSeekStrip.FormatTime(ChooseInitialSeconds(timeline.Duration,new Random().NextDouble()));
                automaticRequest=true;StartRender();
            }));
        }

        internal static double ChooseInitialSeconds(double duration,double random)
        {
            if(double.IsNaN(duration)||double.IsInfinity(duration)||duration<=0)return 0;
            duration=Math.Min(duration,359999);
            double lower=duration*.10,upper=Math.Min(duration*.85,Math.Max(0,duration-10));
            if(upper<lower){lower=0;upper=Math.Max(0,duration-1);}
            random=double.IsNaN(random)?0:Math.Max(0,Math.Min(1,random));
            return Math.Floor(lower+(upper-lower)*random);
        }

        private void StartRender()
        {
            if(closing||IsDisposed||running)return;
            try { if(info.Identity==null)throw new InvalidOperationException(T("视频探测尚未完成。","Video probing is not ready.")); info.Identity.RequireCurrent(); }
            catch(Exception ex){status.Text=ex.Message;return;}
            double seconds;
            if(!SharedEncodePreviewCore.TryParseStart(start.Text.Trim(),out seconds)) {FgsMessageBox.Show(this,T("起始时间格式应为：00:00:00","Start time format: 00:00:00"));return;}
            double duration;
            if(double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration)&&duration>0&&seconds>=duration) {FgsMessageBox.Show(this,T("起始时间超出视频时长。","Start time exceeds the video duration."));return;}
            bool automatic=automaticRequest;automaticRequest=false;
            string requestedTime=NativeSeekStrip.FormatTime(seconds);
            running=true;timeline.Enabled=false;render.Enabled=start.Enabled=false;playback.Stop();play.Text=T("播放","Play");play.Enabled=frame.Enabled=showClean.Enabled=false;
            foreach(var button in choose)button.Enabled=false;
            ReleaseImages();cleanFrames.Clear();foreach(var list in variants)list.Clear();
            renderLocation=(automatic?T("已随机选点：","Random position: "):T("请求起始：","Requested start: "))+requestedTime;
            status.Text=renderLocation+T("；正在生成四档预览…","; rendering four strengths...");
            bool skipOpening=skipBlack.Checked;
            var worker=new Thread(new ThreadStart(delegate {
                string error="";int firstFrame=0;double actualStart=0;
                try
                {
                    info.Identity.RequireCurrent();
                    Directory.CreateDirectory(temporary);
                    foreach(string path in Directory.GetFiles(temporary))File.Delete(path);
                    string sample=Path.Combine(temporary,"sample.mkv"),baseIvf=Path.Combine(temporary,"base.ivf"),model=Path.Combine(temporary,"model.ivf");
                    string probe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
                    if(pixelState!=null) {
                        // Decode from the original source with accurate seeking. Stream-copy
                        // cuts can expose missing references and unstable timestamps to previews.
                        Run(ffmpeg,BuildPixelSampleArguments(source,seconds,sample));
                        actualStart=seconds;
                        RenderPixelSample(sample,probe,skipOpening);
                    }
                    else
                    {
                    if (ResizeCore.Enabled(preprocessingState) || PreprocessingCore.Enabled(preprocessingState) || !string.Equals(info.VideoCodec,"av1",StringComparison.OrdinalIgnoreCase))
                    {
                        ReportProgress(T("正在将短片段转为 AV1（仅预览）…","Converting a short sample to AV1 (preview only)..."));
                        string pre,preReason;if(!PreprocessingCore.TryBuild(preprocessingState,out pre,out preReason))throw new InvalidDataException(preReason);
                        if(pre.Length>0&&!PreprocessingCore.TryCheckTool(ffmpeg,out preReason))throw new InvalidDataException(preReason);
                        var sampleArgs=BuildMetadataSampleArguments(source,seconds,baseIvf,info,capabilities!=null&&capabilities.Av1Available);
                        CinematicFramePlan geometry;ResizePlan resize;
                        if(!CinematicFrameCore.TryBuild(info,preprocessingState,out geometry,out preReason)||!ResizeCore.TryBuild(info,preprocessingState,geometry,out resize,out preReason))throw new InvalidDataException(preReason);
                        if(resize.Enabled) {
                            if(!ResizeCore.TryCheckTool(ffmpeg,resize,out preReason))throw new InvalidDataException(preReason);
                            sampleArgs.InsertRange(0,new[]{"-init_hw_device",VulkanDevicePolicy.Spec("vk"),"-filter_hw_device","vk"});
                        }
                        sampleArgs[sampleArgs.IndexOf("-vf")+1]=pre+resize.Prefix+(resize.Enabled&&geometry.Filter.Length>0?geometry.Filter.Substring(1)+",":"")+"setpts=PTS-STARTPTS";Run(ffmpeg,sampleArgs);
                        actualStart=seconds;
                    }
                    else
                    {
                    // Copy the keyframe sample with original PTS so the selected table can be rebased accurately.
                    Run(ffmpeg,new List<string>{"-hide_banner","-v","error","-y","-copyts","-ss",seconds.ToString("0.######",CultureInfo.InvariantCulture),"-noaccurate_seek","-i",source,"-map","0:v:0","-c:v","copy","-frames:v","160","-an","-sn","-dn","-avoid_negative_ts","disabled",sample});
                    var parser=new System.Web.Script.Serialization.JavaScriptSerializer();
                    string json=Run(probe,new List<string>{"-v","error","-select_streams","v:0","-read_intervals","%+#1","-show_packets","-show_entries","packet=pts_time,flags","-of","json",sample});
                    var data=parser.DeserializeObject(json) as Dictionary<string,object>;object raw;
                    var packets=data!=null&&data.TryGetValue("packets",out raw)?raw as object[]:null;
                    var packet=packets!=null&&packets.Length>0?packets[0] as Dictionary<string,object>:null;object pts,flags;
                    if(packet==null||!packet.TryGetValue("pts_time",out pts)||!packet.TryGetValue("flags",out flags)||Convert.ToString(flags).IndexOf('K')<0)throw new InvalidDataException(T("预览片段未从已确认的关键帧开始。","The preview sample does not start at a confirmed keyframe."));
                    double firstPts=double.Parse(Convert.ToString(pts),CultureInfo.InvariantCulture);
                    json=Run(probe,new List<string>{"-v","error","-show_entries","format=start_time","-of","json",source});
                    data=parser.DeserializeObject(json) as Dictionary<string,object>;
                    var format=data!=null&&data.TryGetValue("format",out raw)?raw as Dictionary<string,object>:null;object originValue;
                    double origin=0;if(format!=null&&format.TryGetValue("start_time",out originValue))double.TryParse(Convert.ToString(originValue),NumberStyles.Float,CultureInfo.InvariantCulture,out origin);
                    actualStart=Math.Max(0,firstPts-origin);
                    Run(ffmpeg,new List<string>{"-hide_banner","-v","error","-y","-i",sample,"-map","0:v:0","-c:v","copy","-an","-sn","-dn","-f","ivf",baseIvf});
                    json=Run(probe,new List<string>{"-v","error","-select_streams","v:0","-read_intervals","%+#1","-show_packets","-show_entries","packet=pts_time","-of","json",baseIvf});
                    data=parser.DeserializeObject(json) as Dictionary<string,object>;
                    packets=data!=null&&data.TryGetValue("packets",out raw)?raw as object[]:null;
                    packet=packets!=null&&packets.Length>0?packets[0] as Dictionary<string,object>:null;
                    if(packet==null||!packet.TryGetValue("pts_time",out pts)||Math.Abs(double.Parse(Convert.ToString(pts),CultureInfo.InvariantCulture))>0.000001)throw new InvalidDataException(T("预览 IVF 时间轴未归零。","The preview IVF timeline did not normalize to zero."));
                    }
                    var selected=new List<string>(grainArgs);
                    int tableIndex=selected.IndexOf("--grain");if(tableIndex<0)tableIndex=selected.IndexOf("-g");
                    if(tableIndex>=0)
                    {
                        string local=Path.Combine(temporary,"selected.tbl");
                        File.WriteAllText(local,Av1GrainStrengthCore.ShiftTable(File.ReadAllText(selected[tableIndex+1]),actualStart),new UTF8Encoding(false));selected[tableIndex+1]=local;
                    }
                    var apply=new List<string>{"apply",baseIvf,"-o",model};apply.AddRange(selected);apply.AddRange(new[]{"--replace","-y"});Run(grav1synth,apply);
                    string originalText;
                    if(tableIndex>=0)originalText=File.ReadAllText(selected[tableIndex+1]);
                    else if(selected.IndexOf("--preset")>=0)originalText=Av1GrainStrengthCore.PresetTable(selected[selected.IndexOf("--preset")+1]);
                    else {
                        string original=Path.Combine(temporary,"original.tbl");Run(grav1synth,new List<string>{"inspect",model,"-o",original,"-y"});
                        originalText=File.ReadAllText(original);
                    }
                    if(skipOpening) {string scan=Path.Combine(temporary,"scan.gray");Run(ffmpeg,BuildScanArguments(baseIvf,scan));firstFrame=FindPreviewStartFrame(File.ReadAllBytes(scan));if(firstFrame<0)firstFrame=0;}
                    RenderFrames(baseIvf,"0","clean",firstFrame);
                    for(int i=0;i<4;i++)
                    {
                        ReportProgress(GrainProgressText(i));
                        string video=model;
                        if(strengths[i]!=100) {string table=Path.Combine(temporary,"strength"+i+".tbl");video=Path.Combine(temporary,"strength"+i+".ivf");File.WriteAllText(table,Av1GrainStrengthCore.ScaleTable(originalText,strengths[i]),new UTF8Encoding(false));Run(grav1synth,new List<string>{"apply",baseIvf,"-o",video,"--grain",table,"--replace","-y"});}
                        RenderFrames(video,"1","variant"+i+"_",firstFrame);
                    }
                    }
                }
                catch(Exception ex) {error=ex.Message;}
                finally
                {
                    if(closing){running=false;Cleanup();}
                    else try {BeginInvoke(new Action(delegate {
                        running=false;if(closing){Cleanup();return;}
                        timeline.Enabled=true;render.Enabled=start.Enabled=true;
                        if(error.Length>0){status.Text=T("预览失败：","Preview failed: ")+error;return;}
                        try {
                            info.Identity.RequireCurrent();
                            cleanFrames.AddRange(Directory.GetFiles(temporary,"clean*.png"));cleanFrames.Sort(StringComparer.Ordinal);
                            for(int i=0;i<4;i++){variants[i].AddRange(Directory.GetFiles(temporary,"variant"+i+"_*.png"));variants[i].Sort(StringComparer.Ordinal);if(variants[i].Count!=cleanFrames.Count)throw new InvalidDataException(T("四档预览的帧数不一致。","Preview frame counts do not match."));}
                            if(cleanFrames.Count==0)throw new InvalidDataException(T("未生成预览画面。","No preview frames were generated."));
                            frame.Maximum=cleanFrames.Count-1;frame.Value=0;ShowFrame();
                            play.Enabled=frame.Enabled=showClean.Enabled=true;for(int i=0;i<4;i++)choose[i].Enabled=true;
                            status.Text=(automatic?T("随机选点：","Random position: "):T("请求起始：","Requested start: "))+requestedTime+T("；实际片段起始：","; actual sample start: ")+TimeSpan.FromSeconds(actualStart).ToString()+T("。同帧同种子，点击档位应用。\r\n仅比较颗粒；HDR 转换仅用于显示，不包含 LUT、调色或插帧。1:1 居中可检查颗粒细节。",". Same frames and seed; click a strength to apply.\r\nGrain only; HDR conversion is for display. LUTs, color correction and interpolation are excluded. Use 1:1 centered to inspect detail.");
                        }catch(Exception ex){ReleaseImages();status.Text=T("预览失败：","Preview failed: ")+ex.Message;}
                    }));}catch{running=false;Cleanup();}
                }
            }));worker.IsBackground=true;worker.Start();
        }

        private string GrainProgressText(int level)
        {
            string label=pixelKind=="FGSIM"?GrainControlCore.FgsimPresets[level]+" / "+GrainControlCore.FgsimStrengths[level]:
                pixelKind=="PROCEDURAL"?(strengths[level]/100.0).ToString("0.00",CultureInfo.InvariantCulture):strengths[level]+"%";
            return T("正在生成颗粒预览 ","Rendering grain preview ")+(level+1)+"/4"+T("（强度 "," (strength ")+label+T("）…",")...");
        }

        private void ReportProgress(string text)
        {
            if(closing)return;
            try{BeginInvoke(new Action(delegate {if(!closing&&!IsDisposed&&running)status.Text=renderLocation+T("；","; ")+text;}));}catch(InvalidOperationException){}
        }

        internal static List<string> BuildPixelSampleArguments(string input,double seconds,string output)
        {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0)throw new ArgumentOutOfRangeException("seconds");
            return new List<string> {"-hide_banner","-v","error","-nostdin","-y",
                "-ss",seconds.ToString("0.######",CultureInfo.InvariantCulture),"-accurate_seek","-i",input,
                "-map","0:v:0","-an","-sn","-dn","-vf","setpts=PTS-STARTPTS",
                "-c:v","ffv1","-level","3","-threads","2","-frames:v","160","-t","8",
                "-fps_mode","passthrough",output};
        }

        private void RenderPixelSample(string sample,string probe,bool skipOpening)
        {
            string normalized=sample;
            var media=SharedEncodePreviewCore.Probe(normalized,probe,delegate {return closing;});
            int first=0;
            for(int level=0;level<4;level++) {
                if(closing)throw new OperationCanceledException();
                ReportProgress(GrainProgressText(level));
                var state=new Dictionary<string,string>(pixelState,StringComparer.OrdinalIgnoreCase);
                state["FG_PIXEL_GRAIN_STRENGTH"]="100";
                if(pixelKind=="FGSIM")state["FG_FGSIM_PRESET"]=GrainControlCore.FgsimPresets[level];
                else if(pixelKind=="NATIVE")state["FG_GRAIN_PLATE_STRENGTH"]=strengths[level].ToString(CultureInfo.InvariantCulture);
                else state["FG_PROC_STRENGTH"]=strengths[level].ToString(CultureInfo.InvariantCulture);
                state["FG_COLOR_ENABLED"]="0";state["FG_LUT_PATH"]="";state.Remove(ColorGrade.StateKey);
                state["FG_SUBTITLE"]="0";if(!ResizeCore.Enabled(state))state["FG_CINEMATIC_FRAME"]="0";state["FG_SVP_INTERPOLATE"]="0";
                state["FG_FPS_MODE"]="SOURCE";state["FG_X264_PASS_MODE"]="VBR1";
                state["FG_ENCODE_PREVIEW"]="0";state["FG_SFE_ENGINES"]="0";
                state["FG_OUTPUT_MODE"]="CUSTOM";state["FG_OUTPUT_CUSTOM_DIR"]=temporary;
                state["FG_TEMP_MODE"]="CUSTOM";state["FG_TEMP_CUSTOM_DIR"]=temporary;
                BridgePreparedExecution prepared;string reason;
                if(!NativeExecutionPreparationCore.TryPrepare(appRoot,normalized,media,state,capabilities,false,out prepared,out reason))throw new InvalidDataException(reason);
                var request=prepared.ExecutionRequest;
                try {
                    if(request.PixelGrainFullFilter==null||request.PixelGrainCleanFilter==null)throw new InvalidDataException(T("当前路径没有颗粒预览滤镜。","The route has no grain preview filters."));
                    if(request.DirectStages!=null)foreach(var stage in request.DirectStages) {
                        if(stage.OriginalArguments!=null && stage.OriginalArguments.Contains("-filter_complex")) {request.PixelGrainEncodeArguments=stage.OriginalArguments;break;}
                        if(closing)throw new OperationCanceledException();
                        if(stage.PrepareWithCancellation!=null)stage.PrepareWithCancellation(delegate {return closing;});
                        if(stage.BeforeRunPreparation!=null)stage.BeforeRunPreparation();
                        if(stage.ShouldRun!=null&&!stage.ShouldRun())continue;
                        if(stage.OriginalArguments==null)throw new InvalidDataException("Preview preprocessing arguments missing");
                        Run(stage.ToolPath,new List<string>(stage.OriginalArguments),stage.WorkingDirectory);
                    }
                    if(request.PixelGrainEncodeArguments==null)throw new InvalidDataException("Preview encoding arguments missing");
                    string pair=Path.Combine(temporary,"pixel_pair_"+level+".mkv");
                    var args=PixelGrainStrengthCore.PreviewArguments(request.PixelGrainEncodeArguments,request.PixelGrainFullFilter.Replace("noise=c0s=100:","noise=all_seed=123457:c0s=100:"),request.PixelGrainCleanFilter,pair);
                    PixelGrainStrengthCore.StampPreviewColor(args,request.PixelGrainHdr,media);Run(ffmpeg,args,request.WorkingDirectory);
                    var pairInfo=SharedEncodePreviewCore.Probe(pair,probe,delegate {return closing;});
                    if(level==0) {
                        if(skipOpening) {
                            string scan=Path.Combine(temporary,"pixel_scan.gray");
                            Run(ffmpeg,new List<string>{"-v","error","-y","-i",pair,"-map","0:v:0","-vf","scale=64:36,format=gray","-frames:v","32","-threads","1","-f","rawvideo",scan});
                            first=FindPreviewStartFrame(File.ReadAllBytes(scan));if(first<0)first=0;
                        }
                        Run(ffmpeg,PixelGrainStrengthCore.FrameArguments(pair,0,PreviewFilter(pairInfo,first),Path.Combine(temporary,"clean%02d.png")));
                    }
                    // Render the actual algorithm/preset result, never a relative-strength blend.
                    Run(ffmpeg,PixelGrainStrengthCore.FrameArguments(pair,100,PreviewFilter(pairInfo,first),Path.Combine(temporary,"variant"+level+"_%02d.png")));
                    try {File.Delete(pair);}catch(IOException){}
                }finally {BridgeExecutionCore.CleanupTemporaryFiles(request);}
            }
        }

        private void RenderFrames(string input, string filmgrain, string prefix, int firstFrame)
        {
            // libdav1d must expose filmgrain; if unsupported, fail explicitly instead of
            // accidentally displaying an AV1 decode with grain disabled.
            Run(ffmpeg, BuildFrameRenderArguments(input, filmgrain, Path.Combine(temporary, prefix + "%02d.png"), info, firstFrame));
        }

        // One shared, full-resolution base for all four strengths; never encode each pane.
        internal static List<string> BuildMetadataSampleArguments(string input,double seconds,string output,MediaProbeInfo media,bool nvenc)
        {
            var args=new List<string>{"-hide_banner","-v","error","-y","-ss",seconds.ToString("0.######",CultureInfo.InvariantCulture),"-i",input,
                "-map","0:v:0","-an","-sn","-dn","-vf","setpts=PTS-STARTPTS","-frames:v","40","-fps_mode","passthrough"};
            if(nvenc)args.AddRange(new[]{"-c:v","av1_nvenc","-preset","p4","-rc","constqp","-qp","12"});
            else args.AddRange(new[]{"-c:v","libaom-av1","-cpu-used","8","-crf","12","-b:v","0","-row-mt","1","-threads","8"});
            string pixel=media.PixelFormat??"";
            args.AddRange(new[]{"-pix_fmt",media.IsHdr||pixel.IndexOf("10",StringComparison.Ordinal)>=0||pixel.IndexOf("12",StringComparison.Ordinal)>=0?"yuv420p10le":"yuv420p"});
            foreach(var pair in new[]{new[]{"-color_primaries",media.ColorPrimaries},new[]{"-color_trc",media.ColorTransfer},new[]{"-colorspace",media.ColorSpace},new[]{"-color_range",media.ColorRange}})
                if(!string.IsNullOrEmpty(pair[1])&&pair[1]!="unknown"&&pair[1]!="reserved")args.AddRange(pair);
            args.AddRange(new[]{"-f","ivf",output});return args;
        }

        internal static List<string> BuildFrameRenderArguments(string input, string filmgrain, string outputPattern, MediaProbeInfo info)
        {
            return BuildFrameRenderArguments(input, filmgrain, outputPattern, info, 0);
        }

        internal static List<string> BuildFrameRenderArguments(string input, string filmgrain, string outputPattern, MediaProbeInfo info, int firstFrame)
        {
            return new List<string> { "-hide_banner", "-v", "error", "-y", "-c:v", "libdav1d", "-filmgrain", filmgrain, "-i", input,
                "-map", "0:v:0", "-vf", PreviewFilter(info, firstFrame), "-fps_mode", "passthrough", "-frames:v", "8", "-threads", "1", "-pix_fmt", "rgb24", outputPattern };
        }

        internal static string PreviewFilter(MediaProbeInfo info)
        {
            return PreviewFilter(info, 0);
        }

        private static string PreviewFilter(MediaProbeInfo info, int firstFrame)
        {
            string start = Math.Max(0, firstFrame).ToString(CultureInfo.InvariantCulture);
            string select = firstFrame <= 0 ? "select=not(mod(n\\,4))" : "select=gte(n\\," + start + ")*not(mod(n-" + start + "\\,4))";
            if (!info.IsHdr) return select;
            string range = info.ColorRange == "pc" ? "full" : "limited";
            return select + ",zscale=transferin=" + info.ColorTransfer + ":primariesin=bt2020:matrixin=bt2020nc:rangein=" + range
                + ":t=linear:npl=100,format=gbrpf32le,tonemap=tonemap=hable:desat=0,zscale=p=bt709:t=bt709:m=bt709:r=full,format=rgb24";
        }

        // Scan only the clean stream: grain must not make a black frame look visible.
        // Both panes then use identical decoded frame indices; no seek/re-encode.
        internal static List<string> BuildScanArguments(string input, string output)
        {
            return new List<string> { "-hide_banner", "-v", "error", "-y", "-c:v", "libdav1d", "-filmgrain", "0", "-i", input,
                "-map", "0:v:0", "-vf", "scale=64:36,format=gray", "-fps_mode", "passthrough", "-frames:v", "160", "-threads", "1", "-f", "rawvideo", output };
        }

        internal static int FindPreviewStartFrame(byte[] pixels)
        {
            const int size = 64 * 36;
            if (pixels == null || pixels.Length == 0 || pixels.Length % size != 0)
                throw new InvalidDataException("Preview scan has incomplete frames.");
            for (int index = 0; index < pixels.Length / size; index++)
            {
                int visible = 0;
                for (int p = index * size; p < (index + 1) * size; p++) if (pixels[p] > 12) visible++;
                if (visible > size / 100) return index;
            }
            return -1; // Preserve black content if the entire scanned opening is black.
        }

        private string Run(string tool,List<string> arguments) {return Run(tool,arguments,null);}
        private string Run(string tool, List<string> arguments,string workingDirectory)
        {
            if (!File.Exists(tool)) throw new FileNotFoundException(T("缺少预览依赖程序。","A preview dependency is missing."), tool);
            using (var process = new Process())
            {
                var command = new StringBuilder(); foreach (string argument in arguments) { if (command.Length > 0) command.Append(' '); command.Append(Quote(argument)); }
                process.StartInfo = new ProcessStartInfo(tool, command.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if(!string.IsNullOrEmpty(workingDirectory))process.StartInfo.WorkingDirectory=workingDirectory;
                lock (sync) { if (closing) throw new OperationCanceledException(); process.Start(); active = process; }
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000)) { try { process.Kill(); } catch { } process.WaitForExit(); throw new TimeoutException(T("预览生成超时。","The preview process timed out.")); }
                    stdout.Wait(); stderr.Wait();
                    if (closing) throw new OperationCanceledException();
                    if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Result.Length > 0 ? stderr.Result : stdout.Result);
                    return stdout.Result;
                }
                finally { lock (sync) { if (ReferenceEquals(active, process)) active = null; } }
            }
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; } result.Append('\\', slashes); slashes = 0; result.Append(c); }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }
        private void ShowFrame()
        {
            if (cleanFrames.Count == 0 || frame.Value >= cleanFrames.Count) return;
            for(int i=0;i<4;i++) ReplaceImage(panes[i], showClean.Checked ? cleanFrames[frame.Value] : variants[i][frame.Value]);
        }
        private static void ReplaceImage(PictureBox box, string path)
        {
            Image next; using (var stream = new MemoryStream(File.ReadAllBytes(path))) using (var image = Image.FromStream(stream)) next = new Bitmap(image);
            Image previous = box.Image; box.Image = next; if (previous != null) previous.Dispose();
        }
        private void ReleaseImages() { foreach (var box in panes) { if (box.Image != null) { box.Image.Dispose(); box.Image = null; } } }
        private void Cleanup() { try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { } }
    }
    // Same painted rail/filled thumb technique as the color window; commits once on mouse-up.
    internal sealed class NativeSeekStrip : Panel, IThemeSurface
    {
        public bool DarkTheme { get; set; }
        internal event EventHandler PositionChanged;
        internal event EventHandler SeekCommitted;
        internal double Duration;
        private double position;
        internal double Position {get{return position;}set{double next=Math.Max(0,Math.Min(Duration,value));if(Math.Abs(next-position)<0.00001)return;position=next;Invalidate();if(PositionChanged!=null)PositionChanged(this,EventArgs.Empty);}}
        internal static string FormatTime(double seconds){int s=(int)Math.Max(0,Math.Min(359999,seconds));return (s/3600).ToString("00",CultureInfo.InvariantCulture)+":"+((s/60)%60).ToString("00",CultureInfo.InvariantCulture)+":"+(s%60).ToString("00",CultureInfo.InvariantCulture);}
        internal NativeSeekStrip(){DoubleBuffered=true;Height=30;Cursor=Cursors.Hand;AccessibleName="视频时间轴 / Video timeline";ResizeRedraw=true;}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);int left=9,right=Math.Max(10,Width-10),y=Height/2;
            int x=left+(int)Math.Round((right-left)*(Duration>0?Position/Duration:0));
            Color color=Enabled?(DarkTheme?UiTheme.Accent:Color.FromArgb(35,120,220)):(DarkTheme?UiTheme.Muted:SystemColors.GrayText);
            using(var rail=new Pen(DarkTheme?UiTheme.Border:Color.FromArgb(175,180,188),4))using(var fill=new Pen(color,4))using(var brush=new SolidBrush(color))
            {e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;rail.StartCap=rail.EndCap=fill.StartCap=fill.EndCap=System.Drawing.Drawing2D.LineCap.Round;e.Graphics.DrawLine(rail,left,y,right,y);e.Graphics.DrawLine(fill,left,y,x,y);e.Graphics.FillEllipse(brush,x-7,y-7,14,14);}
        }
        private void Seek(int x){Position=Duration*Math.Max(0,Math.Min(1,(x-9.0)/Math.Max(1,Width-19)));}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){Capture=true;Seek(e.X);}}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(Capture)Seek(e.X);}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left && Capture){Seek(e.X);Capture=false;if(SeekCommitted!=null)SeekCommitted(this,EventArgs.Empty);}}
    }

    internal static class CinematicCropGeometry
    {
        private static readonly Tuple<double,string,string>[] Presets = {
            Tuple.Create(2.39, "2.39:1 宽银幕", "2.39:1 Widescreen"),
            Tuple.Create(1.43, "1.43:1 IMAX 全尺寸", "1.43:1 Full-size IMAX"),
            Tuple.Create(1.90, "1.90:1 IMAX", "1.90:1 IMAX"),
            Tuple.Create(16.0 / 9, "16:9 现代高清", "16:9 Modern HD"),
            Tuple.Create(16.0 / 10, "16:10 设计工作站", "16:10 Design Workstation"),
            Tuple.Create(20.0 / 9, "20:9 智能手机", "20:9 Smartphone"),
            Tuple.Create(4.0 / 3, "4:3 经典屏幕", "4:3 Classic Display"),
            Tuple.Create(1.0, "1:1 拍立得", "1:1 Instant Photo")
        };

        internal static object[] PresetLabels(Func<string,string,string> text)
        {
            var labels=new object[Presets.Length+2];
            labels[0]=text("自定义","Custom");
            labels[1]=text("原始画幅","Full frame");
            for(int i=0;i<Presets.Length;i++)labels[i+2]=text(Presets[i].Item2,Presets[i].Item3);
            return labels;
        }

        // A preset title is retained only after a direct selection, not inferred from matching dimensions.
        internal const string PresetKey = "CINEMATIC_CROP_PRESET";
        internal static string PresetShortName(int selectedIndex, bool chinese)
        {
            if (selectedIndex == 1) return chinese ? "原始画幅" : "Full frame";
            if (selectedIndex < 2 || selectedIndex >= Presets.Length+2)
                return chinese ? "自定义" : "Custom";
            string name=chinese ? Presets[selectedIndex-2].Item2 : Presets[selectedIndex-2].Item3;
            int separator=name.IndexOf(' ');
            return separator>=0 ? name.Substring(separator+1) : name;
        }
        internal static double PresetRatio(int selectedIndex) { return Presets[selectedIndex-2].Item1; }
        internal static double SampleAspect(MediaProbeInfo media)
        {
            string[] parts = (media.SampleAspectRatio ?? "").Split(':');
            int numerator, denominator;
            return parts.Length == 2 && int.TryParse(parts[0], out numerator) && int.TryParse(parts[1], out denominator)
                && numerator > 0 && denominator > 0 ? numerator / (double)denominator : 1;
        }

        internal static int MatchPreset(Size source, Size current, double aspect, bool even)
        {
            if (source == current) return 1;
            for (int i = 0; i < Presets.Length; i++)
                if (Fit(source, Presets[i].Item1 / aspect, even).Size == current) return i + 2;
            return 0;
        }
        private static int Span(double value, int limit, bool even)
        {
            int step = even ? 2 : 1;
            return Math.Max(2, Math.Min(limit / step, (int)Math.Round(value / step)) * step);
        }

        internal static Rectangle Fit(Size source, double ratio, bool even)
        {
            int w = Span(Math.Min(source.Width, source.Height * ratio), source.Width, even);
            int h = Span(Math.Min(source.Height, source.Width / ratio), source.Height, even);
            return new Rectangle((source.Width - w) / 2, (source.Height - h) / 2, w, h);
        }

        internal static Rectangle Move(Rectangle start, Size source, double dx, double dy)
        {
            int x = Math.Max(0, Math.Min(source.Width - start.Width, start.X + (int)Math.Round(dx)));
            int y = Math.Max(0, Math.Min(source.Height - start.Height, start.Y + (int)Math.Round(dy)));
            return new Rectangle(x, y, start.Width, start.Height);
        }

        internal static Rectangle Drag(Rectangle start, Size source, Point handle, double dx, double dy,
            bool syncX, bool syncY, double ratio, bool even)
        {
            bool changeX = handle.X != 0 || ratio > 0;
            bool changeY = handle.Y != 0 || ratio > 0;
            double anchorX = syncX ? source.Width / 2.0 : Anchor(start.Left, start.Right, handle.X);
            double anchorY = syncY ? source.Height / 2.0 : Anchor(start.Top, start.Bottom, handle.Y);
            int directionX = syncX ? 0 : handle.X, directionY = syncY ? 0 : handle.Y;
            int limitW = Limit(anchorX, directionX, source.Width);
            int limitH = Limit(anchorY, directionY, source.Height);
            double w = start.Width + dx * handle.X * (syncX ? 2 : 1);
            double h = start.Height + dy * handle.Y * (syncY ? 2 : 1);
            if (ratio > 0)
            {
                double targetW = handle.X == 0 ? h * ratio : handle.Y == 0 ? w : (w + h / ratio) / (1 + 1 / (ratio * ratio));
                double maxW = Math.Min(limitW - (even ? limitW % 2 : 0), (limitH - (even ? limitH % 2 : 0)) * ratio);
                w = Math.Min(maxW, Math.Max(Math.Min(maxW, Math.Max(2, 2 * ratio)), targetW));
                h = w / ratio;
            }

            int width = changeX ? Span(w, limitW, even || syncX && source.Width % 2 == 0) : start.Width;
            int height = changeY ? Span(h, limitH, even || syncY && source.Height % 2 == 0) : start.Height;
            int x = changeX ? Position(anchorX, width, directionX, source.Width) : start.X;
            int y = changeY ? Position(anchorY, height, directionY, source.Height) : start.Y;
            return new Rectangle(x, y, width, height);
        }

        private static double Anchor(int start, int end, int direction)
        {
            return direction < 0 ? end : direction > 0 ? start : (start + end) / 2.0;
        }

        private static int Limit(double anchor, int direction, int extent)
        {
            if (direction < 0) return (int)anchor;
            if (direction > 0) return extent - (int)anchor;
            return Math.Min(extent, Math.Max(2, (int)(2 * Math.Min(anchor, extent - anchor))));
        }

        private static int Position(double anchor, int span, int direction, int extent)
        {
            double start = direction < 0 ? anchor - span : direction > 0 ? anchor : anchor - span / 2.0;
            return Math.Max(0, Math.Min(extent - span, (int)Math.Floor(start)));
        }
    }

    internal sealed class NativeCinematicCropForm : Form
    {
        private const int MoveHandle = 8;
        private static readonly Point[] Handles = {
            new Point(-1, -1), new Point(0, -1), new Point(1, -1),
            new Point(-1, 0),                     new Point(1, 0),
            new Point(-1, 1),  new Point(0, 1),  new Point(1, 1)
        };
        private static readonly Cursor[] HandleCursors = {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeWE, Cursors.SizeNESW, Cursors.SizeNS, Cursors.SizeNWSE, Cursors.SizeAll
        };
        private readonly string ffmpeg,input;
        private readonly MediaProbeInfo media, canvas;
        private readonly IDictionary<string,string> initialState;
        private readonly bool chinese, cinemaPreset;
        private readonly PictureBox picture = new PictureBox();
        private readonly NativeSeekStrip timeline = new NativeSeekStrip();
        private readonly TextBox time = new ThemeTextBox();
        private readonly NumericUpDown[] margins = new NumericUpDown[4];
        private readonly ComboBox mode = new ThemeComboBox(), ratioPreset = new ThemeComboBox();
        private readonly CheckBox lockRatio = new ThemeCheckBox(), syncX = new ThemeCheckBox(), syncY = new ThemeCheckBox();
        private readonly Button apply = new ThemeButton { AutoSize = true };
        private readonly Label status = new ThemeLabel(), geometry = new ThemeLabel();
        private readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
        private readonly object processSync = new object();
        private readonly string temporary = Path.Combine(Path.GetTempPath(),"FGS_Crop_"+Guid.NewGuid().ToString("N"));
        private readonly double sampleAspect;
        private Process active;
        private volatile bool closing,busy;
        private bool pending, updating, edited;
        private int draggedHandle = -1;
        private Point dragOrigin;
        private Rectangle region, dragRegion;
        private double requested, lockedAspect;
        private Image image;

        internal string CropMargins { get { return edited ? FormatRegion(region) : initialState[CinematicFrameCore.MarginsEnvironment]; } }
        internal int FrameMode { get { return mode.SelectedIndex; } }
        internal bool Changed { get { return edited || IsCrop != (initialState["FG_FRAME_MODE"] == "CROP"); } }
        private Size SourceSize { get { return new Size(canvas.Width, canvas.Height); } }
        private bool IsCrop { get { return FrameMode == 1; } }
        private string Instructions { get { return S("拖动边线或控制点调整大小，拖动框内移动位置。", "Drag an edge or handle to resize. Drag inside the frame to move it."); } }
        private string S(string zh, string en) { return chinese ? zh : en; }

        protected override void OnLoad(EventArgs e)
        {
            MainForm.AttachOwnedTheme(this, Owner);
            base.OnLoad(e);
        }

        internal NativeCinematicCropForm(string ffmpegPath, string source, MediaProbeInfo info,
            IDictionary<string,string> state, bool chinese, bool cinemaPreset, MediaProbeInfo scaledCanvas = null)
        {
            ffmpeg = ffmpegPath;
            input = source;
            media = info;
            canvas = scaledCanvas ?? info;
            this.chinese = chinese;
            initialState = new Dictionary<string,string>(state);
            initialState["FG_CINEMATIC_FRAME"] = "1";
            this.cinemaPreset = cinemaPreset;
            sampleAspect = CinematicCropGeometry.SampleAspect(canvas);
            Text = scaledCanvas == null ? S("可视化裁剪", "Visual Crop") : S("可视化裁剪 · 缩放后画面", "Visual Crop · After Resize");
            Size = new Size(1050, 760);
            MinimumSize = new Size(840, 620);
            StartPosition = FormStartPosition.CenterParent;
            Opacity = 0;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            picture.Dock = DockStyle.Fill;
            picture.BackColor = Color.Black;
            picture.Paint += PaintPreview;
            picture.MouseDown += BeginDrag;
            picture.MouseMove += MovePointer;
            picture.MouseUp += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                draggedHandle = -1;
                picture.Capture = false;
                UpdatePointer(e.Location);
            };
            picture.MouseCaptureChanged += delegate { if (!picture.Capture) draggedHandle = -1; };
            picture.MouseLeave += delegate { if (draggedHandle < 0) picture.Cursor = Cursors.Default; };
            layout.Controls.Add(picture, 0, 0);

            double duration;
            double.TryParse(info.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            timeline.Duration = Math.Min(359999, Math.Max(0, duration - 0.05));
            timeline.Dock = DockStyle.Fill;
            timeline.PositionChanged += delegate { time.Text = NativeSeekStrip.FormatTime(timeline.Position); };
            timeline.SeekCommitted += delegate { RequestFrame(); };
            layout.Controls.Add(timeline, 0, 1);

            var controls = new SettingsFlowPanel { Dock = DockStyle.Fill, WrapContents = false };
            time.Text = "00:00:00";
            time.Width = 90;
            time.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                double sec;
                if (!SharedEncodePreviewCore.TryParseStart(time.Text.Trim(), out sec) || sec >= timeline.Duration + 0.05)
                { status.Text = S("时间无效或超出视频时长", "Invalid time or outside the video duration"); return; }
                timeline.Position = sec;
                RequestFrame();
            };
            controls.Controls.Add(new ThemeLabel { Text = S("时间", "Time"), AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            controls.Controls.Add(time);
            mode.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Width = 150;
            mode.Items.AddRange(new object[] { S("加黑边", "Letterbox"), S("裁剪画面", "Crop") });
            mode.SelectedIndex = initialState["FG_FRAME_MODE"] == "CROP" ? 1 : 0;
            controls.Controls.Add(mode);
            ratioPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            ratioPreset.Width = 196;
            ratioPreset.Items.AddRange(CinematicCropGeometry.PresetLabels(S));
            controls.Controls.Add(ratioPreset);
            layout.Controls.Add(controls, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var cross = BuildMarginControls(margins, S, delegate(int edge) { if (!updating) EditMargin(edge); });
            cross.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            footer.Controls.Add(cross, 0, 0);

            var right = new TableLayoutPanel {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                ColumnCount = 1, RowCount = 2, Margin = new Padding(12, 3, 0, 3)
            };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var options = new SettingsFlowPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            lockRatio.Text = S("拖动保持比例", "Lock drag ratio");
            syncX.Text = S("左右同步", "Link left/right");
            syncY.Text = S("上下同步", "Link top/bottom");
            foreach (CheckBox option in new[] { lockRatio, syncX, syncY })
            {
                option.AutoSize = true;
                options.Controls.Add(option);
            }
            lockRatio.Checked = syncX.Checked = syncY.Checked = true;
            lockRatio.CheckedChanged += delegate { CaptureAspect(); };
            syncX.CheckedChanged += delegate { if (!updating) CenterLinkedAxis(true); };
            syncY.CheckedChanged += delegate { if (!updating) CenterLinkedAxis(false); };

            status.AutoSize = true;
            status.Text = Instructions;
            geometry.AutoSize = true;
            var details = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            details.Controls.Add(geometry);
            details.Controls.Add(status);
            right.Controls.Add(options, 0, 0);
            right.Controls.Add(details, 0, 1);
            footer.Controls.Add(right, 1, 0);

            var actions = new SettingsFlowPanel { AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, WrapContents = false };
            var cancel = new ThemeButton { Text = S("取消", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            apply.Text = S("应用", "Apply");
            apply.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            actions.Controls.Add(apply);
            actions.Controls.Add(cancel);
            footer.Controls.Add(actions, 2, 0);
            layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout);
            CancelButton = cancel;

            mode.SelectedIndexChanged += delegate {
                if (!edited) LoadRegion();
                else CommitRegion(region);
            };
            ratioPreset.SelectionChangeCommitted += delegate { SelectRatio(); };
            LoadRegion();
            debounce.Interval = 180;
            debounce.Tick += delegate { debounce.Stop(); StartFrame(); };
            Shown += delegate {
                PerformLayout();
                Update();
                Opacity = 1;
                RequestFrame();
            };
            FormClosing += delegate {
                closing = true;
                debounce.Stop();
                lock(processSync) { try { if (active != null && !active.HasExited) active.Kill(); } catch { } }
            };
            FormClosed += delegate {
                debounce.Dispose();
                if (image != null) { image.Dispose(); image = null; }
                if (!busy) Cleanup();
            };
        }

        internal static TableLayoutPanel BuildMarginControls(NumericUpDown[] margins, Func<string,string,string> S, Action<int> changed)
        {
            var cross = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 5, RowCount = 5 };
            for (int i = 0; i < 5; i++)
            {
                cross.ColumnStyles.Add(new ColumnStyle(i == 2 ? SizeType.Absolute : SizeType.AutoSize, i == 2 ? 68 : 0));
                cross.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            string[] labels = { S("左侧", "Left"), S("顶部", "Top"), S("右侧", "Right"), S("底部", "Bottom") };
            Point[] fields = { new Point(1, 2), new Point(2, 1), new Point(3, 2), new Point(2, 3) };
            Point[] captions = { new Point(0, 2), new Point(2, 0), new Point(4, 2), new Point(2, 4) };
            for (int i = 0; i < margins.Length; i++)
            {
                int edge = i;
                var inputMargin = new ThemeNumericUpDown { Width = 62, Anchor = AnchorStyles.None, AccessibleName = labels[i] };
                margins[i] = inputMargin;
                cross.Controls.Add(inputMargin, fields[i].X, fields[i].Y);
                cross.Controls.Add(new ThemeLabel { Text = labels[i], AutoSize = true, Anchor = AnchorStyles.None }, captions[i].X, captions[i].Y);
                inputMargin.ValueChanged += delegate { changed(edge); };
            }
            return cross;
        }

        private string FormatRegion(Rectangle area)
        {
            return CinematicFrameCore.FormatMargins(area.Left, area.Top, canvas.Width - area.Right, canvas.Height - area.Bottom);
        }

        private bool TryPlan(out CinematicFramePlan plan, out string reason)
        {
            var state = new Dictionary<string,string>(initialState);
            state["FG_FRAME_MODE"] = IsCrop ? "CROP" : "BARS";
            state[CinematicFrameCore.MarginsEnvironment] = CropMargins;
            return CinematicFrameCore.TryBuild(canvas, state, out plan, out reason);
        }

        private static Rectangle PlanRegion(CinematicFramePlan plan)
        {
            return new Rectangle(plan.ContentX, plan.ContentY, plan.ContentWidth, plan.ContentHeight);
        }

        private void LoadRegion()
        {
            CinematicFramePlan plan;
            string reason;
            bool valid = TryPlan(out plan, out reason);
            region = valid ? PlanRegion(plan) : new Rectangle(Point.Empty, SourceSize);
            if (valid && CropMargins.Length != 0 && CropMargins != FormatRegion(region)) edited = true;
            ratioPreset.SelectedIndex = cinemaPreset && !Changed ? 2
                : CinematicCropGeometry.MatchPreset(SourceSize, region.Size, sampleAspect, IsCrop);
            CaptureAspect();
            UpdateGeometry();
        }

        private void SelectRatio()
        {
            int preset = ratioPreset.SelectedIndex;
            if (preset == 0) return;
            CommitRegion(preset == 1 ? new Rectangle(Point.Empty, SourceSize)
                : CinematicCropGeometry.Fit(SourceSize, CinematicCropGeometry.PresetRatio(preset) / sampleAspect, IsCrop));
            CaptureAspect();
        }

        private void CaptureAspect()
        {
            int preset = ratioPreset.SelectedIndex;
            lockedAspect = preset >= 2 ? CinematicCropGeometry.PresetRatio(preset) / sampleAspect : region.Width / (double)Math.Max(1, region.Height);
        }

        private void CenterLinkedAxis(bool horizontal)
        {
            Rectangle centered = region;
            if (horizontal && syncX.Checked) centered.X = (canvas.Width - region.Width) / 2;
            if (!horizontal && syncY.Checked) centered.Y = (canvas.Height - region.Height) / 2;
            if (centered != region) CommitRegion(centered);
            else UpdateGeometry();
        }

        private void EditMargin(int edge)
        {
            bool horizontal = edge % 2 == 0;
            int extent = horizontal ? canvas.Width : canvas.Height;
            int[] values = { region.Left, region.Top, canvas.Width - region.Right, canvas.Height - region.Bottom };
            int opposite = (edge + 2) % 4;
            int value = (int)margins[edge].Value;
            if (horizontal ? syncX.Checked : syncY.Checked)
            {
                value = Math.Min(value, (extent - 2) / 2);
                values[opposite] = value;
            }
            else if (IsCrop && ((extent - value - values[opposite]) & 1) != 0)
            {
                // Align the edited edge without moving its opposite edge.
                value = value < extent - 2 - values[opposite] ? value + 1 : value - 1;
            }
            values[edge] = value;
            ratioPreset.SelectedIndex = 0;
            CommitRegion(Rectangle.FromLTRB(values[0], values[1], canvas.Width - values[2], canvas.Height - values[3]));
            CaptureAspect();
        }

        private void CommitRegion(Rectangle next)
        {
            region = next;
            edited = true;
            CinematicFramePlan plan;
            string reason;
            if (TryPlan(out plan, out reason)) region = PlanRegion(plan);
            UpdateGeometry();
        }

        private void UpdateGeometry()
        {
            updating = true;
            try
            {
                int[] values = { region.Left, region.Top, canvas.Width - region.Right, canvas.Height - region.Bottom };
                for (int i = 0; i < margins.Length; i++)
                {
                    int extent = i % 2 == 0 ? canvas.Width : canvas.Height;
                    bool linked = i % 2 == 0 ? syncX.Checked : syncY.Checked;
                    margins[i].Increment = IsCrop && !linked ? 2 : 1;
                    margins[i].Maximum = linked ? Math.Max(values[i], (extent - 2) / 2)
                        : extent - 2 - values[(i + 2) % 4];
                    margins[i].Value = values[i];
                }
            }
            finally { updating = false; }

            CinematicFramePlan plan;
            string reason;
            apply.Enabled = TryPlan(out plan, out reason);
            geometry.Text = apply.Enabled
                ? string.Format(CultureInfo.InvariantCulture, S("有效区域 {0}×{1}   输出 {2}×{3}", "Active area {0}×{1}   Output {2}×{3}"),
                    region.Width, region.Height, plan.ActiveWidth, plan.ActiveHeight)
                : S("四边裁剪至少需要保留 2×2 像素。", reason);
            picture.Invalidate();
        }

        private RectangleF ImageBounds()
        {
            Rectangle area = picture.ClientRectangle;
            area.Inflate(-10, -10);
            double displayWidth = canvas.Width * sampleAspect;
            double scale = Math.Min(area.Width / displayWidth, area.Height / (double)canvas.Height);
            float width = (float)(displayWidth * scale), height = (float)(canvas.Height * scale);
            return new RectangleF(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        }

        private RectangleF SelectionBounds(RectangleF box)
        {
            float sx = box.Width / canvas.Width, sy = box.Height / canvas.Height;
            return new RectangleF(box.Left + region.X * sx, box.Top + region.Y * sy, region.Width * sx, region.Height * sy);
        }

        private static PointF HandlePosition(RectangleF rect, int index)
        {
            Point direction = Handles[index];
            return new PointF(rect.Left + rect.Width * (direction.X + 1) / 2, rect.Top + rect.Height * (direction.Y + 1) / 2);
        }

        private int HitHandle(Point location)
        {
            if (image == null) return -1;
            RectangleF rect = SelectionBounds(ImageBounds());
            int nearest = -1;
            float distance = 11 * 11;
            for (int i = 0; i < Handles.Length; i++)
            {
                PointF point = HandlePosition(rect, i);
                float dx = point.X - location.X, dy = point.Y - location.Y;
                float squared = dx * dx + dy * dy;
                if (squared > distance) continue;
                distance = squared;
                nearest = i;
            }
            if (nearest >= 0) return nearest;
            const float edgeTolerance = 6;
            if (location.X >= rect.Left && location.X <= rect.Right)
            {
                if (Math.Abs(location.Y - rect.Top) <= edgeTolerance) return 1;
                if (Math.Abs(location.Y - rect.Bottom) <= edgeTolerance) return 6;
            }
            if (location.Y >= rect.Top && location.Y <= rect.Bottom)
            {
                if (Math.Abs(location.X - rect.Left) <= edgeTolerance) return 3;
                if (Math.Abs(location.X - rect.Right) <= edgeTolerance) return 4;
            }
            return region.Size != SourceSize && rect.Contains(location) ? MoveHandle : -1;
        }

        private void UpdatePointer(Point location)
        {
            int handle = draggedHandle >= 0 ? draggedHandle : HitHandle(location);
            picture.Cursor = handle >= 0 ? HandleCursors[handle] : Cursors.Default;
        }

        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            draggedHandle = HitHandle(e.Location);
            if (draggedHandle < 0) return;
            dragOrigin = e.Location;
            dragRegion = region;
            picture.Capture = true;
            UpdatePointer(e.Location);
        }

        private void MovePointer(object sender, MouseEventArgs e)
        {
            if (draggedHandle >= 0)
            {
                RectangleF box = ImageBounds();
                if (box.Width <= 0 || box.Height <= 0) return;
                double dx = (e.X - dragOrigin.X) * canvas.Width / (double)box.Width;
                double dy = (e.Y - dragOrigin.Y) * canvas.Height / (double)box.Height;
                bool moving = draggedHandle == MoveHandle;
                Rectangle next = moving
                    ? CinematicCropGeometry.Move(dragRegion, SourceSize, dx, dy)
                    : CinematicCropGeometry.Drag(dragRegion, SourceSize, Handles[draggedHandle], dx, dy,
                        syncX.Checked, syncY.Checked, lockRatio.Checked ? lockedAspect : 0, IsCrop);
                if (region != next)
                {
                    if (moving)
                    {
                        updating = true;
                        try
                        {
                            if (next.X != dragRegion.X) syncX.Checked = false;
                            if (next.Y != dragRegion.Y) syncY.Checked = false;
                        }
                        finally { updating = false; }
                    }
                    else if (!lockRatio.Checked) ratioPreset.SelectedIndex = 0;
                    CommitRegion(next);
                }
            }
            UpdatePointer(e.Location);
        }

        private void PaintPreview(object sender, PaintEventArgs e)
        {
            if (image == null) return;
            RectangleF box = ImageBounds();
            RectangleF rect = SelectionBounds(box);
            e.Graphics.DrawImage(image, box);
            using (var mask = new SolidBrush(IsCrop ? Color.FromArgb(163, 0, 0, 0) : Color.Black))
            using (var outline = new Pen(Color.White))
            using (var grid = new Pen(Color.FromArgb(116, Color.White)))
            using (var handleBorder = new Pen(Color.FromArgb(62, 62, 62)))
            {
                e.Graphics.FillRectangle(mask, box.Left, box.Top, box.Width, Math.Max(0, rect.Top - box.Top));
                e.Graphics.FillRectangle(mask, box.Left, rect.Bottom, box.Width, Math.Max(0, box.Bottom - rect.Bottom));
                e.Graphics.FillRectangle(mask, box.Left, rect.Top, Math.Max(0, rect.Left - box.Left), rect.Height);
                e.Graphics.FillRectangle(mask, rect.Right, rect.Top, Math.Max(0, box.Right - rect.Right), rect.Height);
                e.Graphics.DrawRectangle(outline, rect.X, rect.Y, rect.Width, rect.Height);
                for (int i = 1; i <= 2; i++)
                {
                    float x = rect.Left + i * rect.Width / 3, y = rect.Top + i * rect.Height / 3;
                    e.Graphics.DrawLine(grid, x, rect.Top, x, rect.Bottom);
                    e.Graphics.DrawLine(grid, rect.Left, y, rect.Right, y);
                }
                for (int i = 0; i < Handles.Length; i++)
                {
                    PointF p = HandlePosition(rect, i);
                    e.Graphics.FillRectangle(Brushes.White, p.X - 4, p.Y - 4, 8, 8);
                    e.Graphics.DrawRectangle(handleBorder, p.X - 4, p.Y - 4, 8, 8);
                }
            }
        }
        private void RequestFrame(){requested=timeline.Position;pending=true;debounce.Stop();debounce.Start();}
        private void StartFrame()
        {
            if(closing)return;if(busy){pending=true;return;}pending=false;busy=true;double seconds=requested;
            status.Text=S("正在读取画面…", "Loading frame…");
            var worker=new Thread(new ThreadStart(delegate {
                byte[] bytes=null;string failure="";
                try {
                    if(media.Identity==null)throw new InvalidOperationException(S("媒体探测尚未完成", "Probe is not ready"));media.Identity.RequireCurrent();
                    Directory.CreateDirectory(temporary);string output=Path.Combine(temporary,"frame.png");
                    var args=new List<string>{"-v","error","-y","-ss",seconds.ToString("0.###",CultureInfo.InvariantCulture),"-i",input,"-map","0:v:0","-vf",NativeGrainPreviewForm.PreviewFilter(media)+",scale=w='min(1280,iw)':h=-2","-frames:v","1","-threads","1",output};
                    using(var process=new Process()) {
                        process.StartInfo=new ProcessStartInfo(ffmpeg,SharedFrucCore.JoinArguments(args)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        lock(processSync){if(closing)throw new OperationCanceledException();process.Start();active=process;}
                        try {var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(30000)){try{process.Kill();}catch{}throw new TimeoutException(S("读取预览帧超时。", "Frame preview timed out."));}stdout.Wait();stderr.Wait();if(process.ExitCode!=0)throw new IOException(stderr.Result);}
                        finally {lock(processSync){active=null;}}
                    }
                    media.Identity.RequireCurrent();
                    if(!closing)bytes=File.ReadAllBytes(output);
                }catch(Exception ex){failure=ex.Message;}
                finally {
                    busy=false;
                    if(closing)Cleanup();
                    else try {BeginInvoke(new Action(delegate {
                        if(closing)return;
                        if(media.Identity==null || !media.Identity.IsCurrent(input,media.Identity.Probe)){bytes=null;failure=S("输入文件或 FFprobe 已更改，请关闭窗口后重新探测。", "Input or FFprobe changed. Close the window and probe again.");}
                        if(bytes!=null && Math.Abs(seconds-requested)<0.001){using(var stream=new MemoryStream(bytes))using(var next=Image.FromStream(stream)){var old=image;image=new Bitmap(next);if(old!=null)old.Dispose();}picture.Invalidate();}
                        status.Text=failure.Length==0?Instructions:S("预览失败：", "Preview failed: ")+failure;
                        if(pending)StartFrame();
                    }));}catch{Cleanup();}
                }
            }));worker.IsBackground=true;worker.Start();
        }
        private void Cleanup(){try{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}catch{}}
    }
    internal sealed class LutThumbnailRenderer : IDisposable
    {
        private readonly PictureBox picture;
        private readonly Action<string> log;
        private readonly System.Windows.Forms.Timer delay=new System.Windows.Forms.Timer();
        private readonly Dictionary<string,byte[]> cache=new Dictionary<string,byte[]>();
        private readonly Queue<string> order=new Queue<string>();
        private System.Threading.CancellationTokenSource active;
        private string key="",tool,lut,reference;private int strength,serial;private bool disposed;
        internal LutThumbnailRenderer(PictureBox target,Action<string> logger){picture=target;log=logger;delay.Interval=160;delay.Tick+=delegate {delay.Stop();Start();};picture.Disposed+=delegate {Dispose();};}
        internal static string Stamp(string path){var f=new FileInfo(path);return f.FullName+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks;}
        private void Cancel(){try{if(active!=null)active.Cancel();}catch(ObjectDisposedException){}}
        internal void Queue(string executable,string cube,string image,int percent)
        {
            if(disposed)return;
            if(string.IsNullOrEmpty(cube)){serial++;key="";delay.Stop();Cancel();Replace(null);return;}
            string next;
            try{next=Stamp(cube)+"|"+Stamp(image)+"|"+Stamp(executable)+"|"+percent;}catch(Exception ex){serial++;key="";delay.Stop();Cancel();Replace(null);if(log!=null)log("[LUT thumbnail] "+ex.Message+Environment.NewLine);return;}
            if(next==key)return;
            serial++;key=next;tool=executable;lut=cube;reference=image;strength=percent;delay.Stop();Cancel();
            byte[] bytes;if(cache.TryGetValue(key,out bytes)){Load(bytes);return;}delay.Start();
        }
        internal void ShowColorThumbnail(byte[] bytes)
        {
            Queue("","","",100);
            if(bytes!=null)Load(bytes);
        }
        private void Replace(Image image){var old=picture.Image;picture.Image=image;if(old!=null)old.Dispose();}
        private void Load(byte[] bytes){using(var stream=new MemoryStream(bytes))using(var image=Image.FromStream(stream))Replace(new Bitmap(image));}
        private void Start()
        {
            if(disposed||picture.IsDisposed)return;
            int id=serial;string requestKey=key,exe=tool,source=lut,input=reference;int amount=strength;
            var cancel=new System.Threading.CancellationTokenSource();active=cancel;
            var worker=new Thread(new ThreadStart(delegate {
                string dir=Path.Combine(Path.GetTempPath(),"FilmGrain_Studio","LutThumb_"+Guid.NewGuid().ToString("N"));byte[] bytes=null;string error="";
                try {
                    Directory.CreateDirectory(dir);string cube=Path.Combine(dir,"strength.cube"),output=Path.Combine(dir,"preview.png");
                    string opacity="1.00";if(amount>0)opacity=LutStrengthCore.CreateFile(source,cube,amount);else cube="";cancel.Token.ThrowIfCancellationRequested();
                    var lines=new StringBuilder();int rc=NativeLibraryTools.RunProcess(exe,NativeLutStrengthPreviewForm.FrameArguments(input,cube,opacity,output,240),dir,cancel.Token,delegate(string line){lock(lines)lines.AppendLine(line);});
                    if(rc!=0)throw new InvalidOperationException(lines.ToString());bytes=File.ReadAllBytes(output);
                }catch(OperationCanceledException){}catch(Exception ex){error=ex.Message;}
                finally {
                    try{if(Directory.Exists(dir))Directory.Delete(dir,true);}catch{}
                    try{picture.BeginInvoke(new Action(delegate {
                        if(object.ReferenceEquals(active,cancel))active=null;cancel.Dispose();
                        if(disposed||picture.IsDisposed||id!=serial)return;
                        if(bytes!=null){cache[requestKey]=bytes;order.Enqueue(requestKey);while(order.Count>12)cache.Remove(order.Dequeue());Load(bytes);}
                        else {key="";Replace(null);if(error.Length>0&&log!=null)log("[LUT thumbnail] "+error+Environment.NewLine);}
                    }));}catch{cancel.Dispose();}
                }
            }));worker.IsBackground=true;worker.Start();
        }
        public void Dispose(){if(disposed)return;disposed=true;serial++;delay.Stop();delay.Dispose();Cancel();Replace(null);cache.Clear();order.Clear();}
    }

    internal sealed class NativeLutStrengthPreviewForm : Form
    {
        private readonly int[] levels={25,50,75,100};
        private readonly PictureBox[] pictures={new PictureBox(),new PictureBox(),new PictureBox(),new PictureBox()};
        private readonly Button[] apply={new ThemeButton(),new ThemeButton(),new ThemeButton(),new ThemeButton()};
        private readonly List<Bitmap> rendered=new List<Bitmap>();
        private Bitmap original;
        private readonly CheckBox showOriginal=new ThemeCheckBox(),actualSize=new ThemeCheckBox();
        private readonly Label status=new ThemeLabel();
        private readonly NativeSeekStrip timeline=new NativeSeekStrip();
        private readonly TextBox time=new ThemeTextBox();
        private readonly Button render=new ThemeButton();
        private System.Threading.CancellationTokenSource active;
        private readonly string ffmpeg,lut,video;
        private readonly MediaProbeInfo media;
        private readonly bool preserveHdr;
        private readonly bool normalizedLut;
        private readonly string tone, toneSettings;
        private readonly string temporary=Path.Combine(Path.GetTempPath(),"FilmGrain_Studio","LutCompare_"+Guid.NewGuid().ToString("N"));
        private readonly bool zh;
        private bool running,closing;private int generation;private double desiredPosition;
        internal int SelectedStrength {get;private set;}
        internal byte[] SelectedReference {get;private set;}
        private string T(string a,string b){return zh?a:b;}
        internal NativeLutStrengthPreviewForm(string tool,string selectedLut,string source,string language,MediaProbeInfo info=null,bool preserve=false,string tonemap="hable",string settings="",bool normalized=false)
        {
            ffmpeg=tool;lut=selectedLut;video=source;media=info;zh=language=="zh-CN";
            preserveHdr=preserve&&media!=null&&media.IsHdr;tone=tonemap;toneSettings=settings;
            normalizedLut=normalized&&!preserveHdr;
            Text=T("LUT 四档对照","LUT strength comparison");Size=new Size(1180,860);MinimumSize=new Size(900,680);StartPosition=FormStartPosition.CenterParent;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=5,Padding=new Padding(8)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,50));layout.RowStyles.Add(new RowStyle(SizeType.Percent,50));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
            for(int i=0;i<4;i++) {
                int index=i;var card=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};
                card.RowStyles.Add(new RowStyle(SizeType.Absolute,30));card.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                apply[i].Dock=DockStyle.Fill;apply[i].Enabled=false;apply[i].Text=levels[i]+T("% · 点击应用","% · Apply");
                apply[i].Click+=delegate {
                    if(running||original==null)return;
                    using(var stream=new MemoryStream()){original.Save(stream,System.Drawing.Imaging.ImageFormat.Png);SelectedReference=stream.ToArray();}
                    SelectedStrength=levels[index];DialogResult=DialogResult.OK;Close();
                };
                pictures[i].Dock=DockStyle.Fill;pictures[i].BackColor=Color.Black;pictures[i].SizeMode=PictureBoxSizeMode.Zoom;
                card.Controls.Add(apply[i],0,0);card.Controls.Add(pictures[i],0,1);layout.Controls.Add(card,i%2,i/2);
            }
            double duration;if(media!=null&&double.TryParse(media.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration))timeline.Duration=Math.Max(0,duration);
            timeline.Dock=DockStyle.Fill;timeline.PositionChanged+=delegate {time.Text=NativeSeekStrip.FormatTime(timeline.Position);};timeline.SeekCommitted+=delegate {RequestRender();};layout.Controls.Add(timeline,0,2);layout.SetColumnSpan(timeline,2);
            var options=new SettingsFlowPanel {Dock=DockStyle.Fill};time.Width=85;time.Text="00:00:00";
            render.Text=T("生成四档","Render four strengths");render.AutoSize=true;render.Click+=delegate {RequestRender();};time.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;RequestRender();}};
            options.Controls.Add(new ThemeLabel {Text=T("时刻","Time"),AutoSize=true,Padding=new Padding(0,6,0,0)});options.Controls.Add(time);options.Controls.Add(render);
            showOriginal.Text=T("查看原图","Show original");showOriginal.AutoSize=true;actualSize.Text=T("1:1 居中","1:1 centered");actualSize.AutoSize=true;actualSize.Checked=false;
            showOriginal.CheckedChanged+=delegate {ShowImages();};actualSize.CheckedChanged+=delegate {ShowImages();};options.Controls.Add(showOriginal);options.Controls.Add(actualSize);
            var close=new ThemeButton {Text=T("关闭","Close"),AutoSize=true};close.Click+=delegate {Close();};options.Controls.Add(close);
            layout.Controls.Add(options,0,3);layout.SetColumnSpan(options,2);status.Dock=DockStyle.Fill;status.Text=T("窗口显示后将随机选点生成四档 LUT 对照。","A random video frame will be selected for four LUT strengths.");layout.Controls.Add(status,0,4);layout.SetColumnSpan(status,2);Controls.Add(layout);
            Shown+=delegate {time.Text=NativeSeekStrip.FormatTime(NativeGrainPreviewForm.ChooseInitialSeconds(timeline.Duration,new Random().NextDouble()));RequestRender();};
            FormClosing+=delegate {closing=true;CancelActive();};FormClosed+=delegate {ReleaseImages();if(!running)Cleanup(temporary);};
        }
        private void CancelActive(){try{if(active!=null)active.Cancel();}catch(ObjectDisposedException){}}
        private static Bitmap LoadCopy(string path){using(var image=Image.FromFile(path))return new Bitmap(image);}
        private void ReleaseImages(){foreach(var p in pictures)p.Image=null;foreach(var b in rendered)b.Dispose();rendered.Clear();if(original!=null){original.Dispose();original=null;}}
        private void ShowImages(){if(closing||rendered.Count!=4)return;for(int i=0;i<4;i++){pictures[i].SizeMode=actualSize.Checked?PictureBoxSizeMode.CenterImage:PictureBoxSizeMode.Zoom;pictures[i].Image=showOriginal.Checked?original:rendered[i];}}
        internal static List<string> FrameArguments(string reference,string cube,string opacity,string output,int maxWidth=1920,MediaProbeInfo hdr=null,string tone="hable",string settings="")
        {
            string filter="scale=w='max(2,trunc(min("+maxWidth.ToString(CultureInfo.InvariantCulture)+",iw)/2)*2)':h=-2,format=gbrp16le";
            if(!string.IsNullOrEmpty(cube)) {
                string name=cube.Replace("\\","/").Replace(":","\\:").Replace("'","'"+new string('\\',3)+"'");
                if(opacity=="1.00")filter+=",lut3d=file='"+name+"':interp=tetrahedral";
                else filter+=",split=2[lutorig][lutsrc];[lutsrc]lut3d=file='"+name+"':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity="+opacity;
            }
            if(hdr!=null) {
                string name=(cube??"").Replace("\\","/").Replace(":","\\:").Replace("'","'"+new string('\\',3)+"'");
                filter=(string.IsNullOrEmpty(cube)?"":HdrColorProcessingCore.Lut(hdr,name,opacity))+HdrColorProcessingCore.DisplayToSdr(hdr,tone,settings)+",scale=w='max(2,trunc(min("+maxWidth.ToString(CultureInfo.InvariantCulture)+",iw)/2)*2)':h=-2";
            }
            return new List<string>{"-hide_banner","-v","error","-y","-i",reference,"-vf",filter,"-frames:v","1","-threads","1","-pix_fmt","rgb24",output};
        }
        internal static List<string> SnapshotArguments(string video,MediaProbeInfo media,double seconds,string output,bool preserve=false,string tone="hable",string settings="")
        {
            string filter="scale=w='max(2,trunc(min(1920,iw)/2)*2)':h=-2";
            if(media!=null) {
                string display=preserve ? "null" : media.IsHdr && !string.IsNullOrEmpty(settings)
                    ? HdrColorProcessingCore.DisplayToSdr(media,tone,settings) : NativeGrainPreviewForm.PreviewFilter(media);
                if(media.IsInterlaced)display=SharedHdrPreprocessCore.ResolveDeinterlaceParity("bwdif=mode=send_frame:parity=auto:deint=all,",media)+display;
                filter=display+","+filter;
            }
            var args=new List<string>{"-hide_banner","-v","error","-y","-ss",seconds.ToString("0.######",CultureInfo.InvariantCulture),"-i",video,"-map","0:v:0","-an","-sn","-dn","-vf",filter,"-frames:v","1","-threads","1","-pix_fmt",preserve?"yuv420p10le":"rgb24"};
            if(preserve)args.AddRange(new[]{"-c:v","ffv1"});
            args.Add(output);return args;
        }
        private void RequestRender()
        {
            if(closing)return;
            double position;
            if(!SharedEncodePreviewCore.TryParseStart(time.Text.Trim(),out position)||(timeline.Duration>0&&position>=timeline.Duration)) {status.Text=T("时刻无效或超出视频时长。","Invalid time or beyond video duration.");return;}
            desiredPosition=position;timeline.Position=position;generation++;foreach(var button in apply)button.Enabled=false;
            status.Text=T("正在生成时刻 ","Rendering time ")+NativeSeekStrip.FormatTime(position)+T(" 的四档对照…","...");
            if(running){CancelActive();return;}StartRender();
        }
        private void StartRender()
        {
            running=true;int id=generation;double position=desiredPosition;
            var cancel=new System.Threading.CancellationTokenSource();active=cancel;
            string dir=Path.Combine(temporary,Guid.NewGuid().ToString("N"));
            var worker=new Thread(new ThreadStart(delegate {
                string error="";
                try {
                    if(media!=null&&media.Identity!=null)media.Identity.RequireCurrent();Directory.CreateDirectory(dir);
                    Action<List<string>> run=delegate(List<string> args) {var log=new StringBuilder();int rc=NativeLibraryTools.RunProcess(ffmpeg,args,dir,cancel.Token,delegate(string line){lock(log)log.AppendLine(line);});if(rc!=0)throw new InvalidOperationException(log.ToString());};
                    if(normalizedLut){
                        string sample=Path.Combine(dir,"source.sdr.mkv");
                        run(new List<string>{"-hide_banner","-v","error","-y","-ss",position.ToString("0.######",CultureInfo.InvariantCulture),"-i",video,
                            "-map","0:v:0","-an","-sn","-dn","-frames:v","1","-vf","format=yuv420p10le,"+G04cLutCore.Signal,"-c:v","ffv1",sample});
                        run(new List<string>{"-hide_banner","-v","error","-y","-i",sample,"-frames:v","1","-vf","format=p010le,scale=in_color_matrix=bt709:in_range=tv:out_range=pc,format=rgb24",Path.Combine(dir,"original.png")});
                        for(int i=0;i<4;i++){
                            cancel.Token.ThrowIfCancellationRequested();string name="strength"+levels[i]+".cube",table;
                            if(!G04cLutCore.TryCanonicalCube(lut,levels[i],out table))throw new InvalidDataException("LUT no longer qualifies for GPU preview.");
                            File.WriteAllText(Path.Combine(dir,name),table,Encoding.ASCII);
                            run(new List<string>{"-hide_banner","-v","error","-y","-init_hw_device",VulkanDevicePolicy.Spec("vk"),"-filter_hw_device","vk",
                                "-i",sample,"-frames:v","1","-vf",G04cLutCore.NormalizedFilter(name)+G04cLutCore.Download+
                                ",scale=in_color_matrix=bt709:in_range=tv:out_range=pc,format=rgb24",Path.Combine(dir,"preview"+i+".png")});
                        }
                    }else{
                    string frame=Path.Combine(dir,preserveHdr?"source.hdr.mkv":"original.png");run(SnapshotArguments(video,media,position,frame,preserveHdr,tone,toneSettings));
                    if(preserveHdr)run(FrameArguments(frame,"","1.00",Path.Combine(dir,"original.png"),1920,media,tone,toneSettings));
                    for(int i=0;i<4;i++) {
                        cancel.Token.ThrowIfCancellationRequested();string cube=Path.Combine(dir,"strength"+levels[i]+".cube");
                        string opacity=LutStrengthCore.CreateFile(lut,cube,levels[i]);run(FrameArguments(frame,cube,opacity,Path.Combine(dir,"preview"+i+".png"),1920,preserveHdr?media:null,tone,toneSettings));
                    }
                    }
                    if(media!=null&&media.Identity!=null)media.Identity.RequireCurrent();
                }catch(OperationCanceledException){}catch(Exception ex){error=ex.Message;}
                finally {
                    if(closing){Cleanup(dir);Cleanup(temporary);cancel.Dispose();}
                    else try {BeginInvoke(new Action(delegate {
                        running=false;if(object.ReferenceEquals(active,cancel))active=null;cancel.Dispose();
                        try {
                            if(closing)return;
                            if(id!=generation){StartRender();return;}
                            if(error.Length>0){status.Text=T("预览失败：","Preview failed: ")+error;return;}
                            Bitmap nextOriginal=null;var next=new List<Bitmap>();
                            try {
                                nextOriginal=LoadCopy(Path.Combine(dir,"original.png"));for(int i=0;i<4;i++)next.Add(LoadCopy(Path.Combine(dir,"preview"+i+".png")));
                                ReleaseImages();original=nextOriginal;nextOriginal=null;rendered.AddRange(next);next.Clear();ShowImages();foreach(var b in apply)b.Enabled=true;
                                status.Text=T("视频取帧时刻：","Video frame time: ")+NativeSeekStrip.FormatTime(position)+(normalizedLut?
                                    T("；GPU Normalized，原始分辨率，和编码使用同一 LUT 核心。","; GPU Normalized, native resolution, same LUT core as encoding."):preserveHdr?T("；先在HDR上应用LUT，再转SDR显示；不含CC，非HDR亮度校准。","; LUT on HDR before SDR display; CC excluded, not HDR luminance calibration."):T("；同一基础帧，仅比较 LUT。HDR 输入先转为 SDR 供预览。","; one shared frame, LUT only. HDR input is converted to SDR for preview."));
                            }finally {if(nextOriginal!=null)nextOriginal.Dispose();foreach(var b in next)b.Dispose();}
                        }catch(Exception ex){status.Text=ex.Message;}finally {Cleanup(dir);if(closing)Cleanup(temporary);}
                    }));}catch{Cleanup(dir);cancel.Dispose();}
                }
            }));worker.IsBackground=true;worker.Start();
        }
        private static void Cleanup(string path){try{if(Directory.Exists(path))Directory.Delete(path,true);}catch{}}
    }
}
