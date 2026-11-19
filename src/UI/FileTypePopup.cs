using System;
using System.Drawing;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>Small multi-select popup for broad file categories. Closes when it loses focus.</summary>
    internal sealed class FileTypePopup : Form
    {
        private readonly Theme _theme;
        private readonly Action<int> _changed;
        private readonly CheckBox _all;
        private readonly CheckBox[] _boxes;
        private int _mask;

        public FileTypePopup(Theme theme, int mask, Action<int> changed)
        {
            _theme = theme;
            _changed = changed;
            _mask = mask & (int)FileCategory.All;

            Text = "文件类型";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Surface;
            ForeColor = theme.TextMain;
            Font = theme.Ui;
            KeyPreview = true;

            int pad = theme.S(12);
            int rowH = theme.S(28);
            int width = theme.S(190);
            int rows = FileTypes.Names.Length + 1;
            ClientSize = new Size(width, pad * 2 + rows * rowH + theme.S(6));

            _all = new CheckBox();
            _all.Text = "全部类型";
            _all.Checked = _mask == 0;
            _all.SetBounds(pad, pad, width - pad * 2, rowH);
            _all.BackColor = theme.Surface;
            _all.ForeColor = theme.TextMain;
            _all.FlatStyle = FlatStyle.Flat;
            _all.AutoCheck = false;
            _all.Click += delegate { ApplyMask(0); };
            Controls.Add(_all);

            _boxes = new CheckBox[FileTypes.Names.Length];
            for (int i = 0; i < _boxes.Length; i++)
            {
                var cb = new CheckBox();
                cb.Text = FileTypes.Names[i];
                cb.Checked = (_mask & (int)FileTypes.Values[i]) != 0;
                cb.SetBounds(pad + theme.S(14), pad + rowH * (i + 1), width - pad * 2 - theme.S(14), rowH);
                cb.BackColor = theme.Surface;
                cb.ForeColor = theme.TextMain;
                cb.FlatStyle = FlatStyle.Flat;
                int bit = (int)FileTypes.Values[i];
                cb.CheckedChanged += delegate
                {
                    if (cb.Checked) _mask |= bit;
                    else _mask &= ~bit;
                    SyncAll();
                    if (_changed != null) _changed(_mask);
                };
                Controls.Add(cb);
                _boxes[i] = cb;
            }

            Deactivate += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(_theme.Line)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
        }

        private void ApplyMask(int mask)
        {
            _mask = mask;
            for (int i = 0; i < _boxes.Length; i++)
            {
                bool on = (mask & (int)FileTypes.Values[i]) != 0;
                if (_boxes[i].Checked != on) _boxes[i].Checked = on;
            }
            SyncAll();
            if (_changed != null) _changed(_mask);
        }

        private void SyncAll()
        {
            bool all = _mask == 0;
            if (_all.Checked != all) _all.Checked = all;
        }
    }
}

