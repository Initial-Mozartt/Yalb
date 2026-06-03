using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Yalb;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Startup banner for easier timing / health checks
        try { YalbLogger.Info("=== YALB INIT START ==="); } catch { }
        string mutexName = "YalbBrowser_SingleInstance_Mutex";
        using var mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Yalb is already running.", "Yalb", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settings = YalbSettings.Instance;
        // Start local startpage server (serves startpage/dist on http://localhost:3000)
        try
        {
            var startpagePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startpage", "dist"));
            if (Directory.Exists(startpagePath))
            {
                StartpageServer.Start(startpagePath);
                YalbLogger.Info($"Startpage server started at {StartpageServer.BaseUrl}", nameof(Program));
            }
            else
            {
                YalbLogger.Info($"Startpage dist not found at {startpagePath}; server not started.", nameof(Program));
            }

            if (settings.ShowSplashOnStartup)
            {
                using (var splash = new SplashForm())
                {
                    splash.Show();
                    Application.DoEvents();
                    Application.Run(new BrowserForm(splash));
                }
            }
            else
            {
                Application.Run(new BrowserForm());
            }
        }
        finally
        {
            // Ensure server is stopped even if application Run throws
            try
            {
                if (StartpageServer.IsRunning)
                {
                    StartpageServer.Stop();
                    YalbLogger.Info("Startpage server stopped", nameof(Program));
                }
            }
            catch { }
        }
        
    }
}
