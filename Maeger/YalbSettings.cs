using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Yalb;

public class YalbSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Yalb", "settings.json");

    // --- Session ---
    public List<TabSession> LastSessionTabs { get; set; } = new();
    public int ActiveTabIndex { get; set; } = 0;
    public bool RestoreLastSession { get; set; } = true;

    // --- Bookmarks / shortcuts ---
    public List<BookmarkItem> Bookmarks { get; set; } = new();
    public List<ShortcutItem> SidebarShortcuts { get; set; } = new();
    public List<ShortcutItem> HomeShortcuts { get; set; } = new()
    {
        new ShortcutItem { Title = "GitHub", Url = "https://github.com/" },
        new ShortcutItem { Title = "Google", Url = "https://www.google.com/" },
        new ShortcutItem { Title = "YouTube", Url = "https://www.youtube.com/" },
        new ShortcutItem { Title = "Reddit", Url = "https://www.reddit.com/" }
    };

    // --- History ---
    public List<HistoryEntry> History { get; set; } = new();
    public int MaxHistoryEntries { get; set; } = 500;

    // --- Preferences ---
    public string HomePageUrl { get; set; } = "https://origin.mozartt.workers.dev/";
    public string SearchEngineUrl { get; set; } = "https://www.google.com/search?q={0}";
    public bool FramelessWindow { get; set; } = true;
    public bool ChromeVisible { get; set; } = true;
    // Show the splash screen on startup
    public bool ShowSplashOnStartup { get; set; } = true;
    public bool ZenModeActive { get; set; } = false;
    public bool ShowBookmarkBar { get; set; } = false;
    public bool ShowSidebar { get; set; } = true;
    public int SidebarWidth { get; set; } = 42;
    public int SidebarExpandedWidth { get; set; } = 180;
    public bool SidebarShowLabels { get; set; } = false;
    public string TabPosition { get; set; } = "top";
    public bool FloatingAddressBar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = false;
    public string Theme { get; set; } = "dark";
    public bool HardwareAcceleration { get; set; } = true;
    public bool BlockTrackers { get; set; } = true;
    public bool BlockAds { get; set; } = false;
    public string DownloadPath { get; set; } = string.Empty;
    public bool AskDownloadLocation { get; set; } = true;

    // --- Downloads ---
    public List<DownloadEntry> Downloads { get; set; } = new();

    // --- Singleton ---
    private static YalbSettings? _instance;
    public static YalbSettings Instance => _instance ??= Load();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(SettingsPath, json);
    }

    private static YalbSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new YalbSettings();
        var json = File.ReadAllText(SettingsPath);
        try
        {
            return JsonSerializer.Deserialize<YalbSettings>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) ?? new YalbSettings();
        }
        catch
        {
            return new YalbSettings();
        }
    }

    public void AddHistoryEntry(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        History.RemoveAll(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, new HistoryEntry { Url = url, Title = title, VisitedAt = DateTime.UtcNow });
        while (History.Count > MaxHistoryEntries) History.RemoveAt(History.Count - 1);
        Save();
    }

    public void RecordSession(List<TabSession> tabs, int activeIndex = 0)
    {
        LastSessionTabs = tabs ?? new List<TabSession>();
        ActiveTabIndex = activeIndex;
        Save();
    }
}

public class TabSession
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Pinned { get; set; } = false;
}

public class ShortcutItem
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public class BookmarkItem
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class HistoryEntry
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime VisitedAt { get; set; }
}

public class DownloadEntry
{
    public string Url { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Starting";
}
