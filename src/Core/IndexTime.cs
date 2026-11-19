using System;
namespace FastFind {
    internal static class IndexTime {
        public const int Offset = 946684800; // 2000-01-01T00:00:00Z
        public static int FromUnixSeconds(long unix) {
            long v = unix - Offset;
            if (v < int.MinValue) return int.MinValue;
            if (v > int.MaxValue) return int.MaxValue;
            return (int)v;
        }
        public static int Now { get { return FromUnixSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } }
    }
}
