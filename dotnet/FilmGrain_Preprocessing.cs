using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
        {var state=new Dictionary<string,string>();
            if(cmbCodec!=null) {
                int index=cmbCodec.SelectedIndex;
                if(index==0||index==1||index==3)state["FG_MODE"]=index==0?"AV1":index==1?"HEVC":"H264";
            }
            foreach(string key in PreprocessingCore.Keys)state["FG_"+key]=config.Get(key);
            foreach(string key in ResizeCore.Keys)state["FG_"+key]=config.Get(key);
            state["FG_CINEMATIC_FRAME"]=chkCinematic!=null&&chkCinematic.Checked?"1":"0";
            state["FG_FRAME_MODE"]=cmbFrameMode!=null&&cmbFrameMode.SelectedIndex==1?"CROP":"LETTERBOX";
            state["FG_CROP_PER_SIDE"]=config.Get("CINEMATIC_CROP_PER_SIDE");state["FG_CROP_HORIZONTAL"]=config.Get("CINEMATIC_CROP_HORIZONTAL");state["FG_NO_CROP"]=config.Get("CINEMATIC_NO_CROP");return state;}
        private void ResizeDimensionsForUi(MediaProbeInfo info,ref int width,ref int height)
        {if(info==null||config.Get("RESIZE_MODE")=="OFF")return;var state=PreprocessEnvironment();CinematicFramePlan frame;ResizePlan plan;string error;if(CinematicFrameCore.TryBuild(info,state,out frame,out error)&&ResizeCore.TryBuild(info,state,frame,out plan,out error)){width=plan.Width;height=plan.Height;}}
        private void RefreshPreprocessButton()
        {
            if(btnPreprocess==null)return;
            string tags=config.GetBool("PRE_BILATERAL_ENABLED")?" [bilateral]":"";
            string mode=config.Get("RESIZE_MODE");
            if(mode!="OFF") {
                string target=mode;
                if(mode=="CUSTOM") {
                    target=config.Get("RESIZE_FACTOR");
                    if(target.Length==0)target=config.Get("RESIZE_WIDTH")+"x"+config.Get("RESIZE_HEIGHT");
                } else if(mode=="720"||mode=="1080"||mode=="1440"||mode=="2160")target+="p";
                tags+=" [Resize "+target+"]";
            }
            btnPreprocess.Text=UiText("前处理","Preprocessing")+(tags.Length==0?"...":tags);
        }
        private void ShowPreprocessDialog()
        {
            using(var dialog=new SettingsDialogForm()) {
                dialog.Text=UiText("前处理","Preprocessing");dialog.Font=Font;
                dialog.AutoScaleMode=AutoScaleMode.Dpi;dialog.StartPosition=FormStartPosition.CenterParent;
                dialog.Size=new Size(820,450);dialog.MinimumSize=new Size(780,430);
                var shell=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(10),ColumnCount=1,RowCount=2};
                shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
                var tabs=new ThemeTabControl {Name="PREPROCESS_TABS",Dock=DockStyle.Fill};
                var filterPage=new TabPage(UiText("滤波","Filtering")) {Name="PREPROCESS_FILTER_PAGE"};
                var resizePage=new TabPage(UiText("缩放","Resize")) {Name="PREPROCESS_RESIZE_PAGE"};
                tabs.TabPages.Add(filterPage);tabs.TabPages.Add(resizePage);shell.Controls.Add(tabs,0,0);
                var grid=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=2,RowCount=5};
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
                for(int i=0;i<3;i++)grid.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                filterPage.Controls.Add(grid);
                var resizeGrid=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=2,RowCount=7};
                resizeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));resizeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));
                for(int i=0;i<6;i++)resizeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
                resizeGrid.RowStyles.Add(new RowStyle(SizeType.Percent,100));resizePage.Controls.Add(resizeGrid);
                var enabled=new ThemeCheckBox {Name="BILATERAL_ENABLED",Text=UiText("启用 Bilateral（CPU 双边滤波）","Enable Bilateral (CPU)"),Dock=DockStyle.Fill,Checked=config.GetBool("PRE_BILATERAL_ENABLED")};
                grid.Controls.Add(enabled,0,0);grid.SetColumnSpan(enabled,2);
                var spatial=new ThemeTextBox {Name="BILATERAL_SIGMA_S",Dock=DockStyle.Fill,Text=config.Get("PRE_BILATERAL_SIGMA_S")};
                var range=new ThemeTextBox {Name="BILATERAL_SIGMA_R",Dock=DockStyle.Fill,Text=config.Get("PRE_BILATERAL_SIGMA_R")};
                var planes=new ThemeIntegerCombo(0,15,1,new int[]{1,2,4,6,7,15},false) {Name="BILATERAL_PLANES",Dock=DockStyle.Fill};
                int planeValue;if(int.TryParse(config.Get("PRE_BILATERAL_PLANES"),out planeValue)&&planeValue>=0&&planeValue<=15)planes.Value=planeValue;
                grid.Controls.Add(new ThemeLabel {Text=UiText("sigmaS · 空间平滑范围（0–512）","sigmaS · Spatial range (0–512)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);grid.Controls.Add(spatial,1,1);
                grid.Controls.Add(new ThemeLabel {Text=UiText("sigmaR · 亮度差异容忍度（0–1）","sigmaR · Range tolerance (0–1)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);grid.Controls.Add(range,1,2);
                grid.Controls.Add(new ThemeLabel {Text=UiText("planes · 通道位掩码（0–15）","planes · Plane bitmask (0–15)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);grid.Controls.Add(planes,1,3);
                var help=new ThemeLabel {Name="PREPROCESS_FILTER_HELP",Dock=DockStyle.Fill,ForeColor=ColorMuted,Padding=new Padding(0,8,0,0),Text=UiText("默认 3 / 0.05 / 1：仅柔化亮度通道。sigmaS 越大影响范围越广；sigmaR 越大越容易抹平纹理。\nplanes：1=Y，2=U，4=V，6=UV，7=YUV；0=不处理。CPU 滤镜可能降低处理速度；GPU 解码仍按原设置运行。","Default 3 / 0.05 / 1: luma only. Larger sigmaS widens smoothing; larger sigmaR removes more texture.\nplanes: 1=Y, 2=U, 4=V, 6=UV, 7=YUV; 0=none. CPU filtering may reduce speed; GPU decode remains available.")};
                grid.Controls.Add(help,0,4);grid.SetColumnSpan(help,2);
                var resizeMode=new ThemeComboBox {Name="RESIZE_MODE",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
                string[] modes={"OFF","720","1080","1440","2160","2x","4x","1/2","1/4","CUSTOM"};
                resizeMode.Items.AddRange(new object[]{UiText("关闭","Off"),"720p · 1280 x 720","1080p · 1920 x 1080","1440p · 2560 x 1440","2160p · 3840 x 2160","2x","4x","1/2","1/4",UiText("自定义","Custom")});
                resizeMode.SelectedIndex=Math.Max(0,Array.IndexOf(modes,config.Get("RESIZE_MODE")));
                var resizeWidth=new ThemeTextBox {Name="RESIZE_WIDTH",Dock=DockStyle.Fill,Text=config.Get("RESIZE_WIDTH")};
                var resizeHeight=new ThemeTextBox {Name="RESIZE_HEIGHT",Dock=DockStyle.Fill,Text=config.Get("RESIZE_HEIGHT")};
                var factorBox=new ThemeTextBox {Name="RESIZE_FACTOR",Dock=DockStyle.Fill,Text=config.Get("RESIZE_FACTOR")};
                var algorithm=new ThemeComboBox {Name="RESIZE_ALGORITHM",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
                string[] algorithms={"LANCZOS","EWA_SHARP","EWA_SHARP_HQ"};
                foreach(string key in algorithms)algorithm.Items.Add(ResizeCore.AlgorithmName(key));
                algorithm.SelectedIndex=Math.Max(0,Array.IndexOf(algorithms,config.Get("RESIZE_ALGORITHM")));
                var keepAspect=new ThemeCheckBox {Name="RESIZE_KEEP_ASPECT",Text=UiText("保持显示宽高比（取消可能拉伸）","Keep display aspect ratio (uncheck to stretch)"),Dock=DockStyle.Fill,Checked=config.GetBool("RESIZE_KEEP_ASPECT")};
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("画面缩放","Resize target"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);resizeGrid.Controls.Add(resizeMode,1,0);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("自定义宽度（2–16384）","Custom width (2–16384)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);resizeGrid.Controls.Add(resizeWidth,1,1);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("自定义高度（2–16384）","Custom height (2–16384)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);resizeGrid.Controls.Add(resizeHeight,1,2);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("自定义倍数（如 1.5x、3/2、1/3；留空使用宽高）","Custom scale (e.g. 1.5x, 3/2, 1/3; blank uses width and height)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);resizeGrid.Controls.Add(factorBox,1,3);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("缩放算法 · Vulkan","Scaling algorithm · Vulkan"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,4);resizeGrid.Controls.Add(algorithm,1,4);
                resizeGrid.Controls.Add(keepAspect,0,5);resizeGrid.SetColumnSpan(keepAspect,2);
                var resizeSummary=new ThemeLabel {Name="RESIZE_SUMMARY",Dock=DockStyle.Fill,ForeColor=ColorMuted,Padding=new Padding(0,8,0,0)};resizeGrid.Controls.Add(resizeSummary,0,6);resizeGrid.SetColumnSpan(resizeSummary,2);
                Func<Dictionary<string,string>> resizeValues=delegate {return new Dictionary<string,string>{{"RESIZE_MODE",modes[resizeMode.SelectedIndex]},{"RESIZE_WIDTH",resizeWidth.Text.Trim()},{"RESIZE_HEIGHT",resizeHeight.Text.Trim()},{"RESIZE_KEEP_ASPECT",keepAspect.Checked?"true":"false"},{"RESIZE_FACTOR",factorBox.Text.Trim()},{"RESIZE_ALGORITHM",algorithms[algorithm.SelectedIndex]}};};
                Action updateResize=delegate {
                    bool custom=modes[resizeMode.SelectedIndex]=="CUSTOM";
                    factorBox.Enabled=custom;
                    resizeWidth.Enabled=resizeHeight.Enabled=custom&&factorBox.Text.Trim().Length==0;
                    keepAspect.Enabled=algorithm.Enabled=resizeMode.SelectedIndex!=0;
                    string note=UiText("按边界框保持比例，偶数对齐；不自动补边。颗粒在缩放后添加。","Fit within bounds, even dimensions; no padding. Grain follows resize.");
                    MediaProbeInfo media=GetBitrateMediaContext();
                    if(media!=null&&resizeMode.SelectedIndex!=0) {
                        var state=PreprocessEnvironment();foreach(var pair in resizeValues())state["FG_"+pair.Key]=pair.Value;
                        CinematicFramePlan frame;ResizePlan plan;string error;
                        if(CinematicFrameCore.TryBuild(media,state,out frame,out error)) {
                            int cw=frame.ActiveWidth,ch=frame.ActiveHeight;
                            if(ResizeCore.TryBuild(media,state,frame,out plan,out error))note=media.Width+"x"+media.Height+" → "+cw+"x"+ch+" → "+plan.Width+"x"+plan.Height+"\n"+note;
                        }
                        if(error.Length>0)note=error;
                    }
                    resizeSummary.Text=note;
                };
                resizeMode.SelectedIndexChanged+=delegate {updateResize();};resizeWidth.TextChanged+=delegate {updateResize();};resizeHeight.TextChanged+=delegate {updateResize();};factorBox.TextChanged+=delegate {updateResize();};algorithm.SelectedIndexChanged+=delegate {updateResize();};keepAspect.CheckedChanged+=delegate {updateResize();};updateResize();
                var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                var cancel=new ThemeButton {Name="PREPROCESS_CANCEL",Text=UiText("取消","Cancel"),DialogResult=DialogResult.Cancel,Width=88};
                var ok=new ThemeButton {Name="PREPROCESS_OK",Text=UiText("确定","OK"),Width=88};var reset=new ThemeButton {Name="PREPROCESS_DEFAULTS",Text=UiText("恢复默认值","Defaults"),Width=118};
                reset.Click+=delegate {if(tabs.SelectedTab==filterPage){enabled.Checked=false;spatial.Text="3";range.Text="0.05";planes.Value=1;}else{resizeMode.SelectedIndex=0;resizeWidth.Text="1920";resizeHeight.Text="1080";factorBox.Text="";algorithm.SelectedIndex=0;keepAspect.Checked=true;}};
                enabled.CheckedChanged+=delegate {spatial.Enabled=range.Enabled=planes.Enabled=enabled.Checked;};
                spatial.Enabled=range.Enabled=planes.Enabled=enabled.Checked;
                ok.Click+=delegate {
     