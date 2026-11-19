using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FastFind
{
    /// <summary>
    /// Owns the published volume indexes: loads caches for instant startup, rescans in the
    /// background when caches are stale, and schedules refreshed scans after file system changes.
    /// All events may be raised on a background thread.
    /// </summary>
    internal sealed class IndexManager : IDisposable
    {
        private readonly Settings _settings;
        private readonly object _sync = new object();
        private VolumeIndex[] _published = new VolumeIndex[0];
        private bool[] _enabled = new bool[0];
        private string[] _roots = new string[0];

        private FileSystemWatcher[] _watchers;
        private Timer _debounce;
        private volatile bool _dirty;
        private volatile bool _closed;
        private int _rescanRequested;      // 1 = full rebuild wanted
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;
        private long _lastRescanTicks;

        public const int StaleMinutes = 10;
        public const int DebounceMs = 4000;
        public const int MinRescanIntervalMs = 120000;

        public event Action<int, string, long> ScanProgress;   // slot, root, node count
        public event Action VolumesChanged;                    // published arrays swapped
        public event Action<string> Notice;

        public IndexManager(Settings settings)
        {
            _settings = settings;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "FastFind.IndexManager";
            _worker.Start();
        }

        public VolumeIndex[] Volumes
        {
            get { lock (_sync) { return _published; } }
        }

        public bool[] Enabled
        {
            get { lock (_sync) { return (bool[])_enabled.Clone(); } }
        }

        public string[] Roots
        {
            get { lock (_sync) { return (string[])_roots.Clone(); } }
        }

        public int TotalNodes
        {
            get
            {
                VolumeIndex[] vols = Volumes;
                int total = 0;
                for (int i = 0; i < vols.Length; i++) if (vols[i] != null) total += vols[i].Count;
                return total;
            }
        }

        public void Start()
        {
            var roots = new List<string>();
            foreach (string r in _settings.Volumes)
            {
                string n = Settings.NormalizeRoot(r);
                if (n != null && !roots.Contains(n)) roots.Add(n);
            }
            if (roots.Count == 0) roots.AddRange(Settings.DetectFixedNtfsVolumes());

            var vols = new VolumeIndex[roots.Count];
            var enabled = new bool[roots.Count];
            bool anyStale = false;
            for (int i = 0; i < roots.Count; i++)
            {
                enabled[i] = true;
                uint serial;
                bool hasSerial = Native.TryGetVolumeSerial(roots[i], out serial);
                string cachePath = _settings.CachePath(roots[i]);
                VolumeIndex cached;
                string error;
                if (CacheStore.TryLoad(cachePath, hasSerial ? serial : 0, out cached, out error))
                {
                    vols[i] = cached;
                    if (IsStale(cached)) anyStale = true;
                    RaiseNotice("已从缓存载入 " + roots[i] + "（" + cached.Count.ToString("N0") + " 项）");
                }
                else
                {
                    anyStale = true;
                }
            }

            lock (_sync)
            {
                _roots = roots.ToArray();
                _published = vols;
                _enabled = enabled;
            }
            if (VolumesChanged != null) VolumesChanged();

            SetupWatchers();

            if (anyStale)
            {
                _rescanRequested = 1;
                _wake.Set();
            }
        }

        private bool IsStale(VolumeIndex idx)
        {
            if (idx == null) return true;
            if (!idx.Complete) return true;
            double ageMinutes = (DateTime.UtcNow - new DateTime(idx.ScannedUtcTicks, DateTimeKind.Utc)).TotalMinutes;
            return ageMinutes > StaleMinutes;
        }

        /// <summary>Reload volume list after settings change (adds/removes volumes).</summary>
        public void Reload()
        {
            DisposeWatchersOnly();
            Start();
        }

        public void RequestRescan()
        {
            _rescanRequested = 1;
            _wake.Set();
        }

        public void SetVolumeEnabled(string root, bool enabled)
        {
            lock (_sync)
            {
                for (int i = 0; i < _roots.Length; i++)
                {
                    if (string.Equals(_roots[i], root, StringComparison.OrdinalIgnoreCase))
                    {
                        bool[] copy = (bool[])_enabled.Clone();
                        copy[i] = enabled;
                        _enabled = copy;
                        break;
                    }
                }
            }
            if (VolumesChanged != null) VolumesChanged();
        }

        private void WorkerLoop()
        {
            while (!_closed)
            {
                _wake.WaitOne(30000);
                if (_closed) return;

                bool forced = Interlocked.Exchange(ref _rescanRequested, 0) == 1;
                if (!forced)
                {
                    if (!_settings.AutoRefresh) continue;
                    if (!_dirty) continue;
                    long since = Environment.TickCount - Interlocked.Read(ref _lastRescanTicks);
                    if (since < 0 || since > 600000) since = 0;
                    if (since < MinRescanIntervalMs) continue;
                }
                _dirty = false;
                try
                {
                    RescanAll();
                    Interlocked.Exchange(ref _lastRescanTicks, Environment.TickCount);
                }
                catch (Exception ex)
                {
                    if (Notice != null) Notice("重建索引失败：" + ex.Message);
                }
            }
        }

        private void RescanAll()
        {
            string[] roots;
            VolumeIndex[] vols;
            lock (_sync)
            {
                roots = (string[])_roots.Clone();
                vols = (VolumeIndex[])_published.Clone();
            }
            int threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
            for (int i = 0; i < roots.Length; i++)
            {
                if (_closed) return;
                string root = roots[i];
                long start = Environment.TickCount;
                int slot = i;
                VolumeIndex idx = Scanner.Scan(root, threads, delegate(ScanProgress p)
                {
                    if (ScanProgress != null) ScanProgress(slot, p.Volume, p.Count);
                }, delegate { return _closed; });
                if (idx == null) continue;
                try { CacheStore.Save(_settings.CachePath(root), idx); }
                catch (Exception) { }
                lock (_sync)
                {
                    vols[slot] = idx;
                    _published = (VolumeIndex[])vols.Clone();
                }
                CompactHeap();
                if (VolumesChanged != null) VolumesChanged();
                if (Notice != null)
                    Notice("索引完成 " + root + "：" + idx.Count.ToString("N0") + " 项 / " + (Environment.TickCount - start) + "ms");
            }
        }

        /// <summary>Give back scan garbage and compact the large object heap so the working set stays small.</summary>
        private static void CompactHeap()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }
            catch (Exception) { }
        }

        private void SetupWatchers()
        {
            DisposeWatchersOnly();
            try
            {
                string[] roots;
                lock (_sync) { roots = (string[])_roots.Clone(); }
                var list = new List<FileSystemWatcher>();
                foreach (string root in roots)
                {
                    try
                    {
                        var w = new FileSystemWatcher(root);
                        w.IncludeSubdirectories = true;
                        w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                        w.InternalBufferSize = 64 * 1024;
                        w.Created += OnFsEvent;
                        w.Deleted += OnFsEvent;
                        w.Renamed += delegate(object s, RenamedEventArgs e) { if (!IsInternalPath(e.FullPath)) MarkDirty(); };
                        w.Error += delegate { MarkDirty(); };
                        w.EnableRaisingEvents = true;
                        list.Add(w);
                    }
                    catch (Exception) { }
                }
                _watchers = list.ToArray();
                _debounce = new Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);
            }
            catch (Exception) { }
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            if (IsInternalPath(e.FullPath)) return;
            MarkDirty();
        }

        /// <summary>Ignore our own data directory (cache / settings writes) to avoid rescan loops.</summary>
        private bool IsInternalPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            try
            {
                string data = System.IO.Path.GetFullPath(_settings.DataDir).TrimEnd('\\') + "\\";
                return fullPath.StartsWith(data, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void MarkDirty()
        {
            _dirty = true;
            Timer t = _debounce;
            if (t != null)
            {
                try { t.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }

        private void OnDebounce(object state)
        {
            if (_closed) return;
            if (!_settings.AutoRefresh) return;
            long since = Environment.TickCount - Interlocked.Read(ref _lastRescanTicks);
            if (since < 0 || since > 600000) since = long.MaxValue;
            if (since < MinRescanIntervalMs) return;
            _rescanRequested = 1;
            _wake.Set();
        }

        private void DisposeWatchersOnly()
        {
            FileSystemWatcher[] watchers = _watchers;
            _watchers = null;
            if (watchers != null)
            {
                foreach (FileSystemWatcher w in watchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); }
                    catch (Exception) { }
                }
            }
            Timer debounce = _debounce;
            _debounce = null;
            if (debounce != null)
            {
                try { debounce.Dispose(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            _closed = true;
            _wake.Set();
            if (_watchers != null)
            {
                foreach (FileSystemWatcher w in _watchers)
                {
                    try { w.EnableRaisingEvents = false; w.Dispose(); }
                    catch (Exception) { }
                }
                _watchers = null;
            }
            if (_debounce != null)
            {
                try { _debounce.Dispose(); } catch (Exception) { }
                _debounce = null;
            }
        }

        private void RaiseNotice(string message)
        {
            if (Notice != null) Notice(message);
        }
    }
}


