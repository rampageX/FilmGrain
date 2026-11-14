using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal enum OutputPreviewStage { Preparing, Live, AfterMux }

    internal sealed class OutputPreviewSample
    {
        internal readonly string Input, Output;
        internal readonly double Seconds, Offset;
        internal readonly bool Finished;

        internal OutputPreviewSample(BridgeExecutionRequest request,string output,double seconds,bool finished)
        {
            Input=request.PreviewOriginal ?? request.InputFiles[0];
            Output=output;
            Seconds=seconds;
            Offset=request.PreviewOriginalOffset;
            Finished=finished;
        }
    }

    internal sealed class OutputPreviewForm : Form
    {
        private readonly Func<BridgeTaskRequest> currentTask;
        private readonly string ffmpeg, ffprobe;
        private readonly bool chinese;
        private readonly OutputComparisonCanvas canvas;
        private readonly Label status;
        private readonly Button mode;
        private readonly System.Windows.Forms.Timer timer;
        private CancellationTokenSource active;
        private BridgeTaskRequest shownTask;
        private OutputPreviewSample lastSample;
        private bool closed;

        private string T(string zh,string en) {return chinese?zh:en;}

        internal OutputPreviewForm(Func<BridgeTaskRequest> task,string ffmpeg,bool chinese)
        {
            this.currentTask=task;
            this.ffmpeg=ffmpeg;
            ffprobe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
            this.chinese=chinese;
            Text=T("输出实时预览","Live output preview");
            StartPosition=FormStartPosition.CenterParent;
            ClientSize=new Size(1160,700);
            MinimumSize=new Size(560,320);
            Opacity=0;
            KeyPreview=true;
            canvas=new OutputComparisonCanvas(T("原片","ORIGINAL"),T("输出","FINAL")) {Dock=DockStyle.Fill};
            var footer=new Panel {Dock=DockStyle.Bottom,Height=40,Padding=new Padding(10,5,10,5)};
            status=new ThemeLabel {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
            mode=new ThemeButton {Dock=DockStyle.Right,Width=120,Text=T("左右并排","Side by Side")};
            mode.Click+=delegate {ToggleMode();};
            footer.Controls.Add(status);footer.Controls.Add(mode);
            Controls.Add(canvas);Controls.Add(footer);
            KeyDown+=delegate(object sender,KeyEventArgs e)
            {
                if(e.KeyCode==Keys.Space){ToggleMode();e.SuppressKeyPress=true;}
                else if(e.KeyCode==Keys.Left || e.KeyCode==Keys.Right)
                {canvas.MoveSplit(e.KeyCode==Keys.Left?-0.025f:0.025f);e.SuppressKeyPress=true;}
            };
            timer=new System.Windows.Forms.Timer {Interval=2000};
            timer.Tick+=delegate {RefreshComparison();};
            Shown+=delegate {Refresh();Opacity=1;RefreshComparison();timer.Start();};
            FormClosed+=delegate {closed=true;timer.Dispose();if(active!=null)active.Cancel();canvas.SetFrames(null,null);};
        }

        private void ToggleMode()
        {
            canvas.SideBySide=!canvas.SideBySide;
            mode.Text=canvas.SideBySide?T("滑动对照","Slider"):T("左右并排","Side by Side");
            canvas.Invalidate();
        }

        private async void RefreshComparison()
        {
            if(closed)return;
            var task=currentTask();
            if(task!=shownTask)
            {
                shownTask=task;lastSample=null;
                canvas.SetFrames(null,null);
                if(active!=null)active.Cancel();
            }
            if(task==null){status.Text=T("等待编码任务","Waiting for an encode");return;}
            var request=task.ExecutionRequest;
            var result=request.PreviewResult;
            if(result!=null && (result.Cancelled || result.ExitCode!=0 || result.Exception!=null))
            {
                if(active!=null)active.Cancel();
                status.Text=result.Cancelled?T("编码已取消","Encode cancelled"):T("编码失败","Encode failed");
                return;
            }
            var sample=request.PreviewSample;
            if(sample==null)
            {
                string waiting = request.PreviewStage == OutputPreviewStage.AfterMux
                    ? T("此 MP4 任务将在封装完成后显示","This MP4 will be shown after muxing")
                    : request.PreviewStage == OutputPreviewStage.Live
                        ? T("等待首帧可读","Waiting for the first readable frame")
                        : T("等待最终输出阶段","Waiting for the final output stage");
                status.Text=Path.GetFileName(request.InputFiles[0])+"  ·  "+waiting;
                return;
            }
            if(active!=null || sample==lastSample)return;
            lastSample=sample;
            var cancellation=new CancellationTokenSource();
            active=cancellation;
            try
            {
                bool hasPair=canvas.HasPair;
                var frames=await Task.Run(()=>ReadPair(sample,cancellation.Token,hasPair));
                if(closed || cancellation.IsCancellationRequested || currentTask()!=task)
                {frames.Item1.Dispose();frames.Item2.Dispose();return;}
                canvas.SetFrames(frames.Item1,frames.Item2);
                status.Text=Path.GetFileName(sample.Input)+"  ·  "+TimeSpan.FromSeconds(frames.Item3).ToString(@"hh\:mm\:ss\.fff")+
                    "  ·  "+(sample.Finished?T("已完成","Finished"):T("编码中","Encoding"))+
                    "  ·  "+T("空格切换布局","Space switches layout");
            }
            catch(OperationCanceledException) {}
            catch(Exception ex)
            {
                if(!closed && !cancellation.IsCancellationRequested && currentTask()==task)
                {
                    status.Text=sample.Finished?T("预览读取失败　","Preview unavailable  ")+ex.Message:
                        Path.GetFileName(sample.Input)+"  ·  "+T("等待可读取的输出帧","Waiting for a readable output frame");
                }
            }
            finally {active=null;cancellation.Dispose();}
        }

        private Tuple<Bitmap,Bitmap,double> ReadPair(OutputPreviewSample sample,CancellationToken cancel,bool canvasHasPair)
        {
            string dir=Path.Combine(Path.GetTempPath(),"FGS_OutputPreview_"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var output=Probe(sample.Output,dir,cancel);
                var input=Probe(sample.Input,dir,cancel);
                string final=Path.Combine(dir,"final.png"), original=Path.Combine(dir,"original.png");
                double seconds=sample.Seconds;
                try {seconds=ReadFrame(sample.Output,output,seconds,final,dir,cancel);}
                catch(IOException)
                {
                    if(seconds<=0 || (!sample.Finished && canvasHasPair))throw;
                    seconds=0;
                    seconds=ReadFrame(sample.Output,output,seconds,final,dir,cancel);
                }
                ReadFrame(sample.Input,input,seconds+sample.Offset,original,dir,cancel);
                var before=LoadBitmap(original);
                try{return Tuple.Create(before,LoadBitmap(final),seconds+sample.Offset);}
                catch{before.Dispose();throw;}
            }
            finally {try{Directory.Delete(dir,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }

        private static Bitmap LoadBitmap(string path)
        {using(var image=Image.FromFile(path))return new Bitmap(image);}

        private MediaProbeInfo Probe(string path,string dir,CancellationToken cancel)
        {
            var json=new StringBuilder();
            int rc=NativeLibraryTools.RunProcessEnvironment(ffprobe,new[]{"-v","quiet","-select_streams","v:0",
                "-show_entries","stream=index,codec_type,codec_name,width,height,pix_fmt,field_order,color_range,color_space,color_transfer,color_primaries:format=format_name",
                "-of","json",path},dir,null,7000,cancel,line=>{lock(json)json.AppendLine(line);});
            if(rc!=0)throw new IOException("No readable video header.");
            var media=MediaProbeInfo.Parse(path,json.ToString());
            if(!media.HasVideo)throw new IOException("No readable video stream.");
            return media;
        }

        internal static string FrameFilter(MediaProbeInfo media)
        {
            // Apply the same SDR display transform to PQ/HLG on either side. Use
            // the stream tags of each actual file, so an SDR encode is never tone mapped again.
            string filter=media.IsHdr?HdrColorProcessingCore.DisplayToSdr(media,"hable")+",":"";
            string deinterlace = media.IsInterlaced
                ? SharedHdrPreprocessCore.ResolveDeinterlaceParity("bwdif=mode=send_frame:parity=auto:deint=all,",media)
                : "";
            return deinterlace+filter+"scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic:out_range=full,setsar=1,format=rgb24";
        }

        private double ReadFrame(string path,MediaProbeInfo media,double seconds,string destination,string dir,CancellationToken cancel)
        {
            var log=new StringBuilder();
            double frameOffset=0;
            int result=NativeLibraryTools.RunProcessEnvironment(ffmpeg,new[]{"-hide_banner","-nostdin","-v","info","-y",
                "-filter_threads","1","-threads","2","-ss",Math.Max(0,seconds).ToString("0.000",CultureInfo.InvariantCulture),
                "-i",path,"-map","0:v:0","-frames:v","1","-an","-sn","-dn","-vf","showinfo,"+FrameFilter(media),
                "-threads","1",destination},dir,null,10000,cancel,line=>
                {
                    lock(log)
                    {
                        if(line.Contains("Parsed_showinfo"))
                        {
                            var match=Regex.Match(line,@"\bn:\s*0\b.*?\bpts_time:([0-9.eE+-]+)",RegexOptions.CultureInvariant);
                            double value;
                            if(match.Success && double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out value))
                                frameOffset=value;
                        }
                        if(!line.StartsWith("[Parsed_showinfo",StringComparison.Ordinal) && log.Length<1600)
                            log.AppendLine(line);
                    }
                });
            if(result!=0 || !File.Exists(destination))throw new IOException(log.Length>0?log.ToString().Trim():"Frame not yet available.");
            return Math.Max(0,seconds+frameOffset);
        }
    }

    internal sealed class OutputComparisonCanvas : Control
    {
        private Bitmap left,right;
        private readonly string original,final;
        private float divider=0.5f;
        private Rectangle viewport;
        internal bool SideBySide;
        internal bool HasPair {get{return left!=null && right!=null;}}

        internal OutputComparisonCanvas(string original,string final)
        {
            this.original=original;this.final=final;
            BackColor=Color.Black;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|
                ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        protected override void OnPaintBackground(PaintEventArgs e) {e.Graphics.Clear(Color.Black);}

        internal void SetFrames(Bitmap a,Bitmap b)
        {
            if(left!=null)left.Dispose();if(right!=null)right.Dispose();
            left=a;right=b;Invalidate();
        }

        internal void MoveSplit(float delta) {if(!SideBySide)SetSplit(divider+delta);}
        private void SetSplit(float value){divider=Math.Max(0,Math.Min(1,value));Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Left && !SideBySide && viewport.Contains(e.Location))
            {Capture=true;SetSplit((e.X-viewport.X)/(float)viewport.Width);}
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor=!SideBySide && viewport.Contains(e.Location)?Cursors.VSplit:Cursors.Default;
            if(Capture && e.Button==MouseButtons.Left && viewport.Width>0)SetSplit((e.X-viewport.X)/(float)viewport.Width);
        }
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);Capture=false;}

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g=e.Graphics;
            g.InterpolationMode=InterpolationMode.HighQualityBilinear;
            var area=ClientRectangle;
            if(left!=null && right!=null)
            {
                if(SideBySide)
                {
                    viewport=Rectangle.Empty;
                    int mid=area.Width/2;
                    g.DrawImage(left,Fit(left,new Rectangle(0,0,Math.Max(0,mid-2),area.Height)));
                    g.DrawImage(right,Fit(right,new Rectangle(mid+2,0,Math.Max(0,area.Width-mid-2),area.Height)));
                }
                else
                {
                    viewport=Fit(left,area);
                    int x=viewport.Left+(int)(viewport.Width*divider);
                    g.DrawImage(left,viewport);
                    var state=g.Save();
                    g.SetClip(new Rectangle(x,viewport.Top,viewport.Right-x,viewport.Height),CombineMode.Intersect);
                    var output=Fit(right,viewport);
                    g.FillRectangle(Brushes.Black,viewport);
                    g.DrawImage(right,output);
                    g.Restore(state);
                    g.DrawLine(Pens.White,x,viewport.Top,x,viewport.Bottom);
                    int y=viewport.Top+viewport.Height/2;
                    g.FillEllipse(Brushes.White,x-9,y-9,18,18);
                    g.DrawLines(Pens.Black,new[]{new Point(x-3,y-3),new Point(x-6,y),new Point(x-3,y+3)});
                    g.DrawLines(Pens.Black,new[]{new Point(x+3,y-3),new Point(x+6,y),new Point(x+3,y+3)});
                }
            }
            DrawLabel(g,original,false);
            DrawLabel(g,final,true);
        }

        private void DrawLabel(Graphics g,string text,bool rightSide)
        {
            var size=TextRenderer.MeasureText(text,Font);
            var bounds=new Rectangle(rightSide?Width-size.Width-16:8,8,size.Width+8,size.Height+6);
            using(var brush=new SolidBrush(Color.FromArgb(185,0,0,0)))g.FillRectangle(brush,bounds);
            TextRenderer.DrawText(g,text,Font,bounds,Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        }

        private static Rectangle Fit(Image image,Rectangle area)
        {
            double scale=Math.Min(area.Width/(double)image.Width,area.Height/(double)image.Height);
            var size=new Size((int)(image.Width*scale),(int)(image.Height*scale));
            return new Rectangle(area.X+(area.Width-size.Width)/2,area.Y+(area.Height-size.Height)/2,size.Width,size.Height);
        }
    }

    internal sealed partial class MainForm
    {
        private Button btnOutputPreview;
        private BridgeTaskRequest outputPreviewTask;
        private OutputPreviewForm outputPreviewWindow;

        private void ShowOutputPreview()
        {
            if(outputPreviewWindow!=null && !outputPreviewWindow.IsDisposed)
            {outputPreviewWindow.Activate();return;}
            string ffmpeg=Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe");
            outputPreviewWindow=new OutputPreviewForm(()=>outputPreviewTask,ffmpeg,lang.Code=="zh-CN");
            var window=outputPreviewWindow;
            window.FormClosed+=delegate
            {
                if(outputPreviewWindow!=window)return;
                outputPreviewWindow=null;
                SetOutputPreviewHighlight(false);
            };
            AttachOwnedTheme(window,this);
            window.Show(this);
            SetOutputPreviewHighlight(true);
        }
        private void SetOutputPreviewHighlight(bool enabled)
        {
            if(btnOutputPreview==null||btnOutputPreview.IsDisposed)return;
            btnOutputPreview.BackColor=enabled?Color.FromArgb(32,132,87):SystemColors.Control;
            btnOutputPreview.ForeColor=enabled?Color.White:SystemColors.ControlText;
            btnOutputPreview.FlatStyle=enabled?FlatStyle.Flat:FlatStyle.Standard;
            btnOutputPreview.UseVisualStyleBackColor=!enabled;
            if(uiTheme!=null)uiTheme.CorrectionState(btnOutputPreview,enabled);
        }
    }
}
