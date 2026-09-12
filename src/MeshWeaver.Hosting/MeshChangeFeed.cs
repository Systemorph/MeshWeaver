using System.Reactive.Subjects;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// In-process implementation of <see cref="IMeshChangeFeed"/>.
/// Uses an Rx Subject for local pub/sub and relays the storage adapter's process-local change
/// channel, so monolith and Orleans hosts receive committed changes made by every process that
/// shares their durable store. The legacy Orleans wrapper can still forward into this feed during
/// its additive retirement window.
/// </summary>
public class InProcessMeshChangeFeed : IMeshChangeFeed, IDisposable
{
    private readonly Subject<MeshChangeEvent> _subject = new();
    private readonly StorageChangeFeedRelay? _storageRelay;
    private bool _disposed;

    /// <summary>
    /// Creates a standalone in-process feed. Retained as a real parameterless constructor so
    /// already-compiled consumers keep their existing constructor binding.
    /// </summary>
    public InProcessMeshChangeFeed()
    {
    }

    /// <summary>
    /// Creates the mesh-scoped feed and attaches the storage-backed per-process relay.
    /// Dependency injection selects this constructor in a persistence-enabled host; direct
    /// standalone callers keep using <see cref="InProcessMeshChangeFeed()"/>.
    /// </summary>
    public InProcessMeshChangeFeed(
        IStorageAdapter storage,
        ILogger<InProcessMeshChangeFeed>? logger = null)
    {
        _storageRelay = new StorageChangeFeedRelay(storage, PublishLocal, logger);
    }

    /// <summary>
    /// Publishes a mesh change event to all local subscribers (no-op once disposed).
    /// </summary>
    /// <param name="change">The change event to publish.</param>
    public void Publish(MeshChangeEvent change)
    {
        if (!_disposed)
            _subject.OnNext(change);
    }

    /// <summary>
    /// Publishes locally without re-broadcasting to Orleans streams.
    /// Used by PathCacheInvalidatorGrain to relay cross-silo events
    /// to local subscribers without creating an infinite loop.
    /// </summary>
    public void PublishLocal(MeshChangeEvent change)
    {
        if (!_disposed)
            _subject.OnNext(change);
    }

    /// <summary>
    /// Subscribes a handler to mesh change events, optionally filtered by change kind.
    /// </summary>
    /// <param name="handler">The callback invoked for each matching change event.</param>
    /// <param name="filter">When set, only events of this kind are delivered; otherwise all events are delivered.</param>
    /// <returns>A disposable that ends the subscription when disposed.</returns>
    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
    {
        if (filter == null)
            return _subject.Subscribe(handler);

        var kind = filter.Value;
        return _subject.Subscribe(e =>
        {
            if (e.Kind == kind)
                handler(e);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storageRelay?.Dispose();
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
