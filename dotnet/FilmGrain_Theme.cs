using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    // Round one: one instance owned by MainForm; no global events or image transforms.
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
            internal bool OwnerDraw, GridLines;
            internal BorderStyle TextBorder;
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
            if (c is PictureBox || controls.ContainsKey(c)) return;
            Snapshot s = new Snapshot(); s.Back = LocalColor(c, "BackColor"); s.Fore = LocalColor(c, "ForeColor");
            Button b = c as Button;
            if (b != null) { s.Flat = b.FlatStyle; s.Visual = b.UseVisualStyleBackColor; s.Edge = b.FlatAppearance.BorderColor; s.Hover = b.FlatAppearance.MouseOverBackColor; s.Down = b.FlatAppearance.MouseDownBackColor; }
            MaskedTextBox masked = c as MaskedTextBox;
            if (masked != null) s.TextBorder = masked.BorderStyle;
            ComboBox combo = c as ComboBox;
            if (combo != null) { s.Draw = combo.DrawMode; if (s.Draw == DrawMode.Normal) combo.DrawItem += DrawCombo; }
            ListView list = c as ListView;
            if (list != null) {
                s.OwnerDraw = list.OwnerDraw; s.GridLines = list.GridLines;
                if (!s.OwnerDraw) { list.DrawColumnHeader += DrawHeader; list.DrawItem += DrawListItem; list.DrawSubItem += DrawSubItem; }
            }
            controls.Add(c, s);
            c.ForeColorChanged += ForeChanged;
            c.BackColorChanged += BackChanged;
            c.ControlAdded += Added;
            c.Disposed += Removed;
            ToolStrip strip = c as ToolStrip;
            if (strip != null) { s.Renderer = strip.Renderer; foreach (ToolStripItem i in strip.Items) RegisterItem(i); }
            foreach (Control child in c.Controls) Register(child);
        }
        private void RegisterItem(ToolStripItem i)
        {
            if (items.ContainsKey(i)) return;
            items.Add(i, i.ForeColor); i.ForeColorChanged += ItemForeChanged;
            ToolStripDropDownItem d = i as ToolStripDropDownItem;
            if (d != null) foreach (ToolStripItem child in d.DropDownItems) RegisterItem(child);
        }
        private void PaintControl(Control c)
        {
            Snapshot s = controls[c]; bool previousApplying = applying; applying = true;
            try {
                bool field = c is TextBoxBase || c is ComboBox || c is NumericUpDown || c is ListView || c is ListBox;
                c.BackColor = dark ? (field || c is Button ? Input : Surface) : s.Back;
                c.ForeColor = dark ? Foreground(s.Fore) : s.Fore;
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
                    if (dark && (s.Back.R > s.Back.G * 1.5 || s.Back.B > s.Back.R * 1.5)) { b.BackColor = s.Back.R > s.Back.G * 1.5 ? s.Back : Accent; b.ForeColor = Color.White; if (s.Back.B > s.Back.R * 1.5) { b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0,170,126); b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0,123,90); } }
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
        private void ItemForeChanged(object sender, EventArgs e) { if (applying) return; ToolStripItem i = (ToolStripItem)sender; items[i] = i.ForeColor; PaintItem(i); }
        private void Added(object sender, ControlEventArgs e)
        {
            Register(e.Control);
            if (dark) ApplyBranch(e.Control);
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
            c.ForeColorChanged -= ForeChanged; c.BackColorChanged -= BackChanged; c.ControlAdded -= Added; c.Disposed -= Removed;
            ComboBox combo = c as ComboBox; if (combo != null && s.Draw == DrawMode.Normal) combo.DrawItem -= DrawCombo;
            ListView list = c as ListView;
            if (list != null && !s.OwnerDraw) { list.DrawColumnHeader -= DrawHeader; list.DrawItem -= DrawListItem; list.DrawSubItem -= DrawSubItem; }
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
            foreach (ToolStripItem i in items.Keys) i.ForeColorChanged -= ItemForeChanged;
            items.Clear();
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
        }
        private void InitializeTheme()
        {
            uiTheme = new UiTheme();
            uiTheme.EnableNativeScrollbars(listFiles);
            uiTheme.EnableNativeScrollbars(log);
            bool dark = string.Equals(config.Get("UI_THEME"), "DARK", StringComparison.OrdinalIgnoreCase);
            uiTheme.Apply(this, dark); UpdateThemeMenu(dark);
            FormClosed += delegate { uiTheme.Dispose(); themeButton.Image = null; sunThemeIcon.Dispose(); moonThemeIcon.Dispose(); };
        }
        private void UpdateThemeMenu(bool dark)
        {
            selectedDarkTheme = dark;
            themeButton.Image = dark ? sunThemeIcon : moonThemeIcon;
            themeButton.Text = dark ? UiText("切换为浅色", "Switch to light") : UiText("切换为暗色", "Switch to dark");
            themeButton.ToolTipText = themeButton.Text;
            themeButton.AccessibleName = themeButton.Text;
        }
        private void SelectTheme(bool dark)
        {
            try { config.SaveValue("UI_THEME", dark ? "DARK" : "LIGHT"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            uiTheme.Apply(this, dark); UpdateThemeMenu(dark);
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal interface IThemeSurface { bool DarkTheme { get; set; } }
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
            ThemeDrawing.DrawText(e.Graphics,Text,Font,r,UiTheme.Muted,ThemeDrawing.TextFlags(TextAlign,AutoEllipsis) | (UseMnemonic ? 0 : TextFormatFlags.NoPrefix));
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
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!DarkTheme || Enabled) return;
            using (Brush b = new SolidBrush(UiTheme.Input)) e.Graphics.FillRectangle(b,ClientRectangle);
            using (Pen p = new Pen(UiTheme.Border)) e.Graphics.DrawRectangle(p,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
            ThemeDrawing.DrawText(e.Graphics,Text,Font,Rectangle.Inflate(ClientRectangle,-3,-2),UiTheme.Muted,ThemeDrawing.TextFlags(TextAlign,AutoEllipsis));
        }
    }
    // Use the framework checkbox renderer; do not mix custom painting with its adapter.
    internal sealed class ThemeCheckBox : CheckBox, IThemeSurface
    {
        private bool dark;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!dark || Enabled || CheckAlign != ContentAlignment.MiddleLeft) return;
            int glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics,System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width;
            Rectangle r = new Rectangle(Padding.Left+glyph+4,Padding.Top,Math.Max(0,Width-Padding.Horizontal-glyph-4),Math.Max(0,Height-Padding.Vertical));
            using (Brush brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush,r);
            ThemeDrawing.DrawText(e.Graphics,Text,Font,r,UiTheme.Muted,ThemeDrawing.TextFlags(TextAlign,AutoEllipsis));
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
                FlatAppearance.CheckedBackColor = dark ? UiTheme.Accent : originalChecked;
                FlatAppearance.MouseOverBackColor = dark ? UiTheme.Input : originalHover;
                FlatAppearance.MouseDownBackColor = dark ? UiTheme.Input : originalDown;
                Invalidate();
            }
        }
    }
    internal class ThemeComboBox : ComboBox, IThemeSurface
    {
        private IntPtr disabledBrush;
        private bool pointerOver;
        protected override void OnMouseEnter(EventArgs e) { pointerOver = true; base.OnMouseEnter(e); if (DarkTheme) Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { pointerOver = false; base.OnMouseLeave(e); if (DarkTheme) Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (DarkTheme) Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (DarkTheme) Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); if (DarkTheme) Invalidate(); }
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
                if (DropDownStyle == ComboBoxStyle.DropDown)
                    FlatStyle = value ? FlatStyle.Flat : lightFlatStyle;
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
            if (DarkTheme && !Enabled && (m.Msg == 0x0138 || m.Msg == 0x0133)) {
                if (disabledBrush == IntPtr.Zero) disabledBrush = CreateSolidBrush(ColorRef(UiTheme.Input));
                if (disabledBrush != IntPtr.Zero) {
                    SetTextColor(m.WParam, ColorRef(UiTheme.Muted));
                    SetBkColor(m.WParam, ColorRef(UiTheme.Input));
                    m.Result = disabledBrush; return;
                }
            }
            base.WndProc(ref m);
            // Normal paint only: preserve native popup, keyboard, IME and accessibility behavior.
            if (!DarkTheme || m.Msg != 0x000F || DropDownStyle == ComboBoxStyle.Simple) return;
            using (Graphics g = Graphics.FromHwnd(Handle)) {
                int width = SystemInformation.VerticalScrollBarWidth;
                Rectangle arrow = new Rectangle(Math.Max(1,ClientSize.Width-width-1),1,width,Math.Max(0,ClientSize.Height-2));
                if (!Enabled) {
                    Rectangle text = new Rectangle(2,1,Math.Max(0,arrow.Left-3),Math.Max(0,ClientSize.Height-2));
                    using (Brush b = new SolidBrush(UiTheme.Input)) g.FillRectangle(b,text);
                    ThemeDrawing.DrawText(g,Text,Font,text,UiTheme.Muted,TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                using (Brush b = new SolidBrush(UiTheme.Input)) {
                    g.FillRectangle(b,arrow);
                    // Include the native separator immediately to the left of the arrow.
                    g.FillRectangle(b,Math.Max(1,arrow.Left-2),2,2,Math.Max(0,ClientSize.Height-4));
                }
                // Cover both native edge pixels; the old single outline left a bright inner rim.
                // Confine this to the border, preserving the editable child and popup behavior.
                using (Pen inner = new Pen(UiTheme.Input))
                    if (ClientSize.Width > 3 && ClientSize.Height > 3) g.DrawRectangle(inner,1,1,ClientSize.Width-3,ClientSize.Height-3);
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
        public bool DarkTheme { get; set; }
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (!(e.Control is TextBoxBase)) { e.Control.Paint -= PaintArrows; e.Control.Paint += PaintArrows; }
        }
        private void PaintArrows(object sender, PaintEventArgs e)
        {
            if (!DarkTheme) return;
            Control buttons = (Control)sender;
            int w = buttons.ClientSize.Width, h = buttons.ClientSize.Height;
            using (Brush b = new SolidBrush(UiTheme.Input)) e.Graphics.FillRectangle(b,buttons.ClientRectangle);
            using (Pen p = new Pen(UiTheme.Border)) {
                e.Graphics.DrawRectangle(p,0,0,Math.Max(0,w-1),Math.Max(0,h-1));
                e.Graphics.DrawLine(p,0,h/2,w,h/2);
            }
            int x = w/2, top = h/4, bottom = h*3/4;
            using (Brush b = new SolidBrush(Enabled ? UiTheme.TextColor : UiTheme.Muted)) {
                e.Graphics.FillPolygon(b,new Point[]{new Point(x-3,top+1),new Point(x+3,top+1),new Point(x,top-2)});
                e.Graphics.FillPolygon(b,new Point[]{new Point(x-3,bottom-1),new Point(x+3,bottom-1),new Point(x,bottom+2)});
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
