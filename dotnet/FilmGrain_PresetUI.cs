using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private PresetStore presetStore;
        private ComboBox presetSelector;
        private FileEditorSettings copiedFileSettings;
        private ThemeToolTip presetTips;
        private bool loadingPresets;
        private string lastAppliedPresetId;
        private PresetStore Presets()
        {
            if(presetStore==null){var store=new PresetStore(PresetStore.ResolveUserRoot(appRoot));SeedInitialPresets(store);store.Load();presetStore=store;}
            return presetStore;
        }
        private void SeedInitialPresets(PresetStore targetStore)
        {
            if(Directory.Exists(targetStore.Root))return;
            string stage=targetStore.Root+".seed."+Guid.NewGuid().ToString("N");
            try {
                var store=new PresetStore(stage);
                for(int i=1;i<=5;i++) {
                    string path=Path.Combine(appRoot,"Presets","Initial",i.ToString("00",CultureInfo.InvariantCulture)+".fgspreset.json");
                    if(!File.Exists(path))throw new FileNotFoundException("缺少随附初始预设",path);
                    var p=PresetCore.Read(path);store.Save(p);store.Bind(i-1,p.Id);
                }
                Directory.Move(stage,targetStore.Root);
            } finally {if(Directory.Exists(stage))Directory.Delete(stage,true);}
        }
        private bool PresetEditable()
        {
            if(bridgeTaskCoordinator!=null&&bridgeTaskCoordinator.IsActive)throw new InvalidOperationException("任务执行中，不能修改处理配置。");
            if(independentFiles&&(editingFile==null||listFiles.SelectedItems.Count!=1||!string.Equals(listFiles.SelectedItems[0].Tag as string,editingFile,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("请先选择一个明确的目标文件。");
            return true;
        }
        private void PresetAction(Action action){try{action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"预设配置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        private string ResourceRoot(string kind) {return kind=="lut"?config.Get("LUT_ROOT"):kind=="table"?Path.Combine(appRoot,"_AV1_Grain_Tables"):config.Get("GRAIN_ROOT");}
        private string ResourceReference(string path,string kind)
        {
            if(string.IsNullOrWhiteSpace(path))return "";
            string root=ResourceRoot(kind);if(!string.IsNullOrWhiteSpace(root)) {
                root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
                string full=Path.GetFullPath(path);if(full.StartsWith(root,StringComparison.OrdinalIgnoreCase))return kind+":"+full.Substring(root.Length).Replace('\\','/');
            }
            return "external:"+Path.GetFullPath(path);
        }
        private string ResolveResource(string reference,bool relocate)
        {
            if(reference=="")return "";PresetCore.ValidateResource(reference);int split=reference.IndexOf(':');string kind=reference.Substring(0,split),relative=reference.Substring(split+1);
            string path=kind=="external"?relative:Path.Combine(ResourceRoot(kind),relative.Replace('/',Path.DirectorySeparatorChar));
            if(File.Exists(path))return Path.GetFullPath(path);
            if(!relocate)throw new FileNotFoundException("缺少资源: "+reference+Environment.NewLine+"解析位置: "+path);
            using(var dialog=new OpenFileDialog {Title="重新定位缺少的资源: "+reference,FileName=Path.GetFileName(relative),Filter="对应类型资源 (*"+Path.GetExtension(relative)+")|*"+Path.GetExtension(relative)}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)throw new InvalidOperationException("未应用预设，资源尚未定位: "+reference);
                return dialog.FileName;
            }
        }
        private FgsPreset CapturePreset(string kind,string name,string description)
        {
            PresetEditable();var editor=CaptureFileEditor();var p=new FgsPreset {FormatVersion=1,Id=Guid.NewGuid().ToString("D"),Kind=kind,Name=name,Description=description,SubtitlePolicy=!editor.Subtitle.Enabled?"OFF":editor.Subtitle.Mode=="AUTO"?"AUTO":"TARGET",
                Fields=new Dictionary<string,string>(),Controls=new Dictionary<string,string>(editor.Controls),Config=new Dictionary<string,string>(),SubtitleStyle=new Dictionary<string,string>(),Bitrates=(string[])editor.Bitrates.Clone(),Automatic=(bool[])editor.Automatic.Clone()};
            foreach(string k in PresetCore.Fields.Split(' '))p.Fields[k]=Convert.ToString(editor.Fields[k],CultureInfo.InvariantCulture)??"";
            p.Fields["selectedLutPath"]=ResourceReference(selectedLutPath,"lut");p.Fields["selectedAv1GrainTable"]=ResourceReference(selectedAv1GrainTable,"table");p.Fields["selectedGrainPlatePath"]=ResourceReference(selectedGrainPlatePath,"plate");
            foreach(var x in editor.Config)if(PresetCore.Includes(kind,x.Key))p.Config[x.Key]=x.Value;
            // CQ AUTO retains its intent; never export the media-derived recommendation.
            if(p.Controls["chkCq"]=="1"&&p.Controls["chkBitrateAuto"]=="1")p.Controls["cmbBitrate"]="30";
            foreach(string k in PresetCore.Styles.Split(' '))p.SubtitleStyle[k]=Convert.ToString(typeof(SubtitleState).GetField(k,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(editor.Subtitle),CultureInfo.InvariantCulture);
            return PresetCore.CoherentCopy(p);
        }
        private FileEditorSettings MergePreset(FgsPreset p,FileEditorSettings target,bool relocate)
        {
            p=PresetCore.CoherentCopy(p);var value=target.Copy();value.Config["CPU_CODEC"]=p.Config.ContainsKey("CPU_CODEC")?p.Config["CPU_CODEC"]:"X264";foreach(var x in p.Config)value.Config[x.Key]=x.Value;
            foreach(var x in p.Controls)value.Controls[x.Key]=x.Value;
            value.Bitrates=(string[])p.Bitrates.Clone();value.Automatic=(bool[])p.Automatic.Clone();
            foreach(var x in p.Fields) {
                Type t=EditorField(x.Key).FieldType;
                value.Fields[x.Key]=t==typeof(string)?(object)x.Value:Convert.ChangeType(x.Value,t,CultureInfo.InvariantCulture);
            }
            value.Fields["selectedLutSource"]="预设";
            // Resolve every explicit resource before touching the editor. Missing inactive resources are also reported.
            foreach(string k in new[]{"selectedLutPath","selectedAv1GrainTable","selectedGrainPlatePath"})value.Fields[k]=ResolveResource(p.Fields[k],relocate);
            foreach(var x in p.SubtitleStyle){var f=typeof(SubtitleState).GetField(x.Key,BindingFlags.Instance|BindingFlags.NonPublic);f.SetValue(value.Subtitle,Convert.ChangeType(x.Value,f.FieldType,CultureInfo.InvariantCulture));}
            if(p.SubtitlePolicy!="TARGET"){value.Subtitle.Enabled=p.SubtitlePolicy=="AUTO";value.Subtitle.Mode=p.SubtitlePolicy;value.Subtitle.Label=p.SubtitlePolicy=="AUTO"?"自动匹配":"关闭";}
            if(value.Controls["chkLut"]=="1"&&value.Controls["trackLutStrength"]!="0"&&string.IsNullOrEmpty((string)value.Fields["selectedLutPath"]))throw new InvalidDataException("LUT 已启用但缺少资源引用");
            int codec=int.Parse(value.Controls["cmbCodec"],CultureInfo.InvariantCulture);
            if(value.Controls["chkGrainEnabled"]=="1"&&((codec==0||codec==4)?(int)value.Fields["av1GrainModeIndex"]==2:(int)value.Fields["pixelGrainModeIndex"]==1)) {
                string resource=(string)value.Fields[codec==0||codec==4?"selectedAv1GrainTable":"selectedGrainPlatePath"];
                if(string.IsNullOrEmpty(resource))throw new InvalidDataException("所选颗粒算法缺少资源引用");
            }
            string error;if(!SharedSvpCore.TryValidateOptions(value.Config["SVP_SPEED"],value.Config["SVP_TUNING"],value.Config["SVP_BLOCK_SIZE"],value.Config["SVP_ANALYSE"],out error))throw new InvalidDataException(error);
            ValidatePresetHardware(value);return value;
        }
        private void ValidateEditorResources(FileEditorSettings value)
        {
            foreach(string k in new[]{"selectedLutPath","selectedAv1GrainTable","selectedGrainPlatePath"}) {
                string path=(string)value.Fields[k];if(!string.IsNullOrEmpty(path)&&!File.Exists(path))throw new FileNotFoundException("缺少处理资源: "+k,path);
            }
            if(value.Controls["chkLut"]=="1"&&value.Controls["trackLutStrength"]!="0"&&string.IsNullOrEmpty((string)value.Fields["selectedLutPath"]))throw new InvalidDataException("LUT 已启用但缺少资源");
            int c=int.Parse(value.Controls["cmbCodec"],CultureInfo.InvariantCulture);
            if(value.Controls["chkGrainEnabled"]=="1"&&((c==0||c==4)?(int)value.Fields["av1GrainModeIndex"]==2:(int)value.Fields["pixelGrainModeIndex"]==1)&&string.IsNullOrEmpty((string)value.Fields[c==0||c==4?"selectedAv1GrainTable":"selectedGrainPlatePath"]))throw new InvalidDataException("所选颗粒类型缺少资源");
        }
        private void ValidatePresetHardware(FileEditorSettings p)
        {
            int c=int.Parse(p.Controls["cmbCodec"],CultureInfo.InvariantCulture);var caps=hardwareCaps;
            if(!hardwareCapsReady||caps==null)throw new InvalidOperationException("硬件能力缓存尚未就绪，请等待现有检测完成后应用。");
            if(c==0&&!caps.Av1Available||c==1&&!caps.HevcAvailable||c==2&&p.Config["CPU_CODEC"]=="X264"&&!caps.X264Available||c==3&&!caps.H264Available)throw new InvalidOperationException("当前机器不支持预设编码器。");
            if(p.Controls["chkInterpolation"]=="1") {
                if(p.Controls["cmbInterpolationMode"]=="2") {
                    if(!caps.FrucReady||!caps.FrucAvailable)throw new InvalidOperationException("当前能力缓存未确认 FRUC Vulkan 可用；预设没有更换算法。");
                    string grid=p.Config["FRUC_GRID"],supported=SharedFrucCore.SupportedGrids(caps.FrucEvidence);
                    if(grid!="auto"&&Array.IndexOf(supported.Split(','),grid)<0)throw new InvalidOperationException("FRUC 网格不兼容: "+grid+"；缓存支持: "+supported);
                } else if(!caps.OpenSvpGpuAvailable)throw new InvalidOperationException("当前机器未确认 OpenSVPFlow 可用。");
            }
            if(c==2&&p.Config["CPU_CODEC"]=="X264"&&p.Config["H264_HIGH10"]=="true"&&!caps.X264High10Available)throw new InvalidOperationException("当前 x264 不支持 High10。");
            if(c==0&&p.Controls["cmbSpeed"]=="2"&&!caps.Av1UhqAvailable)throw new InvalidOperationException("当前 AV1 不支持 UHQ。");
            if(c==0&&p.Controls["chkSfe"]=="1"&&p.Controls["chkGrainEnabled"]=="1"&&!caps.Grav1synthSfeCompatible)throw new InvalidOperationException("当前 Grav1synth 不支持颗粒与分割编码组合");
            if(p.Controls["chkSfe"]=="1"&&((c==0&&caps.Av1SplitEncodeMaxEngines<2)||(c==1&&caps.HevcSplitEncodeMaxEngines<2)||c==2||c==3))throw new InvalidOperationException("当前编码器不支持所选分割编码。");
            if(c==0||c==1||c==3) {
                string prefix=c==0?"AV1":c==1?"HEVC":"H264";
                string aqPrefix=c==0?"NVENC":prefix,capPrefix=c==0?"Av1":c==1?"Hevc":"H264";
                if(p.Config[aqPrefix+"_SPATIAL_AQ"]!="0"&&!(bool)caps.GetType().GetProperty(capPrefix+"SpatialAq").GetValue(caps,null))throw new InvalidOperationException("硬件不支持空间 AQ: "+prefix);
                if(p.Config[aqPrefix+"_TEMPORAL_AQ"]=="true"&&!(bool)caps.GetType().GetProperty(capPrefix+"TemporalAq").GetValue(caps,null))throw new InvalidOperationException("硬件不支持时间 AQ: "+prefix);
                foreach(string suffix in new[]{"BFRAMES","BREF_MODE","LOOKAHEAD","MULTIPASS"}) {
                    string v;if(!p.Config.TryGetValue(prefix+"_"+suffix,out v)||v=="FGS"||v=="0"||v=="disabled"||v=="DISABLED")continue;
                    string cap=prefix=="AV1"?"Av1":prefix=="HEVC"?"Hevc":"H264";
                    cap+=suffix=="BFRAMES"?"BFrames":suffix=="BREF_MODE"?"BReference":suffix=="LOOKAHEAD"?"Lookahead":v=="QRES"?"MultipassQres":"MultipassFullres";
                    if(!(bool)caps.GetType().GetProperty(cap).GetValue(caps,null))throw new InvalidOperationException("硬件不兼容: "+prefix+"_"+suffix+" = "+v);
                }
            }
        }
        private void CommitPresetEditor(FileEditorSettings next)
        {
            PresetEditable();var before=CaptureFileEditor();
            try {
                // An overlay prevents change events from persisting partial state.
                ApplyFileEditor(next,true);
                if(independentFiles){fileSettings[editingFile]=CaptureFileEditor().Copy();initializedFileSettings.Add(editingFile);}
                else {var newShared=next.Copy();config.UseTaskValues(null);config.Save(next.Config);sharedEditor=newShared;}
            } catch {if(!independentFiles){config.UseTaskValues(null);config.Load();}ApplyFileEditor(before,true);if(!independentFiles)config.UseTaskValues(null);throw;}
            UpdateFileSettingsLabels();
        }
        private List<string> PresetTargets()
        {
            if(bridgeTaskCoordinator!=null&&bridgeTaskCoordinator.IsActive)throw new InvalidOperationException("任务执行中，不能修改处理配置。");
            var paths=new List<string>();
            if(independentFiles){foreach(ListViewItem item in listFiles.SelectedItems){string path=item.Tag as string;if(!string.IsNullOrEmpty(path))paths.Add(path);}if(paths.Count==0)throw new InvalidOperationException("请先选择目标文件；支持 Shift 连选、Ctrl 加选。");}
            return paths;
        }
        private FileEditorSettings TargetEditor(string path)
        {
            if(string.Equals(path,editingFile,StringComparison.OrdinalIgnoreCase))return CaptureFileEditor().Copy();
            FileEditorSettings value;return (fileSettings.TryGetValue(path,out value)?value:sharedEditor).Copy();
        }
        private void ApplyToSelected(Func<FileEditorSettings,FileEditorSettings> prepare)
        {
            var paths=PresetTargets();
            if(!independentFiles){CommitPresetEditor(prepare(CaptureFileEditor().Copy()));return;}
            // Prepare every target before touching any stored editor. Each merge starts
            // from that file, retaining its omitted advanced values and subtitle binding.
            var staged=new Dictionary<string,FileEditorSettings>(StringComparer.OrdinalIgnoreCase);
            foreach(string path in paths){try{staged[path]=prepare(TargetEditor(path)).Copy();}catch(Exception ex){throw new InvalidOperationException(Path.GetFileName(path)+": "+ex.Message,ex);}}
            var previous=new Dictionary<string,FileEditorSettings>(StringComparer.OrdinalIgnoreCase);
            foreach(string path in paths){FileEditorSettings old;previous[path]=fileSettings.TryGetValue(path,out old)?old:null;}
            var before=CaptureFileEditor().Copy();bool refresh=editingFile!=null&&staged.ContainsKey(editingFile);
            try {
                if(refresh)ApplyFileEditor(staged[editingFile].Copy(),true);
                foreach(string path in paths)fileSettings[path]=staged[path];
                foreach(string path in paths)initializedFileSettings.Add(path);
            }catch{
                foreach(string path in paths){if(previous[path]==null)fileSettings.Remove(path);else fileSettings[path]=previous[path];}
                if(refresh)ApplyFileEditor(before,true);throw;
            }
            UpdateFileSettingsLabels();
        }
        private void PasteSelectedConfiguration()
        {
            PresetTargets();if(!independentFiles)throw new InvalidOperationException("请先启用独立配置。");
            if(copiedFileSettings==null)throw new InvalidOperationException("尚未复制配置");
            var source=copiedFileSettings.Copy();
            ApplyToSelected(delegate(FileEditorSettings target){
                var next=source.Copy();
                if(next.Subtitle.Mode=="EXTERNAL"||next.Subtitle.Mode=="EMBEDDED"){
                    var style=next.Subtitle;next.Subtitle=target.Subtitle.Copy();
                    foreach(string key in PresetCore.Styles.Split(' ')){var field=typeof(SubtitleState).GetField(key,BindingFlags.Instance|BindingFlags.NonPublic);field.SetValue(next.Subtitle,field.GetValue(style));}
                }
                ValidateEditorResources(next);ValidatePresetHardware(next);return next;
            });
        }
        private string PresetTargetDescription()
        {
            var paths=PresetTargets();if(!independentFiles)return "统一设置（随后应用到全部队列文件）";
            var names=new List<string>();foreach(string path in paths)names.Add(Path.GetFileName(path));
            return "选中的 "+paths.Count+" 个文件: "+string.Join("、",names.ToArray());
        }
        private void ApplyPreset(FgsPreset p)
        {PresetHoverCard.HideCard();ApplyToSelected(delegate(FileEditorSettings target){return MergePreset(p,target,true);});lastAppliedPresetId=p.Id;RefreshPresetSelector();}
        private string PresetDetails(FgsPreset p)
        {
            string text=PresetCore.Details(p);foreach(string k in new[]{"selectedLutPath","selectedAv1GrainTable","selectedGrainPlatePath"})try{ResolveResource(p.Fields[k],false);}catch(Exception ex){text+=Environment.NewLine+ex.Message;}
            try {var preview=CaptureFileEditor();foreach(var x in p.Config)preview.Config[x.Key]=x.Value;foreach(var x in p.Controls)preview.Controls[x.Key]=x.Value;ValidatePresetHardware(preview);}catch(Exception ex){text+=Environment.NewLine+"兼容性: "+ex.Message;}
            return text;
        }
        private string PresetQuickDetails(FgsPreset p)
        {
            string text=PresetCore.BriefDetails(p);var missing=new List<string>();
            foreach(string k in new[]{"selectedLutPath","selectedAv1GrainTable","selectedGrainPlatePath"})try{ResolveResource(p.Fields[k],false);}catch{missing.Add(Path.GetFileName(p.Fields[k].Replace('\\','/')));}
            if(missing.Count>0)text+=Environment.NewLine+"⚠ 缺少资源："+PresetCore.ShortText(string.Join("、",missing.ToArray()),100);
            try{var preview=CaptureFileEditor();foreach(var x in p.Config)preview.Config[x.Key]=x.Value;foreach(var x in p.Controls)preview.Controls[x.Key]=x.Value;ValidatePresetHardware(preview);}catch(Exception ex){text+=Environment.NewLine+"⚠ "+PresetCore.ShortText(ex.Message,100);}
            return text;
        }
        private void RefreshPresetSelector()
        {
            if(presetSelector==null)return;loadingPresets=true;try{presetSelector.Items.Clear();presetSelector.SelectedIndex=-1;foreach(var p in Presets().Items){int i=presetSelector.Items.Add(p);if(p.Id==lastAppliedPresetId)presetSelector.SelectedIndex=i;}}finally{loadingPresets=false;}
        }
        private void AddPresetControls(Panel panel)
        {
            presetSelector=new PresetComboBox {Width=116,DropDownStyle=ComboBoxStyle.DropDownList,Anchor=AnchorStyles.Top|AnchorStyles.Left,Margin=Padding.Empty};
            ((PresetComboBox)presetSelector).Details=PresetQuickDetails;
            presetSelector.SelectionChangeCommitted+=delegate{if(!loadingPresets&&presetSelector.SelectedItem!=null)PresetAction(delegate{try{ApplyPreset((FgsPreset)presetSelector.SelectedItem);}finally{RefreshPresetSelector();}});};
            var button=new ThemeButton {Text="预设管理",Size=new Size(82,29),Margin=Padding.Empty};
            button.Click+=delegate{PresetAction(ShowPresetManager);};
            var quick=new ContextMenuStrip();quick.Closed+=delegate{PresetHoverCard.HideCard();};quick.Opening+=delegate(object sender,System.ComponentModel.CancelEventArgs e){try{quick.Items.Clear();for(int i=0;i<5;i++){int slot=i;var p=Presets().Find(Presets().Quick[i]);var item=new ToolStripMenuItem((i+1)+". "+(p==null?"未绑定":p.Name)){Enabled=p!=null,AutoToolTip=false,ToolTipText=""};if(p!=null)AttachQuickPresetTip(item,quick,PresetQuickDetails(p));item.Click+=delegate{PresetAction(delegate{var target=Presets().Find(Presets().Quick[slot]);if(target==null)throw new InvalidOperationException("快捷位置未绑定");ApplyPreset(target);});};quick.Items.Add(item);}}catch(Exception ex){e.Cancel=true;MessageBox.Show(this,ex.Message,"预设配置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
            button.ContextMenuStrip=quick;panel.Controls.Add(button);panel.Controls.Add(presetSelector);
            presetTips=new ThemeToolTip();presetTips.SetToolTip(button,"左键：管理预设；右键：五个快捷位置。选择器选择后应用到当前统一设置或明确的独立文件。");
            button.Disposed+=delegate{presetTips.Dispose();quick.Dispose();};
            // Load only on first interaction; file I/O is not added to the first-paint path.
            presetSelector.DropDown+=delegate{if(presetStore==null)PresetAction(RefreshPresetSelector);};
            panel.HandleCreated+=delegate{BeginInvoke((MethodInvoker)delegate{if(listFiles!=null)InstallPresetQueueMenu();});};
        }
        private static readonly Dictionary<ContextMenuStrip,QuickPresetHints> quickPresetHints=new Dictionary<ContextMenuStrip,QuickPresetHints>();
        internal static void AttachQuickPresetTip(ToolStripMenuItem item,ContextMenuStrip menu,string text)
        {
            QuickPresetHints hints;
            if(!quickPresetHints.TryGetValue(menu,out hints)){
                hints=new QuickPresetHints(menu);quickPresetHints.Add(menu,hints);
                menu.Disposed+=delegate{hints.Dispose();quickPresetHints.Remove(menu);};
            }
            hints.Add(item,text);
        }
        // ToolStrip can route movement to an item or capture it at the popup level.
        // Polling only while open covers both routes without touching the shared theme system.
        private sealed class QuickPresetHints : IDisposable
        {
            private readonly ContextMenuStrip menu;
            private readonly Dictionary<ToolStripItem,string> texts=new Dictionary<ToolStripItem,string>();
            private readonly Timer timer=new Timer {Interval=100};
            private ToolStripItem hovered;
            internal QuickPresetHints(ContextMenuStrip menu){
                this.menu=menu;timer.Tick+=delegate{Inspect();};
                menu.Opened+=delegate{hovered=null;timer.Start();Inspect();};
                menu.Closed+=delegate{timer.Stop();hovered=null;texts.Clear();PresetHoverCard.HideCard();};
                menu.MouseMove+=delegate{Inspect();};
            }
            internal void Add(ToolStripItem item,string text){texts[item]=text;}
            private void Inspect(){
                if(menu.IsDisposed||!menu.Visible)return;
                var item=menu.GetItemAt(menu.PointToClient(Cursor.Position));string text;
                if(item==hovered)return;hovered=item;PresetHoverCard.HideCard();
                if(item!=null&&item.Enabled&&texts.TryGetValue(item,out text))PresetHoverCard.Display(menu,text,menu.RectangleToScreen(item.Bounds));
            }
            public void Dispose(){timer.Stop();timer.Dispose();texts.Clear();PresetHoverCard.HideCard();}
        }
        private void PrepareQueuePresetTarget()
        {
            // Unified mode captures the shared editor, regardless of queue selection.
            if(independentFiles)SelectFileEditor();
            PresetEditable();
        }
        private void UpdatePresetQueueMenu(ContextMenuStrip menu)
        {
            bool fileTarget=independentFiles&&listFiles.SelectedItems.Count==1;
            menu.Items[0].Enabled=fileTarget;menu.Items[1].Enabled=independentFiles&&listFiles.SelectedItems.Count>0&&copiedFileSettings!=null;
            menu.Items[1].Text=independentFiles?"粘贴配置到选中的 "+listFiles.SelectedItems.Count+" 个文件":"粘贴配置";
            menu.Items[2].Enabled=!independentFiles||fileTarget;
            menu.Items[2].Text=independentFiles?"从该文件配置保存预设":"从当前统一配置保存预设";
            menu.Items[3].Text=independentFiles?"打开预设管理器，为选中的 "+listFiles.SelectedItems.Count+" 个文件应用预设":"打开预设管理器，为统一设置应用预设";
        }
        private void InstallPresetQueueMenu()
        {
            if(listFiles.ContextMenuStrip!=null)return;
            var menu=new ContextMenuStrip();listFiles.ContextMenuStrip=menu;
            Action requireTarget=delegate{if(listFiles.SelectedItems.Count!=1)throw new InvalidOperationException("请选择一个文件。复制与粘贴每次只作用于一个明确的文件，且需要独立配置模式。");if(!independentFiles)throw new InvalidOperationException("请先启用独立配置。");SelectFileEditor();PresetEditable();};
            var copy=new ToolStripMenuItem("复制配置");copy.Click+=delegate{PresetAction(delegate{requireTarget();copiedFileSettings=CaptureFileEditor().Copy();});};menu.Items.Add(copy);
            var paste=new ToolStripMenuItem("粘贴配置");paste.Click+=delegate{PresetAction(PasteSelectedConfiguration);};menu.Items.Add(paste);
            var savePreset=new ToolStripMenuItem("从该文件配置保存预设");savePreset.Click+=delegate{PresetAction(delegate{PrepareQueuePresetTarget();var p=new FgsPreset {Name="新预设",Description="",Kind="Simple"};if(EditPresetMetadata(this,p,independentFiles?"从当前文件保存预设: "+Path.GetFileName(editingFile):"从当前统一设置保存预设")){var captured=CapturePreset(p.Kind,p.Name,p.Description);Presets().Save(captured);RefreshPresetSelector();}});};menu.Items.Add(savePreset);
            var applyPreset=new ToolStripMenuItem("打开预设管理器，为该文件应用预设");applyPreset.Click+=delegate{PresetAction(delegate{PresetTargets();ShowPresetManager();});};menu.Items.Add(applyPreset);
            menu.Opening+=delegate{UpdatePresetQueueMenu(menu);};UpdatePresetQueueMenu(menu);
        }
        private bool EditPresetMetadata(Form owner,FgsPreset p,string title)
        {
            using(var dialog=new Form {Font=owner.Font,AutoScaleMode=AutoScaleMode.Dpi,Text=title,Size=new Size(540,385),MinimumSize=new Size(480,350),StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false}) {
                var grid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(12)};
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute,24));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,30));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,28));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,30));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,46));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
                var name=new ThemeTextBox {Text=p.Name,Dock=DockStyle.Fill,MaxLength=48};var description=new TextBox {Text=p.Description,Multiline=true,Dock=DockStyle.Fill,MaxLength=4000,ScrollBars=ScrollBars.Vertical,Margin=Padding.Empty};
                var descriptionFrame=new ThemeLogFrame(description) {Margin=new Padding(3,0,3,3)};
                var kind=new ThemeComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};kind.Items.AddRange(new object[]{"简单预设","完整预设"});kind.SelectedIndex=p.Kind=="Full"?1:0;
                var scope=new ThemeLabel {Dock=DockStyle.Fill};Action update=delegate{scope.Text=kind.SelectedIndex==0?"主界面流程、HDR/音频策略、裁剪及字幕样式；未包含的高级参数保持当前设置。":"包括编码、插帧、HDR、音频高级参数；不含机器环境、界面偏好和字幕文件绑定。";};kind.SelectedIndexChanged+=delegate{update();};update();
                var ok=new ThemeButton {Text="保存",DialogResult=DialogResult.OK,Width=90};var cancel=new ThemeButton {Text="取消",DialogResult=DialogResult.Cancel,Width=90};var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill};buttons.Controls.Add(ok);buttons.Controls.Add(cancel);
                grid.Controls.Add(new ThemeLabel {Text="短名称",Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft,Margin=new Padding(3,0,3,2)});grid.Controls.Add(name);grid.Controls.Add(new ThemeLabel {Text="用途说明",Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft,Margin=new Padding(3,0,3,2)});grid.Controls.Add(descriptionFrame);grid.Controls.Add(kind);grid.Controls.Add(scope);grid.Controls.Add(buttons);dialog.Controls.Add(grid);dialog.AcceptButton=ok;dialog.CancelButton=cancel;AttachOwnedTheme(dialog,owner,description);
                if(dialog.ShowDialog(owner)!=DialogResult.OK)return false;p.Name=name.Text.Trim();p.Description=description.Text;p.Kind=kind.SelectedIndex==0?"Simple":"Full";return true;
            }
        }
        internal static int[] PresetColumnWidths(int clientWidth,float dpi)
        {
            int available=Math.Max(3,clientWidth-4);
            int name=Math.Min((int)(130*dpi/96f),Math.Max(1,available*36/100));
            int kind=Math.Min((int)(58*dpi/96f),Math.Max(1,available*18/100));
            return new[]{name,kind,Math.Max(1,available-name-kind)};
        }
        private static void AttachPresetColumnFitting(ListView list)
        {
            bool fitting=false;
            Action fit=delegate{
                if(fitting||list.Columns.Count!=3||list.ClientSize.Width<20)return;
                fitting=true;list.BeginUpdate();
                try{float dpi;using(var g=list.CreateGraphics())dpi=g.DpiX;int[] widths=PresetColumnWidths(list.ClientSize.Width,dpi);
                    for(int i=0;i<3;i++)if(list.Columns[i].Width>widths[i])list.Columns[i].Width=widths[i];
                    for(int i=0;i<3;i++)if(list.Columns[i].Width!=widths[i])list.Columns[i].Width=widths[i];
                }finally{list.EndUpdate();fitting=false;}
            };
            list.SizeChanged+=delegate{fit();};list.HandleCreated+=delegate{fit();};
            list.ColumnWidthChanging+=delegate(object sender,ColumnWidthChangingEventArgs e){int total=e.NewWidth;for(int i=0;i<list.Columns.Count;i++)if(i!=e.ColumnIndex)total+=list.Columns[i].Width;if(total>Math.Max(0,list.ClientSize.Width-4)){e.Cancel=true;e.NewWidth=list.Columns[e.ColumnIndex].Width;}};
            list.ColumnWidthChanged+=delegate{if(!fitting){int total=0;foreach(ColumnHeader c in list.Columns)total+=c.Width;if(total>Math.Max(0,list.ClientSize.Width-4))fit();}};
            list.Layout+=delegate{fit();};
        }
        private void ShowPresetManager()
        {
            PresetHoverCard.HideCard();string target=PresetTargetDescription();bool multiple=independentFiles&&listFiles.SelectedItems.Count>1;
            using(var dialog=new Form {Font=Font,AutoScaleMode=AutoScaleMode.Dpi,Text="预设管理器 · "+(multiple?"选中的 "+listFiles.SelectedItems.Count+" 个文件":target),Size=new Size(1080,720),MinimumSize=new Size(800,560),StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false}) {
                var grid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Padding=new Padding(10)};grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,56));grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,108));
                var list=new ThemeListView {View=View.Details,FullRowSelect=true,MultiSelect=false,Dock=DockStyle.Fill,ShowItemToolTips=false};list.Columns.Add("短名称",130);list.Columns.Add("类型",70);list.Columns.Add("流程摘要",330);
                var detail=new RichTextBox {ReadOnly=true,WordWrap=true,DetectUrls=false,ScrollBars=RichTextBoxScrollBars.Vertical,Dock=DockStyle.Fill};
                var detailFrame=new ThemeLogFrame(detail);
                AttachPresetColumnFitting(list);
                var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};var slot=new ThemeComboBox {Width=105,DropDownStyle=ComboBoxStyle.DropDownList};for(int i=1;i<=5;i++)slot.Items.Add("快捷位置 "+i);slot.SelectedIndex=0;
                var bindings=new ThemeLabel {AutoSize=true};Action updateBindings=delegate{var names=new List<string>();for(int i=0;i<5;i++){var p=Presets().Find(Presets().Quick[i]);names.Add((i+1)+": "+(p==null?"未绑定":p.Name));}bindings.Text=string.Join(" | ",names.ToArray());};
                Func<FgsPreset> selected=delegate{if(list.SelectedItems.Count!=1)throw new InvalidOperationException("请先选择一份预设");return (FgsPreset)list.SelectedItems[0].Tag;};
                Action<string> refresh=delegate(string id){list.Items.Clear();foreach(var p in Presets().Items){var item=new ListViewItem(p.Name){Tag=p,ToolTipText=""};item.SubItems.Add(p.Kind=="Full"?"完整":"简单");item.SubItems.Add(PresetCore.Summary(p));list.Items.Add(item);if(p.Id==id)item.Selected=true;}detail.Text="目标: "+target+Environment.NewLine+"浏览列表不会应用。选择预设后点击应用。";RefreshPresetSelector();updateBindings();};
                list.SelectedIndexChanged+=delegate{if(list.SelectedItems.Count==1)detail.Text="目标: "+target+Environment.NewLine+PresetDetails(selected());};
                Action<string,Action> add=delegate(string text,Action action){var b=new ThemeButton {Text=text,AutoSize=true,Height=28};b.Click+=delegate{PresetAction(action);};if(multiple&&(text=="新建（从当前设置）"||text=="更新保存"||text=="另存为"))b.Enabled=false;buttons.Controls.Add(b);};
                add("新建（从当前设置）",delegate{var p=new FgsPreset {Name="新预设",Description="",Kind="Simple"};if(EditPresetMetadata(dialog,p,"从当前目标保存预设")){var saved=CapturePreset(p.Kind,p.Name,p.Description);Presets().Save(saved);refresh(saved.Id);}});
                add("应用",delegate{var p=selected();ApplyPreset(p);dialog.Close();});
                add("名称 / 说明",delegate{var p=PresetCore.Copy(selected());string original=p.Kind;if(EditPresetMetadata(dialog,p,"编辑名称和说明（不捕获当前配置）")){if(p.Kind!=original)throw new InvalidOperationException("更改保存范围请使用更新保存或另存为。");Presets().Save(p);refresh(p.Id);}});
                add("更新保存",delegate{var old=selected();var metadata=PresetCore.Copy(old);if(EditPresetMetadata(dialog,metadata,"用当前目标配置更新预设")){var p=CapturePreset(metadata.Kind,metadata.Name,metadata.Description);p.Id=old.Id;Presets().Save(p);refresh(p.Id);}});
                add("另存为",delegate{var p=PresetCore.Copy(selected());if(EditPresetMetadata(dialog,p,"当前目标配置另存为新预设")){p=CapturePreset(p.Kind,p.Name,p.Description);Presets().Save(p);refresh(p.Id);}});
                add("删除",delegate{var p=selected();if(MessageBox.Show(dialog,"删除预设并清除其快捷绑定: "+p.Name+"？","删除",MessageBoxButtons.YesNo)==DialogResult.Yes){Presets().Delete(p);refresh(null);}});
                add("导入",delegate{using(var open=new OpenFileDialog {Filter="FGS 预设|*.fgspreset.json"}){if(open.ShowDialog(dialog)!=DialogResult.OK)return;var p=PresetCore.Read(open.FileName);bool conflict=Presets().Find(p.Id)!=null||Presets().Items.Exists(delegate(FgsPreset x){return x.Name==p.Name;});if(conflict){if(MessageBox.Show(dialog,"发现相同 ID 或名称。以新 ID 和新名称导入副本？不会覆盖已有预设。","导入冲突",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;p.Id=Guid.NewGuid().ToString("D");p.Name=p.Name.Substring(0,Math.Min(40,p.Name.Length))+"（导入副本）";}Presets().Save(p);refresh(p.Id);}});
                add("导出",delegate{var p=selected();using(var save=new SaveFileDialog {Filter="FGS 预设|*.fgspreset.json",FileName="FGS_Preset.fgspreset.json"}){if(save.ShowDialog(dialog)==DialogResult.OK)PresetCore.Write(save.FileName,p);}});
                buttons.Controls.Add(slot);add("绑定快捷位置",delegate{var p=selected();Presets().Bind(slot.SelectedIndex,p.Id);updateBindings();detail.Text=PresetDetails(p)+Environment.NewLine+"已绑定快捷位置 "+(slot.SelectedIndex+1);});add("清空该位置",delegate{Presets().Bind(slot.SelectedIndex,null);updateBindings();detail.Text="快捷位置 "+(slot.SelectedIndex+1)+" 已清空";});
                buttons.Controls.Add(bindings);updateBindings();
                grid.Controls.Add(list,0,0);grid.Controls.Add(detailFrame,1,0);grid.Controls.Add(buttons,0,1);grid.SetColumnSpan(buttons,2);dialog.Controls.Add(grid);AttachThemeWindow(dialog,detail);themedWindows[dialog].EnableNativeScrollbars(list);themedWindows[dialog].Apply(dialog,selectedDarkTheme);refresh(null);dialog.ShowDialog(this);
            }
        }
    }
    // No managed Form.Show path: create a disabled NOACTIVATE native popup, then show it
    // with SWP_NOACTIVATE. It cannot become the active window or dismiss a captured menu.
    internal sealed class PresetHoverCard : NativeWindow,IDisposable
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]private struct Rect {internal int Left,Top,Right,Bottom;}
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]private struct PaintState {
            internal IntPtr Hdc;internal int Erase;internal Rect Paint;internal int Restore,IncUpdate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray,SizeConst=32)]internal byte[] Reserved;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr BeginPaint(IntPtr window,out PaintState state);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool EndPaint(IntPtr window,ref PaintState state);
        [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(IntPtr window,int index);
        private static PresetHoverCard card;
        private static Timer pending;
        private readonly Timer lifetime=new Timer {Interval=120};
        private readonly string text;
        private readonly bool dark;
        private readonly Font font;
        private readonly Rectangle bounds;
        private bool disposed;
        internal string SummaryText {get{return text;}}
        internal Rectangle Bounds {get{return bounds;}}
        internal Point Location {get{return bounds.Location;}}
        internal bool TopMost {get{return Handle!=IntPtr.Zero&&(GetWindowLong(Handle,-20)&8)!=0;}}
        internal static PresetHoverCard VisibleCard {get{return card;}}
        private PresetHoverCard(string text,bool dark,Font font,Rectangle bounds){this.text=text;this.dark=dark;this.font=font;this.bounds=bounds;}
        protected override void WndProc(ref Message m){
            if(m.Msg==0x21){m.Result=new IntPtr(3);return;}
            if(m.Msg==0x84){m.Result=new IntPtr(-1);return;}
            if(m.Msg==0x14){m.Result=new IntPtr(1);return;}
            if(m.Msg==0xF){
                PaintState state;IntPtr dc=BeginPaint(Handle,out state);
                try{if(dc!=IntPtr.Zero){
                    using(var graphics=Graphics.FromHdc(dc))
                    using(var buffer=BufferedGraphicsManager.Current.Allocate(graphics,new Rectangle(Point.Empty,bounds.Size))){
                        ThemeToolTip.Render(buffer.Graphics,new Rectangle(Point.Empty,bounds.Size),text,font,dark);buffer.Render(graphics);
                    }
                }}finally{EndPaint(Handle,ref state);}m.Result=IntPtr.Zero;return;
            }
            base.WndProc(ref m);
        }
        internal static void HideCard(){
            if(pending!=null){pending.Stop();pending.Dispose();pending=null;}
            if(card!=null){var old=card;card=null;old.Dispose();}
        }
        internal static void Display(Control source,string text,Rectangle anchor){
            HideCard();pending=new Timer {Interval=450};
            pending.Tick+=delegate{var timer=pending;pending=null;if(timer!=null)timer.Dispose();if(source.IsDisposed||!source.Visible||!anchor.Contains(Cursor.Position))return;ShowCard(source,text,anchor);};pending.Start();
        }
        private static void ShowCard(Control source,string text,Rectangle anchor){
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;float dpi;Size size;
            using(var g=source.CreateGraphics()){dpi=g.DpiX;Size measured=ThemeToolTip.Measure(g,text,source.Font,area.Width);size=new Size(Math.Min(measured.Width,Math.Max(1,area.Width-16)),Math.Min(measured.Height,Math.Max(1,area.Height-16)));}
            Point origin=ThemeToolTip.NearPointer(Cursor.Position,size,area,dpi);
            var popup=new PresetHoverCard(text,ThemeToolTip.UsesDark(source),source.Font,new Rectangle(origin,size));
            Form owner=source.FindForm();var menu=source as ContextMenuStrip;if(owner==null&&menu!=null&&menu.SourceControl!=null)owner=menu.SourceControl.FindForm();
            try {
                popup.CreateHandle(new CreateParams {
                    Caption="FGS preset summary",X=origin.X,Y=origin.Y,Width=size.Width,Height=size.Height,
                    // WS_POPUP | WS_DISABLED. Never add WS_VISIBLE during creation.
                    Style=unchecked((int)0x88000000),
                    // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST.
                    ExStyle=0x080000A8,Parent=owner==null?IntPtr.Zero:owner.Handle
                });
                if(!SetWindowPos(popup.Handle,new IntPtr(-1),origin.X,origin.Y,size.Width,size.Height,0x0010|0x0040))throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                card=popup;popup.lifetime.Tick+=delegate{if(source.IsDisposed||!source.Visible||!anchor.Contains(Cursor.Position))HideCard();};popup.lifetime.Start();
            }catch{popup.Dispose();throw;}
        }
        public void Dispose(){if(disposed)return;disposed=true;lifetime.Stop();lifetime.Dispose();if(Handle!=IntPtr.Zero)DestroyHandle();}
    }
    // The native drop-down is a separate HWND. Hit-test the hovered row, not the selected item.
    internal sealed class PresetComboBox : ThemeComboBox
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]private struct Rect {public int L,T,R,B;}
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]private struct Info {public int Size;public Rect Item,Button;public int State;public IntPtr Combo,Edit,List;}
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr window,out Rect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool GetComboBoxInfo(IntPtr window,ref Info info);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
        private HoverWindow hover;
        internal Func<FgsPreset,string> Details;
        protected override void OnDropDown(EventArgs e){base.OnDropDown(e);if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return;Info info=new Info();info.Size=System.Runtime.InteropServices.Marshal.SizeOf(typeof(Info));if(GetComboBoxInfo(Handle,ref info)){if(hover!=null)hover.ReleaseHandle();hover=new HoverWindow(this,info.List);}}
        protected override void OnDropDownClosed(EventArgs e){PresetHoverCard.HideCard();base.OnDropDownClosed(e);}
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);if(SelectedItem is FgsPreset&&Details!=null)PresetHoverCard.Display(this,Details((FgsPreset)SelectedItem),RectangleToScreen(ClientRectangle));}
        protected override void Dispose(bool disposing){if(disposing){if(hover!=null)hover.ReleaseHandle();PresetHoverCard.HideCard();}base.Dispose(disposing);}
        private sealed class HoverWindow : NativeWindow
        {
            private readonly PresetComboBox owner;private int last=-1;
            internal HoverWindow(PresetComboBox owner,IntPtr handle){this.owner=owner;AssignHandle(handle);}
            protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0x0200){long hit=SendMessage(Handle,0x01A9,IntPtr.Zero,m.LParam).ToInt64();int index=(int)(hit&0xffff);if((hit>>16)!=0||index>=owner.Items.Count)return;if(index!=last){last=index;var p=owner.Items[index] as FgsPreset;if(p!=null&&owner.Details!=null){Rect bounds;GetWindowRect(Handle,out bounds);PresetHoverCard.Display(owner,owner.Details(p),Rectangle.FromLTRB(bounds.L,bounds.T,bounds.R,bounds.B));}}}}
        }
    }
}
