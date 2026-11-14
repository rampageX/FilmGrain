using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal sealed partial class NativeColorCorrectionForm
    {
        private Panel gradePanel;
        private Label referenceLabel;
        private Button matchButton, clearReferenceButton;
        private Panel stylePanel;
        private Button styleTabButton, clearStyleReferenceButton;
        private Label styleReferenceLabel;
        private readonly TrackBar[] styleTracks = new TrackBar[3];
        private string styleReferenceImage;
        private StyleTransferStyle activeStyle;
        private readonly TrackBar[] gradeTracks = new TrackBar[ColorGrade.Count];
        private readonly NumericUpDown[] gradeNumbers = new NumericUpDown[ColorGrade.Count];
        private readonly ToolTip gradeTips = new ToolTip();
        private string referenceImage;
        private int matchStrength;
        internal int MatchStrength {get{return matchStrength;}}
        private bool updatingGrade;
        private int gradeRevision;
        private CancellationTokenSource matchCancellation;
        private bool matchingStyle;

        private string FilmLutFolder { get { return Path.Combine(lutRoot,"FGS_FilmLUT"); } }

        private void BuildGradePanel(Control parent)
        {
            gradePanel = new Panel { Location = new Point(6,19), Size = new Size(440,477) };
            parent.Controls.Add(gradePanel);
            gradePanel.Controls.Add(new ThemeLabel { Text=S("光线","Light"), Location=new Point(4,0), Size=new Size(245,17) });
            gradePanel.Controls.Add(new ThemeLabel { Text=S("色彩","Color"), Location=new Point(4,214), Size=new Size(245,17) });
            string[] zh = {"曝光 EV","对比度","高光","阴影","白色色阶","黑色色阶","中间调","色温","色调","色相偏移","自然饱和度","饱和度"};
            string[] en = {"Exposure EV","Contrast","Highlights","Shadows","Whites","Blacks","Midtones","Temperature","Tint","Hue shift","Vibrance","Saturation"};
            for(int i=0;i<ColorGrade.Count;i++)
                AddGradeRow(i,S(zh[i],en[i]),i<7?18+28*i:232+28*(i-7));

            referenceLabel = new ThemeLabel {Location=new Point(4,374),Size=new Size(425,21),AutoEllipsis=true};
            gradePanel.Controls.Add(referenceLabel);
            AddGradeButton(S("参考图片…","Reference…"),4,397,104,ChooseGradeReference);
            matchButton=AddGradeButton("",209,397,200,EditMatchStrength);
            clearReferenceButton=AddGradeButton(S("清空","Clear"),116,397,85,delegate
            {
                if(matchCancellation!=null)matchCancellation.Cancel();
                referenceImage=null;
                UpdateReferenceUi();
            });
            AddGradeButton(S("重置","Reset"),4,432,92,delegate { ResetColorControls(); RequestRender(true); });
            AddGradeButton(S("导出 LUT…","Export LUT…"),104,432,100,delegate { SaveGradeCube(33); });
            AddGradeButton(S("导入 .cube…","Import .cube…"),212,432,94,ImportGradeCube);
            AddGradeButton(S("旧版色彩纠正","Legacy correction"),314,432,120,delegate {SwitchAdjustmentPage(false);});
            FormClosed+=delegate {gradeTips.Dispose();};
            BuildStylePanel(parent);
            UpdateReferenceUi();
        }

        private Button AddGradeButton(string caption,int x,int y,int width,Action action,Control panel=null)
        {
            var button=new ThemeButton {Text=caption,Location=new Point(x,y),Size=new Size(width,30)};
            if(action!=null)button.Click+=delegate {action();};
            (panel ?? gradePanel).Controls.Add(button);
            return button;
        }

        private void BuildStylePanel(Control parent)
        {
            stylePanel = new Panel { Location = gradePanel.Location, Size = gradePanel.Size, Visible = false };
            parent.Controls.Add(stylePanel);
            styleReferenceLabel = new ThemeLabel { Location = new Point(4,8), Size = new Size(425,22), AutoEllipsis = true };
            stylePanel.Controls.Add(styleReferenceLabel);
            AddGradeButton(S("参考图片…","Reference…"),4,36,104,delegate { ChooseStyleReference(true); },stylePanel);
            clearStyleReferenceButton = AddGradeButton(S("清空","Clear"),116,36,85,delegate
            {
                if (matchCancellation != null) matchCancellation.Cancel();
                styleReferenceImage = null;
                UpdateReferenceUi();
            },stylePanel);
            string[] labels = { S("总强度","Intensity"), S("色彩匹配","Color Grade"), S("明暗曲线","Tone Curve") };
            for (int i = 0; i < styleTracks.Length; i++)
            {
                int y = 88 + 75 * i;
                int defaultValue = i == 0 ? 35 : 100;
                var label = new ThemeLabel { Text = labels[i], Location = new Point(4,y), Size = new Size(282,24) };
                var value = new ThemeLabel { Location = new Point(320,y), Size = new Size(102,24), TextAlign = ContentAlignment.MiddleRight };
                var track = new ThemeTrackBar { AutoSize = false, Minimum = 0, Maximum = 200,
                    Value = defaultValue, TickStyle = TickStyle.None, LargeChange = 10,
                    Location = new Point(4,y + 27), Size = new Size(396,29), AccessibleName = labels[i] };
                styleTracks[i] = track;
                value.Text = track.Value + "%";
                track.ValueChanged += delegate
                {
                    value.Text = track.Value + "%";
                    if (updatingGrade || activeStyle == null) return;
                    activeStyle = activeStyle.WithControls(styleTracks[0].Value,styleTracks[1].Value,styleTracks[2].Value);
                    RequestRender(false);
                };
                var reset = SliderReset(labels[i],420,y + 27,delegate { track.Value = defaultValue; });
                stylePanel.Controls.AddRange(new Control[] { label,value,track,reset });
            }
            AddGradeButton(S("移除风格","Remove style"),4,331,108,delegate
            {
                if (matchCancellation != null) matchCancellation.Cancel();
                activeStyle = null;
                gradeRevision++;
                RequestRender(true);
            },stylePanel);
            AddGradeButton(S("重置","Reset"),120,331,85,delegate { ResetStyleControls(false); RequestRender(true); },stylePanel);
            AddGradeButton(S("导出 LUT…","Export LUT…"),213,331,112,
                delegate { SaveGradeCube(33); },stylePanel);
        }

        private void ResetStyleControls(bool removeStyle)
        {
            updatingGrade = true;
            try
            {
                styleTracks[0].Value = 35;
                styleTracks[1].Value = 100;
                styleTracks[2].Value = 100;
                if (removeStyle) activeStyle = null;
                else if (activeStyle != null) activeStyle = activeStyle.WithControls(35,100,100);
            }
            finally { updatingGrade = false; }
        }

        private Button SliderReset(string caption, int x, int y, Action resetValue)
        {
            var reset = new ThemeButton { Text="↺", Location=new Point(x,y), Size=new Size(26,26),
                AccessibleName=S("重置 ","Reset ")+caption };
            gradeTips.SetToolTip(reset,reset.AccessibleName);
            reset.Click += delegate { resetValue(); };
            return reset;
        }

        private void ShowStylePage()
        {
            SwitchAdjustmentPage(false);
            foreach (Control control in colorPageControls) control.Visible = false;
            colorTabButton.Text = S("色彩纠正","Color correction");
            styleTabButton.Text = "● " + S("风格迁移","Style Transfer");
            adjustmentGroup.Text = S("风格迁移","Style Transfer");
            stylePanel.Visible = true;
        }

        private void AddGradeRow(int index,string caption,int y)
        {
            int limit=ColorGrade.Limit(index);
            decimal step=index==0?0.05M:1M;
            var label=new ThemeLabel {Text=caption,Location=new Point(4,y),Size=new Size(94,26),TextAlign=ContentAlignment.MiddleLeft};
            var track=new ThemeTrackBar {AutoSize=false,TickStyle=TickStyle.None,Minimum=-limit,Maximum=limit,
                Location=new Point(100,y),Size=new Size(238,26),SmallChange=1,LargeChange=index==0?4:10};
            var number=new ThemeNumericUpDown {DecimalPlaces=index==0?2:0,Increment=step,Minimum=-limit*step,
                Maximum=limit*step,Location=new Point(344,y),Size=new Size(60,26),TextAlign=HorizontalAlignment.Right};
            var reset=SliderReset(caption,408,y,delegate { ChangeGradeRow(index,0); });
            gradeTips.SetToolTip(track,caption);
            track.AccessibleName=number.AccessibleName=caption;
            gradeTracks[index]=track; gradeNumbers[index]=number;
            track.ValueChanged+=delegate {ChangeGradeRow(index,track.Value);};
            number.ValueChanged+=delegate {ChangeGradeRow(index,(int)Math.Round(number.Value/step,MidpointRounding.AwayFromZero));};
            gradePanel.Controls.AddRange(new Control[]{label,track,number,reset});
        }

        private void ChangeGradeRow(int index,int value)
        {
            if(updatingGrade)return;
            updatingGrade=true;
            try {SetGradeRow(index,value);}
            finally {updatingGrade=false;}
            checkEnable.Checked=false;
            gradeRevision++;
            RequestRender(false);
        }

        private void SetGradeRow(int index,int value)
        {
            gradeTracks[index].Value=value;
            gradeNumbers[index].Value=value*gradeNumbers[index].Increment;
        }

        private ColorGrade CaptureGrade()
        {
            var grade=new ColorGrade { Style = activeStyle };
            for(int i=0;i<ColorGrade.Count;i++)grade.Values[i]=gradeTracks[i].Value;
            return grade;
        }

        private void SetGrade(ColorGrade grade)
        {
            updatingGrade=true;
            try
            {
                activeStyle = grade.Style;
                if (activeStyle != null)
                {
                    styleTracks[0].Value = activeStyle.Intensity;
                    styleTracks[1].Value = activeStyle.Color;
                    styleTracks[2].Value = activeStyle.Finish;
                }
                for(int i=0;i<ColorGrade.Count;i++)SetGradeRow(i,grade.Values[i]);
            }
            finally {updatingGrade=false;}
            gradeRevision++;
        }

        private void ShowGradePage()
        {
            SwitchAdjustmentPage(false);
            foreach(Control control in colorPageControls)control.Visible=false;
            gradePanel.Visible=true;
            adjustmentGroup.Text=S("12 参数调色","12-parameter color grade");
        }

        private void UpdateReferenceUi()
        {
            bool available=!string.IsNullOrEmpty(referenceImage);
            referenceLabel.Text=available?Path.GetFileName(referenceImage):S("未选择参考图片","No reference image selected");
            gradeTips.SetToolTip(referenceLabel,referenceImage??"");
            matchButton.Text=S("自定义匹配强度","Match strength")+" ("+matchStrength+"%)";
            if(matchCancellation!=null && available && !matchingStyle)referenceLabel.Text+=S(" · 匹配中…"," · Matching…");
            clearReferenceButton.Enabled=available;
            bool styleAvailable = !string.IsNullOrEmpty(styleReferenceImage);
            styleReferenceLabel.Text = styleAvailable ? Path.GetFileName(styleReferenceImage) : S("未选择参考图片","No reference image selected");
            if (styleAvailable && matchCancellation != null && matchingStyle)
                styleReferenceLabel.Text += S(" · 匹配中…"," · Matching…");
            clearStyleReferenceButton.Enabled = styleAvailable;
            gradeTips.SetToolTip(styleReferenceLabel,styleReferenceImage ?? "");
        }

        private void ChooseGradeReference() { ChooseStyleReference(false); }

        private void ChooseStyleReference(bool styleTransfer)
        {
            using(var dialog=new OpenFileDialog {
                Title=S("选择参考图片","Choose reference image"),
                Filter=S("图片","Images")+"|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.avif|"+S("所有文件","All files")+"|*.*" })
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                if(matchCancellation!=null)matchCancellation.Cancel();
                if (styleTransfer) styleReferenceImage = dialog.FileName;
                else referenceImage = dialog.FileName;
                MatchGradeReference(styleTransfer);
            }
        }

        private string BaseLut(RenderState state)
        {return state.FilmSettings==null?state.CompatLutPath:FilmCubeForPreview(state.FilmSettings);}

        private FilmLutCube ReadGradeBase(RenderState state)
        {
            if(!state.UseLut)return null;
            if(state.FilmSettings==null)return FilmLutCube.Read(state.CompatLutPath,true);
            string path=Path.Combine(Path.GetTempPath(),"FGS_film_"+Guid.NewGuid().ToString("N")+".cube");
            try
            {
                var full=state.FilmSettings.Copy();
                full.Strength=100;
                FilmLutCore.WriteCube(path,FilmLutCube.Read(FilmLutCore.ProfilePath(galleryAppRoot,full.ProfileId)),full);
                return FilmLutCube.Read(path);
            }
            finally {File.Delete(path);}
        }

        private void ImportGradeCube()
        {
            using(var dialog=new OpenFileDialog {Filter="3D LUT (*.cube)|*.cube",InitialDirectory=lutRoot})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    FilmLutCube.Read(dialog.FileName,true);
                    filmUiSync=true;
                    syncing=true;
                    try
                    {
                        filmModeActive=false;
                        checkFilm.Checked=false;
                        PrepareLut(dialog.FileName);
                        if(string.IsNullOrEmpty(activeCompatLutPath))throw new IOException("Could not prepare selected LUT.");
                        lutSource="External";
                        lutStrengthTrack.Value=100;
                        checkLut.Checked=true;
                    }
                    finally {syncing=false;filmUiSync=false;}
                    UpdateLutUi();
                    RequestRender(true);
                }
                catch(Exception ex){FgsMessageBox.Show(this,ex.Message);}
            }
        }

        private async void SaveGradeCube(int size)
        {
            RenderState state=CaptureRenderState();
            if(state.Grade==null&&state.Enabled)
            {
                FgsMessageBox.Show(this,S("旧版色彩纠正仍在生效。请关闭旧版纠正，或调整任一新参数后导出。","Legacy correction is active. Disable it or adjust a new parameter before exporting."));
                return;
            }
            GradeSignal signal=ColorGradeCore.Signal(preserveHdrColor?hdrPreviewInfo:null);
            string path;
            string folder=FilmLutFolder;
            try {Directory.CreateDirectory(folder);}
            catch(Exception ex){FgsMessageBox.Show(this,ex.Message);return;}
            using(var dialog=new SaveFileDialog {Title=S("导出 LUT · ","Export LUT · ")+ColorGradeCore.SignalName(signal),
                Filter="3D LUT (*.cube)|*.cube",DefaultExt="cube",AddExtension=true,OverwritePrompt=true,
                InitialDirectory=folder,FileName="FGS_Grade_"+signal+"_"+size.ToString(CultureInfo.InvariantCulture)+".cube"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                path=dialog.FileName;
            }
            try
            {
                await Task.Run(delegate
                {
                    FilmLutCube cube=ReadGradeBase(state);
                    var transform=new ColorGradeTransform(state.Grade??new ColorGrade(),signal,cube,state.LutStrength);
                    ColorGradeCore.WriteCube(path,size,signal,transform);
                });
                if(!closing)status.Text=S("已导出 ","Exported ")+Path.GetFileName(path)+" · "+ColorGradeCore.SignalName(signal);
            }
            catch(Exception ex){if(!closing)FgsMessageBox.Show(this,ex.Message);}
        }

        private bool GradeBaseChanged(RenderState original)
        {
            var current=CaptureRenderState();
            if(current.UseLut!=original.UseLut||current.LutStrength!=original.LutStrength||
                current.LutVersion!=original.LutVersion)return true;
            var a=current.FilmSettings;var b=original.FilmSettings;
            if(a==null||b==null)return a!=b;
            return a.ProfileId!=b.ProfileId||a.ExposureTenths!=b.ExposureTenths||
                a.Warmth!=b.Warmth||a.Saturation!=b.Saturation;
        }

        private void EditMatchStrength()
        {
            using(var dialog=new SettingsDialogForm {Text=S("自定义匹配强度","Match strength"),Font=Font,
                ClientSize=new Size(280,110),StartPosition=FormStartPosition.CenterParent,
                FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false})
            {
                var value=new ThemeNumericUpDown {Minimum=0,Maximum=100,Value=matchStrength,
                    Location=new Point(20,20),Size=new Size(100,26),AccessibleName=S("匹配强度百分比","Match strength percent")};
                var percent=new ThemeLabel {Text="%",Location=new Point(126,22),AutoSize=true};
                var ok=new ThemeButton {Text=S("确定","OK"),Location=new Point(170,62),Size=new Size(90,28),DialogResult=DialogResult.OK};
                dialog.Controls.AddRange(new Control[]{value,percent,ok});
                dialog.AcceptButton=ok;
                MainForm.AttachOwnedTheme(dialog,this);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                matchStrength=Decimal.ToInt32(value.Value);
                if(string.IsNullOrEmpty(referenceImage))UpdateReferenceUi();
                else MatchGradeReference();
            }
        }

        private async void MatchGradeReference(bool styleTransfer = false)
        {
            string image = styleTransfer ? styleReferenceImage : referenceImage;
            if(string.IsNullOrEmpty(image))return;
            if(matchCancellation!=null)matchCancellation.Cancel();
            int amount=matchStrength;
            if(!styleTransfer && amount==0)
            {
                matchCancellation=null;
                checkEnable.Checked=false;
                SetGrade(new ColorGrade { Style = activeStyle });
                UpdateReferenceUi();
                RequestRender(true);
                return;
            }
            var cancellation=new CancellationTokenSource();
            matchCancellation=cancellation;
            matchingStyle = styleTransfer;
            RenderState state=CaptureRenderState();
            int revision=gradeRevision;
            GradeSignal signal=ColorGradeCore.Signal(preserveHdrColor?hdrPreviewInfo:null);
            string working=Path.Combine(Path.GetTempPath(),"FGS_Match_"+Guid.NewGuid().ToString("N"));
            UpdateReferenceUi();

            try
            {
                ColorGrade grade=await Task.Run(delegate
                {
                    Directory.CreateDirectory(working);
                    try
                    {
                        sourceIdentity.RequireCurrent();
                        var source=ReadGradePixels(videoPath,state.Position,signal,true,working,cancellation.Token);
                        var reference=ReadGradePixels(image,0,GradeSignal.Bt709,false,working,cancellation.Token);
                        var result = styleTransfer
                            ? StyleTransferStyle.Match(source,reference,signal,100,cancellation.Token)
                            : ColorGradeCore.Match(source,reference,signal,ReadGradeBase(state),state.LutStrength,cancellation.Token,amount);
                        sourceIdentity.RequireCurrent();
                        return result;
                    }
                    finally {Directory.Delete(working,true);}
                });
                if(closing||cancellation.IsCancellationRequested)return;
                if(revision!=gradeRevision||state.Position!=CurrentTime()||GradeBaseChanged(state))
                {
                    status.Text=S("选帧或调色已变更，请重新匹配。","Frame or grade changed. Match again.");
                    return;
                }
                checkEnable.Checked=false;
                if (styleTransfer)
                {
                    var matchedStyle = grade.Style;
                    grade = CaptureGrade();
                    grade.Style = matchedStyle.WithControls(styleTracks[0].Value,styleTracks[1].Value,styleTracks[2].Value);
                }
                else grade.Style = activeStyle;
                SetGrade(grade);
                RequestRender(true);
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(!closing&&!cancellation.IsCancellationRequested)FgsMessageBox.Show(this,S("参考图匹配失败 · ","Reference match failed · ")+ex.Message);}
            finally
            {
                if(matchCancellation==cancellation)
                {
                    matchCancellation=null;
                    if(!closing)UpdateReferenceUi();
                }
                cancellation.Dispose();
            }
        }

        private GradeRgb[] ReadGradePixels(string input,double position,GradeSignal signal,bool video,
            string working,CancellationToken cancel)
        {
            const int side=40;
            string output=Path.Combine(working,video?"source.rgb":"reference.rgb");
            var args=new List<string>{"-hide_banner","-v","error","-nostdin","-y"};
            if(video)args.AddRange(new[]{"-ss",position.ToString("0.000",CultureInfo.InvariantCulture)});
            args.AddRange(new[]{"-i",input,"-map","0:v:0","-an","-sn","-dn","-frames:v","1"});
            string filter="scale=40:40:flags=area";
            if(video&&hdrPreviewInfo!=null)
            {
                if(signal==GradeSignal.Bt709)filter=HdrColorProcessingCore.DisplayToSdr(hdrPreviewInfo,previewTonemap)+","+filter;
                else
                {
                    filter+=","+HdrColorProcessingCore.RgbInput(hdrPreviewInfo);
                }
            }
            args.AddRange(new[]{"-vf",filter+",format=rgb48le","-threads","1","-f","rawvideo",output});
            var errors=new System.Text.StringBuilder();
            int result=NativeLibraryTools.RunProcessEnvironment(ffmpegPath,args,working,null,30000,cancel,
                delegate(string line){lock(errors){if(errors.Length<1600)errors.AppendLine(line);}});
            cancel.ThrowIfCancellationRequested();
            if(result!=0||!File.Exists(output)||new FileInfo(output).Length!=side*side*6)
                throw new InvalidDataException(errors.Length>0?errors.ToString():"Could not decode image.");
            byte[] bytes=File.ReadAllBytes(output);
            var pixels=new GradeRgb[side*side];
            for(int i=0;i<pixels.Length;i++)
            {
                int offset=i*6;
                double r=(bytes[offset]+256*bytes[offset+1])/65535.0;
                double g=(bytes[offset+2]+256*bytes[offset+3])/65535.0;
                double b=(bytes[offset+4]+256*bytes[offset+5])/65535.0;
                pixels[i]=video?new GradeRgb(r,g,b):new GradeRgb(ColorGradeTransform.SrgbTo709(r),
                    ColorGradeTransform.SrgbTo709(g),ColorGradeTransform.SrgbTo709(b));
            }
            return pixels;
        }
    }
}
