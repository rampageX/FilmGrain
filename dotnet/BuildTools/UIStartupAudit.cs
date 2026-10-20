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
     tabs.SelectedIndex=0;TabPage enc=tabs.TabPages[0];
     string[] keys={"AV1_MULTIPASS","HEVC_MULTIPASS","H264_MULTIPASS","HEVC_BFRAMES","H264_BFRAMES","HEVC_BREF_MODE","H264_BREF_MODE","HEVC_LOOKAHEAD","H264_LOOKAHEAD"};
     foreach(string key in keys){
      Control[] found=enc.Controls.Find(key,false);
      if(found.Length!=1||string.IsNullOrWhiteSpace(found[0].AccessibleDescription))throw new Exception("Missing recommendation tooltip: "+key);
      string text=found[0].AccessibleDescription;
      string required=key.EndsWith("MULTIPASS")?"QRes":key.EndsWith("BFRAMES")?"4":key.EndsWith("BREF_MODE")?"middle":"27";
      if(!text.Contains(required))throw new Exception("Missing concrete recommendation: "+key);
     }
     Control aq=enc.Controls.Find("NVENC_SPATIAL_AQ",false)[0],taq=enc.Controls.Find("NVENC_TEMPORAL_AQ",false)[0];
     if(aq.Enabled!=spatial||taq.Enabled!=temporal)throw new Exception("AQ capability state mismatch: Spatial="+aq.Enabled+" Temporal="+taq.Enabled);
     if(!aq.AccessibleDescription.Contains("8")||!aq.AccessibleDescription.Contains("x264")||!taq.AccessibleDescription.Contains("x264")||!taq.AccessibleDescription.Contains("UHQ"))throw new Exception("AQ concrete scope/default tooltip missing");
     Control audio=enc.Controls.Find("AUDIO_BITRATE",false)[0],note=enc.Controls.Find("AUDIO_INFO",false)[0],mp=enc.Controls.Find("AV1_MULTIPASS",false)[0];
     if(note.Top<audio.Bottom||note.Bottom>mp.Top)throw new Exception("Audio note is not between AAC bitrate and Multipass");
     foreach(Control a in enc.Controls)foreach(Control b in enc.Controls)if(!object.ReferenceEquals(a,b)&&a.Bounds.IntersectsWith(b.Bounds))throw new Exception("Overlapping encoding controls: "+a.Text+" / "+b.Text);
     inspected=true;
     lines.Add("PASS advanced dialog: all three pages fit; no scrolling or clipped controls");
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
