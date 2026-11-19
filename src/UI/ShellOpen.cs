using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FastFind
{
    /// <summary>
    /// Shell integration that never spawns explorer.exe: revealing uses the in-process
    /// SHOpenFolderAndSelectItems API, so a broken/blocked explorer.exe cannot turn a
    /// failed open into a second crash dialog on top of our own notice.
    /// </summary>
    internal static class ShellOpen
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        /// <summary>Returns null on success, otherwise a user facing reason.</summary>
        public static string TryOpen(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }

        /// <summary>Opens the "Open with" chooser through the shell verb (no helper process).</summary>
        public static string TryOpenWith(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { Verb = "openas", UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }

        /// <summary>Selects the item in its folder using the running shell (no new explorer.exe).</summary>
        public static string TryReveal(string path)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attrs;
                int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs);
                if (hr != 0 || pidl == IntPtr.Zero) return Hresult(hr);
                hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                return hr == 0 ? null : Hresult(hr);
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            }
        }

        private static string Hresult(int hr)
        {
            if (hr >= 0) return null;
            return "资源管理器定位失败（HRESULT 0x" + ((uint)hr).ToString("X8") + "）";
        }

        private static string Describe(Exception ex)
        {
            if (ex is System.ComponentModel.Win32Exception)
            {
                var w = (System.ComponentModel.Win32Exception)ex;
                return w.NativeErrorCode == 5
                    ? "系统拒绝了打开请求（Access denied，通常是权限受限或默认程序不可用）。"
                    : w.Message;
            }
            return ex == null ? "未知错误" : ex.Message;
        }
    }
}
