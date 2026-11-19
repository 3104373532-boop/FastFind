using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FastFind
{
    internal static class App
    {
        private const string MutexName = "Global\\FastFind.SingleInstance.7B1C";
        private const string ShowEventName = "Global\\FastFind.Show.7B1C";

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExistingInstance();
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnDomainException;

                if (args.Length > 0 && args[0] == "--version")
                {
                    Console.WriteLine("FastFind " + typeof(App).Assembly.GetName().Version);
                    return;
                }

                string initialQuery = null;
                bool openSettings = false;
                bool toggleTheme = false;
                bool diag = false;
                string toggleVolume = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--search" && i + 1 < args.Length) initialQuery = args[i + 1];
                    if (args[i] == "--settings") openSettings = true;
                    if (args[i] == "--toggle-theme") toggleTheme = true;
                    if (args[i] == "--diag") diag = true;
                    if (args[i] == "--toggle-volume" && i + 1 < args.Length) toggleVolume = args[i + 1];
                }
                MainForm.DiagEnabled = diag;
                Settings settings = Settings.Load();
                using (var form = new MainForm(settings, initialQuery, openSettings, toggleTheme, toggleVolume))
                {
                    StartShowListener(form);
                    Application.Run(form);
                }
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out handle))
                {
                    using (handle) handle.Set();
                }
            }
            catch (Exception) { }
        }

        private static void StartShowListener(MainForm form)
        {
            var thread = new Thread(delegate()
            {
                try
                {
                    using (var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                    {
                        while (true)
                        {
                            handle.WaitOne();
                            if (form.IsDisposed) return;
                            try
                            {
                                form.BeginInvoke(new MethodInvoker(delegate
                                {
                                    if (!form.IsDisposed) form.ShowWindow();
                                }));
                            }
                            catch (Exception) { return; }
                        }
                    }
                }
                catch (Exception) { }
            });
            thread.IsBackground = true;
            thread.Name = "FastFind.ShowListener";
            thread.Start();
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ReportError(e.Exception);
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            ReportError(e.ExceptionObject as Exception);
        }

        private static void ReportError(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
            try
            {
                MessageBox.Show("发生错误：" + ex.Message + Environment.NewLine + "详情见 data\\error.log",
                    "FastFind", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }
    }
}




