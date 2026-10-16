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
        private readonly string ffmpeg, grav1synth, source;
        private readonly List<string> grainArgs;
        private readonly MediaProbeInfo info;
        private readonly string appRoot;
        private readonly IDictionary<string,string> pixelState;
        private readonly HardwareCapabilitySnapshot capabilities;
        private readonly string temporary = Path.Combine(Path.GetTempPath(), "FGS_GrainCompare_" + Guid.NewGuid().ToString("N"));
        private readonly object sync = new object();
        private Process active;
        private volatile bool closing;
        private readonly PictureBox[] panes = { new PictureBox(), new PictureBox(), new PictureBox(), new PictureBox() };
        private readonly List<string>[] variants = { new List<string>(), new List<string>(), new List<string>(), new List<string>() };
        private readonly int[] strengths = {25,50,75,100};
        private readonly Button[] choose = {new Button(),new Button(),new Button(),new Button()};
        private readonly Label status = new Label();
        private readonly TrackBar frame = new TrackBar();
        private readonly List<string> cleanFrames = new List<string>();
        private readonly System.Windows.Forms.Timer playback = new System.Windows.Forms.Timer();
        private readonly Button play = new Button(), render = new Button();
        private readonly CheckBox actualSize = new CheckBox(), skipBlack = new CheckBox(), showClean = new CheckBox();
        private readonly TextBox start = new TextBox();
        private readonly NativeSeekStrip timeline = new NativeSeekStrip();
        private volatile bool running;
        internal int SelectedStrength { get; private set; }

        internal NativeGrainPreviewForm(string ffmpegPath, string gravPath, string input, List<string> args, MediaProbeInfo media)
        {
            info=media; ffmpeg=ffmpegPath; grav1synth=gravPath; source=input; grainArgs=new List<string>(args);
            Text="颗粒四档对比 / Four grain strengths";
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
                choose[i].Text=strengths[i]+"% · 点击应用 / Apply";choose[i].Dock=DockStyle.Fill;choose[i].Enabled=false;
                choose[i].Click+=delegate {SelectedStrength=strengths[index]; DialogResult=DialogResult.OK; Close();};
                panes[i].Dock=DockStyle.Fill;panes[i].SizeMode=PictureBoxSizeMode.Zoom;panes[i].BackColor=Color.Black;
                panel.Controls.Add(choose[i],0,0);panel.Controls.Add(panes[i],0,1);layout.Controls.Add(panel,i%2,i/2);
            }
            var bar=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};
            start.Text="00:00:00";start.Width=80;
            render.Text="生成四档 / Render";render.AutoSize=true;render.Click+=delegate {if(!running)StartRender();};
            play.Text="播放 / Play";play.AutoSize=true;play.Enabled=false;play.Click+=delegate {playback.Enabled=!playback.Enabled;};
            frame.Width=180;frame.Maximum=0;frame.TickStyle=TickStyle.None;frame.Enabled=false;frame.ValueChanged+=delegate {ShowFrame();};
            actualSize.Text="1:1 中心 / Center";actualSize.AutoSize=true;
            actualSize.CheckedChanged+=delegate {foreach(var box in panes)box.SizeMode=actualSize.Checked?PictureBoxSizeMode.CenterImage:PictureBoxSizeMode.Zoom;};
            actualSize.Checked=true;
            skipBlack.Text="跳过黑帧 / Skip black";skipBlack.Checked=true;skipBlack.AutoSize=true;
            showClean.Text="查看原图 / Clean";showClean.AutoSize=true;showClean.Enabled=false;showClean.CheckedChanged+=delegate {ShowFrame();};
            bar.Controls.Add(new Label {Text="起始 / Start",AutoSize=true,Padding=new Padding(0,7,0,0)});bar.Controls.Add(start);bar.Controls.Add(render);bar.Controls.Add(play);bar.Controls.Add(frame);bar.Controls.Add(actualSize);bar.Controls.Add(skipBlack);bar.Controls.Add(showClean);
            double sourceDuration;double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out sourceDuration);
            timeline.Duration=Math.Min(359999,Math.Max(0,sourceDuration));timeline.Dock=DockStyle.Fill;
            timeline.PositionChanged+=delegate {start.Text=NativeSeekStrip.FormatTime(timeline.Position);};
            timeline.SeekCommitted+=delegate {if(!running)StartRender();};
            start.TextChanged+=delegate {double seconds;if(SharedEncodePreviewCore.TryParseStart(start.Text.Trim(),out seconds))timeline.Position=seconds;};
            var seekLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            seekLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,30));seekLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            seekLayout.Controls.Add(timeline,0,0);seekLayout.Controls.Add(bar,0,1);
            layout.Controls.Add(seekLayout,0,2);layout.SetColumnSpan(seekLayout,2);
            status.Dock=DockStyle.Fill;status.Text="同片段、同帧、同随机种子；仅颗粒。起始时间按关键帧裁剪；HDR 转换仅用于显示。";
            layout.Controls.Add(status,0,3);layout.SetColumnSpan(status,2);Controls.Add(layout);
            playback.Interval=150;playback.Tick+=delegate {if(frame.Maximum>0)frame.Value=(frame.Value+1)%(frame.Maximum+1);};
            FormClosing+=delegate {closing=true;playback.Stop();lock(sync){try {if(active!=null&&!active.HasExited)active.Kill();}catch{}}};
            FormClosed+=delegate {playback.Dispose();ReleaseImages();if(!running)Cleanup();};
        }

        internal NativeGrainPreviewForm(string ffmpegPath,string root,string input,IDictionary<string,string> state,HardwareCapabilitySnapshot caps,MediaProbeInfo media)
            : this(ffmpegPath,"",input,new List<string>(),media)
        {
            appRoot=root;pixelState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);capabilities=caps;
            Text="像素颗粒四档对比 / Pixel grain comparison";
        }

        private void StartRender()
        {
            double seconds;
            if(!SharedEncodePreviewCore.TryParseStart(start.Text.Trim(),out seconds)) {MessageBox.Show(this,"起始格式 / Start format: 00:00:00");return;}
            double duration;
            if(double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration)&&seconds>=duration) {MessageBox.Show(this,"起始时间超出视频时长 / Start exceeds duration");return;}
            running=true;timeline.Enabled=false;render.Enabled=start.Enabled=false;playback.Stop();play.Enabled=frame.Enabled=showClean.Enabled=false;
            foreach(var button in choose)button.Enabled=false;
            ReleaseImages();cleanFrames.Clear();foreach(var list in variants)list.Clear();
            status.Text="正在生成同片段的 25% / 50% / 75% / 100% 实际颗粒… / Rendering actual grain…";
            bool skipOpening=skipBlack.Checked;
            var worker=new Thread(new ThreadStart(delegate {
                string error="";int firstFrame=0;double actualStart=0;
                try
                {
                    Directory.CreateDirectory(temporary);
                    foreach(string path in Directory.GetFiles(temporary))File.Delete(path);
                    string sample=Path.Combine(temporary,"sample.mkv"),baseIvf=Path.Combine(temporary,"base.ivf"),model=Path.Combine(temporary,"model.ivf");
                    // Copy the keyframe sample with original PTS so the selected table can be rebased accurately.
                    Run(ffmpeg,new List<string>{"-hide_banner","-v","error","-y","-copyts","-ss",seconds.ToString("0.######",CultureInfo.InvariantCulture),"-noaccurate_seek","-i",source,"-map","0:v:0","-c:v","copy","-frames:v","160","-an","-sn","-dn","-avoid_negative_ts","disabled",sample});
                    string probe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
                    var parser=new System.Web.Script.Serialization.JavaScriptSerializer();
                    string json=Run(probe,new List<string>{"-v","error","-select_streams","v:0","-read_intervals","%+#1","-show_packets","-show_entries","packet=pts_time,flags","-of","json",sample});
                    var data=parser.DeserializeObject(json) as Dictionary<string,object>;object raw;
                    var packets=data!=null&&data.TryGetValue("packets",out raw)?raw as object[]:null;
                    var packet=packets!=null&&packets.Length>0?packets[0] as Dictionary<string,object>:null;object pts,flags;
                    if(packet==null||!packet.TryGetValue("pts_time",out pts)||!packet.TryGetValue("flags",out flags)||Convert.ToString(flags).IndexOf('K')<0)throw new InvalidDataException("Preview sample does not start at a confirmed keyframe.");
                    double firstPts=double.Parse(Convert.ToString(pts),CultureInfo.InvariantCulture);
                    json=Run(probe,new List<string>{"-v","error","-show_entries","format=start_time","-of","json",source});
                    data=parser.DeserializeObject(json) as Dictionary<string,object>;
                    var format=data!=null&&data.TryGetValue("format",out raw)?raw as Dictionary<string,object>:null;object originValue;
                    double origin=0;if(format!=null&&format.TryGetValue("start_time",out originValue))double.TryParse(Convert.ToString(originValue),NumberStyles.Float,CultureInfo.InvariantCulture,out origin);
                    actualStart=Math.Max(0,firstPts-origin);
                    if(pixelState!=null) RenderPixelSample(sample,probe,skipOpening);
                    else
                    {
                    Run(ffmpeg,new List<string>{"-hide_banner","-v","error","-y","-i",sample,"-map","0:v:0","-c:v","copy","-an","-sn","-dn","-f","ivf",baseIvf});
                    json=Run(probe,new List<string>{"-v","error","-select_streams","v:0","-read_intervals","%+#1","-show_packets","-show_entries","packet=pts_time","-of","json",baseIvf});
                    data=parser.DeserializeObject(json) as Dictionary<string,object>;
                    packets=data!=null&&data.TryGetValue("packets",out raw)?raw as object[]:null;
                    packet=packets!=null&&packets.Length>0?packets[0] as Dictionary<string,object>:null;
                    if(packet==null||!packet.TryGetValue("pts_time",out pts)||Math.Abs(double.Parse(Convert.ToString(pts),CultureInfo.InvariantCulture))>0.000001)throw new InvalidDataException("Preview IVF timeline did not normalize to zero.");
                    var selected=new List<string>(grainArgs);
                    int tableIndex=selected.IndexOf("--grain");if(tableIndex<0)tableIndex=selected.IndexOf("-g");
                    if(tableIndex>=0)
                    {
                        string local=Path.Combine(temporary,"selected.tbl");
                        File.WriteAllText(local,Av1GrainStrengthCore.ShiftTable(File.ReadAllText(selected[tableIndex+1]),actualStart),new UTF8Encoding(false));selected[tableIndex+1]=local;
                    }
                    var apply=new List<string>{"apply",baseIvf,"-o",model};apply.AddRange(selected);apply.AddRange(new[]{"--replace","-y"});Run(grav1synth,apply);
                    string original=Path.Combine(temporary,"original.tbl");Run(grav1synth,new List<string>{"inspect",model,"-o",original,"-y"});
                    string originalText=File.ReadAllText(original);
                    if(skipOpening) {string scan=Path.Combine(temporary,"scan.gray");Run(ffmpeg,BuildScanArguments(baseIvf,scan));firstFrame=FindPreviewStartFrame(File.ReadAllBytes(scan));if(firstFrame<0)firstFrame=0;}
                    RenderFrames(baseIvf,"0","clean",firstFrame);
                    for(int i=0;i<4;i++)
                    {
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
                    else try {running=false;BeginInvoke(new Action(delegate {
                        running=false;if(closing){Cleanup();return;}
                        timeline.Enabled=true;render.Enabled=start.Enabled=true;
                        if(error.Length>0){status.Text="预览失败 / Preview failed: "+error;return;}
                        try {
                            cleanFrames.AddRange(Directory.GetFiles(temporary,"clean*.png"));cleanFrames.Sort(StringComparer.Ordinal);
                            for(int i=0;i<4;i++){variants[i].AddRange(Directory.GetFiles(temporary,"variant"+i+"_*.png"));variants[i].Sort(StringComparer.Ordinal);if(variants[i].Count!=cleanFrames.Count)throw new InvalidDataException("Preview frame counts do not match.");}
                            if(cleanFrames.Count==0)throw new InvalidDataException("No preview frames.");
                            frame.Maximum=cleanFrames.Count-1;frame.Value=0;ShowFrame();
                            play.Enabled=frame.Enabled=showClean.Enabled=true;foreach(var button in choose)button.Enabled=true;
                            status.Text="实际关键帧起始 / Actual start: "+TimeSpan.FromSeconds(actualStart).ToString()+"；同帧同种子，点击档位应用。\r\n仅颗粒；HDR 显示转换，不含 LUT/调色/插帧。中心 1:1 可检查颗粒细节。";
                        }catch(Exception ex){ReleaseImages();status.Text="预览失败 / Preview failed: "+ex.Message;}
                    }));}catch{running=false;Cleanup();}
                }
            }));worker.IsBackground=true;worker.Start();
        }

        private void RenderPixelSample(string sample,string probe,bool skipOpening)
        {
            string normalized=Path.Combine(temporary,"normalized.mkv");
            Run(ffmpeg,new List<string>{"-hide_banner","-v","error","-y","-i",sample,"-map","0:v:0","-c:v","copy",normalized});
            var media=SharedEncodePreviewCore.Probe(normalized,probe,delegate {return closing;});
            var state=new Dictionary<string,string>(pixelState,StringComparer.OrdinalIgnoreCase);
            state["FG_PIXEL_GRAIN_STRENGTH"]="100";state["FG_COLOR_ENABLED"]="0";state["FG_LUT_PATH"]="";
            state["FG_SUBTITLE"]="0";state["FG_CINEMATIC_FRAME"]="0";state["FG_SVP_INTERPOLATE"]="0";
            state["FG_FPS_MODE"]="SOURCE";state["FG_UPLOAD"]="0";state["FG_X264_PASS_MODE"]="VBR1";
            state["FG_ENCODE_PREVIEW"]="0";state["FG_SFE_ENGINES"]="0";
            state["FG_OUTPUT_MODE"]="CUSTOM";state["FG_OUTPUT_CUSTOM_DIR"]=temporary;
            state["FG_TEMP_MODE"]="CUSTOM";state["FG_TEMP_CUSTOM_DIR"]=temporary;
            // Compare only grain; retain the chosen HDR policy and the established field handling.
            BridgePreparedExecution prepared;string reason;
            if(!NativeExecutionPreparationCore.TryPrepare(appRoot,normalized,media,state,capabilities,false,out prepared,out reason))throw new InvalidDataException(reason);
            var request=prepared.ExecutionRequest;
            try
            {
                if(request.PixelGrainFullFilter==null||request.PixelGrainCleanFilter==null)throw new InvalidDataException("Selected route did not supply a native pixel grain comparison graph.");
                if(request.DirectStages!=null)foreach(var stage in request.DirectStages)
                {
                    if(stage.OriginalArguments!=null && stage.OriginalArguments.Contains("-filter_complex"))
                    {request.PixelGrainEncodeArguments=stage.OriginalArguments;break;}
                    if(closing)throw new OperationCanceledException();
                    if(stage.PrepareWithCancellation!=null)stage.PrepareWithCancellation(delegate {return closing;});
                    if(stage.BeforeRunPreparation!=null)stage.BeforeRunPreparation();
                    if(stage.ShouldRun!=null&&!stage.ShouldRun())continue;
                    if(stage.OriginalArguments==null)throw new InvalidDataException("Preview preprocessing arguments unavailable.");
                    Run(stage.ToolPath,new List<string>(stage.OriginalArguments),stage.WorkingDirectory);
                }
                if(request.PixelGrainEncodeArguments==null)throw new InvalidDataException("Native preview encode arguments missing.");
                string pair=Path.Combine(temporary,"pixel_pair.mkv");
                var args=PixelGrainStrengthCore.PreviewArguments(request.PixelGrainEncodeArguments,request.PixelGrainFullFilter,request.PixelGrainCleanFilter,pair);
                PixelGrainStrengthCore.StampPreviewColor(args,request.PixelGrainHdr,media);
                Run(ffmpeg,args,request.WorkingDirectory);
                var pairInfo=SharedEncodePreviewCore.Probe(pair,probe,delegate {return closing;});
                int first=0;
                if(skipOpening)
                {
                    string scan=Path.Combine(temporary,"pixel_scan.gray");
                    Run(ffmpeg,new List<string>{"-v","error","-y","-i",pair,"-map","0:v:0","-vf","scale=64:36,format=gray","-frames:v","32","-threads","1","-f","rawvideo",scan});
                    first=FindPreviewStartFrame(File.ReadAllBytes(scan));if(first<0)first=0;
                }
                Run(ffmpeg,PixelGrainStrengthCore.FrameArguments(pair,0,PreviewFilter(pairInfo,first),Path.Combine(temporary,"clean%02d.png")));
                for(int i=0;i<4;i++)Run(ffmpeg,PixelGrainStrengthCore.FrameArguments(pair,strengths[i],PreviewFilter(pairInfo,first),Path.Combine(temporary,"variant"+i+"_%02d.png")));

            }
            finally {BridgeExecutionCore.CleanupTemporaryFiles(request);}
        }

        private void RenderFrames(string input, string filmgrain, string prefix, int firstFrame)
        {
            // libdav1d must expose filmgrain; if unsupported, fail explicitly instead of
            // accidentally displaying an AV1 decode with grain disabled.
            Run(ffmpeg, BuildFrameRenderArguments(input, filmgrain, Path.Combine(temporary, prefix + "%02d.png"), info, firstFrame));
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
            if (!File.Exists(tool)) throw new FileNotFoundException("Preview dependency missing", tool);
            using (var process = new Process())
            {
                var command = new StringBuilder(); foreach (string argument in arguments) { if (command.Length > 0) command.Append(' '); command.Append(Quote(argument)); }
                process.StartInfo = new ProcessStartInfo(tool, command.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if(!string.IsNullOrEmpty(workingDirectory))process.StartInfo.WorkingDirectory=workingDirectory;
                lock (sync) { if (closing) throw new OperationCanceledException(); process.Start(); active = process; }
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000)) { try { process.Kill(); } catch { } process.WaitForExit(); throw new TimeoutException("Preview process timed out."); }
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
    internal sealed class NativeSeekStrip : Panel
    {
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
            Color color=Enabled?Color.FromArgb(35,120,220):SystemColors.GrayText;
            using(var rail=new Pen(Color.FromArgb(175,180,188),4))using(var fill=new Pen(color,4))using(var brush=new SolidBrush(color))
            {e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;rail.StartCap=rail.EndCap=fill.StartCap=fill.EndCap=System.Drawing.Drawing2D.LineCap.Round;e.Graphics.DrawLine(rail,left,y,right,y);e.Graphics.DrawLine(fill,left,y,x,y);e.Graphics.FillEllipse(brush,x-7,y-7,14,14);}
        }
        private void Seek(int x){Position=Duration*Math.Max(0,Math.Min(1,(x-9.0)/Math.Max(1,Width-19)));}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){Capture=true;Seek(e.X);}}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(Capture)Seek(e.X);}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left && Capture){Seek(e.X);Capture=false;if(SeekCommitted!=null)SeekCommitted(this,EventArgs.Empty);}}
    }

    internal static class CinematicPreviewGeometryCore
    {
        internal static Rectangle Region(int width,int height,int perSide,int mode,int horizontal)
        {
            if(width<2||height<2)throw new ArgumentException("Invalid video dimensions.");
            if(perSide<0||perSide>(height-2)/2||horizontal>(width-2)/2)throw new ArgumentOutOfRangeException("crop");
            var state=new Dictionary<string,string> {
                {"FG_CINEMATIC_FRAME","1"},{"FG_FRAME_MODE",mode==1?"CROP":"BARS"},
                {"FG_CROP_PER_SIDE",perSide.ToString(CultureInfo.InvariantCulture)},
                {"FG_CROP_HORIZONTAL",horizontal.ToString(CultureInfo.InvariantCulture)}};
            CinematicFramePlan plan;string reason;
            if(!CinematicFrameCore.TryBuild(new MediaProbeInfo {Width=width,Height=height},state,out plan,out reason))
                throw new ArgumentException(reason);
            return new Rectangle(plan.ContentX,plan.ContentY,plan.ContentWidth,plan.ContentHeight);
        }
        internal static Rectangle Region(int width,int height,int perSide,int mode)
        {return Region(width,height,perSide,mode,0);}
    }

    internal sealed class NativeCinematicCropForm : Form
    {
        private readonly string ffmpeg,input;
        private readonly MediaProbeInfo media;
        private readonly PictureBox picture=new PictureBox();
        private readonly NativeSeekStrip timeline=new NativeSeekStrip();
        private readonly TextBox time=new TextBox();
        private readonly NumericUpDown crop=new NumericUpDown(),horizontal=new NumericUpDown();
        private readonly ComboBox mode=new ComboBox();
        private readonly Label status=new Label(),geometry=new Label();
        private readonly System.Windows.Forms.Timer debounce=new System.Windows.Forms.Timer();
        private readonly object processSync=new object();
        private readonly string temporary=Path.Combine(Path.GetTempPath(),"FGS_Crop_"+Guid.NewGuid().ToString("N"));
        private Process active;
        private volatile bool closing,busy;
        private bool pending,dragging,dragHorizontal;
        private double requested;
        private Image image;
        internal int CropPerSide {get{return (int)crop.Value;}}
        internal int CropHorizontal {get{return (int)horizontal.Value;}}
        internal int FrameMode {get{return mode.SelectedIndex;}}
        internal NativeCinematicCropForm(string ffmpegPath,string source,MediaProbeInfo info,int perSide,int framing,int horizontalSide=0)
        {
            ffmpeg=ffmpegPath;input=source;media=info;
            Text="Cinematic Style · 可视化裁剪 / Visual crop";Size=new Size(1050,760);MinimumSize=new Size(840,620);StartPosition=FormStartPosition.CenterParent;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(10)};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            picture.Dock=DockStyle.Fill;picture.BackColor=Color.FromArgb(22,22,22);picture.Paint+=PaintPreview;
            picture.MouseDown+=delegate(object sender,MouseEventArgs e){if(e.Button!=MouseButtons.Left||image==null)return;RectangleF box=ImageBounds();if(!box.Contains(e.Location))return;
                Rectangle r=CinematicPreviewGeometryCore.Region(media.Width,media.Height,CropPerSide,FrameMode,CropHorizontal);
                float dx=Math.Min(Math.Abs(e.X-(box.Left+r.Left*box.Width/media.Width)),Math.Abs(e.X-(box.Left+r.Right*box.Width/media.Width)));
                float dy=Math.Min(Math.Abs(e.Y-(box.Top+r.Top*box.Height/media.Height)),Math.Abs(e.Y-(box.Top+r.Bottom*box.Height/media.Height)));
                if(Math.Min(dx,dy)>12)return;dragHorizontal=dx<dy;dragging=true;picture.Capture=true;DragCrop(e.X,e.Y);};
            picture.MouseMove+=delegate(object sender,MouseEventArgs e){if(dragging)DragCrop(e.X,e.Y);};
            picture.MouseUp+=delegate{dragging=false;picture.Capture=false;};picture.Cursor=Cursors.Cross;
            layout.Controls.Add(picture,0,0);
            double duration;double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration);timeline.Duration=Math.Min(359999,Math.Max(0,duration-0.05));timeline.Dock=DockStyle.Fill;
            timeline.PositionChanged+=delegate {time.Text=NativeSeekStrip.FormatTime(timeline.Position);};timeline.SeekCommitted+=delegate{RequestFrame();};layout.Controls.Add(timeline,0,1);
            var controls=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};time.Text="00:00:00";time.Width=90;
            var show=new Button {Text="预览 / Preview",AutoSize=true};show.Click+=delegate {double sec;if(!SharedEncodePreviewCore.TryParseStart(time.Text.Trim(),out sec)||sec>=timeline.Duration+0.05){status.Text="时间无效或超出视频时长 / Invalid time";return;}timeline.Position=sec;RequestFrame();};
            time.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Enter){show.PerformClick();e.SuppressKeyPress=true;}};
            controls.Controls.Add(new Label {Text="时间 / Time",AutoSize=true,Padding=new Padding(0,7,0,0)});controls.Controls.Add(time);controls.Controls.Add(show);
            mode.DropDownStyle=ComboBoxStyle.DropDownList;mode.Width=225;mode.Items.AddRange(new object[]{"加黑边 / Letterbox","裁剪画面 / Crop"});mode.SelectedIndex=framing==1?1:0;mode.SelectedIndexChanged+=delegate{UpdateGeometry();};controls.Controls.Add(mode);layout.Controls.Add(controls,0,2);
            var values=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};values.Controls.Add(new Label {Text="上下每边像素 / Per side",AutoSize=true,Padding=new Padding(0,7,0,0)});
            crop.Minimum=0;crop.Maximum=Math.Max(0,(info.Height-2)/2);crop.Width=90;crop.Value=Math.Min(crop.Maximum,Math.Max(0,perSide));crop.ValueChanged+=delegate {UpdateGeometry();};values.Controls.Add(crop);
            values.Controls.Add(new Label {Text="左右每边 / Left-right",AutoSize=true,Padding=new Padding(4,7,0,0)});
            horizontal.Maximum=Math.Max(0,(info.Width-2)/2);horizontal.Width=80;horizontal.Value=Math.Min(horizontal.Maximum,Math.Max(0,horizontalSide));horizontal.ValueChanged+=delegate{UpdateGeometry();};values.Controls.Add(horizontal);
            var automatic=new Button {Text="自动 2.39:1 / Auto",AutoSize=true};automatic.Click+=delegate{horizontal.Value=0;crop.Value=0;};values.Controls.Add(automatic);geometry.AutoSize=true;geometry.Padding=new Padding(6,7,0,0);layout.Controls.Add(values,0,3);
            status.AutoSize=true;status.Text="拖动四边对称调整；左右 > 0 时，上下 0 = 不裁剪。HDR 仅转换预览显示。";var details=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};details.Controls.Add(geometry);details.Controls.Add(status);layout.Controls.Add(details,0,4);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var cancel=new Button {Text="取消 / Cancel",AutoSize=true,DialogResult=DialogResult.Cancel};var apply=new Button {Text="应用 / Apply",AutoSize=true};
            apply.Click+=delegate {DialogResult=DialogResult.OK;Close();};actions.Controls.Add(cancel);actions.Controls.Add(apply);layout.Controls.Add(actions,0,5);Controls.Add(layout);CancelButton=cancel;
            debounce.Interval=180;debounce.Tick+=delegate {debounce.Stop();StartFrame();};Shown+=delegate{UpdateGeometry();RequestFrame();};
            FormClosing+=delegate {closing=true;debounce.Stop();lock(processSync){try{if(active!=null&&!active.HasExited)active.Kill();}catch{}}};
            FormClosed+=delegate{debounce.Dispose();if(image!=null){image.Dispose();image=null;}if(!busy)Cleanup();};
        }
        private RectangleF ImageBounds()
        {
            if(image==null)return RectangleF.Empty;
            float scale=Math.Min(picture.ClientSize.Width/(float)media.Width,picture.ClientSize.Height/(float)media.Height);
            float w=media.Width*scale,h=media.Height*scale;return new RectangleF((picture.ClientSize.Width-w)/2,(picture.ClientSize.Height-h)/2,w,h);
        }
        private void PaintPreview(object sender,PaintEventArgs e)
        {
            if(image==null)return;RectangleF box=ImageBounds();e.Graphics.DrawImage(image,box);
            Rectangle region=CinematicPreviewGeometryCore.Region(media.Width,media.Height,CropPerSide,FrameMode,CropHorizontal);
            float top=box.Top+region.Top*box.Height/media.Height,bottom=box.Top+region.Bottom*box.Height/media.Height;
            using(var mask=new SolidBrush(FrameMode==0?Color.Black:Color.FromArgb(165,0,0,0)))using(var line=new Pen(Color.FromArgb(60,210,240),2))
            {e.Graphics.FillRectangle(mask,box.Left,box.Top,box.Width,Math.Max(0,top-box.Top));e.Graphics.FillRectangle(mask,box.Left,bottom,box.Width,Math.Max(0,box.Bottom-bottom));e.Graphics.DrawLine(line,box.Left,top,box.Right,top);e.Graphics.DrawLine(line,box.Left,bottom,box.Right,bottom);
                float left=box.Left+region.Left*box.Width/media.Width,right=box.Left+region.Right*box.Width/media.Width;
                e.Graphics.FillRectangle(mask,box.Left,top,Math.Max(0,left-box.Left),bottom-top);e.Graphics.FillRectangle(mask,right,top,Math.Max(0,box.Right-right),bottom-top);
                e.Graphics.DrawLine(line,left,top,left,bottom);e.Graphics.DrawLine(line,right,top,right,bottom);}
        }
        private void DragCrop(int x,int y)
        {
            RectangleF box=ImageBounds();if(box.Height<1)return;
            if(dragHorizontal){double localX=(x-box.Left)/box.Width*media.Width;int px=(int)Math.Round(Math.Min(localX,media.Width-localX));horizontal.Value=Math.Max(horizontal.Minimum,Math.Min(horizontal.Maximum,px));return;}
            double local=(y-box.Top)/box.Height*media.Height;
            int pixels=(int)Math.Round(Math.Min(local,media.Height-local));
            crop.Value=Math.Max(crop.Minimum,Math.Min(crop.Maximum,pixels));
        }
        private void UpdateGeometry()
        {
            Rectangle region=CinematicPreviewGeometryCore.Region(media.Width,media.Height,CropPerSide,FrameMode,CropHorizontal);
            geometry.Text="有效 / Active: "+region.Width+"×"+region.Height+"   输出 / Output: "+(FrameMode==1?region.Width:media.Width)+"×"+(FrameMode==1?region.Height:media.Height);
            picture.Invalidate();
        }
        private void RequestFrame(){requested=timeline.Position;pending=true;debounce.Stop();debounce.Start();}
        private void StartFrame()
        {
            if(closing)return;if(busy){pending=true;return;}pending=false;busy=true;double seconds=requested;
            status.Text="正在读取画面 / Loading frame…";
            var worker=new Thread(new ThreadStart(delegate {
                byte[] bytes=null;string failure="";
                try {
                    Directory.CreateDirectory(temporary);string output=Path.Combine(temporary,"frame.png");
                    var args=new List<string>{"-v","error","-y","-ss",seconds.ToString("0.###",CultureInfo.InvariantCulture),"-i",input,"-map","0:v:0","-vf",NativeGrainPreviewForm.PreviewFilter(media)+",scale=w='min(1280,iw)':h=-2","-frames:v","1","-threads","1",output};
                    using(var process=new Process()) {
                        process.StartInfo=new ProcessStartInfo(ffmpeg,SharedFrucCore.JoinArguments(args)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                        lock(processSync){if(closing)throw new OperationCanceledException();process.Start();active=process;}
                        try {var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(30000)){try{process.Kill();}catch{}throw new TimeoutException("Frame preview timed out.");}stdout.Wait();stderr.Wait();if(process.ExitCode!=0)throw new IOException(stderr.Result);}
                        finally {lock(processSync){active=null;}}
                    }
                    if(!closing)bytes=File.ReadAllBytes(output);
                }catch(Exception ex){failure=ex.Message;}
                finally {
                    busy=false;
                    if(closing)Cleanup();
                    else try {BeginInvoke(new Action(delegate {
                        if(closing)return;
                        if(bytes!=null && Math.Abs(seconds-requested)<0.001){using(var stream=new MemoryStream(bytes))using(var next=Image.FromStream(stream)){var old=image;image=new Bitmap(next);if(old!=null)old.Dispose();}picture.Invalidate();}
                        status.Text=failure.Length==0?"拖动四边对称调整；左右 > 0 时，上下 0 = 不裁剪。HDR 仅用于显示转换。":"预览失败 / Preview failed: "+failure;
                        if(pending)StartFrame();
                    }));}catch{Cleanup();}
                }
            }));worker.IsBackground=true;worker.Start();
        }
        private void Cleanup(){try{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}catch{}}
    }
}
