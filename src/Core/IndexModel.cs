using System;
using System.Text;

namespace FastFind
{
    [Flags]
    internal enum NodeFlags : byte
    {
        None = 0,
        Directory = 1,
        ReparsePoint = 2
    }

    /// <summary>
    /// Immutable-after-build, cache friendly index of one volume.
    /// Structure-of-arrays: names live in one char pool, paths are rebuilt on demand.
    /// </summary>
    internal sealed class VolumeIndex
    {
        public string Root;          // "C:\" - display form, with trailing backslash
        public uint Serial;          // NTFS volume serial
        public int Count;            // node count, node 0 is the volume root
        public char[] Pool = new char[0];
        public int[] Off = new int[0];
        public ushort[] Len = new ushort[0];
        public int[] Parent = new int[0];
        public byte[] Flags = new byte[0];
        public byte[] Category = new byte[0];
        public long[] Size = new long[0];
        public int[] MTime = new int[0];   // unix seconds, UTC
        public bool Complete = true;
        public long ScannedUtcTicks;
        public int ScanMs;
        public int SkippedDirs;
        public int ErrorDirs;

        [NonSerialized] private string[] _dirPathCache;

        public bool IsDir(int node) { return (Flags[node] & (byte)NodeFlags.Directory) != 0; }
        public bool IsReparse(int node) { return (Flags[node] & (byte)NodeFlags.ReparsePoint) != 0; }

        public string GetName(int node)
        {
            int len = Len[node];
            if (len == 0) return string.Empty;
            return new string(Pool, Off[node], len);
        }

        public string GetExtension(int node)
        {
            int len = Len[node];
            int off = Off[node];
            for (int i = len - 1; i > 0; i--)
            {
                char c = Pool[off + i];
                if (c == '.')
                    return new string(Pool, off + i + 1, len - i - 1);
                if (c == '\\') break;
            }
            return string.Empty;
        }

        /// <summary>Build the full display path, e.g. "C:\Users\tang\a.txt".</summary>
        public string GetPath(int node)
        {
            int depth = 0;
            int n = node;
            while (n >= 0)
            {
                depth++;
                n = Parent[n];
            }
            if (depth == 1) return Root;

            var seg = new int[depth - 1];
            n = node;
            for (int i = depth - 2; i >= 0; i--)
            {
                seg[i] = n;
                n = Parent[n];
            }

            var sb = new StringBuilder(64 + depth * 12);
            sb.Append(Root);
            for (int i = 0; i < seg.Length; i++)
            {
                int id = seg[i];
                if (sb.Length > Root.Length && sb[sb.Length - 1] != '\\') sb.Append('\\');
                sb.Append(Pool, Off[id], Len[id]);
                if (i < seg.Length - 1) sb.Append('\\');
            }
            return sb.ToString();
        }

        /// <summary>Full path of the parent directory of a node (used by path filters).</summary>
        public string GetDirectoryPath(int node)
        {
            int p = Parent[node];
            if (p < 0) return Root;
            string[] cache = _dirPathCache;
            if (cache == null)
            {
                cache = new string[Count];
                _dirPathCache = cache;
            }
            string hit = cache[p];
            if (hit != null) return hit;
            string built = GetPath(p);
            cache[p] = built;
            return built;
        }

        public void DropPathCache()
        {
            _dirPathCache = null;
        }
    }
}

