using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>Minimal settings dialog: indexed volumes, auto refresh, hotkey, dark theme.</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly Settings _settings;
        private readonly CheckBox[] _volumeBoxes;
        private readonly List<string> _volumeRoots;
        private readonly CheckBox _autoRefresh;
        private readonly CheckBox _hotkey;
        private readonly CheckBox _dark;
        private readonly NumericUpDown _limit;
        private readonly float _scale;

        public bool RebuildRequested;

        public SettingsForm(Settings settings)
        {
            _settings = settings;
            _scale = 1f;
            try { _scale = CreateGraphics().DpiX / 96f; }
            catch (Exception) { }
            if (_scale < 0.9f) _scale = 0.9f;
            if (_scale > 3f) _scale = 3f;

            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.White;

            var roots = new List<string>();
            foreach (string r in Settings.DetectFixedNtfsVolumes()) roots.Add(r);
            foreach (string r in settings.Volumes)
            {
                string n = Settings.NormalizeRoot(r);
                if (n != null && !roots.Contains(n)) roots.Add(n);
            }
            _volumeRoots = roots;

            int pad = S(22);
            int top = S(18);
            int width = S(430);
            int rowH = S(26);

            var title = new Label();
            title.Text = "索引范围";
            title.Font = new Font(Font, FontStyle.Bold);
            title.SetBounds(pad, top, width - pad * 2, S(22));
            Controls.Add(title);
            top += S(30);

            _volumeBoxes = new CheckBox[roots.Count];
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                string label = root;
                try
                {
                    var drive = new System.IO.DriveInfo(root);
                    double gb = drive.TotalSize / 1073741824.0;
                    string vol = string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
                    label = root + "   " + vol + (gb > 0 ? "   (" + gb.ToString("0") + " GB)" : string.Empty);
                }
                catch (Exception) { }
                var cb = new CheckBox();
                cb.Text = label;
                cb.Checked = settings.Volumes.Contains(root);
                cb.SetBounds(pad + S(6), top, width - pad * 2 - S(6), rowH);
                Controls.Add(cb);
                _volumeBoxes[i] = cb;
                top += rowH;
            }

            top += S(14);
            _autoRefresh = MakeCheck("文件变化后在后台自动刷新索引", settings.AutoRefresh, pad, ref top, width);
            _hotkey = MakeCheck("全局热键 Ctrl+Alt+Space 唤出 / 隐藏", settings.EnableHotkey, pad, ref top, width);
            _dark = MakeCheck("深色主题（立即生效）", settings.DarkTheme, pad, ref top, width);

            top += S(8);
            var limitLabel = new Label();
            limitLabel.Text = "结果上限";
            limitLabel.SetBounds(pad + S(6), top + S(4), S(90), S(22));
            Controls.Add(limitLabel);

            _limit = new NumericUpDown();
            _limit.Minimum = 1000;
            _limit.Maximum = 200000;
            _limit.Increment = 1000;
            _limit.Value = Math.Min(200000, Math.Max(1000, settings.ResultLimit));
            _limit.SetBounds(pad + S(100), top, S(110), S(24));
            Controls.Add(_limit);

            top += S(44);
            var rebuild = new Button();
            rebuild.Text = "立即重建索引";
            rebuild.SetBounds(pad + S(6), top, S(140), S(32));
            rebuild.Click += delegate { RebuildRequested = true; };
            Controls.Add(rebuild);

            int clientH = top + S(74);
            ClientSize = new Size(width, clientH);

            var ok = new Button();
            ok.Text = "保存";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(ClientSize.Width - S(190), ClientSize.Height - S(48), S(80), S(30));
            ok.Click += delegate { Apply(); };
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(ClientSize.Width - S(100), ClientSize.Height - S(48), S(80), S(30));
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private int S(int value)
        {
            int scaled = (int)Math.Round(value * _scale);
            return scaled < 1 ? 1 : scaled;
        }

        private CheckBox MakeCheck(string text, bool value, int pad, ref int top, int width)
        {
            var cb = new CheckBox();
            cb.Text = text;
            cb.Checked = value;
            cb.SetBounds(pad + S(6), top, width - pad * 2 - S(6), S(26));
            Controls.Add(cb);
            top += S(28);
            return cb;
        }

        private void Apply()
        {
            var volumes = new List<string>();
            for (int i = 0; i < _volumeBoxes.Length; i++)
                if (_volumeBoxes[i].Checked) volumes.Add(_volumeRoots[i]);
            if (volumes.Count == 0) volumes.AddRange(Settings.DetectFixedNtfsVolumes());
            _settings.Volumes = volumes;
            _settings.AutoRefresh = _autoRefresh.Checked;
            _settings.EnableHotkey = _hotkey.Checked;
            _settings.DarkTheme = _dark.Checked;
            _settings.ResultLimit = (int)_limit.Value;
        }
    }
}


