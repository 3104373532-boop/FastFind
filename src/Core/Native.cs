using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FastFind
{
    /// <summary>Win32 interop used by the indexing engine (no UI dependencies).</summary>
    internal static class Native
    {
        public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
        public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_ARCHIVE = 0x00000020;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;

        public const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;
        public const int FIND_FIRST_EX_ON_DISK_ENTRIES_ONLY = 0x00000004;

        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_PATH_NOT_FOUND = 3;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_NO_MORE_FILES = 18;
        public const int ERROR_INVALID_PARAMETER = 87;

        public const int FindExInfoStandard = 0;
        public const int FindExInfoBasic = 1;
        public const int FindExSearchNameMatch = 0;

        public const int GetFileExInfoStandard = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr FindFirstFileExW(
            string lpFileName,
            int fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData,
            int fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationW(
            string lpRootPathName,
            StringBuilder lpVolumeNameBuffer,
            int nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer,
            int nFileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileAttributesExW(
            string lpFileName,
            int fInfoLevelId,
            out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        public static long FileTimeToUnixSeconds(uint low, uint high)
        {
            long ft = ((long)high << 32) | low;
            if (ft <= 0) return 0;
            return (ft - 116444736000000000L) / 10000000L;
        }

        public static bool TryGetVolumeSerial(string root, out uint serial)
        {
            uint maxComponent, fsFlags;
            var name = new StringBuilder(32);
            var fs = new StringBuilder(32);
            return GetVolumeInformationW(root, name, name.Capacity, out serial, out maxComponent, out fsFlags, fs, fs.Capacity);
        }

        // ---- storage bus type (to skip USB / removable volumes) ----

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const int StorageDeviceProperty = 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
            byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Returns the STORAGE_BUS_TYPE of the volume device, or -1 when unknown.</summary>
        public static int GetBusType(string root)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                string device = @"\\.\" + root.Substring(0, 2);
                h = CreateFileW(device, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == new IntPtr(-1) || h == IntPtr.Zero) return -1;
                var query = new byte[12];      // PropertyId + QueryType + 4 byte padding
                var output = new byte[1024];
                int returned;
                if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out returned, IntPtr.Zero))
                    return -1;
                if (returned < 32) return -1;
                return output[28];             // STORAGE_DEVICE_DESCRIPTOR.BusType
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }

        public static uint LastError { get { return (uint)Marshal.GetLastWin32Error(); } }
    }
}
