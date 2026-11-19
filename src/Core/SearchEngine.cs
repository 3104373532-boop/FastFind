using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace FastFind
{
    internal struct Hit
    {
        public int Vol;
        public int Node;
        public int Rank;
    }

    internal sealed class SearchResult
    {
        public Hit[] Hits = new Hit[0];
        public int Total;
        public bool Truncated;
        public int ElapsedMs;
        public string Warning;
        public bool Cancelled;
        public VolumeIndex[] Volumes;
        public bool[] VolumeEnabled;
        public SearchQuery Query;
    }

    /// <summary>
    /// Parallel name search over the published volume indexes. Matches are bucketed by rank
    /// (exact / prefix / word boundary / substring) so no full sort is needed for the default view.
    /// </summary>
    internal static class SearchEngine
    {
        private struct Chunk
        {
            public int Vol;
            public int Start;
            public int End;
        }

        private sealed class WorkerResult
        {
            public readonly List<Hit>[] Buckets = new List<Hit>[5];
            public WorkerResult()
            {
                for (int i = 0; i < 5; i++) Buckets[i] = new List<Hit>(64);
            }
        }

        public static SearchResult Search(SearchQuery q, VolumeIndex[] volumes, bool[] volumeEnabled, int limit, Func<bool> cancelled)
        {
            var sw = Stopwatch.StartNew();
            var result = new SearchResult();
            result.Query = q;
            result.Volumes = volumes;
            result.VolumeEnabled = volumeEnabled;
            result.Warning = q.Warning;
            if (q.IsEmpty || limit <= 0)
            {
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                return result;
            }

            var chunks = new List<Chunk>();
            if (volumes != null)
            {
                for (int v = 0; v < volumes.Length; v++)
                {
                    if (volumes[v] == null) continue;
                    if (volumeEnabled != null && v < volumeEnabled.Length && !volumeEnabled[v]) continue;
                    int n = volumes[v].Count;
                    if (n <= 0) continue;
                    int parts = (n + 119999) / 120000;
                    if (parts < 1) parts = 1;
                    for (int p = 0; p < parts; p++)
                    {
                        int start = (int)((long)n * p / parts);
                        int end = (int)((long)n * (p + 1) / parts);
                        if (end > start)
                            chunks.Add(new Chunk { Vol = v, Start = start, End = end });
                    }
                }
            }

            int threadCount = Math.Min(Math.Min(8, Environment.ProcessorCount), Math.Max(1, chunks.Count));
            if (chunks.Count == 0)
            {
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                return result;
            }

            var workers = new WorkerResult[threadCount];
            var threads = new Thread[threadCount];
            int nextChunk = -1;
            for (int t = 0; t < threadCount; t++)
            {
                var wr = new WorkerResult();
                workers[t] = wr;
                threads[t] = new Thread(delegate()
                {
                    int taken;
                    while ((taken = Interlocked.Increment(ref nextChunk)) < chunks.Count)
                    {
                        Chunk ch = chunks[taken];
                        MatchChunk(ch, volumes[ch.Vol], q, wr, cancelled);
                        if (cancelled != null && cancelled()) return;
                    }
                });
                threads[t].IsBackground = true;
                threads[t].Name = "FastFind.Search." + t;
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();

            if (cancelled != null && cancelled())
            {
                result.Cancelled = true;
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                return result;
            }

            long total = 0;
            for (int t = 0; t < workers.Length; t++)
                for (int r = 0; r < 5; r++)
                    total += workers[t].Buckets[r].Count;
            result.Total = (int)Math.Min(int.MaxValue, total);

            var selected = new List<Hit>(Math.Min(limit, (int)Math.Min(total, limit)));
            for (int rank = 0; rank < 5 && selected.Count < limit; rank++)
            {
                for (int t = 0; t < workers.Length && selected.Count < limit; t++)
                {
                    var bucket = workers[t].Buckets[rank];
                    int take = Math.Min(bucket.Count, limit - selected.Count);
                    for (int i = 0; i < take; i++) selected.Add(bucket[i]);
                }
            }
            result.Truncated = total > selected.Count;
            var hits = selected.ToArray();
            Array.Sort(hits, new HitComparer(volumes));
            result.Hits = hits;
            result.ElapsedMs = (int)sw.ElapsedMilliseconds;
            return result;
        }

        private static void MatchChunk(Chunk ch, VolumeIndex v, SearchQuery q, WorkerResult wr, Func<bool> cancelled)
        {
            char[] pool = v.Pool;
            int[] off = v.Off;
            ushort[] len = v.Len;
            byte[] flags = v.Flags;
            byte[] category = v.Category;
            long[] size = v.Size;
            int[] mtime = v.MTime;
            int[] parent = v.Parent;
            int counter = 0;

            for (int node = ch.Start; node < ch.End; node++)
            {
                if ((++counter & 0x3FF) == 0 && cancelled != null && cancelled()) return;

                byte fl = flags[node];
                bool isDir = (fl & (byte)NodeFlags.Directory) != 0;
                if (q.TypeFilter == NameFilterKind.FilesOnly && isDir) continue;
                if (q.TypeFilter == NameFilterKind.DirsOnly && !isDir) continue;
                if (q.TypeMask != 0 && (category[node] & q.TypeMask) == 0) continue;

                int no = off[node];
                int nl = len[node];
                if (nl == 0) continue; // volume root

                if (q.Extensions.Count > 0 || q.NegativeExtensions.Count > 0)
                {
                    int eOff, eLen;
                    FindExtension(pool, no, nl, out eOff, out eLen);
                    bool extMatched = false;
                    for (int i = 0; i < q.Extensions.Count; i++)
                    {
                        if (ExtEquals(pool, eOff, eLen, q.Extensions[i])) { extMatched = true; break; }
                    }
                    if (!extMatched) continue;
                    if (q.NegativeExtensions.Count > 0)
                    {
                        bool excluded = false;
                        for (int i = 0; i < q.NegativeExtensions.Count; i++)
                            if (ExtEquals(pool, eOff, eLen, q.NegativeExtensions[i])) { excluded = true; break; }
                        if (excluded) continue;
                    }
                }

                if (q.SizeMin >= 0 && size[node] < q.SizeMin) continue;
                if (q.SizeMax >= 0 && size[node] > q.SizeMax) continue;
                if (q.MTimeMin != 0 && mtime[node] < q.MTimeMin) continue;
                if (q.MTimeMax != 0 && mtime[node] >= q.MTimeMax) continue;

                int rank = 4;
                bool failed = false;
                if (q.Terms.Count > 0)
                {
                    for (int i = 0; i < q.Terms.Count; i++)
                    {
                        SearchTerm term = q.Terms[i];
                        int r;
                        if (!MatchName(pool, no, nl, term, out r)) { failed = true; break; }
                        if (i == 0) rank = r;
                        else if (r < rank) rank = r;
                    }
                    if (failed) continue;
                }

                if (q.NegativeTerms.Count > 0)
                {
                    bool neg = false;
                    for (int i = 0; i < q.NegativeTerms.Count; i++)
                    {
                        int dummy;
                        if (MatchName(pool, no, nl, q.NegativeTerms[i], out dummy)) { neg = true; break; }
                    }
                    if (neg) continue;
                }

                if (q.PathTerms.Count > 0)
                {
                    bool pathOk = false;
                    for (int i = 0; i < q.PathTerms.Count; i++)
                    {
                        if (MatchPath(v, node, q.PathTerms[i])) { pathOk = true; break; }
                    }
                    if (!pathOk) continue;
                }
                if (q.NegativePathTerms.Count > 0)
                {
                    bool neg = false;
                    for (int i = 0; i < q.NegativePathTerms.Count; i++)
                    {
                        if (MatchPath(v, node, q.NegativePathTerms[i])) { neg = true; break; }
                    }
                    if (neg) continue;
                }

                if (rank < 0) rank = 0;
                if (rank > 4) rank = 4;
                wr.Buckets[rank].Add(new Hit { Vol = ch.Vol, Node = node, Rank = rank });
            }
        }

        // ---------------- name matching ----------------

        internal static bool MatchName(char[] pool, int off, int len, SearchTerm term, out int rank)
        {
            rank = 3;
            if (term.IsRegex)
            {
                string name = new string(pool, off, len);
                bool ok;
                try { ok = term.Compiled.IsMatch(name); }
                catch (RegexMatchTimeoutException) { ok = false; }
                if (!ok) return false;
                rank = 2;
                return true;
            }
            if (term.Wildcard)
            {
                if (!GlobMatch(pool, off, len, term.Text)) return false;
                rank = 2;
                return true;
            }
            int pos = IndexOfIgnoreCase(pool, off, len, term.Text);
            if (pos < 0) return false;
            if (len == term.Text.Length) rank = 0;
            else if (pos == 0) rank = 1;
            else if (IsWordBoundary(pool[off + pos - 1])) rank = 2;
            else rank = 3;
            return true;
        }

        private static bool IsWordBoundary(char c)
        {
            return !char.IsLetterOrDigit(c) && c != '_';
        }

        internal static int IndexOfIgnoreCase(char[] pool, int off, int len, string needle)
        {
            int n = needle.Length;
            if (n == 0) return 0;
            if (n > len) return -1;
            char c0 = needle[0];
            int last = len - n;
            for (int i = 0; i <= last; i++)
            {
                if (!CharEq(pool[off + i], c0)) continue;
                int k = 1;
                while (k < n && CharEq(pool[off + i + k], needle[k])) k++;
                if (k == n) return i;
            }
            return -1;
        }

        internal static bool ContainsIgnoreCase(char[] pool, int off, int len, string needle)
        {
            return IndexOfIgnoreCase(pool, off, len, needle) >= 0;
        }

        internal static bool ContainsIgnoreCase(string text, string needle)
        {
            return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool CharEq(char a, char b)
        {
            if (a == b) return true;
            if (a < 128 && b < 128)
            {
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                return a == b;
            }
            return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
        }

        internal static bool GlobMatch(char[] pool, int off, int len, string pattern)
        {
            int p = 0, s = 0;
            int star = -1, mark = 0;
            while (s < len)
            {
                if (p < pattern.Length && (pattern[p] == '?' || CharEq(pattern[p], pool[off + s])))
                {
                    p++; s++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = s;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    s = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private static void FindExtension(char[] pool, int off, int len, out int eOff, out int eLen)
        {
            eOff = off + len;
            eLen = 0;
            for (int i = len - 1; i > 0; i--)
            {
                char c = pool[off + i];
                if (c == '.')
                {
                    eOff = off + i + 1;
                    eLen = len - i - 1;
                    return;
                }
                if (c == '\\') return;
            }
        }

        private static bool ExtEquals(char[] pool, int eOff, int eLen, string ext)
        {
            if (eLen != ext.Length) return false;
            for (int i = 0; i < eLen; i++)
                if (!CharEq(pool[eOff + i], ext[i])) return false;
            return true;
        }

        private static bool MatchPath(VolumeIndex v, int node, string term)
        {
            char[] pool = v.Pool;
            if (ContainsIgnoreCase(pool, v.Off[node], v.Len[node], term)) return true;
            if (term.IndexOf('\\') >= 0 || term.IndexOf(':') >= 0)
            {
                int p0 = v.Parent[node];
                if (p0 < 0) return false;
                string dir = v.GetDirectoryPath(node);
                return dir.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            int p = v.Parent[node];
            while (p > 0)
            {
                if (ContainsIgnoreCase(pool, v.Off[p], v.Len[p], term)) return true;
                p = v.Parent[p];
            }
            if (p == 0)
            {
                // volume root: match against root text e.g. "C:\"
                if (ContainsIgnoreCase(v.Root, term)) return true;
            }
            return false;
        }

        // ---------------- default ordering ----------------

        private sealed class HitComparer : IComparer<Hit>
        {
            private readonly VolumeIndex[] _vols;
            public HitComparer(VolumeIndex[] vols) { _vols = vols; }
            public int Compare(Hit x, Hit y)
            {
                if (x.Rank != y.Rank) return x.Rank - y.Rank;
                VolumeIndex vx = _vols[x.Vol];
                VolumeIndex vy = _vols[y.Vol];
                int lx = vx.Len[x.Node];
                int ly = vy.Len[y.Node];
                if (lx != ly) return lx - ly;
                int cmp = CompareNames(vx, x.Node, vy, y.Node);
                if (cmp != 0) return cmp;
                if (x.Vol != y.Vol) return x.Vol - y.Vol;
                return x.Node - y.Node;
            }
        }

        private static int CompareNames(VolumeIndex vx, int nx, VolumeIndex vy, int ny)
        {
            int lx = vx.Len[nx], ly = vy.Len[ny];
            int n = Math.Min(lx, ly);
            int ox = vx.Off[nx], oy = vy.Off[ny];
            for (int i = 0; i < n; i++)
            {
                char a = vx.Pool[ox + i], b = vy.Pool[oy + i];
                if (a == b) continue;
                char la = a < 128 ? (a >= 'A' && a <= 'Z' ? (char)(a + 32) : a) : char.ToUpperInvariant(a);
                char lb = b < 128 ? (b >= 'A' && b <= 'Z' ? (char)(b + 32) : b) : char.ToUpperInvariant(b);
                if (la != lb) return la < lb ? -1 : 1;
                return a < b ? -1 : 1;
            }
            return lx - ly;
        }
    }
}

