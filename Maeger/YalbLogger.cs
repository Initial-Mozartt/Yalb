using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yalb;

public static class YalbLogger

{
    public enum LogLevel
    {
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4
    }

    private static volatile LogLevel _minimumLevel = LogLevel.Debug;

    public static LogLevel MinimumLevel => _minimumLevel;

    public static void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    private static readonly object _recentLock = new();
    private static readonly int MaxInMemoryLines = 500;
    private static readonly int MaxFileBytes = 5 * 1024 * 1024;

    private static readonly Queue<string> _recentLines = new();

    private static readonly ConcurrentQueue<string> _writeQueue = new();
    private static readonly AutoResetEvent _queueEvent = new(false);
    private static readonly CancellationTokenSource _cts = new();

    private static readonly string _logDir;
    private static readonly string _logPath;

    static YalbLogger()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yalb");

        _logDir = Path.Combine(baseDir, "logs");

        Directory.CreateDirectory(_logDir);

        _logPath = Path.Combine(_logDir, $"yalb-{DateTime.Now:yyyy-MM-dd}.log");

        // Start background writer loop
        Task.Run(() => ProcessQueueLoopAsync(_cts.Token));
    }

    public static IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (_recentLock)
            {
                return _recentLines.ToArray();
            }
        }
    }

    public static void Debug(string message, string? context = null) => Enqueue("DEBUG", message, context);
    public static void Info(string message, string? context = null) => Enqueue("INFO", message, context);
    public static void Warn(string message, string? context = null) => Enqueue("WARN", message, context);
    public static void Error(string context, Exception? ex = null) => Enqueue("ERROR", context, ex);

    public static void Time(string label, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            sw.Stop();
            Info($"{label} took {sw.ElapsedMilliseconds}ms");
        }
    }

    public static void TimeAsync(string label, Func<System.Threading.Tasks.Task> action)
    {
        var sw = Stopwatch.StartNew();
        action().GetAwaiter().GetResult();
        sw.Stop();
        Info($"{label} took {sw.ElapsedMilliseconds}ms");
    }

    private static void Write(string level, string message, object? exOrContext)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        string ctx = exOrContext switch
        {
            null => "",
            Exception ex => $" | EX: {ex.GetType().Name}: {ex.Message} | {ex.StackTrace}",
            string s => s,
            _ => exOrContext.ToString() ?? ""
        };

        var line = $"[{timestamp}] [{level}] {message}{(string.IsNullOrWhiteSpace(ctx) ? "" : " [" + ctx + "]")}";
        EnqueueLine(line);
    }

    private static void Write(string level, string message, string? context)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] [{context ?? ""}] {message}";
        EnqueueLine(line);
    }

    private static void Enqueue(string level, string message, object? exOrContext)
    {
        // Level filtering
        var lvl = level switch
        {
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info
        };

        if (lvl < _minimumLevel) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        string ctx = exOrContext switch
        {
            null => "",
            Exception ex => $"EX: {ex.GetType().Name}: {ex.Message} | {ex.StackTrace}",
            string s => s,
            _ => exOrContext.ToString() ?? ""
        };

        var line = $"[{timestamp}] [{level}] {message}{(string.IsNullOrWhiteSpace(ctx) ? "" : " [" + ctx + "]")}";
        EnqueueLine(line);
    }

    private static void EnqueueLine(string line)
    {
        // Keep recent lines in memory quickly
        lock (_recentLock)
        {
            _recentLines.Enqueue(line);
            while (_recentLines.Count > MaxInMemoryLines)
                _recentLines.Dequeue();
        }

        _writeQueue.Enqueue(line + Environment.NewLine);
        _queueEvent.Set();
    }

    private static async Task ProcessQueueLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _queueEvent.WaitOne(1000);

                // Dequeue all available items
                if (_writeQueue.IsEmpty) continue;

                var sb = new StringBuilder();
                while (_writeQueue.TryDequeue(out var item))
                {
                    sb.Append(item);
                }

                try
                {
                    // Rotate if needed before writing
                    RotateIfNeeded();
                    File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
                }
                catch
                {
                    // best-effort; never throw from logger
                }
            }
        }
        catch
        {
            // swallow
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length >= MaxFileBytes)
            {
                var rotated = _logPath + ".1";
                if (File.Exists(rotated)) File.Delete(rotated);
                File.Move(_logPath, rotated);
            }
        }
        catch
        {
            // best-effort; never break app startup
        }
    }
}

