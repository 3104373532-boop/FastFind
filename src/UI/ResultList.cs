using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>Small shell icons cached per file extension.</summary>
    internal sealed class IconCache : IDisposable
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly ImageList _list;
        private readonly Dictionary<string, int> _map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public IconCache(int size)
        {
            _list = new ImageList();
            _list.ImageSize = new Size(size, size);
            _list.ColorDepth = ColorDepth.Depth32Bit;
        }

        public ImageList Images { get { return _list; } }

        public int GetIndex(string extension, bool isDir)
        {
            string key = isDir ? "<dir>" : (extension == null || extension.Length == 0 ? "<none>" : "." + extension.ToLowerInvariant());
            int index;
            if (_map.TryGetValue(key, out index)) return index;

            var info = new SHFILEINFO();
            string probe = isDir ? "folder" : "file" + (key == "<none>" ? "" : key);
            uint attrs = isDir ? 0x10u : 0x80u;
            IntPtr result = SHGetFileInfoW(probe, attrs, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            index = -1;
            if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    _list.Images.Add(Icon.FromHandle(info.hIcon));
                    index = _list.Images.Count - 1;
                }
                catch (Exception) { index = -1; }
                finally { DestroyIcon(info.hIcon); }
            }
            _map[key] = index;
            return index;
        }

        public void Dispose()
        {
            _list.Dispose();
        }
    }

    /// <summary>Virtual, owner drawn result table.</summary>
    internal sealed class ResultList : ListView
    {
        private readonly Theme _theme;
        private readonly IconCache _icons;
        private Hit[] _hits = new Hit[0];
        private VolumeIndex[] _volumes = new VolumeIndex[0];
        private SearchTerm _highlightTerm;
        private int _rowHeight;
        private int _hoverIndex = -1;
        private int _sortColumn = -1;
        private bool _sortAscending = true;

        public event EventHandler SortChanged;

        public ResultList(Theme theme, IconCache icons)
        {
            _theme = theme;
            _icons = icons;
            View = View.Details;
            FullRowSelect = true;
            GridLines = false;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.None;
            OwnerDraw = true;
            VirtualMode = true;
            MultiSelect = true;
            HideSelection = false;
            BackColor = theme.Surface;
            ForeColor = theme.TextMain;
            Font = theme.RowFont;
            AllowColumnReorder = false;

            Columns.Add("名称", 380);
            Columns.Add("路径", 420);
            Columns.Add("大小", 90, HorizontalAlignment.Right);
            Columns.Add("修改时间", 140);

            _rowHeight = Math.Max(theme.S(26), theme.Ui.Height + theme.S(12));
            var imgSize = new ImageList();
            imgSize.ImageSize = new Size(theme.S(16), _rowHeight);
            imgSize.ColorDepth = ColorDepth.Depth32Bit;
            SmallImageList = imgSize;
            imgSize.Images.Add(new Bitmap(theme.S(16), _rowHeight));

            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(this, true, null);

            RetrieveVirtualItem += delegate(object s, RetrieveVirtualItemEventArgs e)
            {
                e.Item = new ListViewItem(new string[] { string.Empty, string.Empty, string.Empty, string.Empty });
            };
            DrawColumnHeader += OnDrawHeader;
            DrawSubItem += OnDrawSubItem;
            DrawItem += delegate { };
            MouseMove += OnMouseMove2;
            MouseLeave += delegate { if (_hoverIndex >= 0) { _hoverIndex = -1; Invalidate(); } };
            ColumnClick += OnColumnClick;
            KeyDown += OnKeyDown;
        }

        /// <summary>Column titles are painted by a sibling control, so expose geometry.</summary>
        public int ColumnLeft(int index)
        {
            int x = 0;
            for (int i = 0; i < index && i < Columns.Count; i++) x += Columns[i].Width;
            return x;
        }

        public int ColumnWidth(int index)
        {
            return index >= 0 && index < Columns.Count ? Columns[index].Width : 0;
        }

        public int TotalColumnWidth
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Columns.Count; i++) total += Columns[i].Width;
                return total;
            }
        }

        public bool VerticalScrollVisible
        {
            get { return _hits.Length > 0 && TotalColumnWidth >= 0; }
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        public void ApplyTheme()
        {
            BackColor = _theme.Surface;
            ForeColor = _theme.TextMain;
            try { SetWindowTheme(Handle, _theme.Dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (Exception) { }
            Invalidate();
        }

        public Hit[] Hits { get { return _hits; } }

        public void SetResults(Hit[] hits, VolumeIndex[] volumes)
        {
            _hits = hits ?? new Hit[0];
            _volumes = volumes ?? new VolumeIndex[0];
            VirtualListSize = _hits.Length;
            if (_hits.Length > 0)
            {
                try { EnsureVisible(0); } catch (Exception) { }
                try { if (TopItem != null) TopItem = TopItem; } catch (Exception) { }
            }
            Invalidate();
        }

        public void SetHighlight(SearchTerm term)
        {
            _highlightTerm = term != null && !term.IsRegex && !term.Wildcard ? term : null;
        }

        public int SortColumn { get { return _sortColumn; } }

        public bool SortAscending { get { return _sortAscending; } }

        public void Sort(int column, bool ascending)
        {
            _sortColumn = column;
            _sortAscending = ascending;
            Array.Sort(_hits, new RowComparer(_volumes, column, ascending));
            Invalidate();
            if (SortChanged != null) SortChanged(this, EventArgs.Empty);
        }

        public void ClearSort()
        {
            _sortColumn = -1;
        }

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            bool asc = _sortColumn == e.Column ? !_sortAscending : true;
            Sort(e.Column, asc);
        }

        /// <summary>Called by the header row when a title is clicked.</summary>
        public void ToggleSort(int column)
        {
            bool asc = _sortColumn == column ? !_sortAscending : true;
            Sort(column, asc);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Apps)
            {
                e.Handled = true;
            }
        }

        public int HoverIndex { get { return _hoverIndex; } }

        private void OnMouseMove2(object sender, MouseEventArgs e)
        {
            var info = HitTest(e.Location);
            int index = info.Item != null ? info.Item.Index : -1;
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutColumns();
        }

        public void LayoutColumns()
        {
            if (Columns.Count != 4) return;
            int usable = ClientSize.Width - SystemInformation.VerticalScrollBarWidth - _theme.S(2);
            if (usable < _theme.S(320)) usable = _theme.S(320);
            int sizeW = _theme.S(86);
            int timeW = _theme.S(132);
            int nameW = (int)(usable * 0.38);
            int pathW = usable - nameW - sizeW - timeW;
            if (pathW < _theme.S(120)) pathW = _theme.S(120);
            Columns[0].Width = nameW;
            Columns[1].Width = pathW;
            Columns[2].Width = sizeW;
            Columns[3].Width = Math.Max(timeW, usable - nameW - pathW - sizeW);
        }

        private void OnDrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(_theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var pen = new Pen(_theme.Line)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            string label = e.Header.Text;
            if (_sortColumn == e.ColumnIndex) label += _sortAscending ? "  ▲" : "  ▼";
            if (e.ColumnIndex == 2) flags |= TextFormatFlags.Right;
            var rect = Rectangle.Inflate(e.Bounds, -_theme.S(8), 0);
            TextRenderer.DrawText(e.Graphics, label, _theme.UiSmall, rect, _theme.TextFaint, flags);
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle cell = e.Bounds;
            bool selected = e.Item.Selected;
            bool hover = e.ItemIndex == _hoverIndex;

            using (var b = new SolidBrush(selected ? _theme.SelectBg : (hover ? _theme.Hover : _theme.Surface)))
                g.FillRectangle(b, cell);

            if (e.ColumnIndex == 0)
            {
                if (selected)
                {
                    using (var b = new SolidBrush(_theme.Accent))
                        g.FillRectangle(b, new Rectangle(cell.Left, cell.Top, _theme.S(3), cell.Height));
                }
            }

            int index = e.ItemIndex;
            if (index < 0 || index >= _hits.Length) return;
            Hit hit = _hits[index];
            if (hit.Vol < 0 || hit.Vol >= _volumes.Length) return;
            VolumeIndex vol = _volumes[hit.Vol];
            if (vol == null || hit.Node < 0 || hit.Node >= vol.Count) return;

            int pad = _theme.S(8);
            if (e.ColumnIndex == 0)
            {
                int iconSize = _theme.S(16);
                int iconIndex = _icons.GetIndex(vol.GetExtension(hit.Node), vol.IsDir(hit.Node));
                int ix = cell.Left + _theme.S(10);
                int iy = cell.Top + (cell.Height - iconSize) / 2;
                if (iconIndex >= 0 && iconIndex < _icons.Images.Images.Count)
                {
                    try { _icons.Images.Draw(g, ix, iy, iconSize, iconSize, iconIndex); }
                    catch (Exception) { }
                }
                int textX = ix + iconSize + _theme.S(8);
                DrawName(g, vol, hit.Node, textX, cell.Top, cell.Height, Math.Max(_theme.S(20), cell.Right - textX - pad));
            }
            else if (e.ColumnIndex == 1)
            {
                string dir = SafeDirectory(vol, hit.Node);
                TextRenderer.DrawText(g, dir, _theme.UiSmall, new Rectangle(cell.Left + pad, cell.Top, cell.Width - pad * 2, cell.Height),
                    _theme.TextSub, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
            }
            else if (e.ColumnIndex == 2)
            {
                string size = vol.IsDir(hit.Node) ? string.Empty : FormatSize(vol.Size[hit.Node]);
                TextRenderer.DrawText(g, size, _theme.UiSmall, new Rectangle(cell.Left, cell.Top, cell.Width - pad, cell.Height),
                    _theme.TextSub, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
            else
            {
                string when = FormatTime(vol.MTime[hit.Node]);
                TextRenderer.DrawText(g, when, _theme.UiSmall, new Rectangle(cell.Left + pad, cell.Top, cell.Width - pad * 2, cell.Height),
                    _theme.TextSub, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        private void DrawName(Graphics g, VolumeIndex vol, int node, int x, int top, int height, int width)
        {
            string name = vol.GetName(node);
            int y = top + (height - _theme.RowFont.Height) / 2;
            Color color = _theme.TextMain;
            if (_highlightTerm == null)
            {
                TextRenderer.DrawText(g, name, _theme.RowFont, new Rectangle(x, top, width, height), color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                return;
            }
            char[] pool = vol.Pool;
            int pos = SearchEngine.IndexOfIgnoreCase(pool, vol.Off[node], vol.Len[node], _highlightTerm.Text);
            if (pos < 0)
            {
                TextRenderer.DrawText(g, name, _theme.RowFont, new Rectangle(x, top, width, height), color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                return;
            }
            int ml = _highlightTerm.Text.Length;
            string before = name.Substring(0, pos);
            string match = name.Substring(pos, ml);
            string after = name.Substring(pos + ml);
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            Size bs = TextRenderer.MeasureText(g, before, _theme.RowFont, new Size(int.MaxValue, int.MaxValue), flags);
            TextRenderer.DrawText(g, before, _theme.RowFont, new Point(x, y), color, flags);
            Size ms = TextRenderer.MeasureText(g, match, _theme.RowFontBold, new Size(int.MaxValue, int.MaxValue), flags);
            TextRenderer.DrawText(g, match, _theme.RowFontBold, new Point(x + bs.Width, y), _theme.Accent, flags);
            TextRenderer.DrawText(g, after, _theme.RowFont, new Point(x + bs.Width + ms.Width, y), color, flags);
        }

        private string SafeDirectory(VolumeIndex vol, int node)
        {
            try
            {
                int parent = vol.Parent[node];
                if (parent < 0) return vol.Root;
                return vol.GetDirectoryPath(node);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public string PathOf(int index)
        {
            if (index < 0 || index >= _hits.Length) return null;
            Hit hit = _hits[index];
            if (hit.Vol < 0 || hit.Vol >= _volumes.Length) return null;
            VolumeIndex vol = _volumes[hit.Vol];
            if (vol == null || hit.Node < 0 || hit.Node >= vol.Count) return null;
            return vol.GetPath(hit.Node);
        }

        public bool IsDirAt(int index)
        {
            if (index < 0 || index >= _hits.Length) return false;
            Hit hit = _hits[index];
            if (hit.Vol < 0 || hit.Vol >= _volumes.Length) return false;
            VolumeIndex vol = _volumes[hit.Vol];
            return vol != null && hit.Node >= 0 && hit.Node < vol.Count && vol.IsDir(hit.Node);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            double gb = mb / 1024.0;
            if (gb < 1024) return gb.ToString("0.00") + " GB";
            return (gb / 1024.0).ToString("0.00") + " TB";
        }

        public static string FormatTime(int indexTime)
        {
            if (indexTime <= -900000000) return string.Empty;
            try
            {
                DateTime utc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(indexTime);
                DateTime local = utc.ToLocalTime();
                if (local.Year < 1980) return string.Empty;
                DateTime now = DateTime.Now;
                if (local.Date == now.Date) return "今天 " + local.ToString("HH:mm");
                if (local.Date == now.Date.AddDays(-1)) return "昨天 " + local.ToString("HH:mm");
                if (local.Year == now.Year) return local.ToString("MM-dd HH:mm");
                return local.ToString("yyyy-MM-dd");
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private sealed class RowComparer : IComparer<Hit>
        {
            private readonly VolumeIndex[] _vols;
            private readonly int _column;
            private readonly bool _ascending;

            public RowComparer(VolumeIndex[] vols, int column, bool ascending)
            {
                _vols = vols;
                _column = column;
                _ascending = ascending;
            }

            public int Compare(Hit x, Hit y)
            {
                VolumeIndex vx = _vols[x.Vol];
                VolumeIndex vy = _vols[y.Vol];
                int cmp;
                if (_column == 2) cmp = vx.Size[x.Node].CompareTo(vy.Size[y.Node]);
                else if (_column == 3) cmp = vx.MTime[x.Node].CompareTo(vy.MTime[y.Node]);
                else
                {
                    string ax = _column == 0 ? vx.GetName(x.Node) : vx.GetPath(x.Node);
                    string ay = _column == 0 ? vy.GetName(y.Node) : vy.GetPath(y.Node);
                    cmp = string.Compare(ax, ay, StringComparison.CurrentCultureIgnoreCase);
                }
                return _ascending ? cmp : -cmp;
            }
        }
    }
}





