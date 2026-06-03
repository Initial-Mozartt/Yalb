using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace Yalb;

public class SplashForm : Form
{
    private WebView2 _webView;
    public bool SplashComplete { get; set; }

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(500, 350);
        MinimumSize = new Size(500, 350);
        BackColor = Color.FromArgb(10, 10, 10);
        ShowInTaskbar = false;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        Load += async (s, e) => await InitializeWebView();
    }

    private async Task InitializeWebView()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Yalb", "Maeger", "WebView2");

            var env = await CoreWebView2Environment.CreateAsync(
                null, userDataFolder, null);

            await _webView.EnsureCoreWebView2Async(env);

            var splashHtmlPath = Path.Combine(
                AppContext.BaseDirectory, "Splash", "Resources", "splash.html");

            if (File.Exists(splashHtmlPath))
            {
                _webView.Source = new Uri($"file:///{splashHtmlPath}");
            }
            else
            {
                _webView.NavigateToString("<html><body style='background:#0a0a0a;'></body></html>");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebView2 initialization error: {ex.Message}");
            BackColor = Color.FromArgb(10, 10, 10);
        }
    }
}
