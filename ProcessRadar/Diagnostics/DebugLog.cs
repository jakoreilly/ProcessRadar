using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProcessRadar.Diagnostics;

/// <summary>
/// Dead-simple diagnostic log for chasing the two things this app gets wrong under load:
/// the UI freezing (ETW event storms, unbounded list growth, per-frame layout cost) and the
/// diagram distorting (DPI mismatch between WPF's DIP coordinates and Skia's pixel surface,
/// plus nodes jumping between frames when layout ordering isn't stable).
///
/// Writes to the debugger's Output window *and* a per-run file, because the interesting case -
/// the app wedged for two seconds during a live trace - is exactly the case where you aren't
/// sitting in the debugger. Fire-and-forget by design: logging must never be the thing that
/// blocks the UI thread, so a failed write is swallowed rather than thrown.
/// </summary>
public static class DebugLog
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static readonly Dictionary<string, DateTime> LastEmit = new();
    private static StreamWriter? _writer;
    private static bool _initialized;

    /// <summary>Master switch - flipped by the Debug toggle in the toolbar. Off means the
    /// formatting cost isn't paid either, so leaving log calls on hot paths is fine.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Where this run's log went, for the "Debug" toggle to show the user.</summary>
    public static string? LogFilePath { get; private set; }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);

    /// <summary>Logs at most once per <paramref name="every"/> for the given key. For events that
    /// are only interesting in aggregate (per-frame stats, per-ETW-event counters) and would
    /// otherwise turn the log itself into a performance problem.</summary>
    public static void Throttled(string key, TimeSpan every, string category, Func<string> message)
    {
        if (!Enabled)
            return;

        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastEmit.TryGetValue(key, out var last) && now - last < every)
                return;
            LastEmit[key] = now;
        }

        Write("INFO", category, message());
    }

    public static void Write(string level, string category, string message)
    {
        if (!Enabled)
            return;

        var line = $"{DateTime.Now:HH:mm:ss.fff} +{Uptime.Elapsed.TotalSeconds,8:F3}s [{level}] [{category,-10}] (t{Environment.CurrentManagedThreadId,3}) {message}";

        Debug.WriteLine(line);

        try
        {
            lock (Gate)
            {
                EnsureWriterLocked();
                _writer?.WriteLine(line);
            }
        }
        catch (IOException)
        {
            // A log that can't be written is not worth taking the app down for.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void EnsureWriterLocked()
    {
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProcessRadar", "logs");
            Directory.CreateDirectory(dir);

            LogFilePath = Path.Combine(dir, $"processradar-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(LogFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine($"=== Process Radar debug log - started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _writer = null;
            LogFilePath = null;
        }
    }
}
