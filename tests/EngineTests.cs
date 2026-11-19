using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FastFind
{
    internal static class EngineTests
    {
        private static int _pass;
        private static int _fail;
        private static string _tempRoot;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            bool perf = args.Length > 0 && args[0] == "--perf";
            string baseDir = Path.Combine(Path.GetDirectoryName(typeof(EngineTests).Assembly.Location), "test-tmp");
            _tempRoot = Path.Combine(baseDir, "FastFindTests-" + Process.GetCurrentProcess().Id);
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
                BuildTree(_tempRoot);

                TestScan();
                TestCache();
                TestQueryParse();
                TestFileTypes();
                TestSearch();
                TestRanking();
                if (perf) TestPerformance();
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("FAIL exception: " + ex);
            }
            finally
            {
                try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine(string.Format("RESULT: {0} passed, {1} failed", _pass, _fail));
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(bool cond, string what)
        {
            if (cond) { _pass++; Console.WriteLine("  ok   " + what); }
            else { _fail++; Console.WriteLine("  FAIL " + what); }
        }

        private static void BuildTree(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            Directory.CreateDirectory(Path.Combine(root, "images"));
            Directory.CreateDirectory(Path.Combine(root, "中文目录"));
            Directory.CreateDirectory(Path.Combine(root, "docs", "sub"));

            WriteFile(Path.Combine(root, "docs", "report-2026.docx"), 12345, new DateTime(2026, 1, 15, 10, 30, 0));
            WriteFile(Path.Combine(root, "docs", "notes.txt"), 100, new DateTime(2026, 2, 1, 9, 0, 0));
            WriteFile(Path.Combine(root, "docs", "tmp-debug.log"), 2048, new DateTime(2025, 12, 31, 23, 0, 0));
            WriteFile(Path.Combine(root, "docs", "sub", "deep.txt"), 42, new DateTime(2026, 3, 3, 3, 3, 3));
            WriteFile(Path.Combine(root, "images", "photo.JPG"), 200000, new DateTime(2026, 2, 14, 12, 0, 0));
            WriteFile(Path.Combine(root, "中文目录", "报价单-最终.xlsx"), 9999, new DateTime(2026, 1, 20, 8, 0, 0));
            WriteFile(Path.Combine(root, "empty.bin"), 0, new DateTime(2025, 11, 11, 11, 11, 11));
        }

        private static void WriteFile(string path, int size, DateTime mtime)
        {
            var data = new byte[size];
            for (int i = 0; i < size && i < 4096; i++) data[i] = (byte)(i & 0xFF);
            File.WriteAllBytes(path, data);
            File.SetLastWriteTime(path, mtime);
        }

        private static VolumeIndex ScanTree()
        {
            string root = _tempRoot.EndsWith("\\") ? _tempRoot : _tempRoot + "\\";
            var idx = Scanner.Scan(root, 4, null, null);
            return idx;
        }

        private static string FindName(VolumeIndex idx, string name)
        {
            for (int i = 0; i < idx.Count; i++)
                if (string.Equals(idx.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return idx.GetPath(i);
            return null;
        }

        private static void TestScan()
        {
            Console.WriteLine("[scan]");
            var idx = ScanTree();
            Check(idx != null, "scan returned index");
            Check(idx.Count == 12, "node count 12 (got " + (idx == null ? -1 : idx.Count) + ")");
            Check(idx.IsDir(0), "node 0 is directory");
            string report = FindName(idx, "report-2026.docx");
            Check(report != null && report.EndsWith("docs\\report-2026.docx", StringComparison.OrdinalIgnoreCase),
                "path reconstruction: " + report);
            string cn = FindName(idx, "报价单-最终.xlsx");
            Check(cn != null && cn.EndsWith("中文目录\\报价单-最终.xlsx"), "unicode path: " + cn);
            string deep = FindName(idx, "deep.txt");
            Check(deep != null && deep.EndsWith("docs\\sub\\deep.txt"), "nested path: " + deep);

            // size and mtime
            int node = -1;
            for (int i = 0; i < idx.Count; i++) if (idx.GetName(i) == "photo.JPG") node = i;
            Check(node > 0 && idx.Size[node] == 200000, "file size stored");
            var mtime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(idx.MTime[node]).ToLocalTime();
            Check(mtime.Year == 2026 && mtime.Month == 2 && mtime.Day == 14, "mtime stored: " + mtime);
            Check(!idx.IsDir(node), "file flag");
        }

        private static void TestCache()
        {
            Console.WriteLine("[cache]");
            var idx = ScanTree();
            string path = Path.Combine(_tempRoot, "cache-test.dat");
            var sw = Stopwatch.StartNew();
            CacheStore.Save(path, idx);
            long saveMs = sw.ElapsedMilliseconds;
            var fi = new FileInfo(path);
            Check(fi.Exists && fi.Length > 100, "cache file written (" + fi.Length + " bytes, " + saveMs + "ms)");
            VolumeIndex loaded;
            string err;
            sw.Restart();
            bool ok = CacheStore.TryLoad(path, idx.Serial, out loaded, out err);
            long loadMs = sw.ElapsedMilliseconds;
            Check(ok, "cache load: " + err);
            if (loaded != null)
            {
                Check(loaded.Count == idx.Count, "cache count");
                Check(loaded.Category.Length == idx.Category.Length, "cache category array");
                Check(loaded.Pool.Length == idx.Pool.Length, "cache pool size");
                Check(loaded.GetName(5) != null, "cache name access");
                Check(loaded.GetPath(loaded.Count - 1) != null, "cache path access");
                string r1 = FindName(idx, "report-2026.docx");
                string r2 = FindName(loaded, "report-2026.docx");
                Check(r1 == r2, "cache roundtrip path equality");
                // rebuild search result over loaded index
                var q = SearchQuery.Parse("report");
                var res = SearchEngine.Search(q, new VolumeIndex[] { loaded }, null, 100, null);
                Check(res.Total == 1, "search on loaded cache");
            }
            bool badSerial = CacheStore.TryLoad(path, idx.Serial + 1, out loaded, out err);
            Check(!badSerial && err == "serial-mismatch", "cache serial validation");
        }

        private static void TestQueryParse()
        {
            Console.WriteLine("[query]");
            var q = SearchQuery.Parse("report");
            Check(q.Terms.Count == 1 && q.Terms[0].Text == "report", "plain term");

            q = SearchQuery.Parse("ext:jpg,png");
            Check(q.Extensions.Count == 2 && q.Extensions[0] == "jpg" && q.Extensions[1] == "png", "ext list");

            q = SearchQuery.Parse("size:>1mb");
            Check(q.SizeMin == 1048577, "size >1mb -> " + q.SizeMin);

            q = SearchQuery.Parse("size:1mb..2mb");
            Check(q.SizeMin == 1048576 && q.SizeMax == 2097152, "size range");

            q = SearchQuery.Parse("dm:2026-01");
            Check(q.MTimeMin != 0 && q.MTimeMax > q.MTimeMin, "date month range");

            q = SearchQuery.Parse("path:\"My Docs\" 报价");
            Check(q.PathTerms.Count == 1 && q.PathTerms[0] == "My Docs" && q.Terms.Count == 1, "path + quoted");

            q = SearchQuery.Parse("-tmp ext:log");
            Check(q.NegativeTerms.Count == 1 && q.Extensions.Count == 1, "negative term + ext");

            q = SearchQuery.Parse("/^IMG_\\d+/");
            Check(q.Terms.Count == 1 && q.Terms[0].IsRegex, "regex term");

            q = SearchQuery.Parse("\"foo bar\"");
            Check(q.Terms.Count == 1 && q.Terms[0].Text == "foo bar", "quoted phrase");

            q = SearchQuery.Parse("/(/");
            Check(q.Terms.Count == 0 && !string.IsNullOrEmpty(q.Warning), "invalid regex warning");

            q = SearchQuery.Parse("*.pdf");
            Check(q.Terms.Count == 1 && q.Terms[0].Wildcard, "wildcard");

            q = SearchQuery.Parse("type:dir 中文");
            Check(q.TypeFilter == NameFilterKind.DirsOnly && q.Terms.Count == 1, "type filter");

            q = SearchQuery.Parse("  ");
            Check(q.IsEmpty, "empty query");
        }

        private static void TestFileTypes()
        {
            Console.WriteLine("[file types]");
            Check(FileTypes.ClassifyName("a.docx") == FileCategory.Documents, "docx -> documents");
            Check(FileTypes.ClassifyName("a.JPG") == FileCategory.Images, "JPG -> images (case insensitive)");
            Check(FileTypes.ClassifyName("movie.mkv") == FileCategory.Videos, "mkv -> videos");
            Check(FileTypes.ClassifyName("song.flac") == FileCategory.Audio, "flac -> audio");
            Check(FileTypes.ClassifyName("pack.7z") == FileCategory.Archives, "7z -> archives");
            Check(FileTypes.ClassifyName("app.exe") == FileCategory.Programs, "exe -> programs");
            Check(FileTypes.ClassifyName("main.cs") == FileCategory.Code, "cs -> code");
            Check(FileTypes.ClassifyName("README") == FileCategory.Other, "no extension -> other");
            Check(FileTypes.ClassifyName("a.unknownext") == FileCategory.Other, "unknown -> other");
            Check(FileTypes.Describe(0) == "全部", "describe all");
            Check(FileTypes.Describe((int)(FileCategory.Images | FileCategory.Videos)) == "图片+视频", "describe two");

            var idx = ScanTree();
            int photo = -1;
            for (int i = 0; i < idx.Count; i++) if (idx.GetName(i) == "photo.JPG") photo = i;
            Check(photo > 0 && idx.Category[photo] == (byte)FileCategory.Images, "scanner stores category");
            var q = SearchQuery.Parse("");
            q.TypeMask = (int)FileCategory.Images;
            var res = SearchEngine.Search(q, new VolumeIndex[] { idx }, null, 100, null);
            Check(res.Total == 1, "filter-only query returns only images (got " + res.Total + ")");
            var q2 = SearchQuery.Parse("");
            q2.TypeMask = (int)(FileCategory.Images | FileCategory.Documents);
            var res2 = SearchEngine.Search(q2, new VolumeIndex[] { idx }, null, 100, null);
            Check(res2.Total == 5, "multi category OR filter returns images+documents (got " + res2.Total + ")");
        }

        private static void TestSearch()
        {
            Console.WriteLine("[search]");
            var idx = ScanTree();
            var vols = new VolumeIndex[] { idx };

            Check(SearchTotal("report", vols) == 1, "search report");
            Check(SearchTotal("REPORT", vols) == 1, "case insensitive");
            Check(SearchTotal("*.docx", vols) == 1, "wildcard docx");
            Check(SearchTotal("ext:jpg", vols) == 1, "ext filter");
            Check(SearchTotal("ext:txt", vols) == 2, "ext txt count (notes, deep) -> got " + SearchTotal("ext:txt", vols));
            Check(SearchTotal("size:>1000", vols) == 4, "size >1000 (report, tmp, photo) -> got " + SearchTotal("size:>1000", vols));
            Check(SearchTotal("report -tmp", vols) == 1, "negative term");
            Check(SearchTotal("path:docs report", vols) == 1, "path filter");
            Check(SearchTotal("path:images", vols) == 2, "path filter finds dir + child");
            Check(SearchTotal("type:dir 中文", vols) == 1, "type dir");
            Check(SearchTotal("/^photo/", vols) == 1, "regex search");
            Check(SearchTotal("deep", vols) == 1, "nested file");
            var res = SearchEngine.Search(SearchQuery.Parse("ext:txt"), vols, null, 100, null);
            Check(res.Hits.Length == 2, "hits array length");
            Check(res.ElapsedMs >= 0, "timing reported");
            Check(res.Warning == null, "no warnings");

            // volume scope mask
            var qOff = SearchQuery.Parse("report");
            var resOff = SearchEngine.Search(qOff, new VolumeIndex[] { idx }, new bool[] { false }, 100, null);
            Check(resOff.Total == 0, "disabled volume yields no hits (got " + resOff.Total + ")");
            var resOn = SearchEngine.Search(qOff, new VolumeIndex[] { idx }, new bool[] { true }, 100, null);
            Check(resOn.Total == 1, "enabled volume yields hits (got " + resOn.Total + ")");

            // settings: scope persistence and type mask
            var st = new Settings();
            st.ApplySetting("scope", "C:, D:");
            Check(st.ScopeRaw == "C:, D:" && st.ScopeVolumes.Count == 2 && st.ScopeVolumes[1] == "D:\\", "settings scope parse");
            st.ApplySetting("scope", "");
            Check(st.ScopeVolumes.Count == 0 && st.ScopeRaw == "", "settings empty scope (all disabled)");
            st.ApplySetting("filetypes", "5");
            Check(st.FileTypeMask == 5, "settings file types parse");
            st.ApplySetting("filetypes", "129");
            Check(st.FileTypeMask == 129, "settings file types large mask parse");

            // cancellation
            var q = SearchQuery.Parse("txt");
            var res2 = SearchEngine.Search(q, vols, null, 100, delegate() { return true; });
            Check(res2.Cancelled || res2.Total == 0, "cancellation works");
        }

        private static int SearchTotal(string query, VolumeIndex[] vols)
        {
            var res = SearchEngine.Search(SearchQuery.Parse(query), vols, null, 1000, null);
            return res.Total;
        }

        private static void TestRanking()
        {
            Console.WriteLine("[ranking]");
            var idx = ScanTree();
            var res = SearchEngine.Search(SearchQuery.Parse("report"), new VolumeIndex[] { idx }, null, 100, null);
            Check(res.Hits.Length == 1, "one hit");
            // 'o' appears in many names: exact short names should come before long ones
            res = SearchEngine.Search(SearchQuery.Parse("o"), new VolumeIndex[] { idx }, null, 100, null);
            bool sorted = true;
            for (int i = 1; i < res.Hits.Length; i++)
            {
                if (res.Hits[i].Rank < res.Hits[i - 1].Rank) sorted = false;
            }
            Check(sorted, "rank monotonic in default order");
        }

        private static void TestPerformance()
        {
            Console.WriteLine("[perf]");
            VolumeIndex idx = null;
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "index");
            string cache = Path.Combine(cacheDir, "index-C.dat");
            if (File.Exists(cache))
            {
                string err;
                uint serial;
                Native.TryGetVolumeSerial("C:\\", out serial);
                CacheStore.TryLoad(cache, serial, out idx, out err);
            }
            if (idx == null)
            {
                Console.WriteLine("  (no C: cache found, scanning C:\\Windows)");
                var swScan = Stopwatch.StartNew();
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!win.EndsWith("\\")) win += "\\";
                idx = Scanner.Scan(win, 8, null, null);
                Console.WriteLine("  scanned " + idx.Count.ToString("N0") + " nodes in " + swScan.ElapsedMilliseconds + "ms");
            }
            var vols = new VolumeIndex[] { idx };
            string[] queries = { "setup", "dll", "*.log", "ext:png", "size:>10mb", "config", "x64", "2026" };
            var times = new List<long>();
            foreach (var query in queries)
            {
                var q = SearchQuery.Parse(query);
                SearchEngine.Search(q, vols, null, 20000, null); // warm
                var sw = Stopwatch.StartNew();
                var res = SearchEngine.Search(q, vols, null, 20000, null);
                sw.Stop();
                times.Add(sw.ElapsedMilliseconds);
                Console.WriteLine("  query '" + query + "' -> " + res.Total.ToString("N0") + " hits in " + sw.ElapsedMilliseconds + "ms");
            }
            times.Sort();
            long p95 = times[(int)(times.Count * 0.95 - 1 < 0 ? 0 : times.Count * 0.95 - 1)];
            Console.WriteLine("  p95=" + p95 + "ms");
            Check(p95 < 300, "p95 under 300ms");
        }
    }
}






