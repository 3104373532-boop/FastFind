using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FastFind
{
    internal static class IndexBench
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string root = args.Length > 0 ? args[0] : "C:\\";
            int threads = args.Length > 1 ? int.Parse(args[1]) : 8;
            string cachePath = args.Length > 2 ? args[2] : null;
            if (!root.EndsWith("\\")) root += "\\";

            var sw = Stopwatch.StartNew();
            var idx = Scanner.Scan(root, threads, delegate(ScanProgress p)
            {
                if ((p.Count & 0x3FFFF) == 0)
                    Console.WriteLine("  scanning... " + p.Count.ToString("N0") + " nodes, " + sw.ElapsedMilliseconds + "ms");
            }, null);
            sw.Stop();
            Console.WriteLine("SCAN " + root + " -> " + idx.Count.ToString("N0") + " nodes, dirs=" + CountDirs(idx).ToString("N0") +
                ", files=" + CountFiles(idx).ToString("N0") + ", skipped=" + idx.SkippedDirs + ", errors=" + idx.ErrorDirs +
                ", " + (idx.Count / Math.Max(1.0, sw.Elapsed.TotalSeconds)).ToString("N0") + " nodes/s, " + sw.ElapsedMilliseconds + "ms");
            Console.WriteLine("MEM  managed=" + (GC.GetTotalMemory(true) / 1048576.0).ToString("F1") + "MB, private=" +
                (Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0).ToString("F1") + "MB, peakWS=" +
                (Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0).ToString("F1") + "MB");

            if (cachePath != null)
            {
                sw.Restart();
                CacheStore.Save(cachePath, idx);
                Console.WriteLine("SAVE cache " + new FileInfo(cachePath).Length / 1048576.0 + "MB in " + sw.ElapsedMilliseconds + "ms");
                uint serial;
                Native.TryGetVolumeSerial(root, out serial);
                VolumeIndex loaded;
                string err;
                sw.Restart();
                bool ok = CacheStore.TryLoad(cachePath, serial, out loaded, out err);
                sw.Stop();
                Console.WriteLine("LOAD cache ok=" + ok + " err=" + err + " in " + sw.ElapsedMilliseconds + "ms");
                if (ok) idx = loaded;
            }

            string[] queries = { "setup", "dll", "*.log", "ext:png", "size:>10mb", "config", "2026", "report", "path:python", "path:\"Program Files\" ext:exe", "dm:today", "type:dir 项目" };
            foreach (var query in queries)
            {
                var q = SearchQuery.Parse(query);
                SearchEngine.Search(q, new VolumeIndex[] { idx }, null, 20000, null);
                var res = SearchEngine.Search(q, new VolumeIndex[] { idx }, null, 20000, null);
                Console.WriteLine("QUERY '" + query + "' -> " + res.Total.ToString("N0") + " hits, shown=" + res.Hits.Length +
                    ", " + res.ElapsedMs + "ms");
            }

            // path open check
            int sample = -1;
            for (int i = 1; i < idx.Count; i++)
            {
                if (!idx.IsDir(i) && idx.Parent[i] > 0) { sample = i; break; }
            }
            if (sample > 0)
                Console.WriteLine("SAMPLE " + idx.GetPath(sample) + " (" + idx.Size[sample] + "B)");
            return 0;
        }

        private static int CountDirs(VolumeIndex idx)
        {
            int n = 0;
            for (int i = 0; i < idx.Count; i++) if (idx.IsDir(i)) n++;
            return n;
        }

        private static int CountFiles(VolumeIndex idx)
        {
            int n = 0;
            for (int i = 0; i < idx.Count; i++) if (!idx.IsDir(i)) n++;
            return n;
        }
    }
}


