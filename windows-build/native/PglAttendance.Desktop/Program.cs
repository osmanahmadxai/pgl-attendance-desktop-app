using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using PglAttendance.Core;

namespace PglAttendance.Desktop;

internal static class Program
{
    public const string AppName = "PGL Attendance";
    private static Mutex? _single;

    [STAThread]
    private static void Main()
    {
        _single = new Mutex(true, "Global\\PGLAttendance.Desktop.SingleInstance", out var isOwner);
        if (!isOwner) return;

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Paths.EnsureDirs();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Paths.LogDir, "desktop-crash.log"),
                    $"[{DateTime.Now:O}] {ex}\n");
            }
            catch { /* ignore */ }
            MessageBox.Show("The app crashed: " + ex.Message, AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
