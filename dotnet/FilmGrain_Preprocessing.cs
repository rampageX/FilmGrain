using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    // A CPU-frame prefix shared by all six encoders and preview preparation.
    // No timestamp, range, transfer or output pixel-format changes belong here.
    internal static class PreprocessingCore
    {
        internal static readonly string[] Keys={"PRE_BILATERAL_ENABLED","PRE_BILATERAL_SIGMA_S","PRE_BILATERAL_SIGMA_R","PRE_BILATERAL_PLANES"};
        internal static readonly string[] Defaults={"false","3","0.05","1"};
        static readonly object Sync=new object();
        static readonly System.Collections.Generic.HashSet<string> Verified=new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        static string Read(System.Collections.Generic.IDictionary<string,string> state,string key,string fallback)
        {string value;return state.TryGetValue("FG_"+key,out value)?value:fallback;}
        internal static bool Enabled(System.Collections.Generic.IDictionary<string,string> state)
        {string value=Read(state,Keys[0],"false");return string.Equals(value,"true",StringComparison.OrdinalIgnoreCase)||value=="1";}
        internal static bool TryBuild(System.Collections.Generic.IDictionary<string,string> state,out string filter,out string reason)
        {
            filter="";reason="";if(!Enabled(state))return true;
            double spatial,range;int planes;
            if(!double.TryParse(Read(state,Keys[1],"3"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out spatial)||double.IsNaN(spatial)||double.IsInfinity(spatial)||spatial<0||spatial>512 ||
               !double.TryParse(Read(state,Keys[2],"0.05"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out range)||double.IsNaN(range)||double.IsInfinity(range)||range<0||range>1 ||
               !int.TryParse(Read(state,Keys[3],"1"),System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out planes)||planes<0||planes>15)
            {reason="Bilateral 参数无效：sigmaS 0–512，sigmaR 0–1，planes 0–15。";return false;}
            filter="format=yuv420p10le,bilateral=sigmaS="+spatial.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":sigmaR="+range.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":planes="+planes.ToString(System.Globalization.CultureInfo.InvariantCulture)+",";
            return true;
        }
        internal static bool TryCheckTool(string ffmpeg,out string reason)
        {
            reason="";
            try {
                var file=new System.IO.FileInfo(ffmpeg);
                string identity=file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;
                lock(Sync)if(Verified.Contains(identity))return true;
                string output,detail;
                bool ok=SharedFrucCore.Run(ffmpeg,"-hide_banner -nostdin -v error -f lavfi -i color=s=32x32:d=0.04 -vf format=yuv420p10le,bilateral=sigmaS=3:sigmaR=0.05:planes=1 -frames:v 1 -f null -",10000,out output,out detail);
                if(!ok){reason="此 FFmpeg 未通过 CPU bilateral 10-bit 检查："+ffmpeg+" / "+detail+" / "+output;return false;}
                lock(Sync)Verified.Add(identity);return true;
            }catch(System.Exception ex){reason="CPU bilateral 检查失败："+ex.Message;return false;}
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private Dictionary<string,string> PreprocessEnvironment()
        {var state=new Dictionary<string,string>();foreach(string key in PreprocessingCore.Keys)state["FG_"+key]=config.Get(key);return state;}
        private void RefreshPreprocessButton()
        {
            if(btnPreprocess!=null)btnPreprocess.Text=UiText("前处理","Preprocessing")+(config.GetBool("PRE_BILATERAL_ENABLED")?" [bilateral]":"...");
        }
        private void ShowPreprocessDialog()
        {
            using(var dialog=new Form()) {
                dialog.Text=UiText("前处理 · CPU Bilateral","Preprocessing · CPU Bilateral");dialog.Font=Font;
                dialog.AutoScaleMode=AutoScaleMode.Dpi;dialog.StartPosition=FormStartPosition.CenterParent;
                dialog.Size=new Size(660,390);dialog.MinimumSize=new Size(620,370);
                var grid=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=2,RowCount=6};
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
                for(int i=0;i<3;i++)grid.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
                var enabled=new ThemeCheckBox {Name="BILATERAL_ENABLED",Text=UiText("启用 Bilateral（CPU 双边滤波）","Enable Bilateral (CPU)"),Dock=DockStyle.Fill,Checked=config.GetBool("PRE_BILATERAL_ENABLED")};
                grid.Controls.Add(enabled,0,0);grid.SetColumnSpan(enabled,2);
                var spatial=new ThemeTextBox {Name="BILATERAL_SIGMA_S",Dock=DockStyle.Fill,Text=config.Get("PRE_BILATERAL_SIGMA_S")};
                var range=new ThemeTextBox {Name="BILATERAL_SIGMA_R",Dock=DockStyle.Fill,Text=config.Get("PRE_BILATERAL_SIGMA_R")};
                var planes=new ThemeIntegerCombo(0,15,1,new int[]{1,2,4,6,7,15},false) {Name="BILATERAL_PLANES",Dock=DockStyle.Fill};
                int planeValue;if(int.TryParse(config.Get("PRE_BILATERAL_PLANES"),out planeValue)&&planeValue>=0&&planeValue<=15)planes.Value=planeValue;
                grid.Controls.Add(new ThemeLabel {Text=UiText("sigmaS · 空间平滑范围（0–512）","sigmaS · Spatial range (0–512)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);grid.Controls.Add(spatial,1,1);
                grid.Controls.Add(new ThemeLabel {Text=UiText("sigmaR · 亮度差异容忍度（0–1）","sigmaR · Range tolerance (0–1)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);grid.Controls.Add(range,1,2);
                grid.Controls.Add(new ThemeLabel {Text=UiText("planes · 通道位掩码（0–15）","planes · Plane bitmask (0–15)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);grid.Controls.Add(planes,1,3);
                var help=new ThemeLabel {Dock=DockStyle.Fill,Text=UiText("默认 3 / 0.05 / 1：仅柔化亮度通道。sigmaS 越大影响范围越广；sigmaR 越大越容易抹平纹理。\nplanes：1=Y，2=U，4=V，6=UV，7=YUV；0=不处理。CPU 滤镜可能降低处理速度；GPU 解码仍按原设置运行。","Default 3 / 0.05 / 1: luma only. Larger sigmaS widens smoothing; larger sigmaR removes more texture.\nplanes: 1=Y, 2=U, 4=V, 6=UV, 7=YUV; 0=none. CPU filtering may reduce speed; GPU decode remains available.")};
                grid.Controls.Add(help,0,4);grid.SetColumnSpan(help,2);
                var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                var cancel=new ThemeButton {Name="PREPROCESS_CANCEL",Text=UiText("取消","Cancel"),DialogResult=DialogResult.Cancel,Width=88};
                var ok=new ThemeButton {Name="PREPROCESS_OK",Text=UiText("确定","OK"),Width=88};var reset=new ThemeButton {Name="PREPROCESS_DEFAULTS",Text=UiText("恢复默认值","Defaults"),Width=118};
                reset.Click+=delegate {enabled.Checked=false;spatial.Text="3";range.Text="0.05";planes.Value=1;};
                enabled.CheckedChanged+=delegate {spatial.Enabled=range.Enabled=planes.Enabled=enabled.Checked;};
                spatial.Enabled=range.Enabled=planes.Enabled=enabled.Checked;
                ok.Click+=delegate {
                    if(!planes.ValidateInput())return;
                    var values=new Dictionary<string,string> { {"PRE_BILATERAL_ENABLED",enabled.Checked?"true":"false"},{"PRE_BILATERAL_SIGMA_S",spatial.Text.Trim()},{"PRE_BILATERAL_SIGMA_R",range.Text.Trim()},{"PRE_BILATERAL_PLANES",planes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} };
                    try {
                        foreach(var pair in values)PresetCore.ValidateConfig(pair.Key,pair.Value);
                        config.Save(values);RefreshPreprocessButton();dialog.DialogResult=DialogResult.OK;dialog.Close();
                    }catch(Exception ex){MessageBox.Show(dialog,ex.Message,dialog.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
                };
                buttons.Controls.Add(cancel);buttons.Controls.Add(ok);buttons.Controls.Add(reset);grid.Controls.Add(buttons,0,5);grid.SetColumnSpan(buttons,2);
                dialog.Controls.Add(grid);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                AttachThemeWindow(dialog);AttachPreprocessWindowMemory(dialog);dialog.ShowDialog(this);
            }
        }
        private void AttachPreprocessWindowMemory(Form dialog)
        {
            bool ready=false;FormWindowState remembered=FormWindowState.Normal;
            dialog.Load+=delegate {
                if(!SuppressWindowMemory)try {
                    string[] parts=config.Get("PREPROCESS_WINDOW").Split(',');int x,y,w,h;
                    if(parts.Length==5&&int.TryParse(parts[0],out x)&&int.TryParse(parts[1],out y)&&int.TryParse(parts[2],out w)&&int.TryParse(parts[3],out h)&&w>0&&h>0) {
                        var areas=new List<Rectangle>();foreach(Screen screen in Screen.AllScreens)areas.Add(screen.WorkingArea);
                        dialog.StartPosition=FormStartPosition.Manual;dialog.Bounds=FitWindowBounds(new Rectangle(x,y,w,h),areas.ToArray(),dialog.MinimumSize);
                        remembered=parts[4]=="Maximized"?FormWindowState.Maximized:FormWindowState.Normal;dialog.WindowState=remembered;
                    }
                }catch(Exception ex){System.Diagnostics.Trace.WriteLine(ex.Message);}
                ready=true;
            };
            dialog.Resize+=delegate {if(ready&&dialog.WindowState!=FormWindowState.Minimized)remembered=dialog.WindowState;};
            dialog.FormClosed+=delegate {
                if(!ready||SuppressWindowMemory)return;
                try {
                    Rectangle b=dialog.WindowState==FormWindowState.Normal?dialog.Bounds:dialog.RestoreBounds;
                    config.SaveValue("PREPROCESS_WINDOW",string.Join(",",new[]{b.X.ToString(System.Globalization.CultureInfo.InvariantCulture),b.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),b.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),b.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),remembered==FormWindowState.Maximized?"Maximized":"Normal"}));
                }catch(Exception ex){System.Diagnostics.Trace.WriteLine(ex.Message);}
            };
        }
    }
}
