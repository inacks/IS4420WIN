// IS4420WIN Current Loop Receiver - entry point.
// Usage: IS4420WIN_<version>.exe [COMx]   (with a port name, it connects to that port at start)

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Inacks.IS4420Monitor
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static int errorsShown;

        [STAThread]
        static int Main(string[] args)
        {
            // Any error that escapes is shown and written to error.txt instead of
            // ending the program silently.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            try
            {
                bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
                if (windows)
                {
                    // Sharp text on scaled displays (125 %, 150 %...). The layout scales itself.
                    try { SetProcessDPIAware(); } catch (Exception) { }
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += OnThreadException;

                string port = null;
                foreach (string a in args)
                {
                    string t = a.Trim();
                    if (t.Length == 0) continue;
                    if (!windows && t.StartsWith("/dev/", StringComparison.Ordinal)) port = t;   // test runs on Linux
                    else if (t[0] != '-' && t[0] != '/') port = windows ? t.ToUpperInvariant() : t;
                }
                Application.Run(new MainForm(port));
                return 0;
            }
            catch (Exception ex)
            {
                ReportError(ex, true);
                return 1;
            }
        }

        static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ReportError(e.Exception, false);
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ReportError(e.ExceptionObject as Exception, true);
        }

        // Writes the error to error.txt, adds it to the log and shows it (at most twice).
        public static void ReportError(Exception ex, bool fatal)
        {
            string text = ex == null ? "Unknown error" : ex.ToString();
            string path = WriteErrorFile(text);
            try
            {
                MainForm form = MainForm.Instance;
                if (form != null && !fatal && ex != null) form.LogException(ex);
            }
            catch (Exception)
            {
            }
            if (Interlocked.Increment(ref errorsShown) > 2 && !fatal) return;
            try
            {
                string shown = text.Length > 1500 ? text.Substring(0, 1500) + " ..." : text;
                MessageBox.Show(
                    (fatal ? "IS4420 Monitor has to close because of this error:" : "IS4420 Monitor hit an error and keeps running:") +
                    "\r\n\r\n" + shown + (path != null ? "\r\n\r\nSaved in " + path : ""),
                    AppInfo.Title + " " + AppInfo.Version, MessageBoxButtons.OK,
                    fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
            }
        }

        static string WriteErrorFile(string text)
        {
            try
            {
                string dir = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "INACKS"), "IS4420WIN");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "error.txt");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + AppInfo.Version +
                    "  " + Environment.OSVersion + "  CLR " + Environment.Version + "\r\n" + text + "\r\n\r\n");
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
