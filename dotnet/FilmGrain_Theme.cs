using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    // An independent icon per Form; no shared disposed Icon and no file handle retained.
    internal static class UiWindowIcon
    {
        private static readonly Dictionary<Form, Icon> owned = new Dictionary<Form, Icon>();
        internal static void Apply(Form window, string root)
        {
            if(window == null || window.IsDisposed || window.Disposing || owned.ContainsKey(window))return;
            if(string.IsNullOrEmpty(root))root=AppDomain.CurrentDomain.BaseDirectory;
            Icon icon=null;
            try {
                string[] candidates={System.IO.Path.Combine(root,"Utils","FGS.ico"),System.IO.Path.Combine(root,"images","FGS.ico"),
                    System.IO.Path.Combine(root,"..","Utils","FGS.ico")};
                foreach(string path in candidates) {
                    if(!System.IO.File.Exists(path))continue;
                    try {using(Icon source=new Icon(path)){icon=(Icon)source.Clone();}break;}catch { }
                }
                if(icon==null) {
                    string executable=System.Reflection.Assembly.GetExecutingAssembly().Location;
                    using(Icon source=Icon.ExtractAssociatedIcon(executable))if(source!=null)icon=(Icon)source.Clone();
                }
                if(icon==null)return;
                window.Icon=icon;window.ShowIcon=true;
                owned.Add(window,icon);window.Disposed+=Release;
            } catch {if(icon!=null)icon.Dispose();}
        }
        private static void Release(object sender,EventArgs e)
        {
            Form window=(Form)sender;Icon icon;
            if(!owned.TryGetValue(window,out icon))return;
            window.Disposed-=Release;owned.Remove(window);icon.Dispose();
        }
    }
    // One engine per registered form; images are never transformed.
    internal sealed class UiTheme : IDisposable
    {
        internal static readonly Color Surface = Color.FromArgb(37, 38, 40);
        internal static readonly Color Input = Color.FromArgb(27, 28, 30);
        internal static readonly Color TextColor = Color.FromArgb(235, 237, 240);
        internal static readonly Color Muted = Color.FromArgb(162, 167, 174);
        internal static readonly Color Accent = Color.FromArgb(0, 151, 111);
        internal static readonly Color Border = Color.FromArgb(64, 66, 71);
        internal static readonly Color FieldHoverBorder = Color.FromArgb(88, 94, 101);
        internal static readonly Color FieldFocusBorder = Color.FromArgb(43, 139, 117);
        private bool dark, applying;
        private readonly HashSet<Control> nativeScrollControls = new HashSet<Control>();
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr handle, string appName, string idList);
        internal void EnableNativeScrollbars(Control control)
        {
            if (control == null || !nativeScrollControls.Add(control)) return;
            control.HandleCreated += NativeScrollHandleCreated;
            control.Disposed += NativeScrollDisposed;
        }
        private void NativeScrollHandleCreated(object sender, EventArgs e) { ApplyNativeScrollbars((Control)sender); }
        private void NativeScrollDisposed(object sender, EventArgs e) { RemoveNativeScrollbars((Control)sender); }
        private void RemoveNativeScrollbars(Control control)
        {
            control.HandleCreated -= NativeScrollHandleCreated;
            control.Disposed -= NativeScrollDisposed;
            nativeScrollControls.Remove(control);
        }
        private void ApplyNativeScrollbars(Control control)
        {
            if (control.IsDisposed || control.Disposing || !control.IsHandleCreated) return;
            // Probe MODE 1: change the native theme only, never repaint a scrollbar.
            // NULL/NULL removes our override and restores the Windows default in light mode.
            try { SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : null, null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        private readonly ThemeToolTip stripTip=new ThemeToolTip { ShowAlways=true };
        private readonly Timer statusTipDelay=new Timer();
        private ToolStripItem pendingStatusTip;
        private ToolStrip pendingStatusOwner;
        internal UiTheme()
        {
            statusTipDelay.Interval=450;
            statusTipDelay.Tick+=ShowStatusTip;
        }
        private void HideStatusTip()
        {
            statusTipDelay.Stop();
            if(pendingStatusOwner!=null && !pendingStatusOwner.IsDisposed) {
                stripTip.Hide(pendingStatusOwner);
                stripTip.SetToolTip(pendingStatusOwner, "");
            }
            pendingStatusTip=null;pendingStatusOwner=null;
        }
        private void QueueStatusTip(object sender,EventArgs e)
        {
            ToolStripItem item=sender as ToolStripItem;
            ToolStrip owner=item==null ? null : item.GetCurrentParent();
            if(!(owner is StatusStrip))return;
            if(item==pendingStatusTip)return;
            HideStatusTip();
            if(item.IsDisposed || string.IsNullOrEmpty(item.ToolTipText))return;
            pendingStatusTip=item;pendingStatusOwner=owner;
            statusTipDelay.Start();
        }
        private void StatusItemMouseMove(object sender,MouseEventArgs e) { QueueStatusTip(sender,e); }
        private void StatusItemMouseLeave(object sender,EventArgs e)
        {
            if(sender==pendingStatusTip)HideStatusTip();
        }
        private void ShowStatusTip(object sender,EventArgs e)
        {
            statusTipDelay.Stop();
            ToolStripItem item=pendingStatusTip;ToolStrip owner=pendingStatusOwner;
            if(item==null || item.IsDisposed || owner==null || owner.IsDisposed || !owner.Visible)return;
            Point pointer=owner.PointToClient(Cursor.Position);
            if(owner.GetItemAt(pointer)!=item || string.IsNullOrEmpty(item.ToolTipText)) {HideStatusTip();return;}
            stripTip.SetToolTip(owner,item.ToolTipText);
            stripTip.Show(item.ToolTipText,owner,pointer.X+12,pointer.Y+22,20000);
        }
        private readonly ToolStripRenderer darkRenderer = new ToolStripProfessionalRenderer(new ThemeTable());
        private readonly Dictionary<Control, Snapshot> controls = new Dictionary<Control, Snapshot>();
        private readonly Dictionary<ToolStripItem, Color> items = new Dictionary<ToolStripItem, Color>();
        private sealed class Snapshot
        {
            internal Color Back, Fore;
            internal FlatStyle Flat;
            internal bool Visual;
            internal Color Edge, Hover, Down;
            internal DrawMode Draw;
            internal ToolStripRenderer Renderer;
            internal bool OwnerDraw, GridLines, ShowItemToolTips;
            internal BorderStyle TextBorder;
            internal bool PageVisual;
            internal Color Link, ActiveLink, VisitedLink, DisabledLink;
        }
        internal static Color Foreground(Color value)
        {
            if (value.ToArgb() == Color.White.ToArgb() || value.ToArgb() == Color.Gainsboro.ToArgb()) return value;
            if (value.G > value.R * 1.25 && value.G > value.B) return Color.FromArgb(109, 219, 154);
            if (value.R > value.G * 1.5 && value.R > value.B * 1.5) return Color.FromArgb(255, 157, 126);
            if (value.ToArgb() == Color.FromArgb(100, 107, 116).ToArgb()) return Muted;
            return TextColor;
        }
        internal void Apply(Control root, bool useDark)
        {
            dark = useDark;
            Register(root);
            root.SuspendLayout();
            bool previousApplying = applying; applying = true;
            try {
                foreach (Control c in new List<Control>(controls.Keys)) if (!c.IsDisposed) PaintControl(c);
                foreach (ToolStripItem i in new List<ToolStripItem>(items.Keys)) if (!i.IsDisposed) PaintItem(i);
                foreach (Control c in new List<Control>(nativeScrollControls)) ApplyNativeScrollbars(c);
            } finally { root.ResumeLayout(true); applying = previousApplying; }
            root.Invalidate(true);
        }
        private static Color LocalColor(Control c, string property)
        {
            System.ComponentModel.PropertyDescriptor descriptor = System.ComponentModel.TypeDescriptor.GetProperties(c)[property];
            return descriptor != null && descriptor.ShouldSerializeValue(c) ? (Color)descriptor.GetValue(c) : Color.Empty;
        }
        private void Register(Control c)
        {
            if (c is PictureBox || c is ThemeColorButton || controls.ContainsKey(c)) return;
            Form window = c as Form;
            if (window != null) UiWindowIcon.Apply(window, null);
            Snapshot s = new Snapshot(); s.Back = LocalColor(c, "BackColor"); s.Fore = LocalColor(c, "ForeColor");
            Button b = c as Button;
            if (b != null) { s.Flat = b.FlatStyle; s.Visual = b.UseVisualStyleBackColor; s.Edge = b.FlatAppearance.BorderColor; s.Hover = b.FlatAppearance.MouseOverBackColor; s.Down = b.FlatAppearance.MouseDownBackColor; }
            TabPage page = c as TabPage;
            if (page != null) s.PageVisual = page.UseVisualStyleBackColor;
            LinkLabel link = c as LinkLabel;
            if (link != null) { s.Link=link.LinkColor; s.ActiveLink=link.ActiveLinkColor; s.VisitedLink=link.VisitedLinkColor; s.DisabledLink=link.DisabledLinkColor; }
            MaskedTextBox masked = c as MaskedTextBox;
            if (masked != null) s.TextBorder = masked.BorderStyle;
            ComboBox combo = c as ComboBox;
            if (combo != null) { s.Draw = combo.DrawMode; if (s.Draw == DrawMode.Normal) combo.DrawItem += DrawCombo; }
            ListView list = c as ListView;
            if (list != null) {
                s.OwnerDraw = list.OwnerDraw; s.GridLines = list.GridLines;s.ShowItemToolTips=list.ShowItemToolTips;list.ShowItemToolTips=false;list.MouseMove+=ListMouseMove;list.MouseLeave+=StripMouseLeave;
                if (!s.OwnerDraw) { list.DrawColumnHeader += DrawHeader; list.DrawItem += DrawListItem; list.DrawSubItem += DrawSubItem; }
            }
            controls.Add(c, s);
            c.ForeColorChanged += ForeChanged;
            c.BackColorChanged += BackChanged;
            c.ControlAdded += Added;
            c.ContextMenuStripChanged += ContextMenuChanged;
            c.Disposed += Removed;
            ToolStrip strip = c as ToolStrip;
            if (strip != null) { s.Renderer = strip.Renderer;s.ShowItemToolTips=strip.ShowItemToolTips;strip.ShowItemToolTips=false;strip.MouseMove+=StripMouseMove;strip.MouseLeave+=StripMouseLeave; strip.ItemAdded += StripItemAdded; foreach (ToolStripItem i in strip.Items) RegisterItem(i); }
            if (c.ContextMenuStrip != null) Register(c.ContextMenuStrip);
            foreach (Control child in c.Controls) Register(child);
        }
        private void ListMouseMove(object sender,MouseEventArgs e)
        {
            var list=(ListView)sender;var item=list.GetItemAt(e.X,e.Y);
            string text=item==null?"":item.ToolTipText;
            if(stripTip.GetToolTip(list)!=text)stripTip.SetToolTip(list,text);
        }
        private void StripMouseMove(object sender,MouseEventArgs e)
        {
            var strip=(ToolStrip)sender;
            // StatusStrip routes pointer events through its items; those have a delayed explicit tooltip.
            if(strip is StatusStrip)return;
            var item=strip.GetItemAt(e.Location);
            string text=item==null?"":item.ToolTipText;
            if(stripTip.GetToolTip(strip)!=text)stripTip.SetToolTip(strip,text);
        }
        private void StripMouseLeave(object sender,EventArgs e)
        {
            if(sender is StatusStrip) {
                var strip=(StatusStrip)sender;
                if(!strip.ClientRectangle.Contains(strip.PointToClient(Cursor.Position)))HideStatusTip();
            }
            else stripTip.SetToolTip((Control)sender,"");
        }
        private void StripItemAdded(object sender, ToolStripItemEventArgs e)
        {
            RegisterItem(e.Item);
        }
        private void ContextMenuChanged(object sender, EventArgs e)
        {
            Control control = (Control)sender;
            ContextMenuStrip menu = control.ContextMenuStrip;
            if (control.IsDisposed || menu == null || menu.IsDisposed) return;
            Register(menu); ApplyBranch(menu);
        }
        private static Color LocalItemFore(ToolStripItem item)
        {
            System.ComponentModel.PropertyDescriptor descriptor = System.ComponentModel.TypeDescriptor.GetProperties(item)["ForeColor"];
            return descriptor != null && descriptor.ShouldSerializeValue(item) ? item.ForeColor : Color.Empty;
        }
        private void RegisterItem(ToolStripItem i)
        {
            if (items.ContainsKey(i)) return;
            items.Add(i, LocalItemFore(i)); i.ForeColorChanged += ItemForeChanged; i.Disposed += ItemDisposed;
            i.MouseEnter+=QueueStatusTip;i.MouseMove+=StatusItemMouseMove;i.MouseLeave+=StatusItemMouseLeave;
            ToolStripDropDownItem d = i as ToolStripDropDownItem;
            if (d != null) foreach (ToolStripItem child in d.DropDownItems) RegisterItem(child);
            if (dark) PaintItem(i);
        }
        private void ItemDisposed(object sender, EventArgs e)
        {
            ToolStripItem item = (ToolStripItem)sender;
            item.MouseEnter-=QueueStatusTip;item.MouseMove-=StatusItemMouseMove;item.MouseLeave-=StatusItemMouseLeave;
            if(item==pendingStatusTip)HideStatusTip();
            item.ForeColorChanged -= ItemForeChanged; item.Disposed -= ItemDisposed; items.Remove(item);
        }
        private void PaintControl(Control c)
        {
            Snapshot s = controls[c]; bool previousApplying = applying; applying = true;
            try {
                bool field = c is TextBoxBase || c is ComboBox || c is NumericUpDown || c is ListView || c is ListBox;
                bool selectedCard = c is ThemeSelectionPanel && s.Back.ToArgb() == Color.LightSteelBlue.ToArgb();
                c.BackColor = dark ? (selectedCard ? Color.FromArgb(35,78,66) : field || c is Button ? Input : Surface) : s.Back;
                c.ForeColor = dark ? Foreground(s.Fore) : s.Fore;
                TabPage page = c as TabPage;
                if (page != null) page.UseVisualStyleBackColor = dark ? false : s.PageVisual;
                LinkLabel link = c as LinkLabel;
                if (link != null) {
                    link.LinkColor = dark ? Color.FromArgb(109,219,154) : s.Link;
                    link.ActiveLinkColor = dark ? TextColor : s.ActiveLink;
                    link.VisitedLinkColor = dark ? Color.FromArgb(182,167,218) : s.VisitedLink;
                    link.DisabledLinkColor = dark ? Muted : s.DisabledLink;
                }
                MaskedTextBox masked = c as MaskedTextBox;
                if (masked != null) masked.BorderStyle = dark ? BorderStyle.FixedSingle : s.TextBorder;
                IThemeSurface custom = c as IThemeSurface;
                if (custom != null) custom.DarkTheme = dark;
                ListView list = c as ListView;
                if (list != null && !s.OwnerDraw) { list.OwnerDraw = dark; list.GridLines = dark ? false : s.GridLines; }
                Button b = c as Button;
                if (b != null) {
                    b.FlatStyle = dark ? FlatStyle.Flat : s.Flat;
                    b.UseVisualStyleBackColor = dark ? false : s.Visual;
                    b.FlatAppearance.BorderColor = dark ? Border : s.Edge;
                    b.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(53, 56, 60) : s.Hover;
                    b.FlatAppearance.MouseDownBackColor = dark ? Color.FromArgb(35, 78, 66) : s.Down;
                    if (dark && (s.Back.R > s.Back.G * 1.5 || s.Back.B > s.Back.R * 1.5 || s.Back.ToArgb() == Color.LightSteelBlue.ToArgb())) { b.BackColor = s.Back.R > s.Back.G * 1.5 ? s.Back : Accent; b.ForeColor = Color.White; if (s.Back.B > s.Back.R * 1.5 || s.Back.ToArgb() == Color.LightSteelBlue.ToArgb()) { b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0,170,126); b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0,123,90); } }
                }
                ComboBox combo = c as ComboBox;
                if (combo != null && s.Draw == DrawMode.Normal && combo.DropDownStyle == ComboBoxStyle.DropDownList) combo.DrawMode = dark ? DrawMode.OwnerDrawFixed : s.Draw;
                ToolStrip strip = c as ToolStrip;
                if (strip != null) strip.Renderer = dark ? darkRenderer : s.Renderer;
            } finally { applying = previousApplying; }
        }
        private void PaintItem(ToolStripItem i) { bool previousApplying = applying; applying = true; try { i.ForeColor = dark ? Foreground(items[i]) : items[i]; } finally { applying = previousApplying; } }
        private void ForeChanged(object sender, EventArgs e) { if (applying) return; Control c = (Control)sender; if (LocalColor(c, "ForeColor").IsEmpty) return; controls[c].Fore = c.ForeColor; PaintControl(c); }
        private void BackChanged(object sender, EventArgs e) { if (applying) return; Control c = (Control)sender; if (LocalColor(c, "BackColor").IsEmpty) return; controls[c].Back = c.BackColor; PaintControl(c); }
        private void ItemForeChanged(object sender, EventArgs e) { if (applying) return; ToolStripItem i = (ToolStripItem)sender; items[i] = LocalItemFore(i); PaintItem(i); }
        private void Added(object sender, ControlEventArgs e)
        {
            Register(e.Control);
            if (dark) {
                ApplyBranch(e.Control);
                if(e.Control.ContextMenuStrip != null) ApplyBranch(e.Control.ContextMenuStrip);
            }
        }
        private void ApplyBranch(Control root)
        {
            if (controls.ContainsKey(root)) PaintControl(root);
            foreach (Control child in root.Controls) ApplyBranch(child);
            root.Invalidate();
        }
        private void Removed(object sender, EventArgs e) { Unregister((Control)sender); }
        private void Unregister(Control c)
        {
            Snapshot s; if (!controls.TryGetValue(c, out s)) return;
            c.ForeColorChanged -= ForeChanged; c.BackColorChanged -= BackChanged; c.ControlAdded -= Added; c.ContextMenuStripChanged -= ContextMenuChanged; c.Disposed -= Removed;
            ComboBox combo = c as ComboBox; if (combo != null && s.Draw == DrawMode.Normal) combo.DrawItem -= DrawCombo;
            ListView list = c as ListView;
            if(list!=null) {list.MouseMove-=ListMouseMove;list.MouseLeave-=StripMouseLeave;stripTip.SetToolTip(list,"");if(!list.IsDisposed)list.ShowItemToolTips=s.ShowItemToolTips;}
            if (list != null && !s.OwnerDraw) { list.DrawColumnHeader -= DrawHeader; list.DrawItem -= DrawListItem; list.DrawSubItem -= DrawSubItem; }
            ToolStrip strip = c as ToolStrip; if (strip != null) {strip.ItemAdded -= StripItemAdded;strip.MouseMove-=StripMouseMove;strip.MouseLeave-=StripMouseLeave;stripTip.SetToolTip(strip,"");if(!strip.IsDisposed)strip.ShowItemToolTips=s.ShowItemToolTips;}
            controls.Remove(c);
        }
        private void DrawCombo(object sender, DrawItemEventArgs e)
        {
            ComboBox c = (ComboBox)sender;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush brush = new SolidBrush(dark ? (selected ? Color.FromArgb(35, 78, 66) : Input) : e.BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
            string text = e.Index >= 0 ? c.GetItemText(c.Items[e.Index]) : c.Text;
            ThemeDrawing.DrawText(e.Graphics, text, e.Font, e.Bounds, c.Enabled ? (dark ? TextColor : e.ForeColor) : (dark ? Muted : SystemColors.GrayText), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        }
        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            if (!dark) { e.DrawDefault = true; return; }
            using (Brush b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Border)) e.Graphics.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, Math.Max(0,e.Bounds.Width-1), Math.Max(0,e.Bounds.Height-1));
            ThemeDrawing.DrawText(e.Graphics, e.Header.Text, ((ListView)sender).Font, Rectangle.Inflate(e.Bounds,-5,0), TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        private void DrawListItem(object sender, DrawListViewItemEventArgs e)
        {
            if (!dark || ((ListView)sender).View != View.Details) e.DrawDefault = true;
        }
        private void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (!dark) { e.DrawDefault = true; return; }
            ListView list = (ListView)sender;
            Color back = e.Item.Selected ? Color.FromArgb(35,78,66) : Input;
            using (Brush b = new SolidBrush(back)) e.Graphics.FillRectangle(b,e.Bounds);
            using (Pen p = new Pen(Border)) { e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1); e.Graphics.DrawLine(p,e.Bounds.Right-1,e.Bounds.Top,e.Bounds.Right-1,e.Bounds.Bottom); }
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            flags |= e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
            ThemeDrawing.DrawText(e.Graphics,e.SubItem.Text,list.Font,Rectangle.Inflate(e.Bounds,-4,0),list.Enabled ? TextColor : Muted,flags);
            if (e.ColumnIndex == 0 && e.Item.Focused && list.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, TextColor,back);
        }
        internal void CorrectionState(Button button, bool enabled)
        {
            Snapshot s;
            if (!controls.TryGetValue(button, out s)) return;
            s.Flat = enabled ? FlatStyle.Flat : FlatStyle.Standard;
            s.Visual = !enabled;
            s.Edge = enabled ? Color.FromArgb(135,40,10) : SystemColors.ControlDark;
            PaintControl(button);
        }
        public void Dispose()
        {
            foreach (Control c in new List<Control>(nativeScrollControls)) RemoveNativeScrollbars(c);
            foreach (Control c in new List<Control>(controls.Keys)) Unregister(c);
            foreach (ToolStripItem i in new List<ToolStripItem>(items.Keys)) ItemDisposed(i, EventArgs.Empty);
            HideStatusTip();statusTipDelay.Dispose();
            items.Clear();stripTip.Dispose();
        }
        private sealed class ThemeTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Input; } }
            public override Color ImageMarginGradientBegin { get { return Input; } }
            public override Color ImageMarginGradientMiddle { get { return Input; } }
            public override Color ImageMarginGradientEnd { get { return Input; } }
            public override Color MenuItemSelected { get { return Color.FromArgb(35, 78, 66); } }
            public override Color MenuItemBorder { get { return Accent; } }
            public override Color MenuBorder { get { return Border; } }
        }
    }
    internal sealed partial class MainForm
    {
        private UiTheme uiTheme;
        private ToolStripButton themeButton;
        private bool selectedDarkTheme;
        private string selectedThemeMode = "SYSTEM";
        private bool themeClosing, systemThemeSubscribed, themeApplied;
        private string themeMenuAppliedMode;
        private int systemThemeCallbackPending;
        internal int themeApplyCount, systemThemeEventCount, systemThemeCallbackCount, systemThemeNoopCount;
        private ContextMenuStrip themeModes;
        private readonly Dictionary<Form, UiTheme> themedWindows = new Dictionary<Form, UiTheme>();
        internal static string NormalizeThemeMode(string value)
        {
            return string.Equals(value, "DARK", StringComparison.OrdinalIgnoreCase) ? "DARK" :
                string.Equals(value, "LIGHT", StringComparison.OrdinalIgnoreCase) ? "LIGHT" : "SYSTEM";
        }
        internal static bool ReadSystemDarkTheme()
        {
            try {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            } catch { return false; } // Stable LIGHT fallback; never prevents startup.
        }
        // Resolve through the actual modal owner chain, including setup -> tool task.
        internal static void AttachOwnedTheme(Form window, IWin32Window owner, Control nativeScroll = null)
        {
            Form current = owner as Form;
            HashSet<Form> visited = new HashSet<Form>();
            while (current != null && !current.IsDisposed && visited.Add(current)) {
                MainForm main = current as MainForm;
                if (main != null) { main.AttachThemeWindow(window, nativeScroll); return; }
                current = current.Owner;
            }
        }
        private void AttachThemeWindow(Form window, Control nativeScroll = null)
        {
            if (themeClosing || window.IsDisposed || themedWindows.ContainsKey(window)) return;
            UiTheme engine = new UiTheme();
            if (nativeScroll != null) engine.EnableNativeScrollbars(nativeScroll);
            themedWindows.Add(window, engine);
            window.Disposed += ThemeWindowDisposed;
            engine.Apply(window, selectedDarkTheme);
        }
        private void ThemeWindowDisposed(object sender, EventArgs e)
        {
            Form window = (Form)sender; UiTheme engine;
            if (!themedWindows.TryGetValue(window, out engine)) return;
            window.Disposed -= ThemeWindowDisposed;
            themedWindows.Remove(window); engine.Dispose();
        }
        private void ApplySelectedTheme() { ApplySelectedTheme(false); }
        private void ApplySelectedTheme(bool force)
        {
            if (themeClosing || IsDisposed || Disposing) return;
            bool dark = selectedThemeMode == "DARK" || (selectedThemeMode == "SYSTEM" && ReadSystemDarkTheme());
            if (themeApplied && dark == selectedDarkTheme && !force) {
                ++systemThemeNoopCount;
                StartupTiming.Mark("theme.same.skipped", "mode=" + selectedThemeMode + " dark=" + dark);
                if (themeMenuAppliedMode != selectedThemeMode) {
                    UpdateThemeMenu(dark); themeMenuAppliedMode = selectedThemeMode;
                }
                return;
            }
            ++themeApplyCount;
            StartupTiming.Mark("theme.apply.begin", "mode=" + selectedThemeMode + " dark=" + dark + " force=" + force);
            uiTheme.Apply(this, dark);
            foreach (var pair in new List<KeyValuePair<Form, UiTheme>>(themedWindows))
                if (!pair.Key.IsDisposed && !pair.Key.Disposing) pair.Value.Apply(pair.Key, dark);
            UpdateThemeMenu(dark); themeMenuAppliedMode = selectedThemeMode;
            if (themeModes != null) uiTheme.Apply(themeModes, dark);
            themeApplied = true;
            StartupTiming.Mark("theme.apply.end", "count=" + themeApplyCount);
        }
        private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            // SystemEvents can arrive in bursts on a worker. Resolve mode on the UI thread.
            System.Threading.Interlocked.Increment(ref systemThemeEventCount);
            StartupTiming.Mark("theme.system.event", e == null ? "unknown" : e.Category.ToString());
            if (System.Threading.Interlocked.CompareExchange(ref systemThemeCallbackPending, 1, 0) != 0) return;
            try {
                if (themeClosing || IsDisposed || Disposing || !IsHandleCreated) {
                    System.Threading.Interlocked.Exchange(ref systemThemeCallbackPending, 0); return;
                }
                BeginInvoke((MethodInvoker)delegate {
                    System.Threading.Interlocked.Exchange(ref systemThemeCallbackPending, 0);
                    ++systemThemeCallbackCount;
                    StartupTiming.Mark("theme.system.callback", "mode=" + selectedThemeMode);
                    if (!themeClosing && !IsDisposed && !Disposing && selectedThemeMode == "SYSTEM") ApplySelectedTheme();
                });
            } catch (InvalidOperationException) {
                System.Threading.Interlocked.Exchange(ref systemThemeCallbackPending, 0);
            }
        }
        private void ReleaseThemeResources(object sender, EventArgs e)
        {
            if (themeClosing) return;
            themeClosing = true;
            if (systemThemeSubscribed) {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
                systemThemeSubscribed = false;
            }
            foreach (Form window in new List<Form>(themedWindows.Keys)) ThemeWindowDisposed(window, EventArgs.Empty);
            uiTheme.Dispose();
            themeButton.Image = null; themeModes.Dispose(); sunThemeIcon.Dispose(); moonThemeIcon.Dispose();
        }
        private Bitmap sunThemeIcon, moonThemeIcon;
        private static Bitmap CreateThemeIcon(bool sun)
        {
            Bitmap icon = new Bitmap(20, 20);
            using (Graphics g = Graphics.FromImage(icon)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(sun ? Color.FromArgb(245, 195, 75) : Color.FromArgb(65, 88, 125), 1.8f)) {
                    if (sun) {
                        g.DrawEllipse(pen, 6, 6, 8, 8);
                        for (int i=0; i<8; i++) {
                            double angle=i*Math.PI/4;
                            g.DrawLine(pen, (float)(10+6*Math.Cos(angle)), (float)(10+6*Math.Sin(angle)), (float)(10+9*Math.Cos(angle)), (float)(10+9*Math.Sin(angle)));
                        }
                    } else {
                        using (System.Drawing.Drawing2D.GraphicsPath crescent = new System.Drawing.Drawing2D.GraphicsPath()) {
                            crescent.AddArc(3,2,14,15,60,270);
                            crescent.AddArc(8,0,12,13,150,-180);
                            crescent.CloseFigure();
                            using (Brush brush=new SolidBrush(pen.Color)) g.FillPath(brush,crescent);
                        }
                    }
                }
            }
            return icon;
        }
        private void BuildThemeMenu(StatusStrip strip)
        {
            sunThemeIcon = CreateThemeIcon(true); moonThemeIcon = CreateThemeIcon(false);
            themeButton = new ToolStripButton();
            themeButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            themeButton.ImageScaling = ToolStripItemImageScaling.SizeToFit;
            themeButton.AutoToolTip = false;
            themeButton.Click += delegate { SelectTheme(!selectedDarkTheme); };
            strip.Items.Add(themeButton);
            themeModes = new ContextMenuStrip();
            string[] modes = { "SYSTEM", "LIGHT", "DARK" };
            string[] labels = { UiText("跟随系统", "Follow system"), UiText("浅色", "Light"), UiText("暗色", "Dark") };
            for (int i = 0; i < modes.Length; i++) {
                string mode = modes[i];
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]); item.Tag = mode;
                item.Click += delegate { SelectThemeMode(mode); }; themeModes.Items.Add(item);
            }
            themeButton.MouseUp += delegate(object sender, MouseEventArgs e) {
                if (e.Button == MouseButtons.Right) themeModes.Show(Cursor.Position);
            };
        }
        private void InitializeTheme()
        {
            uiTheme = new UiTheme();
            uiTheme.EnableNativeScrollbars(listFiles);
            uiTheme.EnableNativeScrollbars(log);
            selectedThemeMode = NormalizeThemeMode(config.Get("UI_THEME"));
            ApplySelectedTheme();
            try {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
                systemThemeSubscribed = true;
            } catch { systemThemeSubscribed = false; }
            // Re-read after subscription to close the startup read/subscribe gap.
            ApplySelectedTheme();
            HandleCreated += delegate { if (!themeClosing && uiTheme != null) ApplySelectedTheme(true); };
            Disposed += ReleaseThemeResources;
        }
        private void UpdateThemeMenu(bool dark)
        {
            selectedDarkTheme = dark;
            themeButton.Image = dark ? sunThemeIcon : moonThemeIcon;
            themeButton.Text = dark ? UiText("切换为浅色", "Switch to light") : UiText("切换为暗色", "Switch to dark");
            themeButton.ToolTipText = themeButton.Text + UiText("；右键选择模式。当前：", "; right-click for mode. Current: ") +
                selectedThemeMode + (dark ? " / Dark" : " / Light") +
                (selectedThemeMode == "SYSTEM" && !systemThemeSubscribed ? UiText("（系统监听不可用，仅启动读取）", " (system listener unavailable; startup read only)") : "");
            foreach (ToolStripMenuItem item in themeModes.Items) item.Checked = (string)item.Tag == selectedThemeMode;
            themeButton.AccessibleName = themeButton.Text;
        }
        private void SelectTheme(bool dark)
        {
            SelectThemeMode(dark ? "DARK" : "LIGHT");
        }
        private void SelectThemeMode(string mode)
        {
            mode = NormalizeThemeMode(mode);
            try { config.SaveValue("UI_THEME", mode); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            selectedThemeMode = mode; ApplySelectedTheme();
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal interface IThemeSurface { bool DarkTheme { get; set; } }
    // Shared by every explicit tooltip and ToolStrip hint. Resolve theme at display time.
    internal sealed class ThemeToolTip : ToolTip
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromDC(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int count);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect {public int Left,Top,Right,Bottom;}
        internal static Point NearPointer(Point pointer,Size size,Rectangle workingArea,float dpi)
        {
            int dx=Math.Max(12,(int)Math.Ceiling(12*dpi/96f));
            int dy=Math.Max(20,(int)Math.Ceiling(20*dpi/96f));
            int x=pointer.X+dx,y=pointer.Y+dy;
            if(x+size.Width>workingArea.Right)x=pointer.X-size.Width-dx;
            if(y+size.Height>workingArea.Bottom)y=pointer.Y-size.Height-dy;
            return new Point(Math.Max(workingArea.Left,Math.Min(x,workingArea.Right-size.Width)),
                Math.Max(workingArea.Top,Math.Min(y,workingArea.Bottom-size.Height)));
        }
        internal static bool NeedsCorrection(Point pointer,Rectangle popup,Rectangle area,float dpi)
        {
            Point expected=NearPointer(pointer,popup.Size,area,dpi);
            int tolerance=Math.Max(32,(int)Math.Ceiling(32*dpi/96f));
            return !area.Contains(popup)||Math.Abs(popup.Left-expected.X)>tolerance||Math.Abs(popup.Top-expected.Y)>tolerance;
        }
        internal static bool IsTooltipWindowClass(string name)
        {
            return string.Equals(name,"tooltips_class32",StringComparison.OrdinalIgnoreCase)||
                (name!=null&&name.StartsWith("WindowsForms10.tooltips_class32.app.",StringComparison.OrdinalIgnoreCase));
        }
        private static void PositionNativePopup(Graphics graphics)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return;
            IntPtr dc=graphics.GetHdc(),window;
            try {window=WindowFromDC(dc);}finally {graphics.ReleaseHdc(dc);}
            // Only move this draw event's tooltip, never its associated form or another popup.
            var name=new System.Text.StringBuilder(64);
            if(window==IntPtr.Zero||GetClassName(window,name,name.Capacity)==0||!IsTooltipWindowClass(name.ToString()))return;
            NativeRect rect;if(!GetWindowRect(window,out rect))return;
            Point pointer=Cursor.Position;
            Rectangle popup=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
            Rectangle area=Screen.FromPoint(pointer).WorkingArea;
            if(!NeedsCorrection(pointer,popup,area,graphics.DpiX))return;
            Point origin=NearPointer(pointer,popup.Size,area,graphics.DpiX);
            if(rect.Left!=origin.X||rect.Top!=origin.Y)
                SetWindowPos(window,IntPtr.Zero,origin.X,origin.Y,0,0,0x0001|0x0004|0x0010); // no size / z-order / activation change
        }
        internal static bool UsesDark(Control control)
        {
            if(control==null)return false;
            var menu=control as ContextMenuStrip;
            Form window=menu!=null&&menu.SourceControl!=null?menu.SourceControl.FindForm():control.FindForm();
            return (window!=null?window.BackColor:control.BackColor).GetBrightness()<0.35f;
        }
        internal static Size Measure(Graphics graphics,string text,Font font,int screenWidth)
        {
            int width=Math.Max(120,Math.Min((int)(520*graphics.DpiX/96f),screenWidth-32));
            SizeF size=graphics.MeasureString(text??"",font,width-24,StringFormat.GenericDefault);
            return new Size(size.Height>font.Height*1.5?width:Math.Min(width,(int)Math.Ceiling(size.Width)+26),(int)Math.Ceiling(size.Height)+20);
        }
        internal static void Render(Graphics graphics,Rectangle bounds,string text,Font font,bool dark)
        {
            using(var brush=new SolidBrush(dark?UiTheme.Surface:SystemColors.Info))graphics.FillRectangle(brush,bounds);
            using(var pen=new Pen(dark?UiTheme.Border:SystemColors.InfoText))graphics.DrawRectangle(pen,bounds.X,bounds.Y,Math.Max(0,bounds.Width-1),Math.Max(0,bounds.Height-1));
            using(var brush=new SolidBrush(dark?UiTheme.TextColor:SystemColors.InfoText))
                graphics.DrawString(text??"",font,brush,new RectangleF(bounds.X+12,bounds.Y+9,Math.Max(1,bounds.Width-24),Math.Max(1,bounds.Height-18)),StringFormat.GenericDefault);
        }
        internal ThemeToolTip()
        {
            OwnerDraw=true;IsBalloon=false;UseAnimation=false;UseFading=false;AutoPopDelay=20000;
            Popup+=delegate(object sender,PopupEventArgs e) {
                Control c=e.AssociatedControl;Font font=c!=null?c.Font:SystemFonts.MessageBoxFont;
                using(Graphics graphics=c!=null?c.CreateGraphics():Graphics.FromHwnd(IntPtr.Zero))
                    e.ToolTipSize=Measure(graphics,c!=null?GetToolTip(c):"",font,Screen.FromPoint(Cursor.Position).WorkingArea.Width);
            };
            Draw+=delegate(object sender,DrawToolTipEventArgs e) {
                PositionNativePopup(e.Graphics);
                Render(e.Graphics,e.Bounds,e.ToolTipText,e.AssociatedControl!=null?e.AssociatedControl.Font:SystemFonts.MessageBoxFont,UsesDark(e.AssociatedControl));
            };
        }
    }

    internal static class ThemeDrawing
    {
        // One GDI+ text path for parent-backed and buffered custom controls.
        internal static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
            // Keep all custom paint inside GDI+: no temporary HDC on parent-backed paint surfaces.
            using (StringFormat format = new StringFormat())
            using (Brush brush = new SolidBrush(color)) {
                format.Alignment = (flags & TextFormatFlags.Right) != 0 ? StringAlignment.Far :
                    (flags & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near;
                format.LineAlignment = (flags & TextFormatFlags.Bottom) != 0 ? StringAlignment.Far :
                    (flags & TextFormatFlags.VerticalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near;
                if ((flags & TextFormatFlags.WordBreak) == 0) format.FormatFlags |= StringFormatFlags.NoWrap;
                format.Trimming = (flags & TextFormatFlags.EndEllipsis) != 0 ? StringTrimming.EllipsisCharacter : StringTrimming.Character;
                format.HotkeyPrefix = (flags & TextFormatFlags.NoPrefix) != 0 ? System.Drawing.Text.HotkeyPrefix.None :
                    (flags & TextFormatFlags.HidePrefix) != 0 ? System.Drawing.Text.HotkeyPrefix.Hide : System.Drawing.Text.HotkeyPrefix.Show;
                System.Drawing.Drawing2D.GraphicsState saved = graphics.Save();
                try {
                    graphics.SetClip(bounds, System.Drawing.Drawing2D.CombineMode.Intersect);
                    graphics.DrawString(text, font, brush, bounds, format);
                } finally { graphics.Restore(saved); }
            }
        }
        // Match the native enabled controls' GDI text metrics. Never acquire an HDC
        // from parent-backed/buffered paint graphics: rasterize on an opaque bitmap,
        // then copy it through GDI+, preserving the existing flicker-free paint path.
        internal static void DrawNativeText(Graphics graphics,string text,Font font,Rectangle bounds,Color color,Color background,TextFormatFlags flags)
        {
            if(string.IsNullOrEmpty(text)||bounds.Width<=0||bounds.Height<=0)return;
            using(var bitmap=new Bitmap(bounds.Width,bounds.Height)) {
                bitmap.SetResolution(graphics.DpiX,graphics.DpiY);
                using(var buffer=Graphics.FromImage(bitmap)) {
                    buffer.Clear(background);
                    TextRenderer.DrawText(buffer,text,font,new Rectangle(Point.Empty,bounds.Size),color,background,flags);
                }
                graphics.DrawImageUnscaled(bitmap,bounds.Location);
            }
        }
        internal static TextFormatFlags TextFlags(ContentAlignment align, bool ellipsis)
        {
            TextFormatFlags f = TextFormatFlags.WordBreak;
            if (align == ContentAlignment.TopCenter || align == ContentAlignment.MiddleCenter || align == ContentAlignment.BottomCenter) f |= TextFormatFlags.HorizontalCenter;
            else if (align == ContentAlignment.TopRight || align == ContentAlignment.MiddleRight || align == ContentAlignment.BottomRight) f |= TextFormatFlags.Right;
            if (align == ContentAlignment.MiddleLeft || align == ContentAlignment.MiddleCenter || align == ContentAlignment.MiddleRight) f |= TextFormatFlags.VerticalCenter;
            else if (align == ContentAlignment.BottomLeft || align == ContentAlignment.BottomCenter || align == ContentAlignment.BottomRight) f |= TextFormatFlags.Bottom;
            if (ellipsis) f |= TextFormatFlags.EndEllipsis;
            return f;
        }
    }
    // Managed paint of the entire tab surface, retaining native tab layout and input.
    // No post-WM_PAINT overlay: the framework routes painting here in dark mode.
    internal sealed class ThemeTabControl : TabControl, IThemeSurface
    {
        private bool dark, oldUserPaint, oldAllPaint, oldBuffered;
        private TabDrawMode originalDrawMode;
        public bool DarkTheme
        {
            get { return dark; }
            set {
                if (dark == value) return;
                if (value) {
                    originalDrawMode = DrawMode;
                    oldUserPaint = GetStyle(ControlStyles.UserPaint);
                    oldAllPaint = GetStyle(ControlStyles.AllPaintingInWmPaint);
                    oldBuffered = GetStyle(ControlStyles.OptimizedDoubleBuffer);
                }
                dark = value;
                DrawMode = dark ? TabDrawMode.OwnerDrawFixed : originalDrawMode;
                SetStyle(ControlStyles.UserPaint, dark || oldUserPaint);
                SetStyle(ControlStyles.AllPaintingInWmPaint, dark || oldAllPaint);
                SetStyle(ControlStyles.OptimizedDoubleBuffer, dark || oldBuffered);
                UpdateStyles(); Invalidate(true);
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!dark) { base.OnPaintBackground(e); return; }
            using (Brush brush = new SolidBrush(UiTheme.Surface)) e.Graphics.FillRectangle(brush,ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!dark) { base.OnPaint(e); return; }
            if (IsDisposed || Disposing) return;
            Rectangle body = DisplayRectangle; body.Inflate(2,2);
            using (Pen pen = new Pen(UiTheme.Border))
                e.Graphics.DrawRectangle(pen,body.X,body.Y,Math.Max(0,body.Width-1),Math.Max(0,body.Height-1));
            for (int i=0; i<TabCount; i++) DrawHeader(e.Graphics,i,GetTabRect(i));
        }
        private void DrawHeader(Graphics graphics,int index,Rectangle bounds)
        {
            if (IsDisposed || Disposing || index < 0 || index >= TabPages.Count || bounds.Width <= 0 || bounds.Height <= 0) return;
            bool selected = index == SelectedIndex;
            using (Brush brush = new SolidBrush(selected ? UiTheme.Input : UiTheme.Surface)) graphics.FillRectangle(brush,bounds);
            using (Pen pen = new Pen(selected ? UiTheme.FieldFocusBorder : UiTheme.Border))
                graphics.DrawRectangle(pen,bounds.X,bounds.Y,Math.Max(0,bounds.Width-1),Math.Max(0,bounds.Height-1));
            ThemeDrawing.DrawText(graphics,TabPages[index].Text,Font,Rectangle.Inflate(bounds,-3,-2),
                Enabled && TabPages[index].Enabled ? UiTheme.TextColor : UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (selected && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(graphics,Rectangle.Inflate(bounds,-3,-3),UiTheme.TextColor,UiTheme.Input);
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (!dark) { base.OnDrawItem(e); return; }
            DrawHeader(e.Graphics,e.Index,e.Bounds);
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); if(dark) Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if(dark) Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if(dark) Invalidate(); }
    }
    // Single-line fields only. Keep the native edit/caret/selection, recolor its NC border.
    // Never attach this border painter to multi-line logs or scrollbar controls.
    internal sealed class ThemeTextBox : TextBox, IThemeSurface
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window,IntPtr rect,IntPtr region,uint flags);
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window,string appName,string idList);
        private bool dark, hover;
        private BorderStyle originalBorder;
        private bool OwnsBorder { get { return dark && !Multiline && BorderStyle != BorderStyle.None; } }
        public bool DarkTheme
        {
            get { return dark; }
            set {
                if (dark == value) return;
                if (value) originalBorder = BorderStyle;
                dark = value;
                // Preserve native edit metrics, caret and selection.
                if (!dark) BorderStyle = originalBorder;
                ApplyEditTheme();
                if(IsHandleCreated && !IsDisposed && !Disposing) {
                    Invalidate();
                    RedrawWindow(Handle,IntPtr.Zero,IntPtr.Zero,0x401);
                }
            }
        }
        private void ApplyEditTheme()
        {
            if(!IsHandleCreated || IsDisposed || Disposing || Multiline)return;
            // Disable only this edit's native hover animation while its frame is ours.
            // NULL/NULL restores native Windows rendering in light mode.
            try { SetWindowTheme(Handle,dark ? "" : null,dark ? "" : null); }
            catch(DllNotFoundException) { }
            catch(EntryPointNotFoundException) { }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyEditTheme();
        }
        private bool PaintBorder()
        {
            if(!OwnsBorder || IsDisposed || Disposing || !IsHandleCreated)return false;
            IntPtr dc=IntPtr.Zero;
            try {
                dc=GetWindowDC(Handle);
                if(dc==IntPtr.Zero)return false;
                using(Graphics g=Graphics.FromHdc(dc))
                using(Pen pen=new Pen(!Enabled ? UiTheme.Border : Focused ? UiTheme.FieldFocusBorder : hover ? UiTheme.FieldHoverBorder : UiTheme.Border)) {
                    g.DrawRectangle(pen,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
                    if(BorderStyle==BorderStyle.Fixed3D) {
                        using(Pen inner=new Pen(UiTheme.Input))
                            g.DrawRectangle(inner,1,1,Math.Max(0,Width-3),Math.Max(0,Height-3));
                    }
                }
                return true;
            } catch(DllNotFoundException) { return false; }
              catch(EntryPointNotFoundException) { return false; }
            finally { if(dc!=IntPtr.Zero)ReleaseDC(Handle,dc); }
        }
        protected override void WndProc(ref Message m)
        {
            // Do not draw a native light frame before covering it with our dark frame.
            if(m.Msg==0x85 && PaintBorder()) { m.Result=IntPtr.Zero; return; }
            base.WndProc(ref m);
            if(m.Msg==0xF)PaintBorder();
        }
        protected override void OnMouseEnter(EventArgs e) { hover=true; base.OnMouseEnter(e); PaintBorder(); }
        protected override void OnMouseLeave(EventArgs e) { hover=false; base.OnMouseLeave(e); PaintBorder(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); PaintBorder(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); PaintBorder(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); PaintBorder(); }
    }
    // Paint a client-area frame; never paint over native scrollbars or the RichEdit NC area.
    internal sealed class ThemeLogFrame : Panel, IThemeSurface
    {
        private readonly TextBoxBase edit;
        private readonly BorderStyle originalBorder;
        private bool dark;
        internal ThemeLogFrame(TextBoxBase control)
        {
            edit=control; originalBorder=control.BorderStyle;
            Dock=DockStyle.Fill; Margin=Padding.Empty;
            Controls.Add(control);
        }
        public bool DarkTheme
        {
            get { return dark; }
            set {
                if(dark==value)return;
                dark=value;
                Padding=dark ? new Padding(1) : Padding.Empty;
                edit.BorderStyle=dark ? BorderStyle.None : originalBorder;
                Invalidate();
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(!dark || IsDisposed || Disposing)return;
            using(Pen pen=new Pen(UiTheme.Border))
                e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,ClientSize.Width-1),Math.Max(0,ClientSize.Height-1));
        }
    }
    // Gallery UI only: preserve LightSteelBlue selection semantics through palette snapshots.
    internal sealed class ThemeSelectionPanel : Panel, IThemeSurface
    {
        private bool dark;
        private BorderStyle originalBorder;
        public bool DarkTheme
        {
            get { return dark; }
            set {
                if(dark==value)return;
                if(value)originalBorder=BorderStyle;
                dark=value;BorderStyle=dark?BorderStyle.None:originalBorder;Invalidate();
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(!dark || IsDisposed || Disposing)return;
            using(Pen pen=new Pen(BackColor.ToArgb()==Color.FromArgb(35,78,66).ToArgb()?UiTheme.FieldFocusBorder:UiTheme.Border))
                e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,ClientSize.Width-1),Math.Max(0,ClientSize.Height-1));
        }
    }
    // Semantic color swatch: its BackColor is data read by ColorDialog and subtitle state.
    // Keep native rendering and all explicit colors outside palette remapping.
    internal sealed class ThemeColorButton : Button { }
    internal sealed class ThemeLabel : Label, IThemeSurface
    {
        public ThemeLabel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!DarkTheme) { base.OnPaintBackground(e); return; }
            using (Brush brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush,ClientRectangle);
        }

        public bool DarkTheme { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!DarkTheme || Enabled || Image != null) { base.OnPaint(e); return; }
            Rectangle r = new Rectangle(Padding.Left, Padding.Top, Math.Max(0,ClientSize.Width-Padding.Horizontal),Math.Max(0,ClientSize.Height-Padding.Vertical));
            ThemeDrawing.DrawNativeText(e.Graphics,Text,Font,r,UiTheme.Muted,BackColor,ThemeDrawing.TextFlags(TextAlign,AutoEllipsis) | (UseMnemonic ? (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix) : TextFormatFlags.NoPrefix));
        }
    }
    internal sealed class ThemeGroupBox : GroupBox, IThemeSurface
    {
        public ThemeGroupBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!DarkTheme) { base.OnPaintBackground(e); return; }
            using (Brush brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush,ClientRectangle);
        }

        public bool DarkTheme { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!DarkTheme) { base.OnPaint(e); return; }
            int gap = TextRenderer.MeasureText(" ",Font).Width;
            int y = Font.Height/2;
            using (Pen p = new Pen(UiTheme.Border)) e.Graphics.DrawRectangle(p,0,y,Math.Max(0,Width-1),Math.Max(0,Height-y-1));
            Size size = TextRenderer.MeasureText(Text,Font);
            Rectangle r = new Rectangle(gap,0,Math.Min(size.Width,Math.Max(0,Width-2*gap)),Font.Height+2);
            using (Brush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b,r);
            ThemeDrawing.DrawText(e.Graphics,Text,Font,r,Enabled ? ForeColor : UiTheme.Muted,TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }
    internal sealed class ThemeButton : Button, IThemeSurface
    {
        public bool DarkTheme { get; set; }
        private bool keyboardPressed;
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space)keyboardPressed=true;base.OnKeyDown(e);Invalidate();}
        protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space)keyboardPressed=false;base.OnKeyUp(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){keyboardPressed=false;base.OnLostFocus(e);Invalidate();}
        protected override void OnEnabledChanged(EventArgs e){keyboardPressed=false;base.OnEnabledChanged(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if(!DarkTheme||Image!=null||ImageList!=null)return;
            // Keep the framework border and interaction; both text states share
            // the same rectangle, padding, renderer and mnemonic flags.
            bool hover=ClientRectangle.Contains(PointToClient(Cursor.Position));
            bool pressed=Enabled&&(keyboardPressed||(Capture&&hover&&(MouseButtons&MouseButtons.Left)!=0));
            Color background=!Enabled?UiTheme.Input:pressed?FlatAppearance.MouseDownBackColor:hover?FlatAppearance.MouseOverBackColor:BackColor;
            if(background.IsEmpty)background=BackColor;
            Rectangle interior=Rectangle.Inflate(ClientRectangle,-1,-1);
            using(Brush brush=new SolidBrush(background))e.Graphics.FillRectangle(brush,interior);
            if(!Enabled)using(Pen pen=new Pen(UiTheme.Border))e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
            Rectangle text=Rectangle.FromLTRB(3+Padding.Left,2+Padding.Top,Math.Max(3+Padding.Left,Width-3-Padding.Right),Math.Max(2+Padding.Top,Height-2-Padding.Bottom));
            ThemeDrawing.DrawNativeText(e.Graphics,Text,Font,text,Enabled?ForeColor:UiTheme.Muted,background,ThemeDrawing.TextFlags(TextAlign,AutoEllipsis) | (UseMnemonic?(ShowKeyboardCues?0:TextFormatFlags.HidePrefix):TextFormatFlags.NoPrefix));
            if(Enabled&&Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),ForeColor,background);
        }
    }
    // Keep framework interaction; use one dark glyph/text layout for both enabled states.
    internal sealed class ThemeCheckBox : CheckBox, IThemeSurface
    {
        private bool dark;
        private bool SingleLineText {get{return AutoSize&&Text.IndexOf('\n')<0&&Text.IndexOf('\r')<0;}}
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size native=base.GetPreferredSize(proposedSize);
            if(!dark||!SingleLineText||Appearance!=Appearance.Normal||CheckAlign!=ContentAlignment.MiddleLeft||RightToLeft==RightToLeft.Yes||Image!=null||ImageList!=null)return native;
            int glyph=Math.Max(9,(int)Math.Round(13.0*DeviceDpi/96.0));
            Size text=TextRenderer.MeasureText(Text,Font,new Size(int.MaxValue,int.MaxValue),TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
            // Match the custom glyph, gap and native text overhang; the framework's
            // flat-checkbox preferred width assumes a smaller native text gap.
            return new Size(Math.Max(native.Width,Padding.Horizontal+glyph+4+text.Width+2),Math.Max(native.Height,Padding.Vertical+Math.Max(glyph,text.Height)));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!dark || Appearance != Appearance.Normal || CheckAlign != ContentAlignment.MiddleLeft || RightToLeft == RightToLeft.Yes || Image!=null || ImageList!=null) return;
            // Remove the native glyph and text together so no fragments of the
            // enabled text survive beside the common custom text rectangle.
            using(Brush background=new SolidBrush(BackColor))e.Graphics.FillRectangle(background,ClientRectangle);
            // The flat adapter uses a pale disabled fill. Keep both states dark and
            // draw the mark explicitly so Checked and Indeterminate remain legible.
            int box = Math.Max(9, (int)Math.Round(13.0 * e.Graphics.DpiX / 96.0));
            Rectangle bounds = new Rectangle(Padding.Left, Padding.Top + Math.Max(0, (Height-Padding.Vertical-box)/2), box, box);
            using (Brush fill = new SolidBrush(UiTheme.Input)) e.Graphics.FillRectangle(fill,bounds);
            using (Pen border = new Pen(Enabled ? Color.FromArgb(160,165,172) : Color.FromArgb(105,110,118)))
                e.Graphics.DrawRectangle(border,bounds.X,bounds.Y,bounds.Width-1,bounds.Height-1);
            if (CheckState != CheckState.Unchecked) {
                Color mark = Enabled ? Color.White : Color.FromArgb(205,210,216);
                float unit = box / 13f;
                if (CheckState == CheckState.Indeterminate) {
                    using (Brush fill = new SolidBrush(mark))
                        e.Graphics.FillRectangle(fill,bounds.X+3*unit,bounds.Y+5*unit,7*unit,3*unit);
                } else {
                    using (Pen pen = new Pen(mark,Math.Max(2f,2f*unit))) {
                        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        e.Graphics.DrawLines(pen,new PointF[] {
                            new PointF(bounds.X+3*unit,bounds.Y+6*unit),
                            new PointF(bounds.X+5.5f*unit,bounds.Y+9*unit),
                            new PointF(bounds.X+10*unit,bounds.Y+3.5f*unit) });
                    }
                }
            }
            Rectangle r = new Rectangle(Padding.Left+box+4,Padding.Top,Math.Max(0,Width-Padding.Horizontal-box-4),Math.Max(0,Height-Padding.Vertical));
            TextFormatFlags flags=ThemeDrawing.TextFlags(TextAlign,AutoEllipsis);
            if(SingleLineText)flags=(flags&~TextFormatFlags.WordBreak)|TextFormatFlags.SingleLine;
            ThemeDrawing.DrawNativeText(e.Graphics,Text,Font,r,Enabled?ForeColor:UiTheme.Muted,BackColor,flags | (UseMnemonic ? (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix) : TextFormatFlags.NoPrefix));
            if(Enabled&&Focused&&ShowFocusCues&&r.Width>2&&r.Height>2)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(r,-1,-1),ForeColor,BackColor);
        }
        private FlatStyle originalStyle;
        private Color originalBorder, originalChecked, originalHover, originalDown;
        public bool DarkTheme
        {
            get { return dark; }
            set {
                if (dark == value) return;
                if (value) {
                    originalStyle = FlatStyle;
                    originalBorder = FlatAppearance.BorderColor;
                    originalChecked = FlatAppearance.CheckedBackColor;
                    originalHover = FlatAppearance.MouseOverBackColor;
                    originalDown = FlatAppearance.MouseDownBackColor;
                }
                dark = value;
                FlatStyle = dark ? FlatStyle.Flat : originalStyle;
                FlatAppearance.BorderColor = dark ? UiTheme.Border : originalBorder;
                FlatAppearance.CheckedBackColor = dark ? UiTheme.Input : originalChecked;
                FlatAppearance.MouseOverBackColor = dark ? UiTheme.Input : originalHover;
                FlatAppearance.MouseDownBackColor = dark ? UiTheme.Input : originalDown;
                Invalidate();
            }
        }
    }
    internal sealed class ThemeListView : ListView, IThemeSurface
    {
        private bool dark;
        private HeaderTailWindow header;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { internal int Left,Top,Right,Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SendMessageW")]
        private static extern IntPtr SendRect(IntPtr window,int message,IntPtr wParam,ref NativeRect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window,out NativeRect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr window,IntPtr rect,bool erase);
        public bool DarkTheme
        {
            get { return dark; }
            set { if(dark==value)return;dark=value;AttachHeader();if(header!=null)InvalidateRect(header.Handle,IntPtr.Zero,true); }
        }
        private void AttachHeader()
        {
            if(!IsHandleCreated || IsDisposed || Disposing)return;
            IntPtr handle=SendMessage(Handle,0x101F,IntPtr.Zero,IntPtr.Zero); // LVM_GETHEADER
            if(header!=null && header.Handle==handle)return;
            if(header!=null){header.ReleaseHandle();header=null;}
            if(handle!=IntPtr.Zero){header=new HeaderTailWindow(this);header.AssignHandle(handle);}
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e);AttachHeader(); }
        protected override void OnHandleDestroyed(EventArgs e)
        { if(header!=null){header.ReleaseHandle();header=null;}base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if(m.Msg==0x0005 || m.Msg==0x007D)AttachHeader(); // size / style changes
        }
        private sealed class HeaderTailWindow : NativeWindow
        {
            private readonly ThemeListView owner;
            internal HeaderTailWindow(ThemeListView list){owner=list;}
            protected override void WndProc(ref Message m)
            {
                int message=m.Msg;
                base.WndProc(ref m);
                if(!owner.dark || owner.IsDisposed || owner.Disposing || Handle==IntPtr.Zero ||
                    (message!=0x000F && message!=0x0014))return;
                NativeRect client;
                if(!GetClientRect(Handle,out client))return;
                int right=client.Left;
                int count=SendMessage(Handle,0x1200,IntPtr.Zero,IntPtr.Zero).ToInt32(); // HDM_GETITEMCOUNT
                if(count<0)return;
                for(int i=0;i<count;i++) {
                    NativeRect column=new NativeRect();
                    if(SendRect(Handle,0x1207,new IntPtr(i),ref column)==IntPtr.Zero)return;
                    right=Math.Max(right,column.Right);
                }
                right=Math.Max(client.Left,Math.Min(right,client.Right));
                if(right>=client.Right || client.Bottom<=client.Top)return;
                using(Graphics graphics=Graphics.FromHwnd(Handle))
                using(Brush brush=new SolidBrush(UiTheme.Surface))
                    graphics.FillRectangle(brush,right,client.Top,client.Right-right,client.Bottom-client.Top);
            }
        }
    }
    internal class ThemeComboBox : ComboBox, IThemeSurface
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ComboRect { internal int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ComboInfo {
            internal int Size; internal ComboRect Item, Button; internal int State;
            internal IntPtr Combo, Edit, List;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboInfo info);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
        private InactiveEditWindow editWindow;
        private bool clearingInactiveSelection;
        private void ClearInactiveSelection()
        {
            if (clearingInactiveSelection || DropDownStyle != ComboBoxStyle.DropDown ||
                ContainsFocus || DroppedDown || !IsHandleCreated || IsDisposed || Disposing) return;
            clearingInactiveSelection = true;
            try {
                if (editWindow != null && editWindow.Handle != IntPtr.Zero) {
                    long range=SendMessage(editWindow.Handle,0x00B0,IntPtr.Zero,IntPtr.Zero).ToInt64(); // EM_GETSEL
                    if((range & 0xffff) != ((range >> 16) & 0xffff))
                        SendMessage(editWindow.Handle, 0x00B1, IntPtr.Zero, IntPtr.Zero); // EM_SETSEL
                }
                else if (SelectionLength != 0) SelectionLength = 0;
            }
            finally { clearingInactiveSelection = false; }
        }
        private sealed class InactiveEditWindow : NativeWindow {
            private readonly ThemeComboBox owner;
            internal InactiveEditWindow(ThemeComboBox owner, IntPtr edit) { this.owner=owner; AssignHandle(edit); }
            protected override void WndProc(ref Message m) {
                int message=m.Msg;
                // The inner EDIT receives EM_SETSEL directly, bypassing ComboBox.WndProc.
                // Collapse before paint and after programmatic native selection updates.
                if(message==0x000F) owner.ClearInactiveSelection();
                base.WndProc(ref m);
                if(message==0x00B1 || message==0x000C || message==0x0008) owner.ClearInactiveSelection();
            }
        }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            ApplyComboTheme();
            if(DropDownStyle==ComboBoxStyle.DropDown) {
                ComboInfo info=new ComboInfo();info.Size=System.Runtime.InteropServices.Marshal.SizeOf(typeof(ComboInfo));
                if(GetComboBoxInfo(Handle,ref info) && info.Edit!=IntPtr.Zero)editWindow=new InactiveEditWindow(this,info.Edit);
            }
            ClearInactiveSelection();
        }
        private IntPtr disabledBrush;
        private bool pointerOver;
        private bool repairQueued;
        private void QueueResizeRepair()
        {
            if(repairQueued || !DarkTheme || DropDownStyle!=ComboBoxStyle.DropDown ||
                IsDisposed || Disposing || !IsHandleCreated)return;
            repairQueued=true;
            try {
                BeginInvoke((MethodInvoker)delegate {
                    repairQueued=false;
                    if(IsDisposed || Disposing || !IsHandleCreated || !DarkTheme || !Visible)return;
                    Invalidate();Update();
                });
            } catch(InvalidOperationException) { repairQueued=false; }
        }
        protected override void OnHandleDestroyed(EventArgs e) {
            repairQueued=false;
            if(editWindow!=null) {editWindow.ReleaseHandle();editWindow=null;}
            base.OnHandleDestroyed(e);
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window,string appName,string idList);
        internal bool ThemePopupScrollbars {get;set;}
        private void ApplyPopupTheme()
        {
            if(!ThemePopupScrollbars||!IsHandleCreated||IsDisposed||Disposing)return;
            var info=new ComboInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf(typeof(ComboInfo))};
            if(GetComboBoxInfo(Handle,ref info)&&info.List!=IntPtr.Zero)
                try{SetWindowTheme(info.List,DarkTheme?"DarkMode_Explorer":null,null);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
        }
        protected override void OnDropDown(EventArgs e){ApplyPopupTheme();base.OnDropDown(e);}
        private void ApplyComboTheme()
        {
            if(!IsHandleCreated || IsDisposed || Disposing)return;
            try { SetWindowTheme(Handle,DarkTheme ? "" : null,DarkTheme ? "" : null); }
            catch(DllNotFoundException) { }
            catch(EntryPointNotFoundException) { }
            ApplyPopupTheme();
        }
        protected override void OnMouseEnter(EventArgs e) { pointerOver=true; base.OnMouseEnter(e); PaintDarkChrome(); }
        protected override void OnMouseLeave(EventArgs e) { pointerOver=false; base.OnMouseLeave(e); PaintDarkChrome(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); PaintDarkChrome(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); ClearInactiveSelection(); PaintDarkChrome(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); PaintDarkChrome(); }
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr dc, int color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int SetBkColor(IntPtr dc, int color);
        private static int ColorRef(Color color) { return color.R | (color.G << 8) | (color.B << 16); }
        private bool darkTheme;
        private FlatStyle lightFlatStyle;
        public bool DarkTheme
        {
            get { return darkTheme; }
            set {
                if (darkTheme == value) return;
                if (value) lightFlatStyle = FlatStyle;
                darkTheme = value;
                // Editable combos have a native inner edit rim. Flat mode removes it
                // through WinForms rather than painting over the child edit/caret.
                if (DropDownStyle != ComboBoxStyle.Simple)
                    FlatStyle = value ? FlatStyle.Flat : lightFlatStyle;
                ApplyComboTheme();
                Invalidate();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disabledBrush != IntPtr.Zero) { DeleteObject(disabledBrush); disabledBrush = IntPtr.Zero; }
            base.Dispose(disposing);
        }
        protected override void WndProc(ref Message m)
        {
            if (DarkTheme && ((ThemePopupScrollbars && m.Msg==0x0134) || !Enabled && (m.Msg == 0x0138 || m.Msg == 0x0133))) {
                if (disabledBrush == IntPtr.Zero) disabledBrush = CreateSolidBrush(ColorRef(UiTheme.Input));
                if (disabledBrush != IntPtr.Zero) {
                    SetTextColor(m.WParam, ColorRef(Enabled?UiTheme.TextColor:UiTheme.Muted));
                    SetBkColor(m.WParam, ColorRef(UiTheme.Input));
                    m.Result = disabledBrush; return;
                }
            }
            int message=m.Msg;
            base.WndProc(ref m);
            // CB_SETEDITSEL / CB_SETCURSEL / WM_SETTEXT may leave a native edit
            // selection after programmatic value updates even without focus.
            if (message == 0x0142 || message == 0x014E || message == 0x000C) ClearInactiveSelection();
            if(message==0x0047 || message==0x0005)QueueResizeRepair();
            // Native frame/background painting can overwrite the editable arrow during resize.
            // Restore only the same established overlay; preserve child edit and native popup.
            if(message==0x000F || message==0x0085 || message==0x0014 || message==0x000A)PaintDarkChrome();
        }
        private void PaintDarkChrome()
        {
            if(!DarkTheme || DropDownStyle==ComboBoxStyle.Simple || IsDisposed || Disposing || !IsHandleCreated)return;
            using (Graphics g = Graphics.FromHwnd(Handle)) {
                int width = SystemInformation.VerticalScrollBarWidth;
                Rectangle arrow = new Rectangle(Math.Max(1,ClientSize.Width-width-1),1,width,Math.Max(0,ClientSize.Height-2));
                // Owner-drawn list combos already paint both states in DrawCombo.
                // A second disabled overlay changed the font and text rectangle.
                if (!Enabled && !(DropDownStyle==ComboBoxStyle.DropDownList&&DrawMode!=DrawMode.Normal)) {
                    Rectangle text = new Rectangle(2,1,Math.Max(0,arrow.Left-3),Math.Max(0,ClientSize.Height-2));
                    using (Brush b = new SolidBrush(UiTheme.Input)) g.FillRectangle(b,text);
                    ThemeDrawing.DrawNativeText(g,Text,Font,text,UiTheme.Muted,UiTheme.Input,TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                using (Brush b = new SolidBrush(UiTheme.Input)) {
                    g.FillRectangle(b,arrow);
                    // Include the native separator immediately to the left of the arrow.
                    g.FillRectangle(b,Math.Max(1,arrow.Left-2),2,2,Math.Max(0,ClientSize.Height-4));
                }
                // The native inner rim may occupy more than one pixel (DPI/theme).
                // Clear the chrome outside rcItem, preserving the owner-drawn text
                // and the editable child's area instead of covering the text again.
                if(ClientSize.Width>3&&ClientSize.Height>3) {
                    var info=new ComboInfo {Size=System.Runtime.InteropServices.Marshal.SizeOf(typeof(ComboInfo))};
                    bool known=GetComboBoxInfo(Handle,ref info);
                    int left=known?info.Item.Left:2,top=known?info.Item.Top:2;
                    int right=known?info.Item.Right:arrow.Left,bottom=known?info.Item.Bottom:ClientSize.Height-2;
                    left=Math.Max(1,Math.Min(ClientSize.Width-1,left));right=Math.Max(left,Math.Min(ClientSize.Width-1,right));
                    top=Math.Max(1,Math.Min(ClientSize.Height-1,top));bottom=Math.Max(top,Math.Min(ClientSize.Height-1,bottom));
                    using(var inner=new SolidBrush(UiTheme.Input)) {
                        g.FillRectangle(inner,1,1,ClientSize.Width-2,top-1);
                        g.FillRectangle(inner,1,bottom,ClientSize.Width-2,ClientSize.Height-1-bottom);
                        g.FillRectangle(inner,1,top,left-1,bottom-top);
                        g.FillRectangle(inner,right,top,ClientSize.Width-1-right,bottom-top);
                    }
                }
                Color edge = !Enabled ? UiTheme.Border : ContainsFocus || DroppedDown ? UiTheme.FieldFocusBorder : pointerOver ? UiTheme.FieldHoverBorder : UiTheme.Border;
                using (Pen p = new Pen(edge)) g.DrawRectangle(p,0,0,Math.Max(0,ClientSize.Width-1),Math.Max(0,ClientSize.Height-1));
                int x = arrow.Left+arrow.Width/2, y = arrow.Top+arrow.Height/2;
                using (Pen p = new Pen(Enabled ? UiTheme.TextColor : UiTheme.Muted)) g.DrawLines(p,new Point[]{new Point(x-3,y-1),new Point(x,y+2),new Point(x+3,y-1)});
            }
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal sealed class ThemeProgressBar : ProgressBar, IThemeSurface
    {
        private bool dark;
        private int phase;
        private readonly Timer animation = new Timer();
        public ThemeProgressBar()
        {
            animation.Interval = 80;
            animation.Tick += delegate { if (Style == ProgressBarStyle.Marquee && MarqueeAnimationSpeed > 0) { phase = (phase+7)%140; Invalidate(); } };
        }
        public bool DarkTheme
        {
            get { return dark; }
            set {
                if (dark == value) return;
                dark = value;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, dark);
                UpdateStyles(); animation.Enabled = dark && Visible; Invalidate();
            }
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); animation.Enabled = dark && Visible; }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!dark) { base.OnPaint(e); return; }
            using (Brush b = new SolidBrush(UiTheme.Input)) e.Graphics.FillRectangle(b,ClientRectangle);
            Rectangle inside = Rectangle.Inflate(ClientRectangle,-2,-2);
            if (inside.Width > 0 && inside.Height > 0) {
                Rectangle fill = inside;
                if (Style == ProgressBarStyle.Marquee) {
                    int left = inside.Left+(int)(inside.Width*(phase-35)/100.0);
                    fill = Rectangle.Intersect(inside,new Rectangle(left,inside.Top,Math.Max(8,inside.Width/3),inside.Height));
                } else fill.Width = (int)(inside.Width*Math.Max(0,Value-Minimum)/(double)Math.Max(1,Maximum-Minimum));
                if (fill.Width > 0 && fill.Height > 0) using (Brush b = new SolidBrush(UiTheme.Accent)) e.Graphics.FillRectangle(b,fill);
            }
            using (Pen p = new Pen(UiTheme.Border)) e.Graphics.DrawRectangle(p,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
}

namespace FilmGrainStudioPreview
{
    internal sealed class ThemeNumericUpDown : NumericUpDown, IThemeSurface
    {
        private bool darkTheme, pointerOver, focusWithin;
        private Control arrowButtons;
        private ArrowPaintWindow arrowWindow;
        // Intercept only drawing. Native mouse capture, repeat timer and value changes remain untouched.
        private sealed class ArrowPaintWindow : NativeWindow, IDisposable
        {
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct PaintRect { internal int Left,Top,Right,Bottom; }
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct PaintInfo {
                internal IntPtr DC; internal int Erase; internal PaintRect Rect;
                internal int Restore,IncUpdate;
                internal int R0,R1,R2,R3,R4,R5,R6,R7;
            }
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern IntPtr BeginPaint(IntPtr window,out PaintInfo info);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool EndPaint(IntPtr window,ref PaintInfo info);
            private readonly ThemeNumericUpDown owner;
            private readonly Control buttons;
            internal ArrowPaintWindow(ThemeNumericUpDown owner,Control buttons)
            {
                this.owner=owner;this.buttons=buttons;
                buttons.HandleCreated+=Created;buttons.HandleDestroyed+=Destroyed;
                if(buttons.IsHandleCreated)AssignHandle(buttons.Handle);
            }
            private void Created(object sender,EventArgs e) {AssignHandle(buttons.Handle);}
            private void Destroyed(object sender,EventArgs e) {ReleaseHandle();}
            protected override void WndProc(ref Message m)
            {
                if(owner.DarkTheme && !owner.IsDisposed && !owner.Disposing) {
                    if(m.Msg==0x0014) {m.Result=new IntPtr(1);return;} // no visible background erase
                    if(m.Msg==0x000F) {
                        PaintInfo info;
                        IntPtr dc=BeginPaint(Handle,out info);
                        if(dc!=IntPtr.Zero) {
                            try {
                                int width=buttons.ClientSize.Width,height=buttons.ClientSize.Height;
                                if(width>0 && height>0) {
                                    using(Bitmap buffer=new Bitmap(width,height)) {
                                        using(Graphics g=Graphics.FromImage(buffer))owner.DrawArrows(g,new Rectangle(0,0,width,height));
                                        using(Graphics target=Graphics.FromHdc(dc))target.DrawImageUnscaled(buffer,0,0);
                                    }
                                }
                            } finally {EndPaint(Handle,ref info);}
                            m.Result=IntPtr.Zero;return;
                        }
                    }
                    if(m.Msg==0x0318 && m.WParam!=IntPtr.Zero) {
                        using(Graphics g=Graphics.FromHdc(m.WParam))owner.DrawArrows(g,buttons.ClientRectangle);
                        m.Result=IntPtr.Zero;return;
                    }
                }
                base.WndProc(ref m);
            }
            public void Dispose()
            {
                buttons.HandleCreated-=Created;buttons.HandleDestroyed-=Destroyed;
                ReleaseHandle();
            }
        }
        public bool DarkTheme {
            get { return darkTheme; }
            set {
                if(darkTheme==value)return;
                darkTheme=value;focusWithin=ContainsFocus;
                Invalidate();
                foreach(Control child in Controls)child.Invalidate();
            }
        }
        private Color BorderColor {
            get { return !Enabled ? UiTheme.Border : focusWithin ? UiTheme.FieldFocusBorder : pointerOver ? UiTheme.FieldHoverBorder : UiTheme.Border; }
        }
        private void DrawOuterBorder(Graphics g)
        {
            // UpDownBase is Opaque: replacing its OnPaint also owns the exposed
            // gap between the native edit and spin buttons (normally one pixel).
            // Exclude child bounds so we never cover edit text, selection or caret.
            using(Region exposed=new Region(ClientRectangle))
            using(Brush background=new SolidBrush(UiTheme.Input)) {
                foreach(Control child in Controls)if(child.Visible)exposed.Exclude(child.Bounds);
                g.FillRegion(background,exposed);
            }
            if(BorderStyle==BorderStyle.None)return;
            int w=ClientSize.Width,h=ClientSize.Height;
            if(w<3 || h<3)return;
            using(Pen inner=new Pen(UiTheme.Input))
                g.DrawRectangle(inner,1,1,w-3,h-3);
            using(Pen edge=new Pen(BorderColor))
                g.DrawRectangle(edge,0,0,w-1,h-1);
        }
        private void RefreshOuterBorder()
        {
            if(!DarkTheme || !IsHandleCreated || IsDisposed || Disposing)return;
            using(Graphics g=Graphics.FromHwnd(Handle))DrawOuterBorder(g);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if(!DarkTheme) {base.OnPaint(e);return;}
            // UpDownBase paints its native bright frame in OnPaint; own only this frame.
            DrawOuterBorder(e.Graphics);
        }
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.MouseEnter+=ChildMouseEnter;
            e.Control.MouseLeave+=ChildMouseLeave;
            if (!(e.Control is TextBoxBase)) {
                if(arrowWindow!=null)arrowWindow.Dispose();
                arrowButtons=e.Control;arrowWindow=new ArrowPaintWindow(this,e.Control);
            }
        }
        protected override void OnControlRemoved(ControlEventArgs e)
        {
            e.Control.MouseEnter-=ChildMouseEnter;
            e.Control.MouseLeave-=ChildMouseLeave;
            if(e.Control==arrowButtons) {
                if(arrowWindow!=null)arrowWindow.Dispose();
                arrowWindow=null;arrowButtons=null;
            }
            base.OnControlRemoved(e);
        }
        private void ChildMouseEnter(object sender,EventArgs e) {pointerOver=true;RefreshOuterBorder();}
        private void ChildMouseLeave(object sender,EventArgs e) {UpdatePointer();}
        private void UpdatePointer()
        {
            pointerOver=IsHandleCreated && RectangleToScreen(ClientRectangle).Contains(Cursor.Position);
            RefreshOuterBorder();
        }
        protected override void OnMouseEnter(EventArgs e) {pointerOver=true;base.OnMouseEnter(e);RefreshOuterBorder();}
        protected override void OnMouseLeave(EventArgs e) {base.OnMouseLeave(e);UpdatePointer();}
        protected override void OnEnter(EventArgs e) {focusWithin=true;base.OnEnter(e);RefreshOuterBorder();}
        protected override void OnLeave(EventArgs e) {focusWithin=false;base.OnLeave(e);RefreshOuterBorder();}
        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);RefreshOuterBorder();
            if(DarkTheme)foreach(Control child in Controls)child.Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing && arrowWindow!=null) {arrowWindow.Dispose();arrowWindow=null;arrowButtons=null;}
            base.Dispose(disposing);
        }
        private void DrawArrows(Graphics graphics,Rectangle bounds)
        {
            int w=bounds.Width,h=bounds.Height;
            using (Brush b = new SolidBrush(UiTheme.Input)) graphics.FillRectangle(b,bounds);
            using (Pen p = new Pen(UiTheme.Border)) {
                graphics.DrawRectangle(p,0,0,Math.Max(0,w-1),Math.Max(0,h-1));
                graphics.DrawLine(p,0,h/2,w,h/2);
            }
            int rows=Math.Min(4,Math.Min(w/2,h/2));
            int upper=(h/2-rows)/2, lower=h/2+(h-h/2-rows)/2;
            using (Brush b = new SolidBrush(Enabled ? UiTheme.TextColor : UiTheme.Muted)) {
                // Identical integer-pixel glyphs, mirrored vertically (1/3/5/7 and 7/5/3/1).
                for(int row=0;row<rows;row++) {
                    int upWidth=1+row*2, downWidth=1+(rows-1-row)*2;
                    graphics.FillRectangle(b,w/2-upWidth/2,upper+row,upWidth,1);
                    graphics.FillRectangle(b,w/2-downWidth/2,lower+row,downWidth,1);
                }
            }
        }
    }
    internal sealed class ThemeTrackBar : TrackBar, IThemeSurface
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; public Rectangle Bounds { get { return Rectangle.FromLTRB(Left,Top,Right,Bottom); } } }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr wParam,ref NativeRect rect);
        public bool DarkTheme { get; set; }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (!DarkTheme || m.Msg != 0x000F || Orientation != Orientation.Horizontal) return;
            NativeRect channel = new NativeRect(), thumb = new NativeRect();
            SendMessage(Handle,0x041A,IntPtr.Zero,ref channel);
            SendMessage(Handle,0x0419,IntPtr.Zero,ref thumb);
            Rectangle track = channel.Bounds, handle = thumb.Bounds;
            if (track.Width <= 0 || handle.Width <= 0) return;
            using (Graphics g = Graphics.FromHwnd(Handle)) {
                using (Brush b = new SolidBrush(BackColor)) g.FillRectangle(b,ClientRectangle);
                int y = handle.Top+handle.Height/2;
                using (Pen p = new Pen(UiTheme.Border,3)) g.DrawLine(p,track.Left,y,track.Right,y);
                if (Enabled) using (Pen p = new Pen(UiTheme.Accent,3)) g.DrawLine(p,track.Left,y,handle.Left+handle.Width/2,y);
                using (Brush b = new SolidBrush(Enabled ? UiTheme.TextColor : UiTheme.Muted)) g.FillRectangle(b,handle);
                if (TickStyle != TickStyle.None) {
                    int intervals = Math.Max(1,(Maximum-Minimum)/Math.Max(1,TickFrequency));
                    intervals = Math.Min(intervals,100);
                    using (Pen p = new Pen(UiTheme.Border)) for (int i=0;i<=intervals;i++) {
                        int x = handle.Width/2+(ClientSize.Width-handle.Width)*i/intervals;
                        if (TickStyle == TickStyle.BottomRight || TickStyle == TickStyle.Both) g.DrawLine(p,x,Math.Min(Height-4,handle.Bottom+2),x,Math.Min(Height-2,handle.Bottom+4));
                        if (TickStyle == TickStyle.TopLeft || TickStyle == TickStyle.Both) g.DrawLine(p,x,Math.Max(1,handle.Top-4),x,Math.Max(3,handle.Top-2));
                    }
                }
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-1,-1),UiTheme.Muted,BackColor);
            }
        }
    }
}

