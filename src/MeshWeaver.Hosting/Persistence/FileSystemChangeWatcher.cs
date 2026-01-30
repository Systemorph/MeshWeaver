using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Watches a directory for file system changes and publishes notifications to IDataChangeNotifier.
/// This enables reactive updates when files are modified externally (e.g., by another process or editor).
/// </summary>
public class FileSystemChangeWatcher : IDisposable
{
    private readonly string _baseDirectory;
    private readonly IStorageAdapter _storageAdapter;
    private readonly IDataChangeNotifier _changeNotifier;
    private readonly IMessageHub? _hub;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private readonly object _changeLock = new();
    private readonly Dictionary<string, (WatcherChangeTypes ChangeType, DateTime Timestamp)> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private JsonSerializerOptions JsonOptions => _jsonOptions ?? _hub!.JsonSerializerOptions;

    /// <summary>
    /// Debounce interval in milliseconds. Changes within this window are batched.
    /// </summary>
    public int DebounceIntervalMs { get; init; } = 100;

    /// <summary>
    /// Supported file extensions to watch.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".json", ".md", ".cs"];

    /// <summary>
    /// Creates a FileSystemChangeWatcher with options from the hub.
    /// </summary>
    public FileSystemChangeWatcher(
        string baseDirectory,
        IStorageAdapter storageAdapter,
        IDataChangeNotifier changeNotifier,
        IMessageHub hub)
        : this(baseDirectory, storageAdapter, changeNotifier)
    {
        _hub = hub;
    }

    /// <summary>
    /// Creates a FileSystemChangeWatcher with explicit options (for testing).
    /// </summary>
    public FileSystemChangeWatcher(
        string baseDirectory,
        IStorageAdapter storageAdapter,
        IDataChangeNotifier changeNotifier,
        JsonSerializerOptions jsonOptions)
        : this(baseDirectory, storageAdapter, changeNotifier)
    {
        _jsonOptions = jsonOptions;
    }

    private FileSystemChangeWatcher(
        string baseDirectory,
        IStorageAdapter storageAdapter,
        IDataChangeNotifier changeNotifier)
    {
        _baseDirectory = baseDirectory;
        _storageAdapter = storageAdapter;
        _changeNotifier = changeNotifier;

        _watcher = new FileSystemWatcher(baseDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = true,
            EnableRaisingEvents = false
        };

        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;

        _debounceTimer = new Timer(ProcessPendingChanges, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Starts watching for file system changes.
    /// </summary>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FileSystemChangeWatcher));

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Stops watching for file system changes.
    /// </summary>
    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedFile(e.FullPath))
            return;

        lock (_changeLock)
        {
            var path = GetNodePath(e.FullPath);
            if (path == null)
                return;

            _pendingChanges[path] = (e.ChangeType, DateTime.UtcNow);
            _debounceTimer.Change(DebounceIntervalMs, Timeout.Infinite);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Handle rename as delete + create
        if (IsSupportedFile(e.OldFullPath))
        {
            lock (_changeLock)
            {
                var oldPath = GetNodePath(e.OldFullPath);
                if (oldPath != null)
                {
                    _pendingChanges[oldPath] = (WatcherChangeTypes.Deleted, DateTime.UtcNow);
                }
            }
        }

        if (IsSupportedFile(e.FullPath))
        {
            lock (_changeLock)
            {
                var newPath = GetNodePath(e.FullPath);
                if (newPath != null)
                {
                    _pendingChanges[newPath] = (WatcherChangeTypes.Created, DateTime.UtcNow);
                }
                _debounceTimer.Change(DebounceIntervalMs, Timeout.Infinite);
            }
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Log error but continue watching
        // In production, consider implementing retry logic
    }

    private async void ProcessPendingChanges(object? state)
    {
        Dictionary<string, WatcherChangeTypes> changesToProcess;

        lock (_changeLock)
        {
            if (_pendingChanges.Count == 0)
                return;

            changesToProcess = _pendingChanges.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ChangeType,
                StringComparer.OrdinalIgnoreCase);
            _pendingChanges.Clear();
        }

        foreach (var (path, changeType) in changesToProcess)
        {
            try
            {
                await ProcessChangeAsync(path, changeType);
            }
            catch
            {
                // Swallow exceptions to prevent crashes in the timer callback
            }
        }
    }

    private async Task ProcessChangeAsync(string path, WatcherChangeTypes changeType)
    {
        var normalizedPath = NormalizePath(path);

        switch (changeType)
        {
            case WatcherChangeTypes.Created:
                var createdNode = await _storageAdapter.ReadAsync(path, JsonOptions);
                if (createdNode != null)
                {
                    _changeNotifier.NotifyChange(DataChangeNotification.Created(normalizedPath, createdNode));
                }
                break;

            case WatcherChangeTypes.Changed:
                var updatedNode = await _storageAdapter.ReadAsync(path, JsonOptions);
                if (updatedNode != null)
                {
                    _changeNotifier.NotifyChange(DataChangeNotification.Updated(normalizedPath, updatedNode));
                }
                break;

            case WatcherChangeTypes.Deleted:
                _changeNotifier.NotifyChange(DataChangeNotification.Deleted(normalizedPath));
                break;
        }
    }

    private bool IsSupportedFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SupportedExtensions.Any(ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    private string? GetNodePath(string filePath)
    {
        // Convert file path to node path
        // e.g., "C:\data\graph\org1.json" -> "graph/org1"
        if (!filePath.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
            return null;

        var relativePath = filePath[_baseDirectory.Length..].TrimStart(Path.DirectorySeparatorChar, '/');

        // Remove extension
        foreach (var ext in SupportedExtensions)
        {
            if (relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath[..^ext.Length];
                break;
            }
        }

        // Normalize path separators
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _debounceTimer.Dispose();
        _watcher.Dispose();
    }
}
