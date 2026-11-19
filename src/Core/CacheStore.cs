using System;
using System.IO;

namespace FastFind
{
    /// <summary>
    /// Binary cache for a VolumeIndex. Layout is a small header followed by six parallel arrays.
    /// Saves atomically (tmp + replace); loads with plain buffered IO into pre-sized arrays.
    /// </summary>
    internal static class CacheStore
    {
        private const int Magic = 0x58494646; // "FFIX"
        private const int Version = 4;

        public static bool TryLoad(string path, uint expectedSerial, out VolumeIndex index, out string error)
        {
            index = null;
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "cache-missing"; return false; }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var br = new BinaryReader(fs, System.Text.Encoding.Unicode))
                {
                    if (br.ReadInt32() != Magic) { error = "bad-magic"; return false; }
                    int ver = br.ReadInt32();
                    if (ver != Version) { error = "bad-version"; return false; }
                    long created = br.ReadInt64();
                    int scanMs = br.ReadInt32();
                    uint serial = br.ReadUInt32();
                    int flags = br.ReadInt32();
                    int count = br.ReadInt32();
                    int poolChars = br.ReadInt32();
                    int rootChars = br.ReadInt32();
                    int skipped = br.ReadInt32();
                    int errors = br.ReadInt32();
                    if (count <= 0 || count > 50000000) { error = "bad-count"; return false; }
                    if (poolChars < 0 || poolChars > 2000000000) { error = "bad-pool"; return false; }
                    if (rootChars <= 0 || rootChars > 32767) { error = "bad-root"; return false; }

                    char[] rootCharsArr = ReadChars(fs, rootChars);
                    var idx = new VolumeIndex();
                    idx.Root = new string(rootCharsArr);
                    idx.Serial = serial;
                    idx.Complete = (flags & 1) != 0;
                    idx.ScannedUtcTicks = created;
                    idx.ScanMs = scanMs;
                    idx.Count = count;
                    idx.SkippedDirs = skipped;
                    idx.ErrorDirs = errors;
                    idx.Pool = ReadChars(fs, poolChars);
                    idx.Off = ReadInts(fs, count);
                    idx.Len = ReadUShorts(fs, count);
                    idx.Parent = ReadInts(fs, count);
                    idx.Flags = ReadBytes(fs, count);
                    idx.Category = ReadBytes(fs, count);
                    idx.Size = ReadLongs(fs, count);
                    idx.MTime = ReadInts(fs, count);

                    if (fs.Position != fs.Length) { error = "trailing-bytes"; return false; }
                    if (idx.Parent[0] != -1) { error = "bad-root-parent"; return false; }
                    if ((idx.Flags[0] & (byte)NodeFlags.Directory) == 0) { error = "bad-root-flags"; return false; }
                    if (expectedSerial != 0 && serial != expectedSerial) { error = "serial-mismatch"; return false; }

                    index = idx;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = "load-exception:" + ex.GetType().Name;
                index = null;
                return false;
            }
        }

        public static void Save(string path, VolumeIndex idx)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
            using (var bw = new BinaryWriter(fs, System.Text.Encoding.Unicode))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(idx.ScannedUtcTicks);
                bw.Write(idx.ScanMs);
                bw.Write(idx.Serial);
                bw.Write(idx.Complete ? 1 : 0);
                bw.Write(idx.Count);
                bw.Write(idx.Pool.Length);
                bw.Write(idx.Root.Length);
                bw.Write(idx.SkippedDirs);
                bw.Write(idx.ErrorDirs);
                bw.Flush();
                WriteChars(fs, idx.Root.ToCharArray(), idx.Root.Length);
                WriteChars(fs, idx.Pool, idx.Pool.Length);
                WriteInts(fs, idx.Off, idx.Count);
                WriteUShorts(fs, idx.Len, idx.Count);
                WriteInts(fs, idx.Parent, idx.Count);
                WriteBytes(fs, idx.Flags, idx.Count);
                WriteBytes(fs, idx.Category, idx.Count);
                WriteLongs(fs, idx.Size, idx.Count);
                WriteInts(fs, idx.MTime, idx.Count);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); }
                catch (Exception)
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        // ---- primitive array IO (chunked to stay out of the LOH) ----

        private static void WriteChars(Stream s, char[] data, int count)
        {
            const int ChunkChars = 1 << 20;
            var buf = new byte[ChunkChars * 2];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(ChunkChars, count - done);
                Buffer.BlockCopy(data, done * 2, buf, 0, n * 2);
                s.Write(buf, 0, n * 2);
                done += n;
            }
        }

        private static void WriteInts(Stream s, int[] data, int count)
        {
            const int Chunk = 1 << 18;
            var buf = new byte[Chunk * 4];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(Chunk, count - done);
                Buffer.BlockCopy(data, done * 4, buf, 0, n * 4);
                s.Write(buf, 0, n * 4);
                done += n;
            }
        }

        private static void WriteUShorts(Stream s, ushort[] data, int count)
        {
            const int Chunk = 1 << 18;
            var buf = new byte[Chunk * 2];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(Chunk, count - done);
                Buffer.BlockCopy(data, done * 2, buf, 0, n * 2);
                s.Write(buf, 0, n * 2);
                done += n;
            }
        }

        private static void WriteLongs(Stream s, long[] data, int count)
        {
            const int Chunk = 1 << 18;
            var buf = new byte[Chunk * 8];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(Chunk, count - done);
                Buffer.BlockCopy(data, done * 8, buf, 0, n * 8);
                s.Write(buf, 0, n * 8);
                done += n;
            }
        }

        private static void WriteBytes(Stream s, byte[] data, int count)
        {
            s.Write(data, 0, count);
        }

        private static char[] ReadChars(Stream s, int count)
        {
            var arr = new char[count];
            const int ChunkChars = 1 << 20;
            var buf = new byte[ChunkChars * 2];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(ChunkChars, count - done);
                ReadExact(s, buf, n * 2);
                Buffer.BlockCopy(buf, 0, arr, done * 2, n * 2);
                done += n;
            }
            return arr;
        }

        private static int[] ReadInts(Stream s, int count)
        {
            var arr = new int[count];
            const int Chunk = 1 << 18;
            var buf = new byte[Chunk * 4];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(Chunk, count - done);
                ReadExact(s, buf, n * 4);
                Buffer.BlockCopy(buf, 0, arr, done * 4, n * 4);
                done += n;
            }
            return arr;
        }

        private static ushort[] ReadUShorts(Stream s, int count)
        {
            var arr = new ushort[count];
            const int Chunk = 1 << 18;
            var buf = new byte[Chunk * 2];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(Chunk, count - done);
                ReadExact(s, buf, n * 2);
                Buffer.BlockCopy(buf, 0, arr, done * 2, n * 2);
                done += n;
            }
            return arr;
        }

        private static long[] ReadLongs(Stream s, int count)
        {
            var arr = new long[count];
            const int Chunk = 1 << 18;
            var buf = new byte[Chunk * 8];
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(Chunk, count - done);
                ReadExact(s, buf, n * 8);
                Buffer.BlockCopy(buf, 0, arr, done * 8, n * 8);
                done += n;
            }
            return arr;
        }

        private static byte[] ReadBytes(Stream s, int count)
        {
            var arr = new byte[count];
            ReadExact(s, arr, count);
            return arr;
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buf, done, count - done);
                if (n <= 0) throw new EndOfStreamException();
                done += n;
            }
        }
    }
}


