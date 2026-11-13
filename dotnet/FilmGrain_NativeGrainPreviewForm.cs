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
                    if(packet==null||!packet.TryGetValue("pts_time",out pts)||!packet.TryGetValue("flags",out flags)||Convert.ToString(flags).IndexOf('K')<0)throw new InvalidDataException(T("预览片段未从已确认的关键帧开始