namespace FilmGrainStudioPreview
{
    // Editable presets retain the previous numeric ranges, including non-preset values.
    internal sealed class ThemeIntegerCombo : ThemeComboBox
    {
        private readonly int minimum,maximum;
        private readonly bool percent;
        private int committed;
        public event EventHandler ValueChanged;
        internal ThemeIntegerCombo(int min,int max,int initial,int[] presets,bool showPercent)
        {
            minimum=min;maximum=max;percent=showPercent;
            DropDownStyle=ComboBoxStyle.DropDown;
            foreach(int value in presets)Items.Add(FormatValue(value));
            Value=initial;
        }
        private string FormatValue(int value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture)+(percent ? "%" : ""); }
        internal bool TryGetValue(out int value)
        {
            string text=(Text ?? "").Trim();
            if(percent && (text.EndsWith("%") || text.EndsWith("％")))text=text.Substring(0,text.Length-1).Trim();
            return int.TryParse(text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out value) && value>=minimum && value<=maximum;
        }
        public decimal Value {
            get { int value; if(!TryGetValue(out value))throw new FormatException("Invalid numeric input"); return value; }
            set { if(value!=decimal.Truncate(value) || value<minimum || value>maximum)throw new ArgumentOutOfRangeException("value");Text=FormatValue((int)value); }
        }
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            int value;
            if(TryGetValue(out value) && value!=committed) {committed=value; EventHandler handler=ValueChanged;if(handler!=null)handler(this,EventArgs.Empty);}
        }
        internal bool ValidateInput()
        {
            int value;
            if(TryGetValue(out value))return true;
            MessageBox.Show(FindForm(),"请输入整数 / Enter an integer: "+minimum+"–"+maximum,"FilmGrain Studio",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            Text=FormatValue(committed);return false;
        }
        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            if(!Enabled)return;
            int value;
            if(ValidateInput() && TryGetValue(out value))Text=FormatValue(value);
        }
    }
}
