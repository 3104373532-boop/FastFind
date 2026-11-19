using System;
using System.Runtime.InteropServices;

namespace FastFind
{
    /// <summary>
    /// Detects a Low Integrity / AppContainer token. Such processes are common when the
    /// app is started by a sandboxed agent or a browser-based launcher: Windows then blocks
    /// ShellExecute, so "double click does nothing" is a permission problem, not a UI bug.
    /// </summary>
    internal static class ProcessIntegrity
    {
        private const int TokenIntegrityLevel = 25;
        private const int TokenIsAppContainer = 29;
        private const uint TokenQuery = 0x0008;
        private const uint ProcessQueryLimited = 0x1000;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buffer, int length, out int returned);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>True when the current process runs below Medium integrity or inside an AppContainer.</summary>
        public static bool IsRestricted()
        {
            int rid;
            if (TryGetIntegrityRid(out rid) && rid < 0x2000) return true;
            return IsAppContainer();
        }

        public static bool TryGetIntegrityRid(out int rid)
        {
            rid = 0x2000;
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return false;
            try
            {
                int size;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
                if (size <= 0) return false;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int written;
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out written)) return false;
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    byte subAuthorities = Marshal.ReadByte(sid, 1);
                    rid = Marshal.ReadInt32(sid, 8 + (subAuthorities - 1) * 4);
                    return true;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }

        public static bool IsAppContainer()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return false;
            try
            {
                IntPtr buffer = Marshal.AllocHGlobal(4);
                try
                {
                    int written;
                    if (!GetTokenInformation(token, TokenIsAppContainer, buffer, 4, out written)) return false;
                    return Marshal.ReadInt32(buffer) != 0;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
    }
}
