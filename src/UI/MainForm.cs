using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FastFind
{
    internal sealed class MainForm : Form
    {
        private readonly Settings _settings;
        private readonly string _initialQuery;
        private readonly bool _openSettings;
        private readonly bool _toggleTheme;
        private readonly string _toggleVolume;
        private Theme _theme;
        private HeaderPanel _header;
        private ResultList _list;
        private int _detailsTop;
        private ColumnHeaderRow _headerRow;
        private TableLayoutPanel _root;
        private TableLayoutPanel _listHost;
        private StatusPanel _status;
        private EmptyPanel _empty;
        private IconCache _icons;
        private IndexManager _index;

        private readonly System.Windows.Forms.Timer _debounce;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private int _searchGeneration;
        private volatile string _searchText = string.Empty;
        private SearchQuery _query;
        private SearchResult _lastResult;
        private long _searchStarted;
        private string _scanStatus;
        private bool _closing;
        private string _notice = string.Empty;
        private string _transientStatus = string.Empty;
        private int _transientStatusUntil;
        private bool _restricted;
        private long _noticeUntil;

        private const int HotkeyId = 0xB001;
        private const int WM_HOTKEY = 0x0312;
        private bool _hotkeyRegistered;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int WM_APP_SHOW = 0x8001;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_SPACE = 0x20;

        public MainForm(Settings settings, string initialQuery, bool openSettings, bool toggleTheme, string toggleVolume)
        {
            _settings = settings;
            _initialQuery = initialQuery;
            _openSettings = openSettings;
            _toggleTheme = toggleTheme;
            _toggleVolume = toggleVolume;
            int dpi = 96;
            try { dpi = (int)Math.Round(96f * CreateGraphics().DpiX / 96f); } catch (Exception) { }
            _theme = new Theme(_settings.DarkTheme, dpi);

            Text = "FastFind 极速查找";
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _theme.Bg;
            ForeColor = _theme.TextMain;
            Font = _theme.Ui;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(_theme.S(760), _theme.S(460));
            Rectangle bounds = Screen.PrimaryScreen.WorkingArea;
            int desiredW = Math.Min(_theme.S(_settings.WindowWidth), bounds.Width);
            int desiredH = Math.Min(_theme.S(_settings.WindowHeight), bounds.Height);
            Size = new Size(desiredW, desiredH);
            if (_settings.WindowX >= 0 && _settings.WindowY >= 0 &&
                _settings.WindowX + 200 < bounds.Right && _settings.WindowY + 200 < bounds.Bottom)
            {
                Location = new Point(_settings.WindowX, _settings.WindowY);
            }
            else
            {
                Location = new Point(bounds.Left + (bounds.Width - Width) / 2, bounds.Top + (bounds.Height - Height) / 2);
            }

            _icons = new IconCache(_theme.S(16));

            _root = new TableLayoutPanel();
            var root = _root;
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = _theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, _theme.S(110)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, _theme.S(28)));
            Controls.Add(root);

            _header = new HeaderPanel(_theme);
            _header.Dock = DockStyle.Fill;
            _header.SetVolumes(_settings.Volumes, null);
            _header.TextChanged2 += delegate { OnSearchTextChanged(); };
            _header.VolumeToggled += OnVolumeToggled;
            _header.TypeFilterToggled += delegate { ScheduleSearch(true); };
            _header.FileTypesChanged += delegate { _settings.FileTypeMask = _header.TypeMask; ScheduleSearch(true); };
            _header.ThemeToggled += delegate { ToggleTheme(); };
            _header.SettingsRequested += delegate { ShowSettings(); };
            root.Controls.Add(_header, 0, 0);

            _list = new ResultList(_theme, _icons);
            // Single activation entry: ListView raises ItemActivate exactly once per double-click.
            _list.ItemActivate += delegate { OpenSelected(false); };
            _list.KeyDown += OnListKeyDown;
            _list.MouseUp += OnListMouseUp;
            _list.SortChanged += delegate { UpdateStatus(); };

            _listHost = new TableLayoutPanel();
            _listHost.Dock = DockStyle.Fill;
            _listHost.BackColor = _theme.Surface;
            _listHost.ColumnCount = 1;
            _listHost.RowCount = 2;
            _listHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _listHost.RowStyles.Add(new RowStyle(SizeType.Absolute, _theme.S(30)));
            _listHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _listHost.Padding = new Padding(_theme.S(20), _theme.S(3), _theme.S(20), 0);
            _detailsTop = _theme.S(22);

            _headerRow = new ColumnHeaderRow(_theme, _list);
            _headerRow.Dock = DockStyle.Fill;
            _listHost.Controls.Add(_headerRow, 0, 0);

            var listPane = new Panel();
            listPane.Dock = DockStyle.Fill;
            listPane.BackColor = _theme.Surface;
            _listHost.Controls.Add(listPane, 0, 1);
            _list.Dock = DockStyle.Fill;
            listPane.Controls.Add(_list);
            root.Controls.Add(_listHost, 0, 1);

            _empty = new EmptyPanel(_theme);
            _empty.Visible = true;
            _empty.Dock = DockStyle.Fill;
            listPane.Controls.Add(_empty);
            _empty.BringToFront();
            _empty.Click += delegate { FocusSearch(); };

            _status = new StatusPanel(_theme);
            _status.Dock = DockStyle.Fill;
            root.Controls.Add(_status, 0, 2);

            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = 60;
            _debounce.Tick += delegate
            {
                _debounce.Stop();
                RunSearch();
            };

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 700;
            _statusTimer.Tick += delegate { OnStatusTimer(); };
            _statusTimer.Start();

            _index = new IndexManager(_settings);
            _index.VolumesChanged += delegate { BeginInvokeSafe(OnIndexChanged); };
            _index.ScanProgress += delegate(int slot, string volumeRoot, long count)
            {
                string text = "正在索引 " + volumeRoot + " · " + count.ToString("N0") + " 项";
                _scanStatus = text;
                Interlocked.Exchange(ref _lastProgressTick, Environment.TickCount);
            };
            _index.Notice += delegate(string message)
            {
                _notice = message;
                _noticeUntil = Environment.TickCount + 6000;
            };

            Load += delegate
            {
                _index.Start();
                ApplySavedScope();
                _header.SetVolumes(_index.Roots, _index.Enabled);
                _header.SetTypeFilter(NameFilterKind.Any);
                _header.SetTypeMask(_settings.FileTypeMask);
                _header.SetStatus("索引载入中…");
                ApplyTitleBarTheme();
                if (_list != null) _list.ApplyTheme();
                _restricted = ProcessIntegrity.IsRestricted();
                if (_restricted)
                    SetTransientStatus("受限权限运行：系统会拒绝打开文件，请从资源管理器或开始菜单启动 FastFind", 12000);
                FocusSearch();
                RegisterHotkey();
                if (!string.IsNullOrEmpty(_initialQuery))
                {
                    _header.Box.Text = _initialQuery;
                    _header.Box.SelectionStart = _header.Box.Text.Length;
                    RunSearch();
                }
                if (_openSettings) BeginInvoke(new MethodInvoker(ShowSettings));
                if (_toggleTheme) BeginInvoke(new MethodInvoker(ToggleTheme));
                if (!string.IsNullOrEmpty(_toggleVolume))
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        string r = Settings.NormalizeRoot(_toggleVolume);
                        if (r != null) _header.ToggleVolume(r);
                    }));
            };
            Shown += delegate
            {
                ApplyTitleBarTheme();
                var t = new System.Windows.Forms.Timer { Interval = 400 };
                t.Tick += delegate { t.Stop(); t.Dispose(); ApplyTitleBarTheme(); };
                t.Start();
            };
            FormClosing += OnFormClosing;
            Resize += delegate { };
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_closing) return;
            _closing = true;
            try
            {
                Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                _settings.WindowX = b.X;
                _settings.WindowY = b.Y;
                _settings.WindowWidth = Math.Max(760, (int)Math.Round(b.Width / _theme.Scale));
                _settings.WindowHeight = Math.Max(460, (int)Math.Round(b.Height / _theme.Scale));
                SyncScopeSettings();
                _settings.Save();
            }
            catch (Exception) { }
            UnregisterHotkey();
            if (_index != null) _index.Dispose();
        }

        internal static bool DiagEnabled;

        private static void Diag(string text)
        {
            if (!DiagEnabled && Environment.GetEnvironmentVariable("FASTFIND_DIAG") != "1") return;
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "diag.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
            catch (Exception) { }
        }

        private void BeginInvokeSafe(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (Exception) { }
        }

        private void RegisterHotkey()
        {
            if (!_settings.EnableHotkey) return;
            try
            {
                _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE);
            }
            catch (Exception) { _hotkeyRegistered = false; }
        }

        private void UnregisterHotkey()
        {
            if (!_hotkeyRegistered) return;
            try { UnregisterHotKey(Handle, HotkeyId); }
            catch (Exception) { }
            _hotkeyRegistered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                if (Visible && WindowState != FormWindowState.Minimized) Hide();
                else ShowWindow();
                return;
            }
            if (m.Msg == WM_APP_SHOW)
            {
                ShowWindow();
                return;
            }
            base.WndProc(ref m);
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            SetForegroundWindow(Handle);
            FocusSearch();
        }

        public void FocusSearch()
        {
            _header.Box.Focus();
            _header.Box.SelectAll();
        }

        private void OnSearchTextChanged()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void ScheduleSearch(bool immediate)
        {
            _debounce.Stop();
            if (immediate) RunSearch();
            else _debounce.Start();
        }

        private void RunSearch()
        {
            int generation = Interlocked.Increment(ref _searchGeneration);
            string text = _header.Box.Text;
            _searchText = text;
            _searchStarted = Environment.TickCount;

            var query = SearchQuery.Parse(text);
            if (_header.TypeFilter != NameFilterKind.Any) query.TypeFilter = _header.TypeFilter;
            if (_header.TypeMask != 0) query.TypeMask = _header.TypeMask;
            _query = query;

            if (query.IsEmpty)
            {
                _lastResult = null;
                _list.SetHighlight(null);
                _list.SetResults(new Hit[0], new VolumeIndex[0]);
                ShowEmpty(text.Trim().Length == 0 ? EmptyPanel.State.Idle : (_index.TotalNodes == 0 ? EmptyPanel.State.Indexing : EmptyPanel.State.NoMatch), query.Warning);
                UpdateStatus();
                return;
            }

            VolumeIndex[] vols = _index.Volumes;
            bool[] enabled = _index.Enabled;
            if (!AnyEnabled(enabled))
            {
                _lastResult = null;
                _list.SetHighlight(null);
                _list.SetResults(new Hit[0], new VolumeIndex[0]);
                ShowEmpty(EmptyPanel.State.NoScope, null);
                UpdateStatus();
                WriteDiag(new SearchResult());
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {

                SearchResult result = SearchEngine.Search(query, vols, enabled, Math.Min(_settings.ResultLimit, 50000),
                    delegate { return generation != Volatile.Read(ref _searchGeneration) || _closing; });

                BeginInvokeSafe(delegate { ApplyResult(generation, result); });
            });
        }

        private void ApplyResult(int generation, SearchResult result)
        {
            if (generation != Volatile.Read(ref _searchGeneration) || _closing) return;
            _lastResult = result;
            _list.SetHighlight(result.Query != null && result.Query.Terms.Count > 0 ? result.Query.Terms[0] : null);
            _list.ClearSort();
            _list.SetResults(result.Hits, result.Volumes);
            bool empty = result.Hits.Length == 0;
            if (empty) ShowEmpty(_index.TotalNodes == 0 ? EmptyPanel.State.Indexing : EmptyPanel.State.NoMatch, result.Warning);
            else HideEmpty();
            UpdateStatus();
            WriteDiag(result);
        }

        /// <summary>Show the centered empty/no-result overlay above the list.</summary>
        private void ShowEmpty(EmptyPanel.State state, string warning)
        {
            _empty.SetState(state, warning);
            _empty.Visible = true;
            _empty.BringToFront();
        }

        /// <summary>Hide the overlay and drop it behind the list so it can never swallow mouse input.</summary>
        private void HideEmpty()
        {
            if (_empty.Visible) _empty.Visible = false;
            _empty.SendToBack();
        }

        /// <summary>Opt-in diagnostics used by the acceptance script (FASTFIND_DIAG=1 or --diag).</summary>
        private void WriteDiag(SearchResult result)
        {
            if (!DiagEnabled && Environment.GetEnvironmentVariable("FASTFIND_DIAG") != "1") return;
            try
            {
                string[] roots = _index.Roots;
                bool[] enabled = _index.Enabled;
                var scope = new StringBuilder();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (i >= enabled.Length || !enabled[i]) continue;
                    if (scope.Length > 0) scope.Append(',');
                    scope.Append(roots[i].Substring(0, 1));
                }
                System.IO.Directory.CreateDirectory(_settings.DataDir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(_settings.DataDir, "diag.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " search='" + _header.Box.Text + "' total=" + result.Total +
                    " shown=" + result.Hits.Length + " ms=" + result.ElapsedMs +
                    " scope=" + (scope.Length == 0 ? "-" : scope.ToString()) + Environment.NewLine);
            }
            catch (Exception) { }
        }

        private void OnIndexChanged()
        {
            _header.SetVolumes(_index.Roots, _index.Enabled);
            if (_header.Box.Text.Trim().Length > 0) ScheduleSearch(true);
            else UpdateStatus();
        }        private void OnVolumeToggled(object sender, string root)
        {
            // Read the chip state AFTER the user's click; the index manager must follow it.
            bool active = _header.GetVolumeActive(root);
            _index.SetVolumeEnabled(root, active);
            SyncScopeSettings();
            ScheduleSearch(true);
        }

        /// <summary>Mirror the live chip selection into settings so the scope survives restarts.</summary>
        private void SyncScopeSettings()
        {
            string[] roots = _index.Roots;
            bool[] enabled = _index.Enabled;
            var list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < roots.Length; i++)
                if (i < enabled.Length && enabled[i]) list.Add(roots[i]);
            _settings.ScopeVolumes = list;
            _settings.ScopeRaw = string.Join(",", list.ToArray());
        }

        /// <summary>Restore the scope saved in settings.ini (all volumes when never configured).</summary>
        private void ApplySavedScope()
        {
            string[] roots = _index.Roots;
            bool[] enabled = _index.Enabled;
            if (_settings.ScopeRaw == null)
            {
                SyncScopeSettings();
                return;
            }
            for (int i = 0; i < roots.Length && i < enabled.Length; i++)
            {
                bool want = _settings.ScopeVolumes.Contains(roots[i]);
                if (want != enabled[i]) _index.SetVolumeEnabled(roots[i], want);
            }
            // Drop stale roots (drive removed / reconfigured) from the saved list.
            SyncScopeSettings();
        }

        private static bool AnyEnabled(bool[] enabled)
        {
            if (enabled == null) return true;
            for (int i = 0; i < enabled.Length; i++) if (enabled[i]) return true;
            return false;
        }

        private void OnStatusTimer()
        {
            HeaderPanel header = _header;
            if (header == null) return;
            int total = _index != null ? _index.TotalNodes : 0;
            string status;
            if (!string.IsNullOrEmpty(_scanStatus) && (Environment.TickCount - Interlocked.Read(ref _lastProgressTick)) < 1500)
                status = _scanStatus;
            else if (total > 0)
            {
                status = "已索引 " + total.ToString("N0") + " 项";
                string[] roots = _index.Roots;
                bool[] enabled = _index.Enabled;
                int on = 0;
                for (int i = 0; i < enabled.Length; i++) if (enabled[i]) on++;
                if (roots.Length > 0 && on < roots.Length)
                    status += " · 范围 " + on + "/" + roots.Length;
            }
            else
                status = "索引载入中…";
            header.SetStatus(status);
            if (_notice.Length > 0)
            {
                if (Environment.TickCount > _noticeUntil) _notice = string.Empty;
                else _empty.SetNotice(_notice);
            }
            UpdateStatus();
        }

        private long _lastProgressTick;

        private void UpdateStatus()
        {
            if (_status == null) return;
            string left;
            if (_transientStatus.Length > 0 && Environment.TickCount < _transientStatusUntil)
            {
                left = _transientStatus;
            }
            else
            {
                _transientStatus = string.Empty;
                if (_lastResult == null)
                {
                    if (_header.Box.Text.Trim().Length == 0) left = "输入关键词开始搜索";
                    else if (!AnyEnabled(_index.Enabled)) left = "未选择搜索范围";
                    else left = "搜索中…";
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.Append("命中 ").Append(_lastResult.Total.ToString("N0")).Append(" 项");
                    sb.Append(" · ").Append(_lastResult.ElapsedMs).Append(" ms");
                    if (_lastResult.Truncated) sb.Append(" · 仅显示前 ").Append(_lastResult.Hits.Length.ToString("N0"));
                    if (_list.SortColumn >= 0) sb.Append(" · 已按列排序");
                    if (!string.IsNullOrEmpty(_lastResult.Warning)) sb.Append(" · ").Append(_lastResult.Warning);
                    left = sb.ToString();
                }
            }
            string right = "回车打开   Ctrl+回车 所在文件夹   Ctrl+C 复制路径   Del 回收站   F5 重建索引";
            _status.Set(left, right);
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OpenSelected(e.Control);
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                if (e.Shift) CopyNames();
                else CopyPaths();
            }
            else if (e.KeyCode == Keys.F5)
            {
                e.SuppressKeyPress = true;
                RebuildIndex();
            }
            else if (e.KeyCode == Keys.Delete)
            {
                e.SuppressKeyPress = true;
                DeleteSelected();
            }
            else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10))
            {
                e.SuppressKeyPress = true;
                ShowContextMenu();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                _header.Box.Focus();
                _header.Box.SelectAll();
            }
        }

        private void OnListMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) ShowContextMenu();
        }

        private void ShowContextMenu()
        {
            if (_list.SelectedIndices.Count == 0) return;
            var menu = new ContextMenuStrip();
            menu.Font = _theme.Ui;
            menu.ShowImageMargin = false;
            menu.Items.Add("打开", null, delegate { OpenSelected(false); });
            menu.Items.Add("打开所在文件夹", null, delegate { OpenSelected(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("复制完整路径  Ctrl+C", null, delegate { CopyPaths(); });
            menu.Items.Add("复制文件名  Ctrl+Shift+C", null, delegate { CopyNames(); });
            menu.Items.Add("属性  Alt+Enter", null, delegate { ShowProperties(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("删除到回收站  Del", null, delegate { DeleteSelected(); });
            menu.Show(_list, _list.PointToClient(Cursor.Position));
        }

        private string[] SelectedPaths()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (int index in _list.SelectedIndices)
            {
                string path = _list.PathOf(index);
                if (!string.IsNullOrEmpty(path)) list.Add(path);
            }
            if (list.Count == 0 && _list.SelectedIndices.Count == 0)
            {
                int focused = _list.FocusedItem != null ? _list.FocusedItem.Index : -1;
                string path = _list.PathOf(focused);
                if (!string.IsNullOrEmpty(path)) list.Add(path);
            }
            return list.ToArray();
        }

        private void OpenSelected(bool reveal)
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            if (paths.Length > 10)
            {
                if (MessageBox.Show(this, "将打开 " + paths.Length + " 个项目，继续？", "FastFind",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            }

            var failures = new System.Collections.Generic.List<string>();
            string firstFailurePath = null;
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    failures.Add("已不存在：" + Path.GetFileName(path) + "（索引可能过期，按 F5 重建）");
                    continue;
                }
                string error = reveal ? ShellOpen.TryReveal(path) : ShellOpen.TryOpen(path);
                if (error == null) continue;
                Diag("open-error " + path + " :: " + error.Replace('\n', ' '));
                failures.Add(error);
                if (firstFailurePath == null) firstFailurePath = path;
            }

            if (failures.Count == 0)
            {
                SetTransientStatus(reveal ? "已定位到资源管理器" : "已打开 " + paths.Length + " 个项目");
                return;
            }

            // Fall back to revealing the item so the user always gets a usable result.
            string revealError = null;
            if (!reveal && firstFailurePath != null) revealError = ShellOpen.TryReveal(firstFailurePath);

            string reason = failures[0];
            string message = "无法打开：" + (firstFailurePath == null ? "项目已不存在" : Path.GetFileName(firstFailurePath)) + "\n"
                + "原因：" + reason + "\n";
            if (failures.Count > 1) message = "共 " + failures.Count + " 个项目无法打开（以下为第一个）。\n\n" + message;
            if (!reveal && revealError == null)
                message += "已为你打开所在文件夹，可在文件上右键选择“打开方式”。";
            else
                message += "完整路径已复制到剪贴板，可粘贴到资源管理器打开。";
            if (_restricted)
                message += "\n\n⚠ 当前 FastFind 运行在受限权限下（Low Integrity），Windows 不允许它启动其他程序。请从资源管理器或开始菜单启动 FastFind 后重试。";

            SetTransientStatus("无法直接打开：" + (firstFailurePath == null ? "项目不存在" : Path.GetFileName(firstFailurePath)) + (revealError == null ? "（已在资源管理器中定位）" : string.Empty));
            try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch (Exception) { }
            NoticeForm.Show(this, _theme, message, true);
        }

        private void SetTransientStatus(string text)
        {
            SetTransientStatus(text, 6000);
        }

        private void SetTransientStatus(string text, int milliseconds)
        {
            _transientStatus = text ?? string.Empty;
            _transientStatusUntil = Environment.TickCount + milliseconds;
            UpdateStatus();
        }

        private void OpenWithSelected()
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            string error = ShellOpen.TryOpenWith(paths[0]);
            if (error == null) return;
            SetTransientStatus("打开方式对话框启动失败：" + error);
            try { Clipboard.SetText(paths[0]); } catch (Exception) { }
            MessageBox.Show(this, "无法打开“选择打开方式”对话框：" + error + "\n\n路径已复制到剪贴板。", "FastFind",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void CopyPaths()
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
                _notice = "已复制 " + paths.Length + " 条路径";
                _noticeUntil = Environment.TickCount + 2500;
            }
            catch (Exception) { }
        }

        private void CopyNames()
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            var names = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++) names[i] = Path.GetFileName(paths[i]);
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, names));
                _notice = "已复制 " + names.Length + " 个文件名";
                _noticeUntil = Environment.TickCount + 2500;
            }
            catch (Exception) { }
        }

        private void ShowProperties()
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            ShowFileProperties(this, paths[0]);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private static void RecyclePaths(string[] paths)
        {
            var op = new SHFILEOPSTRUCT();
            op.wFunc = 3; // FO_DELETE
            op.fFlags = 0x40 | 0x10 | 0x4 | 0x400; // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
            op.pFrom = string.Join("\0", paths) + "\0\0";
            int code = SHFileOperation(ref op);
            if (code != 0) throw new System.ComponentModel.Win32Exception(code);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName, string pszPropertyPage);

        private static void ShowFileProperties(IWin32Window owner, string path)
        {
            try { SHObjectProperties(IntPtr.Zero, 2, path, null); }
            catch (Exception) { }
        }

        private void DeleteSelected()
        {
            string[] paths = SelectedPaths();
            if (paths.Length == 0) return;
            string message = paths.Length == 1
                ? "确定把“" + Path.GetFileName(paths[0]) + "”移动到回收站？"
                : "确定把选中的 " + paths.Length + " 个项目移动到回收站？";
            if (MessageBox.Show(this, message, "FastFind", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            try
            {
                RecyclePaths(paths);
            }
            catch (Exception ex)
            {
                _notice = "删除失败：" + ex.Message;
                _noticeUntil = Environment.TickCount + 5000;
            }
            ScheduleSearch(true);
        }

        private void RebuildIndex()
        {
            _notice = "正在重建索引…";
            _noticeUntil = Environment.TickCount + 3000;
            _index.RequestRescan();
        }

        private void ToggleTheme()
        {
            _settings.DarkTheme = !_settings.DarkTheme;
            _theme.SetDark(_settings.DarkTheme);
            ApplyThemeLive();
            try { _settings.Save(); } catch (Exception) { }
            _notice = _settings.DarkTheme ? "已切换为深色主题" : "已切换为浅色主题";
            _noticeUntil = Environment.TickCount + 2500;
            if (_empty != null) _empty.SetNotice(_notice);
        }

        private void ApplyThemeLive()
        {
            BackColor = _theme.Bg;
            if (_root != null) _root.BackColor = _theme.Bg;
            if (_listHost != null) _listHost.BackColor = _theme.Surface;
            if (_header != null) _header.ApplyTheme();
            if (_headerRow != null) { _headerRow.BackColor = _theme.Surface; _headerRow.Invalidate(); }
            if (_list != null) _list.ApplyTheme();
            if (_status != null) { _status.ApplyTheme(); }
            if (_empty != null) _empty.ApplyTheme();
            ApplyTitleBarTheme();
            Invalidate(true);
        }

        private void ApplyTitleBarTheme()
        {
            try
            {
                int value = _theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref value, sizeof(int));
            }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ShowSettings()
        {
            string volumeKeyBefore = string.Join("|", _settings.Volumes.ToArray());
            bool hotkeyBefore = _settings.EnableHotkey;
            using (var dialog = new SettingsForm(_settings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _settings.Save();
                string volumeKeyAfter = string.Join("|", _settings.Volumes.ToArray());
                if (volumeKeyAfter != volumeKeyBefore)
                {
                    _index.Reload();
                    ApplySavedScope();
                }
                if (_settings.EnableHotkey != hotkeyBefore)
                {
                    if (_settings.EnableHotkey) RegisterHotkey();
                    else UnregisterHotkey();
                }
                if (dialog.RebuildRequested) _index.RequestRescan();
                if (_settings.DarkTheme != _theme.Dark)
                {
                    _theme.SetDark(_settings.DarkTheme);
                    ApplyThemeLive();
                }
                _notice = "设置已保存";
                _noticeUntil = Environment.TickCount + 3000;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5)
            {
                RebuildIndex();
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                FocusSearch();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                if (_header.Box.Text.Length > 0)
                {
                    _header.Box.Text = string.Empty;
                }
                else
                {
                    Hide();
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.T))
            {
                ToggleTheme();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Alt | Keys.Space))
            {
                if (Visible) Hide();
                else ShowWindow();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_theme != null) _theme.Dispose();
                if (_icons != null) _icons.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}







































