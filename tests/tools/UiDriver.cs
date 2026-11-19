using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class UiDriver
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_COMMAND = 0x0111;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: UiDriver <pid> <copy|delete|open>"); return; }
        int pid = int.Parse(args[0]);
        string action = args[1].ToLowerInvariant();
        IntPtr main = Process.GetProcessById(pid).MainWindowHandle;
        IntPtr list = FindList(main);
        Console.WriteLine("list=" + list);
        if (list == IntPtr.Zero) return;

        Press(list, 0x28); // VK_DOWN selects the first row
        Thread.Sleep(400);

        if (action == "copy")
        {
            // Share keyboard state with the app UI thread so GetKeyState reports Ctrl down.
            uint pidOwner;
            uint appThread = GetWindowThreadProcessId(main, out pidOwner);
            uint myThread = GetCurrentThreadId();
            AttachThreadInput(myThread, appThread, true);
            var state = new byte[256];
            GetKeyboardState(state);
            state[0x11] = 0x80;
            SetKeyboardState(state);
            Thread.Sleep(120);
            PostMessage(list, WM_KEYDOWN, (IntPtr)0x43, IntPtr.Zero);
            Thread.Sleep(150);
            PostMessage(list, WM_KEYUP, (IntPtr)0x43, unchecked((IntPtr)0xC0000001));
            state[0x11] = 0;
            SetKeyboardState(state);
            AttachThreadInput(myThread, appThread, false);
            Thread.Sleep(800);
            try
            {
                string text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                Console.WriteLine("clipboard=" + text.Replace("\r", " ").Replace("\n", " "));
            }
            catch (Exception ex) { Console.WriteLine("clipboard-error=" + ex.Message); }
            return;
        }

        if (action == "delete")
        {
            Press(list, 0x2E); // VK_DELETE
            Thread.Sleep(1200);
            IntPtr dlg = FindWindow("#32770", "FastFind");
            if (dlg == IntPtr.Zero) dlg = FindByTitle("FastFind");
            Console.WriteLine("dialog=" + dlg);
            if (dlg != IntPtr.Zero)
            {
                PostMessage(dlg, WM_COMMAND, (IntPtr)1, IntPtr.Zero); // IDOK
                Thread.Sleep(1500);
            }
            return;
        }

        if (action == "open")
        {
            Press(list, 0x0D); // VK_RETURN
            Thread.Sleep(2500);
            Console.WriteLine("dialog=" + FindByTitle("FastFind"));
            Console.WriteLine("notepad=" + Process.GetProcessesByName("Notepad").Length);
            return;
        }
    }

    private static IntPtr FindByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (IsWindowVisible(h) && sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindList(IntPtr main)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(main, delegate(IntPtr h, IntPtr l)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, 128);
            if (sb.ToString().StartsWith("WindowsForms10.SysListView32")) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void Press(IntPtr h, int vk)
    {
        PostMessage(h, WM_KEYDOWN, (IntPtr)vk, (IntPtr)0x001C0001);
        Thread.Sleep(60);
        PostMessage(h, WM_KEYUP, (IntPtr)vk, unchecked((IntPtr)0xC01C0001));
        Thread.Sleep(150);
    }
}


