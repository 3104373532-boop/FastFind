using System;
using System.Drawing;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>Column titles for the result list; the ListView keeps its native header hidden.</summary>
    internal sealed class ColumnHeaderRow : Panel
    {
        private readonly Theme _theme;
        private readonly ResultList _list;
        private readonly string[] _titles = { "名称", "路径", "大小", "修改时间" };
        private int _hoverColumn = -1;

        public ColumnHeaderRow(Theme theme, ResultList list)
        {
            _theme = theme;
            _list = list;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = theme.Surface;
            Cursor = Cursors.Hand;
            Click += delegate { int c = HitColumn(PointToClient(Cursor.Position)); if (c >= 0) _list.ToggleSort(c); };
        }

        private int HitColumn(Point p)
        {
            int x = 0;
            for (int i = 0; i < _titles.Length; i++)
            {
                int w = _list.ColumnWidth(i);
                if (p.X >= x && p.X < x + w) return i;
                x += w;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int column = HitColumn(e.Location);
            if (column != _hoverColumn)
            {
                _hoverColumn = column;
                Cursor = column >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverColumn = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(_theme.Surface)) g.FillRectangle(b, ClientRectangle);
            using (var pen = new Pen(_theme.Line)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

            int pad = _theme.S(10);
            int x = 0;
            for (int i = 0; i < _titles.Length; i++)
            {
                int w = _list.ColumnWidth(i);
                if (w <= 0) continue;
                if (i == _hoverColumn)
                {
                    using (var b = new SolidBrush(_theme.SurfaceAlt)) g.FillRectangle(b, new Rectangle(x, 0, w, Height - 1));
                }
                string label = _titles[i];
                if (_list.SortColumn == i) label += _list.SortAscending ? "  ▲" : "  ▼";
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                Rectangle rect;
                if (i == 0) rect = new Rectangle(x + _theme.S(34), 0, w - _theme.S(34) - pad, Height - 1);
                else if (i == 2) rect = new Rectangle(x, 0, w - pad, Height - 1);
                else rect = new Rectangle(x + pad, 0, w - pad * 2, Height - 1);
                if (i == 2) flags |= TextFormatFlags.Right;
                TextRenderer.DrawText(g, label, _theme.UiSmall, rect, _theme.TextFaint, flags);
                x += w;
            }
        }
    }
}