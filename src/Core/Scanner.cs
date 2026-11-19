using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FastFind
{
    internal struct ScanProgress
    {
        public string Volume;
        public long Count;
        public int Dirs;
        public int Skipped;
        public int Errors;
    }

    /// <summary>
    /// Parallel directory walker built on FindFirstFileExW with large fetch buffers.
    /// Node 0 is the volume root. Directory nodes are created immediately (children need a parent id),
    /// file nodes are buffered per worker and merged in batches of 4096 under a single lock.
    /// </summary>
    internal static class Scanner
    {
        private struct RawEntry
        {
            public int Parent;
            public int NameOff;
            public ushort NameLen;
            public byte Flags;
            public byte Category;
            public long Size;
            public int MTime;
        }

        private struct DirJob
        {
            public string Path;
            public int Node;
        }

        private sealed class WorkerState
        {
            public char[] NameBuf = new char[64 * 1024];
            public int NameLen;
            public RawEntry[] Entries = new RawEntry[4096];
            public int EntryCount;
            public int LocalDirs;
            public int LocalSkipped;

            public int AddName(string name)
            {
                int n = name.Length;
                if (NameLen + n > NameBuf.Length)
                {
                    int cap = NameBuf.Length;
                    while (cap < NameLen + n) cap *= 2;
                    Array.Resize(ref NameBuf, cap);
                }
                int off = NameLen;
                name.CopyTo(0, NameBuf, NameLen, n);
                NameLen += n;
                return off;
            }
        }

        private sealed class Shared
        {
            public VolumeIndex Index;
            public readonly object Sync = new object();
            public int Count;
            public int PoolLen;
            public char[] Pool;
            public int[] Off;
            public ushort[] Len;
            public int[] Parent;
            public byte[] Flags;
            public byte[] Category;
            public long[] Size;
            public int[] MTime;
            public int SkippedDirs;
            public int ErrorDirs;
            public int LastProgressTick;

            public Shared(VolumeIndex idx)
            {
                Index = idx;
                Pool = idx.Pool; Off = idx.Off; Len = idx.Len; Parent = idx.Parent;
                Flags = idx.Flags; Category = idx.Category; Size = idx.Size; MTime = idx.MTime;
            }

            public void EnsureNodes(int needed)
            {
                int cap = Parent.Length;
                if (needed <= cap) return;
                int ncap = cap < 1024 ? 1024 : cap;
                while (ncap < needed) ncap *= 2;

                var parent = new int[ncap]; Array.Copy(Parent, parent, Count); Parent = parent;
                var flags = new byte[ncap]; Array.Copy(Flags, flags, Count); Flags = flags;
                var category = new byte[ncap]; Array.Copy(Category, category, Count); Category = category;
                var size = new long[ncap]; Array.Copy(Size, size, Count); Size = size;
                var mtime = new int[ncap]; Array.Copy(MTime, mtime, Count); MTime = mtime;
                var off = new int[ncap]; Array.Copy(Off, off, Count); Off = off;
                var len = new ushort[ncap]; Array.Copy(Len, len, Count); Len = len;
            }

            public void AppendName(int id, char[] src, int srcOff, int len)
            {
                if (PoolLen + len > Pool.Length)
                {
                    int cap = Pool.Length < 4096 ? 4096 : Pool.Length;
                    while (cap < PoolLen + len) cap *= 2;
                    Array.Resize(ref Pool, cap);
                }
                Array.Copy(src, srcOff, Pool, PoolLen, len);
                Off[id] = PoolLen;
                Len[id] = (ushort)len;
                PoolLen += len;
            }

            public void Trim()
            {
                int n = Count;
                if (Parent.Length != n)
                {
                    Array.Resize(ref Parent, n);
                    Array.Resize(ref Flags, n);
                    Array.Resize(ref Category, n);
                    Array.Resize(ref Size, n);
                    Array.Resize(ref MTime, n);
                    Array.Resize(ref Off, n);
                    Array.Resize(ref Len, n);
                }
                if (Pool.Length != PoolLen) Array.Resize(ref Pool, PoolLen);
            }
        }

        public static VolumeIndex Scan(string root, int threads, Action<ScanProgress> progress, Func<bool> cancelled)
        {
            if (threads < 1) threads = 1;
            if (threads > 16) threads = 16;

            var idx = new VolumeIndex();
            idx.Root = root;
            uint serial;
            Native.TryGetVolumeSerial(root, out serial);
            idx.Serial = serial;

            var shared = new Shared(idx);
            shared.Parent = new int[1024];
            shared.Flags = new byte[1024];
            shared.Category = new byte[1024];
            shared.Size = new long[1024];
            shared.MTime = new int[1024];
            shared.Off = new int[1024];
            shared.Len = new ushort[1024];
            shared.Pool = new char[4096];

            shared.Count = 1; // root
            shared.Parent[0] = -1;
            shared.Flags[0] = (byte)NodeFlags.Directory;
            shared.Off[0] = 0;
            shared.Len[0] = 0;

            string longRoot = @"\\?\" + root;
            var queue = new ConcurrentQueue<DirJob>();
            queue.Enqueue(new DirJob { Path = longRoot, Node = 0 });
            int pending = 1;
            int dirsQueued = 0;

            var sw = Stopwatch.StartNew();
            var workers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                var w = new Thread(delegate()
                {
                    var state = new WorkerState();
                    try
                    {
                        int spin = 0;
                        while (true)
                        {
                            if (cancelled != null && cancelled()) return;
                            DirJob job;
                            if (queue.TryDequeue(out job))
                            {
                                spin = 0;
                                ScanDir(job, queue, shared, state, progress, cancelled, ref pending, ref dirsQueued);
                                Interlocked.Decrement(ref pending);
                            }
                            else if (Volatile.Read(ref pending) == 0)
                            {
                                break;
                            }
                            else
                            {
                                if (++spin < 64) Thread.Yield();
                                else { Thread.Sleep(1); spin = 0; }
                            }
                        }
                    }
                    finally
                    {
                        Flush(shared, state);
                    }
                });
                w.IsBackground = true;
                w.Name = "FastFind.Scan." + t;
                workers[t] = w;
                w.Start();
            }
            foreach (var w in workers) w.Join();
            sw.Stop();

            if (cancelled != null && cancelled()) return null;

            lock (shared.Sync)
            {
                shared.Trim();
            }
            idx.Pool = shared.Pool;
            idx.Off = shared.Off;
            idx.Len = shared.Len;
            idx.Parent = shared.Parent;
            idx.Flags = shared.Flags;
            idx.Category = shared.Category;
            idx.Size = shared.Size;
            idx.MTime = shared.MTime;
            idx.Count = shared.Count;
            idx.SkippedDirs = shared.SkippedDirs;
            idx.ErrorDirs = shared.ErrorDirs;
            idx.ScanMs = (int)sw.ElapsedMilliseconds;
            idx.ScannedUtcTicks = DateTime.UtcNow.Ticks;
            idx.Complete = true;

            if (progress != null)
            {
                progress(new ScanProgress
                {
                    Volume = root,
                    Count = idx.Count,
                    Dirs = dirsQueued,
                    Skipped = idx.SkippedDirs,
                    Errors = idx.ErrorDirs
                });
            }
            return idx;
        }

        private static void ScanDir(DirJob job, ConcurrentQueue<DirJob> queue, Shared shared, WorkerState state,
            Action<ScanProgress> progress, Func<bool> cancelled, ref int pending, ref int dirsQueued)
        {
            string pattern = job.Path;
            if (!pattern.EndsWith("\\")) pattern += "\\";
            pattern += "*";

            Native.WIN32_FIND_DATA data;
            IntPtr h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out data, Native.FindExSearchNameMatch,
                IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH | Native.FIND_FIRST_EX_ON_DISK_ENTRIES_ONLY);
            if (h == new IntPtr(-1))
            {
                uint err = Native.LastError;
                if (err == Native.ERROR_INVALID_PARAMETER)
                {
                    h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out data, Native.FindExSearchNameMatch,
                        IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
                }
                if (h == new IntPtr(-1))
                {
                    err = Native.LastError;
                    if (err == Native.ERROR_ACCESS_DENIED || err == Native.ERROR_FILE_NOT_FOUND || err == Native.ERROR_PATH_NOT_FOUND)
                    {
                        Interlocked.Increment(ref shared.SkippedDirs);
                        state.LocalSkipped++;
                    }
                    else
                    {
                        Interlocked.Increment(ref shared.ErrorDirs);
                    }
                    MaybeReport(shared, progress, state);
                    return;
                }
            }

            int localCount = 0;
            try
            {
                do
                {
                    string name = data.cFileName;
                    if (name == null) continue;
                    int nameLen = name.Length;
                    if (nameLen == 0) continue;
                    if (nameLen == 1 && name[0] == '.') continue;
                    if (nameLen == 2 && name[0] == '.' && name[1] == '.') continue;

                    if (cancelled != null && (++localCount & 0xFF) == 0 && cancelled()) return;

                    uint attrs = data.dwFileAttributes;
                    bool isDir = (attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                    bool isReparse = (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                    long size = isDir ? 0L : (((long)data.nFileSizeHigh << 32) | data.nFileSizeLow);
                    int mtime = IndexTime.FromUnixSeconds(Native.FileTimeToUnixSeconds(data.ftLastWriteTimeLow, data.ftLastWriteTimeHigh));
                    byte flags = (byte)((isDir ? (byte)NodeFlags.Directory : 0) | (isReparse ? (byte)NodeFlags.ReparsePoint : 0));

                    if (isDir)
                    {
                        int id;
                        lock (shared.Sync)
                        {
                            shared.EnsureNodes(shared.Count + 1);
                            id = shared.Count;
                            shared.Count = id + 1;
                            shared.AppendName(id, name.ToCharArray(), 0, nameLen);
                            shared.Parent[id] = job.Node;
                            shared.Flags[id] = flags;
                            shared.Category[id] = 0;
                            shared.Size[id] = 0;
                            shared.MTime[id] = mtime;
                        }
                        state.LocalDirs++;
                        Interlocked.Increment(ref dirsQueued);
                        if (!isReparse)
                        {
                            string child = job.Path.EndsWith("\\") ? job.Path + name : job.Path + "\\" + name;
                            Interlocked.Increment(ref pending);
                            queue.Enqueue(new DirJob { Path = child, Node = id });
                        }
                        else
                        {
                            Interlocked.Increment(ref shared.SkippedDirs);
                            state.LocalSkipped++;
                        }
                    }
                    else
                    {
                        if (state.EntryCount == state.Entries.Length)
                            Flush(shared, state);
                        int off = state.AddName(name);
                        var re = new RawEntry();
                        re.Parent = job.Node;
                        re.NameOff = off;
                        re.NameLen = (ushort)nameLen;
                        re.Flags = flags;
                        re.Category = (byte)FileTypes.ClassifyName(name);
                        re.Size = size;
                        re.MTime = mtime;
                        state.Entries[state.EntryCount++] = re;
                    }
                } while (Native.FindNextFileW(h, out data));
            }
            finally
            {
                Native.FindClose(h);
            }

            if (state.EntryCount >= 2048) Flush(shared, state);
            MaybeReport(shared, progress, state);
        }

        private static void Flush(Shared shared, WorkerState state)
        {
            if (state.EntryCount == 0 && state.NameLen == 0) return;
            lock (shared.Sync)
            {
                for (int i = 0; i < state.EntryCount; i++)
                {
                    var re = state.Entries[i];
                    shared.EnsureNodes(shared.Count + 1);
                    int id = shared.Count;
                    shared.Count = id + 1;
                    shared.AppendName(id, state.NameBuf, re.NameOff, re.NameLen);
                    shared.Parent[id] = re.Parent;
                    shared.Flags[id] = re.Flags;
                    shared.Category[id] = re.Category;
                    shared.Size[id] = re.Size;
                    shared.MTime[id] = re.MTime;
                }
            }
            state.EntryCount = 0;
            state.NameLen = 0;
        }

        private static void MaybeReport(Shared shared, Action<ScanProgress> progress, WorkerState state)
        {
            if (progress == null) return;
            int now = Environment.TickCount;
            int last = Volatile.Read(ref shared.LastProgressTick);
            if (now - last < 250) return;
            if (Interlocked.CompareExchange(ref shared.LastProgressTick, now, last) != last) return;
            int count;
            lock (shared.Sync) { count = shared.Count; }
            progress(new ScanProgress
            {
                Volume = shared.Index.Root,
                Count = count,
                Dirs = count,
                Skipped = shared.SkippedDirs + state.LocalSkipped,
                Errors = shared.ErrorDirs
            });
        }
    }
}

