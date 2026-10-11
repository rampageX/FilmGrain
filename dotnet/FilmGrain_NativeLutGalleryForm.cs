using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal static class NativeLibraryToolDialog
    {
        internal static LibraryToolResult Show(IWin32Window owner,string title,Func<CancellationToken,Action<string>,LibraryToolResult> job)
        {
            using(var dialog=new Form()) using(var timer=new System.Windows.Forms.Timer()) using(var cancel=new CancellationTokenSource()) {
                dialog.Text=title;dialog.Size=new Size(880,580);dialog.MinimumSize=new Size(680,420);dialog.StartPosition=FormStartPosition.CenterParent;
                var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));dialog.Controls.Add(layout);
                var phase=new Label{Dock=DockStyle.Fill,Text="准备执行…",AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};layout.Controls.Add(phase,0,0);
                var log=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,WordWrap=false,Font=new Font("Consolas",9)};layout.Controls.Add(log,0,1);
                var button=new Button{Text="取消",Width=110,Dock=DockStyle.Right};layout.Controls.Add(button,0,2);
                var lines=new ConcurrentQueue<string>();bool finished=false;LibraryToolResult result=null;Thread worker=null;
                Action requestCancel=delegate {if(finished){dialog.Close();return;} if(cancel.IsCancellationRequested)return;
                    if(MessageBox.Show(dialog,"取消当前工具任务？已完成的文件会保留。",title,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                    cancel.Cancel();button.Enabled=false;phase.Text="正在取消，等待进程结束和临时文件清理…";
                };
                button.Click+=delegate {requestCancel();};
                dialog.FormClosing+=delegate(object sender,FormClosingEventArgs e){if(!finished){e.Cancel=true;requestCancel();}};
                timer.Interval=100;timer.Tick+=delegate {
                    string line;int n=0;
                    while(n++<500&&lines.TryDequeue(out line)) {if(log.TextLength>250000) {log.Select(0,50000);log.SelectedText="";}
                        log.AppendText(line+Environment.NewLine);
                        if(!cancel.IsCancellationRequested && (line.StartsWith("[") || line.StartsWith("编码") || line.StartsWith("解码")))phase.Text=line;
                    }
                    if(worker==null || worker.IsAlive || finished || !lines.IsEmpty)return;
                    finished=true;timer.Stop();phase.Text=result.Summary;button.Text="关闭";button.Enabled=true;
                };
                dialog.Shown+=delegate {
                    worker=new Thread(delegate() {
                        try {result=job(cancel.Token,x=>lines.Enqueue(x));}
                        catch(OperationCanceledException){result=new LibraryToolResult{Cancelled=true};lines.Enqueue(result.Summary);}
                        catch(Exception ex){result=new LibraryToolResult{Failed=1};lines.Enqueue("失败："+ex.Message);}
                    });worker.IsBackground=true;worker.Name="FGS Native Library Utility";worker.Start();timer.Start();
                };
                dialog.ShowDialog(owner);timer.Stop();return result??new LibraryToolResult{Cancelled=true};
            }
        }
    }
    internal sealed class NativeLutGalleryForm : Form
    {
        readonly string appRoot,ffmpeg,root,previews;
        List<LutGalleryEntry> all=new List<LutGalleryEntry>(),filtered=new List<LutGalleryEntry>();
        Dictionary<string,NativeLutClassification> classes=new Dictionary<string,NativeLutClassification>();
        HashSet<string> favorites=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly TextBox search=new TextBox{Width=220};
        readonly ComboBox folders=new ComboBox{Width=250,DropDownStyle=ComboBoxStyle.DropDownList};
        readonly ComboBox pages=new ComboBox{Width=90,DropDownStyle=ComboBoxStyle.DropDownList};
        readonly FlowLayoutPanel cards=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,WrapContents=true};
        readonly Label status=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
        readonly Button smart=new Button{Text="智能过滤",AutoSize=true};
        string mode="All";int page;bool updating,smartEnabled;LutGalleryEntry selected;
        const int PageSize=24;
        internal string SelectedLut {get;private set;}
        internal bool DisabledLut {get;private set;}
        internal NativeLutGalleryForm(string appRoot,string ffmpeg,string root,string previews)
        {
            this.appRoot=appRoot;this.ffmpeg=ffmpeg;this.root=root;this.previews=previews;
            Text="电影风格 LUT 图库 · .NET";Size=new Size(1140,790);MinimumSize=new Size(900,600);StartPosition=FormStartPosition.CenterParent;KeyPreview=true;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};Controls.Add(layout);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,70));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
            var top=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};layout.Controls.Add(top,0,0);
            top.Controls.Add(new Label{Text="搜索：",AutoSize=true,Padding=new Padding(0,6,0,0)});top.Controls.Add(search);
            foreach(var item in new[]{new[]{"全部 LUT","All"},new[]{"最近使用","Recent"},new[]{"我的最爱","Favorites"}}) {
                string target=item[1];var b=new Button{Text=item[0],AutoSize=true};b.Click+=delegate{mode=target;Filter();};top.Controls.Add(b);
            }
            top.Controls.Add(new Label{Text="文件夹：",AutoSize=true,Padding=new Padding(0,6,0,0)});top.Controls.Add(folders);
            layout.Controls.Add(cards,0,1);layout.Controls.Add(status,0,2);
            var bottom=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};layout.Controls.Add(bottom,0,3);
            var previous=new Button{Text="< 上一页",AutoSize=true};var next=new Button{Text="下一页 >",AutoSize=true};
            bottom.Controls.Add(previous);bottom.Controls.Add(pages);bottom.Controls.Add(next);bottom.Controls.Add(smart);
            var change=new Button{Text="更换参考图",AutoSize=true};var update=new Button{Text="更新预览图",AutoSize=true};
            var use=new Button{Text="使用选中的 LUT",AutoSize=true};var none=new Button{Text="禁用 LUT",AutoSize=true};
            bottom.Controls.Add(change);bottom.Controls.Add(update);bottom.Controls.Add(use);bottom.Controls.Add(none);
            previous.Click+=delegate{if(page>0){page--;RenderPage();}};next.Click+=delegate{if((page+1)*PageSize<filtered.Count){page++;RenderPage();}};
            pages.SelectedIndexChanged+=delegate{if(!updating&&pages.SelectedIndex>=0){page=pages.SelectedIndex;RenderPage();}};
            search.TextChanged+=delegate{if(!updating)Filter();};folders.SelectedIndexChanged+=delegate{if(!updating)Filter();};
            use.Click+=delegate{Commit(selected);};none.Click+=delegate{DisabledLut=true;DialogResult=DialogResult.OK;Close();};
            smart.Click+=delegate {
                try {var cfg=new FgsConfig(appRoot,null);cfg.SaveValue("SMART_FILTER_ENABLED",smartEnabled?"false":"true");smartEnabled=!smartEnabled;UpdateSmart();Filter();}
                catch(Exception ex){MessageBox.Show(this,"无法保存智能过滤状态："+ex.Message);}
            };
            update.Click+=delegate{Build(false);};change.Click+=delegate{Build(true);};
            KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Escape)Close();if(e.KeyCode==Keys.Enter&&selected!=null)Commit(selected);};
            Shown+=delegate {Reload();};
        }
        void UpdateSmart(){smart.Text=smartEnabled?"智能过滤：开":"智能过滤";smart.BackColor=smartEnabled?Color.LightSteelBlue:SystemColors.Control;}
        void Reload()
        {
            string current=folders.SelectedItem as string;updating=true;
            try {
                all=NativeLibraryTools.GalleryEntries(root,previews);classes=NativeLutSmartFilter.Classify(all);
                try {NativeLutSmartFilter.Report(previews,all,classes);}catch(Exception ex){status.Text="报告保存失败："+ex.Message;}
                var cfg=new FgsConfig(appRoot,null);smartEnabled=new[]{"true","1","yes","on"}.Contains(cfg.Get("SMART_FILTER_ENABLED").ToLowerInvariant());UpdateSmart();
                var dirs=new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var e in all) {string d=Path.GetDirectoryName(e.Relative);while(!string.IsNullOrEmpty(d)){dirs.Add(d);d=Path.GetDirectoryName(d);}}
                folders.Items.Clear();folders.Items.Add("全部文件夹");foreach(string d in dirs)folders.Items.Add(d);
                folders.SelectedIndex=current!=null&&folders.Items.Contains(current)?folders.Items.IndexOf(current):0;
            } catch(Exception ex){MessageBox.Show(this,"图库读取失败："+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);}
            finally{updating=false;Filter();}
        }
        List<Dictionary<string,object>> ReadRecords(string name)
        {
            try{return NativeLutRecords.Read(Path.Combine(previews,name)).Where(x=>{
                try{NativeLutRecords.Key(Convert.ToString(x["LutPath"]));return true;}catch{return false;}
            }).ToList();}catch{return new List<Dictionary<string,object>>();}
        }
        void Filter()
        {
            favorites=new HashSet<string>(ReadRecords("_LUT_GALLERY_FAVORITES.json").Select(x=>NativeLutRecords.Key(Convert.ToString(x["LutPath"]))),StringComparer.OrdinalIgnoreCase);
            IEnumerable<LutGalleryEntry> source=all;
            if(mode=="Favorites")source=source.Where(x=>favorites.Contains(NativeLutRecords.Key(x.LutPath)));
            if(mode=="Recent") {
                var lookup=all.ToDictionary(x=>NativeLutRecords.Key(x.LutPath),StringComparer.OrdinalIgnoreCase);
                source=ReadRecords("_LUT_GALLERY_RECENT.json").Select(x=>NativeLutRecords.Key(Convert.ToString(x["LutPath"]))).Distinct(StringComparer.OrdinalIgnoreCase).Where(x=>lookup.ContainsKey(x)).Take(25).Select(x=>lookup[x]);
            }
            string folder=folders.SelectedIndex>0?Convert.ToString(folders.SelectedItem):"",q=search.Text.Trim();
            filtered=source.Where(x=> (folder.Length==0||x.Relative.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) &&
                (q.Length==0||(x.Name+" "+x.Relative).IndexOf(q,StringComparison.OrdinalIgnoreCase)>=0) && (!smartEnabled||!classes[x.LutPath].Technical)).ToList();
            page=0;selected=null;RenderPage();
        }
        void ClearCards()
        {
            var old=cards.Controls.Cast<Control>().ToArray();cards.Controls.Clear();
            foreach(var c in old){foreach(var pic in c.Controls.OfType<PictureBox>()) {var image=pic.Image;pic.Image=null;if(image!=null)image.Dispose();}c.Dispose();}
        }
        void RenderPage()
        {
            cards.SuspendLayout();ClearCards();int total=Math.Max(1,(filtered.Count+PageSize-1)/PageSize);page=Math.Max(0,Math.Min(page,total-1));updating=true;
            pages.Items.Clear();for(int n=1;n<=total;n++)pages.Items.Add(n+" / "+total);pages.SelectedIndex=page;updating=false;
            foreach(var e in filtered.Skip(page*PageSize).Take(PageSize)) {
                var card=new Panel{Width=254,Height=190,Margin=new Padding(5),BorderStyle=BorderStyle.FixedSingle};
                var pic=new PictureBox{Width=240,Height=135,Left=6,Top=4,SizeMode=PictureBoxSizeMode.Zoom};
                try {pic.Image=LoadThumbnail(e.PreviewPath);}catch { }
                var name=new Label{Text=e.Name,Left=6,Top=144,Width=204,Height=36,AutoEllipsis=true};
                var fav=new Button{Text=favorites.Contains(NativeLutRecords.Key(e.LutPath))?"★":"☆",Left=215,Top=143,Width=31,Height=30};
                var tip=new ToolTip();tip.SetToolTip(name,e.Relative);card.Disposed+=delegate{tip.Dispose();};
                Action choose=delegate{selected=e;status.Text=e.Relative;foreach(Control c in cards.Controls)c.BackColor=c==card?Color.LightSteelBlue:SystemColors.Control;};
                pic.Click+=delegate{choose();};name.Click+=delegate{choose();};card.Click+=delegate{choose();};
                pic.DoubleClick+=delegate{Commit(e);};name.DoubleClick+=delegate{Commit(e);};card.DoubleClick+=delegate{Commit(e);};
                fav.Click+=delegate{try {NativeLutRecords.ToggleFavorite(previews,e.LutPath);Filter();}catch(Exception ex){MessageBox.Show(this,"收藏保存失败，原记录保留："+ex.Message);}};
                var menu=new ContextMenuStrip();menu.Items.Add("打开 LUT 所在文件夹",null,delegate{Open(Path.GetDirectoryName(e.LutPath));});menu.Items.Add("打开完整尺寸预览图",null,delegate{Open(e.PreviewPath);});
                card.ContextMenuStrip=menu;pic.ContextMenuStrip=menu;name.ContextMenuStrip=menu;card.Disposed+=delegate{menu.Dispose();};
                card.Controls.Add(pic);card.Controls.Add(name);card.Controls.Add(fav);cards.Controls.Add(card);
            }
            cards.ResumeLayout();status.Text="匹配 "+filtered.Count+" / 共 "+all.Count+"；双击缩略图使用 LUT"+(smartEnabled?"；隐藏纯功能 LUT，保留 Combined":"");
        }
        Bitmap LoadThumbnail(string path)
        {
            string cacheRoot=Path.Combine(previews,"_GALLERY_THUMBS_v3_240x135");Directory.CreateDirectory(cacheRoot);
            string hash;
            using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-","").ToLowerInvariant();
            string cache=Path.Combine(cacheRoot,hash+".jpg");
            if(!File.Exists(cache)||File.GetLastWriteTimeUtc(cache)<File.GetLastWriteTimeUtc(path)) {
                using(var stream=new MemoryStream(File.ReadAllBytes(path)))using(var source=Image.FromStream(stream))using(var thumb=new Bitmap(240,135)) {
                    using(var graphics=Graphics.FromImage(thumb)) {
                        graphics.Clear(Color.Black);graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        double ratio=Math.Min(240.0/source.Width,135.0/source.Height);int width=(int)(source.Width*ratio),height=(int)(source.Height*ratio);
                        graphics.DrawImage(source,(240-width)/2,(135-height)/2,width,height);
                    }
                    string temp=cache+".tmp_"+Guid.NewGuid().ToString("N");
                    try{thumb.Save(temp,System.Drawing.Imaging.ImageFormat.Jpeg);NativeLibraryTools.Publish(temp,cache);}finally{NativeLibraryTools.DeleteTemp(temp);}
                }
            }
            using(var stream=new MemoryStream(File.ReadAllBytes(cache)))using(var image=Image.FromStream(stream))return new Bitmap(image);
        }
        void Open(string path){try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(this,ex.Message);}}
        void Commit(LutGalleryEntry entry)
        {
            if(entry==null)return;
            if(!File.Exists(entry.LutPath)){MessageBox.Show(this,"LUT 已不存在，请更新图库。");return;}
            try{NativeLutRecords.Recent(previews,entry.LutPath);}catch(Exception ex){MessageBox.Show(this,"LUT 已选择，最近使用保存失败，原记录保留："+ex.Message);}
            SelectedLut=entry.LutPath;DialogResult=DialogResult.OK;Close();
        }
        void Build(bool newReference)
        {
            string tools=Path.Combine(appRoot,"_LUT_Tools"),current=Path.Combine(tools,"LUT_Reference_Current.jpg");
            string reference=File.Exists(current)?current:Path.Combine(tools,"LUT_Reference_Default.jpg");
            if(newReference) using(var pick=new OpenFileDialog{Title="更换参考图",Filter="图像|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp|所有文件|*.*"}) {
                if(pick.ShowDialog(this)!=DialogResult.OK)return;reference=pick.FileName;
            }
            if(MessageBox.Show(this,newReference?"使用新参考图重新生成全部 LUT 预览？":"补齐缺失预览，并同步删除已移除 LUT 的对应生成预览？",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            string source=reference;
            NativeLibraryToolDialog.Show(this,newReference?"更换参考图并重建预览":"更新 LUT 预览",delegate(CancellationToken token,Action<string> log){
                if(newReference) {
                    Directory.CreateDirectory(tools);string temp=current+".tmp_"+Guid.NewGuid().ToString("N")+".jpg";
                    try {
                        log("保存当前参考图");
                        if(NativeLibraryTools.RunProcess(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-y","-i",source,"-frames:v","1","-q:v","2",temp},tools,token,log)!=0)throw new IOException("参考图转换失败");
                        token.ThrowIfCancellationRequested();NativeLibraryTools.Publish(temp,current);
                    }finally{NativeLibraryTools.DeleteTemp(temp);}source=current;
                }
                return NativeLibraryTools.BuildPreviews(root,previews,source,ffmpeg,newReference,!newReference,token,log,NativeLibraryTools.RunProcess);
            });Reload();
        }
        protected override void Dispose(bool disposing){if(disposing)ClearCards();base.Dispose(disposing);}
    }
}
