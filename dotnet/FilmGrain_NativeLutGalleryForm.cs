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
                var phase=new ThemeLabel{Dock=DockStyle.Fill,Text="准备执行…",AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};layout.Controls.Add(phase,0,0);
                var log=new RichTextBox{Dock=DockStyle.Fill,ReadOnly=true,WordWrap=false,Font=new Font("Consolas",9)};var logFrame=new ThemeLogFrame(log){Margin=log.Margin};layout.Controls.Add(logFrame,0,1);
                var button=new ThemeButton{Text="取消",Width=110,Dock=DockStyle.Right};layout.Controls.Add(button,0,2);
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
                MainForm.AttachOwnedTheme(dialog, owner, log);
                dialog.ShowDialog(owner);timer.Stop();return result??new LibraryToolResult{Cancelled=true};
            }
        }
    }
    internal sealed partial class NativeLutGalleryForm : Form, IMessageFilter
    {
        // Buffer only the fixed control containers, without changing thumbnail rendering.
        private sealed class GalleryLayoutPanel : TableLayoutPanel
        {
            internal GalleryLayoutPanel(){DoubleBuffered=true;ResizeRedraw=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
            protected override void OnPaintBackground(PaintEventArgs e)
            {using(var brush=new SolidBrush(BackColor))e.Graphics.FillRectangle(brush,ClientRectangle);}
        }
        private sealed class GalleryControlsPanel : FlowLayoutPanel
        {
            internal GalleryControlsPanel(){DoubleBuffered=true;ResizeRedraw=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
            protected override CreateParams CreateParams
            {
                get {var cp=base.CreateParams;cp.ExStyle|=0x02000000;return cp;}
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {using(var brush=new SolidBrush(BackColor))e.Graphics.FillRectangle(brush,ClientRectangle);}
        }

        internal void PrepareTheme(IWin32Window owner)
        {
            MainForm.AttachOwnedTheme(this,owner,cards);
            if(galleryLoaded)return;
            // Complete size restoration and synchronous I/O before entering ShowDialog.
            // A visible native window must not wait for the initial catalog/thumbnail load.
            RestoreGallerySize();
            Reload();
            galleryLoaded=true;
            if(!IgnoreGallerySize)try {
                bool restore=new[]{"true","1","yes","on"}.Contains(new FgsConfig(appRoot,null).Get("LUT_GALLERY_FIXED").ToLowerInvariant());
                restoreFixedPending=restore;
            }catch(Exception ex){System.Diagnostics.Trace.WriteLine("Gallery lock restore: "+ex.Message);}
            PerformLayout();cards.PerformLayout();
        }
        protected override void OnLoad(EventArgs e)
        {
            PrepareTheme(Owner);
            base.OnLoad(e);
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x0014&&m.WParam!=IntPtr.Zero) {
                using(var graphics=Graphics.FromHdc(m.WParam))
                using(var brush=new SolidBrush(BackColor))graphics.FillRectangle(brush,ClientRectangle);
                m.Result=new IntPtr(1);return;
            }
            base.WndProc(ref m);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if(!wheelFilterInstalled){Application.AddMessageFilter(this);wheelFilterInstalled=true;}
            if(restoreFixedPending)BeginInvoke((MethodInvoker)delegate{
                if(IsDisposed||Disposing)return;restoreFixedPending=false;
                PerformLayout();cards.PerformLayout();
                if(FitsFullPage())fixedView.Checked=true;else ApplyFixedView();
                ValidateFixedSpace();
            });
            else ValidateFixedSpace();
        }
        bool IgnoreGallerySize { get { return Environment.GetEnvironmentVariable("FGS_AUTOMATION")=="1"
            || Environment.GetEnvironmentVariable("FGS_NO_WINDOW_STATE")=="1"; } }
        void RestoreGallerySize()
        {
            if(IgnoreGallerySize)return;
            try {
                var cfg=new FgsConfig(appRoot,null);
                string[] value=cfg.Get("LUT_GALLERY_SIZE").Split(',');
                // Legacy fixed windows used thinner non-client borders than an unlocked
                // form. Restore that frame first so their saved outer size remains valid.
                bool wasFixed=cfg.GetBool("LUT_GALLERY_FIXED");
                if(wasFixed){FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;}
                int w,h;
                if(value.Length!=2 || !int.TryParse(value[0],out w) || !int.TryParse(value[1],out h)
                    || w<MinimumSize.Width || h<MinimumSize.Height || w>32767 || h>32767)return;
                Rectangle area=Owner==null?Screen.FromControl(this).WorkingArea:Screen.FromControl(Owner).WorkingArea;
                Size=new Size(Math.Max(MinimumSize.Width,Math.Min(w,area.Width)),
                    Math.Max(MinimumSize.Height,Math.Min(h,area.Height)));
                string[] client=cfg.Get("LUT_GALLERY_CLIENT_SIZE").Split(',');int cw,ch;
                if(client.Length==2&&int.TryParse(client[0],out cw)&&int.TryParse(client[1],out ch)&&cw>0&&ch>0&&cw<=32767&&ch<=32767){
                    Size outer=SizeFromClientSize(new Size(cw,ch));
                    if(outer.Width<=area.Width&&outer.Height<=area.Height&&outer.Width>=MinimumSize.Width&&outer.Height>=MinimumSize.Height)ClientSize=new Size(cw,ch);
                }
            }catch(Exception ex){System.Diagnostics.Trace.WriteLine("Gallery size restore: "+ex.Message);}
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closingGallery=true;fixedSpaceTimer.Stop();
            if(!IgnoreGallerySize)try {
                Size size=WindowState==FormWindowState.Normal?Size:RestoreBounds.Size;
                if(size.Width>=MinimumSize.Width && size.Height>=MinimumSize.Height)
                    new FgsConfig(appRoot,null).Save(new Dictionary<string,string>{{"LUT_GALLERY_SIZE",size.Width+","+size.Height},{"LUT_GALLERY_FIXED",fixedView.Checked?"true":"false"},{"LUT_GALLERY_CLIENT_SIZE",WindowState==FormWindowState.Normal?ClientSize.Width+","+ClientSize.Height:""}});
            }catch(Exception ex){System.Diagnostics.Trace.WriteLine("Gallery size save: "+ex.Message);}
            fixedSpaceTimer.Stop();
            RemoveWheelFilter();
            base.OnFormClosed(e);
        }
        sealed class CardSlot
        {
            internal LutGalleryEntry Entry;
            internal ThemeSelectionPanel Card;
            internal PictureBox Picture;
            internal Label Name;
            internal Button Favorite;
            internal ToolTip Tip;
            internal string ImagePath;
            internal DateTime ImageStamp;
        }
        readonly List<CardSlot> cardSlots=new List<CardSlot>();
        readonly Bitmap favoriteStar=CreateFavoriteStar();
        readonly string appRoot,ffmpeg,root,previews;
        List<LutGalleryEntry> all=new List<LutGalleryEntry>(),filtered=new List<LutGalleryEntry>();
        Dictionary<string,NativeLutClassification> classes=new Dictionary<string,NativeLutClassification>();
        HashSet<string> favorites=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly TextBox search=new ThemeTextBox{Width=220};
        readonly ComboBox folders=new ThemeComboBox{ThemePopupScrollbars=true,Width=250,DropDownStyle=ComboBoxStyle.DropDownList};
        readonly ComboBox pages=new ThemeComboBox{Width=90,DropDownStyle=ComboBoxStyle.DropDownList};
        readonly FlowLayoutPanel cards=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,WrapContents=true};
        readonly Label status=new ThemeLabel{Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
        readonly Button smart=new ThemeButton{Text="智能过滤",AutoSize=true};
        readonly CheckBox fixedView=new ThemeCheckBox{Text="固定界面",AutoSize=true,Margin=new Padding(8,6,3,3)};
        bool changingFixed,wheelFilterInstalled,wheelQueued,galleryLoaded,renderingCards,restoreFixedPending,closingGallery;
        readonly System.Windows.Forms.Timer fixedSpaceTimer=new System.Windows.Forms.Timer{Interval=180};
        int wheelRemainder,wheelDirection;
        long lastWheelPage=-1;
        string mode="All";int page;bool updating,smartEnabled;LutGalleryEntry selected;
        const int PageSize=24;
        internal string SelectedLut {get;private set;}
        internal bool DisabledLut {get;private set;}
        internal NativeLutGalleryForm(string appRoot,string ffmpeg,string root,string previews)
        {
            this.appRoot=appRoot;this.ffmpeg=ffmpeg;this.root=root;this.previews=previews;
            DoubleBuffered=true;
            Text="电影风格 LUT 图库 · .NET";Size=new Size(1100,790);MinimumSize=new Size(900,600);StartPosition=FormStartPosition.CenterParent;KeyPreview=true;
            var layout=new GalleryLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};Controls.Add(layout);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            var top=new GalleryControlsPanel{Dock=DockStyle.Fill,WrapContents=true,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};layout.Controls.Add(top,0,0);
            top.Controls.Add(new ThemeLabel{Text="搜索：",AutoSize=true,Padding=new Padding(0,6,0,0)});top.Controls.Add(search);
            foreach(var item in new[]{new[]{"全部 LUT","All"},new[]{"最近使用","Recent"},new[]{"我的最爱","Favorites"}}) {
                string target=item[1];var b=new ThemeButton{Text=item[0],AutoSize=true};b.Click+=delegate{mode=target;Filter();};top.Controls.Add(b);
            }
            top.Controls.Add(new ThemeLabel{Text="文件夹：",AutoSize=true,Padding=new Padding(0,6,0,0)});top.Controls.Add(folders);
            top.Controls.Add(fixedView);
            fixedView.CheckedChanged+=delegate {ApplyFixedView();};
            cards.SizeChanged+=delegate {ValidateFixedSpace();};
            fixedSpaceTimer.Tick+=delegate{fixedSpaceTimer.Stop();CheckFixedSpace();};
            layout.Controls.Add(cards,0,1);layout.Controls.Add(status,0,2);
            var bottom=new GalleryControlsPanel{Dock=DockStyle.Fill,WrapContents=true};layout.Controls.Add(bottom,0,3);
            var previous=new ThemeButton{Text="< 上一页",AutoSize=true};var next=new ThemeButton{Text="下一页 >",AutoSize=true};
            bottom.Controls.Add(previous);bottom.Controls.Add(pages);bottom.Controls.Add(next);bottom.Controls.Add(smart);
            var change=new ThemeButton{Text="更换参考图",AutoSize=true};var update=new ThemeButton{Text="更新预览图",AutoSize=true};
            var use=new ThemeButton{Text="使用选中的 LUT",AutoSize=true};var none=new ThemeButton{Text="禁用 LUT",AutoSize=true};
            bottom.Controls.Add(change);bottom.Controls.Add(update);bottom.Controls.Add(use);bottom.Controls.Add(none);
            previous.Click+=delegate{TurnPage(-1);};next.Click+=delegate{TurnPage(1);};
            pages.SelectedIndexChanged+=delegate{if(!updating&&pages.SelectedIndex>=0){wheelDirection=0;wheelRemainder=0;page=pages.SelectedIndex;selected=null;RenderPage();}};
            search.TextChanged+=delegate{if(!updating)Filter();};folders.SelectedIndexChanged+=delegate{if(!updating)Filter();};
            use.Click+=delegate{Commit(selected);};none.Click+=delegate{DisabledLut=true;DialogResult=DialogResult.OK;Close();};
            smart.Click+=delegate {
                try {var cfg=new FgsConfig(appRoot,null);cfg.SaveValue("SMART_FILTER_ENABLED",smartEnabled?"false":"true");smartEnabled=!smartEnabled;UpdateSmart();Filter();}
                catch(Exception ex){MessageBox.Show(this,"无法保存智能过滤状态："+ex.Message);}
            };
            update.Click+=delegate{Build(false);};change.Click+=delegate{Build(true);};
            KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Escape)Close();if(e.KeyCode==Keys.Enter&&selected!=null)Commit(selected);};
        }
        internal static int CyclicPage(int current,int count,int direction)
        {
            return count<=1?0:((current+(direction<0?-1:1))%count+count)%count;
        }
        void TurnPage(int direction)
        {
            wheelDirection=0;wheelRemainder=0;
            int total=Math.Max(1,(filtered.Count+PageSize-1)/PageSize);
            if(total<=1)return;
            page=CyclicPage(page,total,direction);selected=null;RenderPage();
        }
        bool FitsFullPage()
        {
            // Test a complete page even when the current filter/last page has fewer entries.
            int width=cardSlots.Count==0?264:cardSlots[0].Card.Width+cardSlots[0].Card.Margin.Horizontal;
            int height=cardSlots.Count==0?200:cardSlots[0].Card.Height+cardSlots[0].Card.Margin.Vertical;
            int usableWidth=cards.ClientSize.Width-cards.Padding.Horizontal+(cards.AutoScroll&&cards.VerticalScroll.Visible?SystemInformation.VerticalScrollBarWidth:0);
            int usableHeight=cards.ClientSize.Height-cards.Padding.Vertical+(cards.AutoScroll&&cards.HorizontalScroll.Visible?SystemInformation.HorizontalScrollBarHeight:0);
            int columns=width>0?usableWidth/width:0;
            return columns>0 && usableHeight>=((PageSize+columns-1)/columns)*height;
        }
        void ApplyFixedView()
        {
            if(changingFixed)return;
            if(fixedView.Checked && (WindowState!=FormWindowState.Normal || !FitsFullPage())) {
                changingFixed=true;fixedView.Checked=false;changingFixed=false;
                status.Text="请恢复普通窗口并扩大图库，确保每页 24 张缩略图完整显示后再固定。";return;
            }
            changingFixed=true;
            try {
                Size client=ClientSize;
                cards.AutoScrollPosition=Point.Empty;cards.AutoScroll=!fixedView.Checked;
                FormBorderStyle=fixedView.Checked?FormBorderStyle.FixedSingle:FormBorderStyle.Sizable;
                MaximizeBox=!fixedView.Checked;ClientSize=client;
                wheelDirection=0;wheelRemainder=0;lastWheelPage=-1;
            }finally{changingFixed=false;}
        }
        void ValidateFixedSpace()
        {
            if(closingGallery||!galleryLoaded||changingFixed||renderingCards||IsDisposed||Disposing)return;
            fixedSpaceTimer.Stop();
            if(fixedView.Checked)fixedSpaceTimer.Start();
        }
        void CheckFixedSpace()
        {
            // Minimize, frame recreation and partial layout are not user unlock requests.
            if(!Visible||WindowState==FormWindowState.Minimized||changingFixed||renderingCards||IsDisposed||Disposing)return;
            PerformLayout();cards.PerformLayout();
            if(fixedView.Checked&&!FitsFullPage()){
                fixedView.Checked=false;
                status.Text="可用空间不足，已解除固定；请调整窗口大小。";
            }
        }
        public bool PreFilterMessage(ref Message message)
        {
            if(message.Msg!=0x020A || !fixedView.Checked || IsDisposed || !Visible || Form.ActiveForm!=this)return false;
            // The wheel may target a focused search box while the pointer is over the cards.
            Control target=Control.FromChildHandle(message.HWnd);
            if(target==null || (target!=this && !Contains(target)))return false;
            if(!cards.RectangleToScreen(cards.ClientRectangle).Contains(Cursor.Position) || folders.DroppedDown || pages.DroppedDown)return false;
            QueueWheel(unchecked((short)((message.WParam.ToInt64()>>16)&0xffff)),System.Diagnostics.Stopwatch.GetTimestamp());
            return true;
        }
        void QueueWheel(int delta,long stamp)
        {
            if(delta==0)return;
            if(wheelRemainder!=0 && Math.Sign(wheelRemainder)!=Math.Sign(delta))wheelRemainder=0;
            wheelRemainder+=delta;
            if(Math.Abs(wheelRemainder)<120)return;
            int direction=wheelRemainder<0?1:-1;wheelRemainder=0;
            // Coalesce one wheel burst; no timer, worker or repeated queued callbacks.
            if(lastWheelPage>=0 && (stamp-lastWheelPage)*1000.0/System.Diagnostics.Stopwatch.Frequency<180)return;
            lastWheelPage=stamp;wheelDirection=direction;
            if(wheelQueued)return;
            wheelQueued=true;
            BeginInvoke((MethodInvoker)delegate {
                wheelQueued=false;int turn=wheelDirection;wheelDirection=0;
                if(turn!=0 && !IsDisposed && Visible && fixedView.Checked)TurnPage(turn);
            });
        }
        void RemoveWheelFilter()
        {
            if(wheelFilterInstalled){Application.RemoveMessageFilter(this);wheelFilterInstalled=false;}
            wheelDirection=0;
        }
        void UpdateSmart(){smart.Text=smartEnabled?"智能过滤：开":"智能过滤";smart.BackColor=smartEnabled?Color.LightSteelBlue:SystemColors.Control;}
        void Reload()
        {
            string current=folders.SelectedItem as string;updating=true;
            try {
                all=NativeLibraryTools.GalleryEntries(root,previews);
                classes=NativeLutSmartFilter.Classify(all);
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
        void Filter(){RefreshFilter(false);}
        void RefreshFilter(bool keepPage)
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
            wheelDirection=0;wheelRemainder=0;if(!keepPage)page=0;
            if(!keepPage||!filtered.Contains(selected))selected=null;RenderPage();
        }
        internal static Bitmap CreateFavoriteStar()
        {
            var image=new Bitmap(18,18);
            using(var graphics=Graphics.FromImage(image)){
                graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var points=new PointF[10];
                for(int i=0;i<10;i++){double angle=-Math.PI/2+i*Math.PI/5;double radius=i%2==0?8:3.4;points[i]=new PointF(9+(float)(Math.Cos(angle)*radius),9+(float)(Math.Sin(angle)*radius));}
                using(var fill=new SolidBrush(Color.FromArgb(255,205,40)))graphics.FillPolygon(fill,points);
                using(var edge=new Pen(Color.FromArgb(180,125,0)))graphics.DrawPolygon(edge,points);
            }
            return image;
        }
        void SetFavoriteAppearance(Button button,bool favorite)
        {
            // The image keeps the yellow fill across theme application and card reuse.
            button.Text=favorite?"":"☆";button.Image=favorite?favoriteStar:null;button.ImageAlign=ContentAlignment.MiddleCenter;
        }
        void ToggleGalleryFavorite(LutGalleryEntry entry)
        {
            bool now=NativeLutRecords.ToggleFavorite(previews,entry.LutPath);
            string key=NativeLutRecords.Key(entry.LutPath);
            if(now)favorites.Add(key);else favorites.Remove(key);
            if(mode=="Favorites")RefreshFilter(true);
            else foreach(var slot in cardSlots)if(slot.Entry!=null&&NativeLutRecords.Key(slot.Entry.LutPath)==key)SetFavoriteAppearance(slot.Favorite,now);
        }
        void ClearCards()
        {
            cardSlots.Clear();
            var old=cards.Controls.Cast<Control>().ToArray();cards.Controls.Clear();
            foreach(var c in old){foreach(var pic in c.Controls.OfType<PictureBox>()) {var image=pic.Image;pic.Image=null;if(image!=null)image.Dispose();}c.Dispose();}
        }
        void RenderPage()
        {
            cards.AutoScrollPosition=Point.Empty;
            renderingCards=true;cards.SuspendLayout();
            int total=Math.Max(1,(filtered.Count+PageSize-1)/PageSize);page=Math.Max(0,Math.Min(page,total-1));updating=true;
            pages.BeginUpdate();
            try {pages.Items.Clear();for(int n=1;n<=total;n++)pages.Items.Add(n+" / "+total);pages.SelectedIndex=page;}
            finally {pages.EndUpdate();updating=false;}
            int index=0;
            foreach(var e in filtered.Skip(page*PageSize).Take(PageSize)) {
                if(index==cardSlots.Count) {
                var slot=new CardSlot();
                var card=new ThemeSelectionPanel{Width=254,Height=190,Margin=new Padding(5),BorderStyle=BorderStyle.FixedSingle};
                var pic=new PictureBox{Width=240,Height=135,Left=6,Top=4,SizeMode=PictureBoxSizeMode.Zoom};

                var name=new ThemeLabel{Text="",Left=6,Top=144,Width=204,Height=36,AutoEllipsis=true};
                var fav=new ThemeButton{Text="",Left=215,Top=143,Width=31,Height=30};
                var tip=new ThemeToolTip();card.Disposed+=delegate{tip.Dispose();};
                Action choose=delegate{selected=slot.Entry;status.Text=slot.Entry.Relative;foreach(Control c in cards.Controls)c.BackColor=c==card?Color.LightSteelBlue:SystemColors.Control;};
                pic.Click+=delegate{choose();};name.Click+=delegate{choose();};card.Click+=delegate{choose();};
                pic.DoubleClick+=delegate{Commit(slot.Entry);};name.DoubleClick+=delegate{Commit(slot.Entry);};card.DoubleClick+=delegate{Commit(slot.Entry);};
                fav.Click+=delegate{try {ToggleGalleryFavorite(slot.Entry);}catch(Exception ex){MessageBox.Show(this,"收藏保存失败，原记录保留："+ex.Message);}};
                var menu=new ContextMenuStrip();menu.Items.Add("打开 LUT 所在文件夹",null,delegate{Open(Path.GetDirectoryName(slot.Entry.LutPath));});menu.Items.Add("打开完整尺寸预览图",null,delegate{Open(slot.Entry.PreviewPath);});
                card.ContextMenuStrip=menu;pic.ContextMenuStrip=menu;name.ContextMenuStrip=menu;card.Disposed+=delegate{menu.Dispose();};
                card.SuspendLayout();
                card.Controls.AddRange(new Control[]{pic,name,fav});
                card.ResumeLayout(false);
                slot.Card=card;slot.Picture=pic;slot.Name=name;slot.Favorite=fav;slot.Tip=tip;
                cardSlots.Add(slot);cards.Controls.Add(card);
                }
                CardSlot bound=cardSlots[index++];
                bound.Entry=e;
                bound.Card.BackColor=selected==e?Color.LightSteelBlue:SystemColors.Control;
                bound.Name.Text=e.Name;bound.Tip.SetToolTip(bound.Name,e.Relative);
                SetFavoriteAppearance(bound.Favorite,favorites.Contains(NativeLutRecords.Key(e.LutPath)));
                // Same entry may be selected again after filtering; keep its owned bitmap.
                DateTime imageStamp=File.GetLastWriteTimeUtc(e.PreviewPath);
                if(!string.Equals(bound.ImagePath,e.PreviewPath,StringComparison.OrdinalIgnoreCase) || bound.ImageStamp!=imageStamp) {
                    Image oldImage=bound.Picture.Image;bound.Picture.Image=null;bound.ImagePath=null;
                    if(oldImage!=null)oldImage.Dispose();
                    try {bound.Picture.Image=LoadThumbnail(e.PreviewPath);bound.ImagePath=e.PreviewPath;bound.ImageStamp=imageStamp;}catch{}
                }
                bound.Card.Visible=true;
            }
            for(int n=index;n<cardSlots.Count;n++) {
                CardSlot hidden=cardSlots[n];hidden.Entry=null;hidden.Card.Visible=false;
                Image image=hidden.Picture.Image;hidden.Picture.Image=null;hidden.ImagePath=null;
                if(image!=null)image.Dispose();
            }
            cards.ResumeLayout();renderingCards=false; ValidateFixedSpace(); status.Text="匹配 "+filtered.Count+" / 共 "+all.Count+"；双击缩略图使用 LUT"+(smartEnabled?"；隐藏纯功能 LUT，保留 Combined":"");
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
            using(var stream=new MemoryStream(File.ReadAllBytes(cache)))using(var image=Image.FromStream(stream)){ var bitmap=new Bitmap(image); return bitmap; }
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
            });ClearCards();Reload();
        }
        protected override void Dispose(bool disposing){
            if(disposing){closingGallery=true;fixedSpaceTimer.Dispose();}
            if(disposing) {
                RemoveWheelFilter();
                ClearCards();favoriteStar.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
