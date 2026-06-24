using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Client.Services;

/// <summary>
/// Minimalistic, phone-friendly logging: appends to a single <b>size-capped rolling</b> file in app data
/// and keeps the last N lines in an in-memory ring buffer for an in-app diagnostics view. No external
/// dependencies; disk is bounded (one file + one rotation) and memory is bounded (the ring), so it's safe
/// on a phone. Registered as a singleton (an INSTANCE — no static state) so a Logs view can read
/// <see cref="Recent"/>; it dies with the app's DI container.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly long _maxBytes;
    private readonly LogLevel _min;
    private readonly int _ringSize;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _recent = new();

    public FileLoggerProvider(string filePath, LogLevel minLevel = LogLevel.Information,
        long maxBytes = 1_000_000, int ringSize = 500)
    {
        _filePath = filePath;
        _min = minLevel;
        _maxBytes = maxBytes;
        _ringSize = ringSize;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    /// <summary>The last in-memory log lines (oldest first) — bind an in-app diagnostics view to this.</summary>
    public IReadOnlyList<string> Recent => _recent.ToArray();

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _min;

    internal void Write(string line)
    {
        _recent.Enqueue(line);
        while (_recent.Count > _ringSize) _recent.TryDequeue(out _);

        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(_filePath);
                if (fi.Exists && fi.Length >= _maxBytes)
                {
                    // Keep exactly ONE previous file — total on-disk footprint stays ≤ 2× maxBytes.
                    var rolled = _filePath + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(_filePath, rolled);
                }
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never throw into the app. The line is still in the ring buffer.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => provider.IsEnabled(level);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var cat = category.Length > 24 ? category[^24..] : category;
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} {Abbrev(level)} {cat}: {formatter(state, ex)}";
            if (ex is not null) line += " | " + ex;
            provider.Write(line);
        }

        private static string Abbrev(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
        };
    }
}

public static class FileLoggerExtensions
{
    /// <summary>
    /// Adds the minimalistic on-device file logger and registers the same provider instance as a singleton
    /// so an in-app diagnostics view can read <see cref="FileLoggerProvider.Recent"/>.
    /// </summary>
    public static ILoggingBuilder AddDeviceFileLogger(
        this ILoggingBuilder logging, string filePath, LogLevel minLevel = LogLevel.Information)
    {
        var provider = new FileLoggerProvider(filePath, minLevel);
        logging.AddProvider(provider);
        logging.Services.AddSingleton(provider);
        return logging;
    }
}
