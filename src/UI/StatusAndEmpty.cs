using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FastFind
{
    internal sealed class StatusPanel : Panel
    {
        private readonly Theme _theme;
        private string _left = string.Empty;
        private string _right = string.Empty;

        public StatusPanel(Theme theme)
        {
            _theme = theme;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Bg;
        }

        public void ApplyTheme()
        {
            BackColor = _theme.Bg;
            Invalidate();
        }

        public void Set(string left, string right)
        {
            if (_left == left && _right == right) return;
            _left = left ?? string.Empty;
            _right = right ?? string.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(_theme.Bg)) g.FillRectangle(b, ClientRectangle);
            using (var pen = new Pen(_theme.Line)) g.DrawLine(pen, 0, 0, Width, 0);
            int pad = _theme.S(22);
            TextRenderer.DrawText(g, _left, _theme.UiSmall, new Rectangle(pad, 0, Width / 2, Height), _theme.TextSub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _right, _theme.UiSmall, new Rectangle(Width / 2, 0, Width / 2 - pad, Height), _theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class EmptyPanel : Panel
    {
        public enum State { Idle, NoMatch, Indexing, NoScope }

        private readonly Theme _theme;
        private State _state = State.Idle;
        private string _warning = string.Empty;
        private string _notice = string.Empty;
        private int _padLeft;
        private int _padTop;

        public EmptyPanel(Theme theme)
        {
            _theme = theme;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Surface;
        }

        public void ApplyTheme()
        {
            BackColor = _theme.Surface;
            Invalidate();
        }

        public void SetState(State state, string warning)
        {
            _state = state;
            _warning = warning ?? string.Empty;
            Invalidate();
        }

        public void SetNotice(string notice)
        {
            _notice = notice ?? string.Empty;
            Invalidate();
        }

        public void SetMetrics(int padLeft, int padTop)
        {
            _padLeft = padLeft;
            _padTop = padTop;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(_theme.Surface)) g.FillRectangle(b, ClientRectangle);

            int cx = _padLeft + Math.Max(_theme.S(120), (Width - _padLeft * 2) / 2);
            int cy = Math.Max(_theme.S(90), Height / 3);
            int icon = _theme.S(46);

            using (var pen = new Pen(_theme.Line, _theme.SF(2f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawEllipse(pen, cx - icon / 2, cy - icon / 2, icon, icon);
                g.DrawLine(pen, cx + icon / 4, cy + icon / 4, cx + icon / 2, cy + icon / 2);
            }

            string title;
            string subtitle;
            if (_state == State.Idle)
            {
                title = "输入关键词，立即找到文件";
                subtitle = "支持空格多关键词、* ? 通配、ext: size: dm: path: 过滤，- 排除";
            }
            else if (_state == State.Indexing)
            {
                title = "正在建立索引…";
                subtitle = "首次启动需要扫描磁盘，完成后即可秒搜；当前已扫描部分已可搜索";
            }
            else if (_state == State.NoScope)
            {
                title = "未选择搜索范围";
                subtitle = "点击上方的 C: / D: / E: 芯片，重新勾选要搜索的磁盘";
            }
            else
            {
                title = "没有匹配的结果";
                subtitle = "试试减少关键词、检查 -排除条件，或切换范围/类型筛选";
            }

            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            {
                using (var b = new SolidBrush(_theme.TextMain))
                    g.DrawString(title, _theme.RowFontBold, b, new RectangleF(0, cy + icon / 2 + _theme.S(18), Width, _theme.S(26)), sf);
                using (var b = new SolidBrush(_theme.TextSub))
                    g.DrawString(subtitle, _theme.Ui, b, new RectangleF(0, cy + icon / 2 + _theme.S(46), Width, _theme.S(22)), sf);

                if (_state == State.Idle)
                {
                    string[] samples = { "ext:jpg size:>5mb", "dm:today", "path:项目 *.xlsx", "*.log -tmp" };
                    int x = cx - _theme.S(250);
                    int y = cy + icon / 2 + _theme.S(84);
                    for (int i = 0; i < samples.Length; i++)
                    {
                        Size sz = TextRenderer.MeasureText(samples[i], _theme.UiSmall);
                        var rect = new Rectangle(x, y, sz.Width + _theme.S(20), _theme.S(26));
                        using (var path = Rounded(rect, _theme.S(13)))
                        {
                            using (var fill = new SolidBrush(_theme.SurfaceAlt)) g.FillPath(fill, path);
                            using (var p = new Pen(_theme.Line)) g.DrawPath(p, path);
                        }
                        TextRenderer.DrawText(g, samples[i], _theme.UiSmall, rect, _theme.TextSub,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        x += rect.Width + _theme.S(8);
                    }
                }

                if (!string.IsNullOrEmpty(_warning))
                {
                    using (var b = new SolidBrush(_theme.Danger))
                        g.DrawString(_warning, _theme.UiSmall, b, new RectangleF(0, cy + icon / 2 + _theme.S(124), Width, _theme.S(20)), sf);
                }
                if (!string.IsNullOrEmpty(_notice))
                {
                    using (var b = new SolidBrush(_theme.TextFaint))
                        g.DrawString(_notice, _theme.UiSmall, b, new RectangleF(0, Height - _theme.S(40), Width, _theme.S(20)), sf);
                }
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
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


