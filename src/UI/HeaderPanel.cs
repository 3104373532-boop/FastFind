using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>Search bar plus filter chips. Fully owner painted for a compact, flat look.</summary>
    internal sealed class HeaderPanel : Panel
    {
        public readonly TextBox Box;
        public event EventHandler TextChanged2;
        public event EventHandler<string> VolumeToggled;
        public event EventHandler TypeFilterToggled;
        public event EventHandler FileTypesChanged;
        public event EventHandler ThemeToggled;
        public event EventHandler SettingsRequested;

        private readonly Theme _theme;
        private readonly List<Chip> _chips = new List<Chip>();
        private Rectangle _searchRect;
        private Rectangle _themeRect;
        private Rectangle _settingsRect;
        private string _status = string.Empty;
        private int _statusRightEdge;
        private int _hoverChip = -1;
        private int _typeMask;
        private Rectangle _fileTypeRect;

        private sealed class Chip
        {
            public Rectangle Rect;
            public string Text;
            public bool Active;
            public int Kind;      // 0 = volume, 1 = file type
            public string Payload;
        }

        public HeaderPanel(Theme theme)
        {
            _theme = theme;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Surface;

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = theme.SearchFont;
            Box.BackColor = theme.Surface;
            Box.ForeColor = theme.TextMain;
            Box.TextChanged += delegate { if (TextChanged2 != null) TextChanged2(this, EventArgs.Empty); };
            Controls.Add(Box);
        }

        public void ApplyTheme()
        {
            BackColor = _theme.Surface;
            Box.BackColor = _theme.Surface;
            Box.ForeColor = _theme.TextMain;
            Invalidate();
        }

        public void SetStatus(string text)
        {
            _status = text == null ? string.Empty : text;
            Invalidate();
        }

        public void SetVolumes(IList<string> roots, bool[] enabled)
        {
            _chips.Clear();
            for (int i = 0; i < roots.Count; i++)
            {
                var chip = new Chip();
                chip.Text = roots[i].Substring(0, 1);
                chip.Active = enabled != null && i < enabled.Length && enabled[i];
                chip.Kind = 0;
                chip.Payload = roots[i];
                _chips.Add(chip);
            }
            LayoutChips();
            Invalidate();
        }

        public void SetTypeFilter(NameFilterKind kind)
        {
            int index = 0;
            if (kind == NameFilterKind.FilesOnly) index = 1;
            else if (kind == NameFilterKind.DirsOnly) index = 2;
            _typeIndex = index;
            Invalidate();
        }

        private int _typeIndex;

        public NameFilterKind TypeFilter
        {
            get
            {
                if (_typeIndex == 1) return NameFilterKind.FilesOnly;
                if (_typeIndex == 2) return NameFilterKind.DirsOnly;
                return NameFilterKind.Any;
            }
        }

        private void LayoutChips()
        {
            int x = _theme.S(64);
            int y = _theme.S(74);
            int h = _theme.S(24);
            for (int i = 0; i < _chips.Count; i++)
            {
                Chip c = _chips[i];
                int w = _theme.S(34);
                c.Rect = new Rectangle(x, y, w, h);
                x += w + _theme.S(6);
            }
            _typeX = x + _theme.S(12);
            _typeY = y;
            int fx = TypeRect(2).Right + _theme.S(16);
            int fw = TextRenderer.MeasureText(FileTypeLabel(), _theme.Ui).Width + _theme.S(28);
            _fileTypeRect = new Rectangle(fx, y, fw, h);
        }

        private string FileTypeLabel()
        {
            return "类型：" + FileTypes.Describe(_typeMask);
        }

        public int TypeMask { get { return _typeMask; } }

        public void SetTypeMask(int mask)
        {
            _typeMask = mask & (int)FileCategory.All;
            LayoutChips();
            Invalidate();
        }

        private int _typeX;
        private int _typeY;

        private Rectangle TypeRect(int index)
        {
            string[] labels = { "全部", "文件", "文件夹" };
            int x = _typeX;
            for (int i = 0; i < index; i++)
                x += TextRenderer.MeasureText(labels[i], _theme.Ui).Width + _theme.S(16);
            return new Rectangle(x, _typeY, TextRenderer.MeasureText(labels[index], _theme.Ui).Width + _theme.S(10), _theme.S(24));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int pad = _theme.S(22);
            int boxH = _theme.S(46);
            _searchRect = new Rectangle(pad, _theme.S(16), Math.Max(_theme.S(200), Width - pad * 2), boxH);
            _settingsRect = new Rectangle(_searchRect.Right - _theme.S(74), _searchRect.Top + (boxH - _theme.S(26)) / 2, _theme.S(26), _theme.S(26));
            _themeRect = new Rectangle(_settingsRect.Right + _theme.S(8), _settingsRect.Top, _theme.S(26), _theme.S(26));
            Box.SetBounds(_searchRect.Left + _theme.S(44), _searchRect.Top + (boxH - Box.PreferredHeight) / 2,
                _searchRect.Width - _theme.S(44) - _theme.S(90), Box.PreferredHeight);
            LayoutChips();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var bg = new SolidBrush(_theme.Surface)) g.FillRectangle(bg, ClientRectangle);
            using (var line = new Pen(_theme.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);

            // search box
            using (var path = Round(_searchRect, _theme.S(9)))
            {
                using (var fill = new SolidBrush(_theme.SurfaceAlt)) g.FillPath(fill, path);
                using (var pen = new Pen(Box.Focused ? _theme.Accent : _theme.Line, Box.Focused ? 1.6f : 1f)) g.DrawPath(pen, path);
            }
            using (var pen = new Pen(_theme.TextSub, _theme.SF(1.7f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int cx = _searchRect.Left + _theme.S(22);
                int cy = _searchRect.Top + _theme.S(18);
                int r = _theme.S(7);
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(pen, cx + _theme.S(5), cy + _theme.S(5), cx + _theme.S(10), cy + _theme.S(10));
            }

            // right side controls
            using (var b = new SolidBrush(_theme.TextSub)) g.DrawString("\uE713", _theme.IconFont, b, _settingsRect.Left, _settingsRect.Top + _theme.S(2));
            using (var b = new SolidBrush(_theme.TextSub)) g.DrawString(_theme.Dark ? "\uE706" : "\uE708", _theme.IconFont, b, _themeRect.Left, _themeRect.Top + _theme.S(2));

            // volume chips
            for (int i = 0; i < _chips.Count; i++)
            {
                Chip c = _chips[i];
                using (var path = Round(c.Rect, _theme.S(6)))
                {
                    Color fill = c.Active ? _theme.Accent : _theme.SurfaceAlt;
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var p = new Pen(_theme.Line)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, c.Text, _theme.Ui, c.Rect, c.Active ? Color.White : _theme.TextSub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // "范围" label before chips
            TextRenderer.DrawText(g, "范围", _theme.UiSmall, new Point(_theme.S(22), TypeRect(0).Top + _theme.S(4)),
                _theme.TextFaint, TextFormatFlags.NoPrefix);

            // type filter
            string[] labels = { "全部", "文件", "文件夹" };
            for (int i = 0; i < 3; i++)
            {
                Rectangle r = TypeRect(i);
                bool active = i == _typeIndex;
                if (active)
                {
                    using (var path = Round(new Rectangle(r.Left - _theme.S(6), r.Top, r.Width, r.Height), _theme.S(6)))
                    using (var b = new SolidBrush(_theme.SelectBg)) g.FillPath(b, path);
                }
                TextRenderer.DrawText(g, labels[i], _theme.Ui, r, active ? _theme.Accent : _theme.TextSub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            // file type multi-select chip
            bool ftActive = _typeMask != 0;
            using (var path = Round(_fileTypeRect, _theme.S(6)))
            {
                using (var b = new SolidBrush(ftActive ? _theme.SelectBg : (_hoverChip == 200 ? _theme.Hover : _theme.SurfaceAlt)))
                    g.FillPath(b, path);
                using (var p = new Pen(ftActive ? _theme.Accent : _theme.Line)) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, FileTypeLabel(), _theme.Ui,
                new Rectangle(_fileTypeRect.Left + _theme.S(10), _fileTypeRect.Top, _fileTypeRect.Width - _theme.S(26), _fileTypeRect.Height),
                ftActive ? _theme.Accent : _theme.TextSub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var b = new SolidBrush(ftActive ? _theme.Accent : _theme.TextSub))
            {
                int cx = _fileTypeRect.Right - _theme.S(14);
                int cy = _fileTypeRect.Top + _fileTypeRect.Height / 2;
                g.FillPolygon(b, new Point[] {
                    new Point(cx - _theme.S(4), cy - _theme.S(1)),
                    new Point(cx + _theme.S(4), cy - _theme.S(1)),
                    new Point(cx, cy + _theme.S(4))
                });
            }

            // status text on the right of the chip row
            if (_status.Length > 0)
            {
                Size sz = TextRenderer.MeasureText(g, _status, _theme.UiSmall);
                _statusRightEdge = Width - _theme.S(22) - sz.Width;
                TextRenderer.DrawText(g, _status, _theme.UiSmall, new Point(_statusRightEdge, TypeRect(0).Top + _theme.S(4)),
                    _theme.TextFaint, TextFormatFlags.NoPrefix);
            }

            // hint line under type filter
            if (Box.Text.Length == 0)
            {
                string hint = "试试：ext:jpg size:>10mb  dm:today  path:项目  -tmp  \"Q3 report\"  /^IMG_\\d+/";
                TextRenderer.DrawText(g, hint, _theme.UiSmall, new Point(_fileTypeRect.Right + _theme.S(18), TypeRect(0).Top + _theme.S(4)),
                    _theme.TextFaint, TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitChip(e.Location);
            if (hit != _hoverChip)
            {
                _hoverChip = hit;
                Cursor = hit >= 0 || _settingsRect.Contains(e.Location) || _themeRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverChip = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        private int HitChip(Point p)
        {
            for (int i = 0; i < _chips.Count; i++) if (_chips[i].Rect.Contains(p)) return i;
            for (int i = 0; i < 3; i++) if (TypeRect(i).Contains(p)) return 100 + i;
            if (_fileTypeRect.Contains(p)) return 200;
            return -1;
        }

        /// <summary>Current selected state of a volume chip.</summary>
        public bool GetVolumeActive(string root)
        {
            for (int i = 0; i < _chips.Count; i++)
                if (string.Equals(_chips[i].Payload, root, StringComparison.OrdinalIgnoreCase))
                    return _chips[i].Active;
            return false;
        }

        /// <summary>Flips a volume chip and notifies listeners. Used by mouse clicks and automation.</summary>
        public void ToggleVolume(string root)
        {
            for (int i = 0; i < _chips.Count; i++)
            {
                Chip c = _chips[i];
                if (!string.Equals(c.Payload, root, StringComparison.OrdinalIgnoreCase)) continue;
                c.Active = !c.Active;
                Invalidate();
                if (VolumeToggled != null) VolumeToggled(this, c.Payload);
                return;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_settingsRect.Contains(e.Location))
            {
                if (SettingsRequested != null) SettingsRequested(this, EventArgs.Empty);
                return;
            }
            if (_themeRect.Contains(e.Location))
            {
                if (ThemeToggled != null) ThemeToggled(this, EventArgs.Empty);
                return;
            }
            int hit = HitChip(e.Location);
            if (hit >= 0 && hit < 100)
            {
                ToggleVolume(_chips[hit].Payload);
                return;
            }
            if (hit == 200)
            {
                ShowFileTypePopup();
                return;
            }
            if (hit >= 100)
            {
                _typeIndex = hit - 100;
                Invalidate();
                if (TypeFilterToggled != null) TypeFilterToggled(this, EventArgs.Empty);
            }
        }

        private void ShowFileTypePopup()
        {
            Point p = PointToScreen(new Point(_fileTypeRect.Left, _fileTypeRect.Bottom + _theme.S(2)));
            var popup = new FileTypePopup(_theme, _typeMask, delegate(int mask)
            {
                SetTypeMask(mask);
                if (FileTypesChanged != null) FileTypesChanged(this, EventArgs.Empty);
            });
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int h = popup.Height;
            if (p.Y + h > wa.Bottom) p.Y = PointToScreen(new Point(0, _fileTypeRect.Top)).Y - h - _theme.S(2);
            if (p.X + popup.Width > wa.Right) p.X = wa.Right - popup.Width;
            popup.StartPosition = FormStartPosition.Manual;
            popup.Location = p;
            popup.Show(this);
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}



