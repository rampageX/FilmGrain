using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private readonly SubtitleState subtitleState = new SubtitleState();
        private readonly ToolTip subtitleToolTip = new ToolTip();

        private sealed class SubtitleChoice
        {
            internal string Mode, Path, Label, Display;
            internal int Index;
            public override string ToString() { return Display; }
        }

        private void RefreshSubtitleButton()
        {
            btnSubtitle.Text = lang.T(subtitleState.Enabled ? "button.subtitle_on" : "button.subtitle");
            subtitleToolTip.SetToolTip(btnSubtitle, subtitleState.Enabled ? subtitleState.Label : lang.T("subtitle.label_off"));
        }

        private static void SetSubtitleColor(Button button, string hex)
        {
            string value = (hex ?? "").Trim().TrimStart('#');
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]{6}$")) value = "FFFFFF";
            button.Text = "#" + value.ToUpperInvariant();
            button.BackColor = ColorTranslator.FromHtml(button.Text);
            int brightness = (button.BackColor.R * 299 + button.BackColor.G * 587 + button.BackColor.B * 114) / 1000;
            button.ForeColor = brightness < 128 ? Color.White : Color.Black;
        }

        private void ShowSubtitleDialog()
        {
            string target = null;
            if (listFiles.SelectedItems.Count == 1) target = listFiles.SelectedItems[0].Tag as string;
            else if (listFiles.Items.Count == 1) target = listFiles.Items[0].Tag as string;
            string ffprobe = Path.Combine(config.Get("FFMPEG_DIR"), "ffprobe.exe");
            List<SubtitleTrack> tracks = target == null ? new List<SubtitleTrack>() : SubtitleCore.Probe(target, ffprobe);
            string sameName = SubtitleCore.FindSameName(target);
            using (Form dialog = new Form())
            {
                dialog.Text = lang.T("subtitle.title");
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(610, 425);
                dialog.Font = UiFont(9f, FontStyle.Regular);
                TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 10 };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
                for (int i = 0; i < 8; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                dialog.Controls.Add(table);
                ComboBox source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 500 };
                List<SubtitleChoice> choices = new List<SubtitleChoice>();
                Action<string, int, string, string, string> add = (mode, index, path, label, display) =>
                {
                    SubtitleChoice choice = new SubtitleChoice { Mode = mode, Index = index, Path = path, Label = label, Display = display };
                    choices.Add(choice);
                    source.Items.Add(choice);
                };
                add("OFF", 0, "", lang.T("subtitle.label_off"), lang.T("subtitle.off"));
                add("AUTO", 0, "", lang.T("subtitle.label_auto"), lang.T("subtitle.auto"));
                foreach (SubtitleTrack track in tracks)
                {
                    string display = LF("subtitle.embedded_item", track.Ordinal + 1, track.Language, track.Codec);
                    if (!string.IsNullOrEmpty(track.Title)) display += " · " + track.Title;
                    add("EMBEDDED", track.Ordinal, "", LF("subtitle.label_embedded", track.Ordinal + 1), display);
                }
                if (sameName != null) add("EXTERNAL", 0, sameName, LF("subtitle.label_external", Path.GetFileName(sameName)), lang.T("subtitle.same_name") + Path.GetFileName(sameName));
                add("BROWSE", 0, "", lang.T("subtitle.label_external_file"), lang.T("subtitle.browse_external"));
                int desired = 0;
                if (subtitleState.Enabled)
                {
                    for (int i = 0; i < choices.Count; i++)
                    {
                        SubtitleChoice c = choices[i];
                        if (subtitleState.Mode == "AUTO" && c.Mode == "AUTO" ||
                            subtitleState.Mode == "EMBEDDED" && c.Mode == "EMBEDDED" && c.Index == subtitleState.EmbeddedIndex ||
                            subtitleState.Mode == "EXTERNAL" && c.Mode == "EXTERNAL" && c.Path == subtitleState.ExternalPath)
                        { desired = i; break; }
                    }
                }
                else if (tracks.Count > 0) desired = 2;
                else if (sameName != null) desired = choices.Count - 2;
                else desired = 1;
                source.SelectedIndex = desired;
                Action<int, string, Control, int> row = (index, key, control, span) =>
                {
                    Label label = new Label { Text = lang.T(key), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4, 3, 3, 3) };
                    table.Controls.Add(label, 0, index);
                    control.Dock = DockStyle.Fill;
                    table.Controls.Add(control, 1, index);
                    if (span == 2) table.SetColumnSpan(control, 2);
                };
                row(0, "subtitle.source", source, 1);
                Button browse = new Button { Text = lang.T("config.browse"), Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
                table.Controls.Add(browse, 2, 0);
                TextBox font = new TextBox { Text = subtitleState.FontName, Margin = new Padding(4, 6, 3, 5) };
                row(1, "subtitle.font", font, 2);
                NumericUpDown size = new NumericUpDown { Minimum = 6, Maximum = 200, Value = subtitleState.FontSize };
                row(2, "subtitle.size", size, 2);
                Button primary = new Button(); SetSubtitleColor(primary, subtitleState.PrimaryHex);
                row(3, "subtitle.primary_color", primary, 2);
                Button border = new Button(); SetSubtitleColor(border, subtitleState.BorderHex);
                row(4, "subtitle.border_color", border, 2);
                NumericUpDown outline = new NumericUpDown { DecimalPlaces = 1, Increment = .5m, Minimum = 0, Maximum = 10, Value = subtitleState.Outline };
                row(5, "subtitle.outline", outline, 2);
                NumericUpDown shadow = new NumericUpDown { DecimalPlaces = 1, Increment = .5m, Minimum = 0, Maximum = 10, Value = subtitleState.Shadow };
                row(6, "subtitle.shadow", shadow, 2);
                NumericUpDown margin = new NumericUpDown { Minimum = 0, Maximum = 300, Value = subtitleState.MarginV };
                row(7, "subtitle.margin_bottom", margin, 2);
                Label note = new Label { Dock = DockStyle.Fill, Text = lang.T("subtitle.note"), ForeColor = Color.FromArgb(100, 107, 116), Padding = new Padding(4, 6, 4, 0) };
                table.Controls.Add(note, 0, 8); table.SetColumnSpan(note, 3);
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                Button ok = new Button { Text = lang.T("advanced.ok"), Width = 82, DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = lang.T("advanced.cancel"), Width = 82, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                table.Controls.Add(buttons, 0, 9); table.SetColumnSpan(buttons, 3);
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                using (ColorDialog color = new ColorDialog())
                {
                    primary.Click += delegate { color.Color = primary.BackColor; if (color.ShowDialog(dialog) == DialogResult.OK) SetSubtitleColor(primary, string.Format("{0:X2}{1:X2}{2:X2}", color.Color.R, color.Color.G, color.Color.B)); };
                    border.Click += delegate { color.Color = border.BackColor; if (color.ShowDialog(dialog) == DialogResult.OK) SetSubtitleColor(border, string.Format("{0:X2}{1:X2}{2:X2}", color.Color.R, color.Color.G, color.Color.B)); };
                    browse.Click += delegate
                    {
                        using (OpenFileDialog files = new OpenFileDialog())
                        {
                            files.Title = lang.T("subtitle.select_external"); files.Filter = lang.T("subtitle.filter");
                            if (target != null) files.InitialDirectory = Path.GetDirectoryName(target);
                            if (files.ShowDialog(dialog) != DialogResult.OK) return;
                            SubtitleChoice choice = choices[choices.Count - 1];
                            choice.Path = files.FileName;
                            choice.Label = LF("subtitle.label_external", Path.GetFileName(files.FileName));
                            choice.Display = LF("subtitle.external_item", Path.GetFileName(files.FileName));
                            source.Items[source.Items.Count - 1] = choice;
                            source.SelectedIndex = source.Items.Count - 1;
                        }
                    };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                }
                SubtitleChoice selected = choices[source.SelectedIndex];
                if (selected.Mode == "BROWSE" && selected.Path.Length == 0)
                {
                    MessageBox.Show(this, lang.T("subtitle.not_selected"), dialog.Text); return;
                }
                subtitleState.Enabled = selected.Mode != "OFF";
                subtitleState.Mode = selected.Mode == "BROWSE" ? "EXTERNAL" : selected.Mode;
                subtitleState.EmbeddedIndex = selected.Index;
                subtitleState.ExternalPath = selected.Path;
                subtitleState.FontName = font.Text.Trim().Length == 0 ? "huiwen-mincho" : font.Text.Trim();
                subtitleState.FontSize = (int)size.Value;
                subtitleState.PrimaryHex = primary.Text.TrimStart('#');
                subtitleState.BorderHex = border.Text.TrimStart('#');
                subtitleState.Outline = outline.Value;
                subtitleState.Shadow = shadow.Value;
                subtitleState.MarginV = (int)margin.Value;
                subtitleState.Label = subtitleState.Enabled ? selected.Label : lang.T("subtitle.label_off");
                RefreshSubtitleButton();
            }
        }
    }
}
