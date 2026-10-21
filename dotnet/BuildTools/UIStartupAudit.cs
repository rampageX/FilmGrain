using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
internal static class UIStartupSmoke {
 private static string visualDirectory;
 private static readonly List<string> metrics=new List<string>{"surface,name,type,left,top,width,height,text"};
 [STAThread] private static int Main(string[] args) {
  var lines=new List<string>();Form form=null;
  try {
   if(args.Length!=2)throw new ArgumentException("Expected application EXE and report paths");
   visualDirectory=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])),"Visual_R4");Directory.CreateDirectory(visualDirectory);
   lines.Add("Runner base directory: "+AppDomain.CurrentDomain.BaseDirectory);
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
   var asm=Assembly.LoadFrom(Path.GetFullPath(args[0]));
   var type=asm.GetType("FilmGrainStudioPreview.MainForm",true);
   var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
   form=(Form)Activator.CreateInstance(type,flags,null,new object[]{true},null);
   // UI events legitimately save settings. Route every subsequent write to a
   // test-owned INI before exercising controls; never inject only in-memory values.
   object isolatedConfig=type.GetField("config",flags).GetValue(form);
   string isolatedDirectory=Path.Combine(Path.GetDirectoryName(visualDirectory),"UI_Config 中文 ' [],; & %");Directory.CreateDirectory(isolatedDirectory);
   isolatedConfig.GetType().GetField("configPath",flags).SetValue(isolatedConfig,Path.Combine(isolatedDirectory,"FilmGrain_Config.ini"));
   isolatedConfig.GetType().GetMethod("Load",flags).Invoke(isolatedConfig,null);
   lines.Add("UI event configuration isolated inside the project audit directory");
   lines.Add("PASS actual MainForm constructor with hardware capabilities not loaded");
   var update=type.GetMethod("UpdateSfeUi",flags);
   var codec=(ComboBox)type.GetField("cmbCodec",flags).GetValue(form);
   var speed=(ComboBox)type.GetField("cmbSpeed",flags).GetValue(form);
   for(int i=0;i<4;i++) {
    codec.SelectedIndex=i;update.Invoke(form,null);
    if(codec.SelectedIndex!=i)throw new Exception("Codec selection did not remain at "+i);
    if(speed.Items.Count>1){speed.SelectedIndex=1;update.Invoke(form,null);}
    lines.Add("PASS codec selection / speed / SFE events: index "+i);
   }
   type.GetField("startupIdleStarted",flags).SetValue(form,true);
   form.Show();form.Refresh();
   if(!form.Visible||!form.IsHandleCreated)throw new Exception("Main window did not become visible");
   lines.Add("PASS actual main window show / first paint");
   using(var graphics=form.CreateGraphics())lines.Add("Observed DPI: "+graphics.DpiX+" x "+graphics.DpiY+"; no simulated DPI coverage claimed");
   foreach(string theme in new[]{"LIGHT","DARK"}){
    type.GetField("selectedThemeMode",flags).SetValue(form,theme);
    type.GetMethod("ApplySelectedTheme",flags,null,Type.EmptyTypes,null).Invoke(form,null);
    for(int i=0;i<4;i++){codec.SelectedIndex=i;form.Refresh();Application.DoEvents();Capture(form,theme+"_codec_"+i);}
    CheckAdvanced(form,type,flags,lines,true,true,theme);
   }
   CheckAqCapabilities(form,type,flags,lines);
   CheckCapturedAq(form,type,flags,lines);
   File.WriteAllLines(Path.Combine(visualDirectory,"ControlBounds.csv"),metrics,new UTF8Encoding(true));
   form.Close();form.Dispose();form=null;
   lines.Add("PASS main window close");
   File.WriteAllLines(args[1],lines,new UTF8Encoding(true));
   foreach(string line in lines)Console.WriteLine(line);
   return 0;
  }catch(Exception ex){
   lines.Add("FAIL "+ex.ToString());
   if(args.Length==2)File.WriteAllLines(args[1],lines,new UTF8Encoding(true));
   foreach(string line in lines)Console.Error.WriteLine(line);return 1;
  }finally{if(form!=null){form.Close();form.Dispose();}}
 }

 private static void CheckAdvanced(Form main,Type type,BindingFlags flags,List<string> lines,bool spatial,bool temporal,string captureTag) {
  Exception failure=null;bool inspected=false;
  using(var timer=new Timer()) {
   timer.Interval=150;
   timer.Tick+=delegate {
    Form dialog=null;
    foreach(Form candidate in Application.OpenForms)if(candidate!=main&&candidate.Controls.Count>0){
     foreach(Control control in candidate.Controls)if(control is TabControl){dialog=candidate;break;}
     if(dialog!=null)break;
    }
    if(dialog==null)return;
    timer.Stop();
    try {
     TabControl tabs=null;foreach(Control control in dialog.Controls)if(control is TabControl)tabs=(TabControl)control;
     if(tabs==null)throw new Exception("Advanced tabs missing");
     foreach(TabPage page in tabs.TabPages) {
      tabs.SelectedTab=page;dialog.Refresh();Application.DoEvents();if(captureTag!=null)Capture(dialog,captureTag+"_advanced_"+tabs.SelectedIndex);
      if(page.AutoScroll||page.HorizontalScroll.Visible||page.VerticalScroll.Visible)throw new Exception("Advanced page has scrolling: "+page.Text);
      foreach(Control control in page.Controls)if(!page.ClientRectangle.Contains(control.Bounds))throw new Exception("Advanced control clipped: "+page.Text+" / "+control.Text+" / "+control.Bounds);
     }
     if(tabs.TabCount!=4||tabs.TabPages[0].Text!="AV1 / HEVC"||tabs.TabPages[1].Text!="H.264 / x264")throw new Exception("Expected split encoder pages");
     tabs.SelectedIndex=0;TabPage enc=tabs.TabPages[0];
     string[] keys={"AV1_MULTIPASS","HEVC_MULTIPASS","H264_MULTIPASS","HEVC_BFRAMES","H264_BFRAMES","HEVC_BREF_MODE","H264_BREF_MODE","HEVC_LOOKAHEAD","H264_LOOKAHEAD"};
     foreach(string key in keys){
      Control[] found=tabs.Controls.Find(key,true);
      if(found.Length!=1||string.IsNullOrWhiteSpace(found[0].AccessibleDescription))throw new Exception("Missing recommendation tooltip: "+key);
      string text=found[0].AccessibleDescription;
      string required=key.EndsWith("MULTIPASS")?"QRes":key.EndsWith("BFRAMES")?"4":key.EndsWith("BREF_MODE")?"middle":"27";
      if(!text.Contains(required))throw new Exception("Missing concrete recommendation: "+key);
     }
     object caps=type.GetField("hardwareCaps",flags).GetValue(main);bool ready=(bool)type.GetField("hardwareCapsReady",flags).GetValue(main);
     foreach(string encoder in new[]{"HEVC","H264"}) {
      var aq=(ComboBox)tabs.Controls.Find(encoder+"_SPATIAL_AQ",true)[0];var taq=(CheckBox)tabs.Controls.Find(encoder+"_TEMPORAL_AQ",true)[0];
      string prefix=encoder=="HEVC"?"Hevc":"H264";
      bool available=!ready||caps==null||(bool)caps.GetType().GetProperty(prefix+"Available").GetValue(caps,null);
      bool sa=available&&(!ready||caps==null||(bool)caps.GetType().GetProperty(prefix+"SpatialAq").GetValue(caps,null));
      bool ta=available&&(!ready||caps==null||(bool)caps.GetType().GetProperty(prefix+"TemporalAq").GetValue(caps,null));
      if(aq.Enabled!=sa||taq.Enabled!=ta)throw new Exception(encoder+" AQ capability isolation failed");
      if(!aq.AccessibleDescription.Contains("8")||!taq.AccessibleDescription.Contains("x264"))throw new Exception("AQ scope/default tooltip missing");
      if(aq.Top!=taq.Top||aq.Right>=taq.Left)throw new Exception("SAQ/TAQ must share a row without overlap");
     }
     Control audio=enc.Controls.Find("AUDIO_BITRATE",false)[0],note=enc.Controls.Find("AUDIO_INFO",false)[0];
     if(note.Top<audio.Bottom)throw new Exception("Audio note must be below AAC bitrate");
     for(int page=0;page<2;page++)foreach(Control a in tabs.TabPages[page].Controls)foreach(Control b in tabs.TabPages[page].Controls)if(!object.ReferenceEquals(a,b)&&a.Bounds.IntersectsWith(b.Bounds))throw new Exception("Overlapping encoding controls: "+a.Text+" / "+b.Text);
     CheckPageDefaults(dialog,tabs);
     inspected=true;
     lines.Add("PASS advanced dialog: all four pages fit; no scrolling or clipped controls; independent page defaults");
     lines.Add("PASS encoding page: audio note below AAC bitrate; no control overlaps");
     lines.Add("PASS nine FGS recommendation controls expose concrete values; AQ capability state and default/scope tooltips checked");
    }catch(Exception ex){failure=ex;}
    finally{dialog.DialogResult=DialogResult.Cancel;dialog.Close();}
   };
   timer.Start();
   type.GetMethod("ShowAdvancedSettingsDialog",flags,null,new Type[]{typeof(int)},null).Invoke(main,new object[]{0});
  }
  if(failure!=null)throw new Exception("Advanced settings regression failed",failure);
  if(!inspected)throw new Exception("Advanced settings were not inspected");
 }
 private static void CheckCapturedAq(Form main,Type type,BindingFlags flags,List<string> lines){
  object config=type.GetField("config",flags).GetValue(main);
  string configPath=(string)config.GetType().GetProperty("ConfigPath").GetValue(config,null);byte[] previous=File.ReadAllBytes(configPath);
  var codec=(ComboBox)type.GetField("cmbCodec",flags).GetValue(main);int oldCodec=codec.SelectedIndex;
  var grain=(CheckBox)type.GetField("chkGrainEnabled",flags).GetValue(main);var lut=(CheckBox)type.GetField("chkLut",flags).GetValue(main);bool oldGrain=grain.Checked,oldLut=lut.Checked;
  var interpolation=(CheckBox)type.GetField("chkInterpolation",flags).GetValue(main);bool oldInterpolation=interpolation.Checked;
  try {
   var updates=new Dictionary<string,string>{{"HEVC_SPATIAL_AQ","4"},{"HEVC_TEMPORAL_AQ","false"},{"H264_SPATIAL_AQ","15"},{"H264_TEMPORAL_AQ","true"},{"NVENC_SPATIAL_AQ","12"}};
   config.GetType().GetMethod("Save",flags).Invoke(config,new object[]{updates});
   foreach(int index in new[]{0,1,3}){
    codec.SelectedIndex=index;grain.Checked=false;lut.Checked=false;interpolation.Checked=false;
    object[] args={null};object snapshot=type.GetMethod("CaptureBridgeEnvironmentSnapshot",flags).Invoke(main,args);
    if(snapshot==null)throw new Exception("Actual UI capture failed: "+args[0]);
    string actual=(string)snapshot.GetType().GetField("HevcSpatialAq").GetValue(snapshot);
    bool temporal=(bool)snapshot.GetType().GetField("HevcTemporalAq").GetValue(snapshot);
    string expected=index==0?"12":index==1?"4":"15";
    if(codec.SelectedIndex!=index||actual!=expected||(index!=0&&temporal!=(index==3)))throw new Exception("Per-encoder AQ capture mismatch: requested codec="+index+", selected="+codec.SelectedIndex+", expected SAQ="+expected+", actual="+actual+", TAQ="+temporal);
    lines.Add("PASS actual capture codec="+index+" SAQ="+actual+" TAQ="+temporal+" after ordinary UI events and config reload");
   }
   lines.Add("PASS actual UI state capture: AV1 preserved; HEVC 4/off; H264 15/on");
  } finally {File.WriteAllBytes(configPath,previous);config.GetType().GetMethod("Load",flags).Invoke(config,null);codec.SelectedIndex=oldCodec;grain.Checked=oldGrain;lut.Checked=oldLut;interpolation.Checked=oldInterpolation;}
 }
 private static void CheckPageDefaults(Form dialog,TabControl tabs){
  var hevc=(ComboBox)tabs.Controls.Find("HEVC_SPATIAL_AQ",true)[0];var h264=(ComboBox)tabs.Controls.Find("H264_SPATIAL_AQ",true)[0];
  var ht=(CheckBox)tabs.Controls.Find("HEVC_TEMPORAL_AQ",true)[0];var nt=(CheckBox)tabs.Controls.Find("H264_TEMPORAL_AQ",true)[0];
  var reset=(Button)dialog.Controls.Find("PAGE_DEFAULTS",false)[0];
  hevc.SelectedIndex=5;ht.Checked=true;h264.SelectedIndex=1;nt.Checked=true;
  tabs.SelectedIndex=0;reset.PerformClick();
  if(hevc.SelectedIndex!=2||ht.Checked||h264.SelectedIndex!=1||!nt.Checked)throw new Exception("AV1/HEVC reset changed H264 settings");
  hevc.SelectedIndex=5;ht.Checked=true;tabs.SelectedIndex=1;reset.PerformClick();
  if(h264.SelectedIndex!=2||nt.Checked||hevc.SelectedIndex!=5||!ht.Checked)throw new Exception("H264/x264 reset changed HEVC settings");
 }
 private static void CheckAqCapabilities(Form main,Type type,BindingFlags flags,List<string> lines){
  var field=type.GetField("hardwareCaps",flags);var ready=type.GetField("hardwareCapsReady",flags);
  object previous=field.GetValue(main);object previousReady=ready.GetValue(main);
  var capsType=type.Assembly.GetType("FilmGrainStudioPreview.HardwareCapabilitySnapshot",true);
  try{
   string[][] cases={new[]{"H264Available"},new[]{"H264Available","H264SpatialAq"},new[]{"Av1Available","Av1TemporalAq"},new[]{"HevcAvailable","HevcSpatialAq","HevcTemporalAq"},new[]{"H264SpatialAq","H264TemporalAq"}};
   bool[] spatial={false,true,false,true,false},temporal={false,false,true,true,false};
   for(int i=0;i<cases.Length;i++){
    object caps=Activator.CreateInstance(capsType,true);capsType.GetProperty("Ready").SetValue(caps,true,null);
    foreach(string key in cases[i])capsType.GetProperty(key).SetValue(caps,true,null);
    field.SetValue(main,caps);ready.SetValue(main,true);CheckAdvanced(main,type,flags,lines,spatial[i],temporal[i],null);
    lines.Add("PASS synthetic capability case "+i+": "+string.Join(",",cases[i])+" -> Spatial="+spatial[i]+" Temporal="+temporal[i]);
   }
  }finally{field.SetValue(main,previous);ready.SetValue(main,previousReady);}
 }
 private static void Capture(Form form,string name){
  form.Refresh();Application.DoEvents();
  using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(visualDirectory,name+".png"),ImageFormat.Png);}
  Record(form,name);
 }
 private static void Record(Control control,string surface){
  metrics.Add(string.Join(",",new[]{Csv(surface),Csv(control.Name),Csv(control.GetType().Name),control.Left.ToString(),control.Top.ToString(),control.Width.ToString(),control.Height.ToString(),Csv(control.Text)}));
  foreach(Control child in control.Controls)Record(child,surface);
 }
 private static string Csv(string text){return "\""+(text??"").Replace("\"","\"\"").Replace("\r"," ").Replace("\n"," ")+"\"";}

}
