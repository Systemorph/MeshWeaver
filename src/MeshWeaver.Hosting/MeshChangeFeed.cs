using System.Reactive.Subjects;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// In-process implementation of <see cref="IMeshChangeFeed"/> and
/// <see cref="IMeshInvalidationFeed"/>. Logical post-commit events use one subject; direct
/// publishes additionally fan into the cache subject. The storage adapter's process-local change
/// channel reaches only the cache subject, so monolith and Orleans hosts invalidate commits made by
/// every process without duplicating logical side effects. The legacy Orleans wrapper can still
/// forward into both feeds during its additive retirement window.
/// </summary>
public class InProcessMeshChangeFeed : IMeshChangeFeed, IMeshInvalidationFeed, IDisposable
{
    private readonly Subject<MeshChangeEvent> _subject = new();
    private readonly Subject<MeshChangeEvent> _invalidations = new();
    private readonly StorageChangeFeedRelay? _storageRelay;
    private readonly ILogger? logger;
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
        this.logger = logger;
        _storageRelay = new StorageChangeFeedRelay(storage, PublishInvalidation, logger);
    }

    /// <summary>
    /// Publishes a mesh change event to all local subscribers (no-op once disposed).
    /// </summary>
    /// <param name="change">The change event to publish.</param>
    public void Publish(MeshChangeEvent change)
    {
        if (_disposed)
            return;
        _subject.OnNext(change);
        _invalidations.OnNext(change);
    }

    /// <summary>
    /// Publishes a cross-silo event to this process's invalidators without re-running logical
    /// consumers or re-broadcasting to Orleans streams. Retains the method's exact public
    /// signature for already-compiled <c>PathCacheInvalidatorGrain</c> callers.
    /// </summary>
    public void PublishLocal(MeshChangeEvent change)
    {
        PublishInvalidation(change);
    }

    /// <summary>
    /// Publishes only to process-local cache invalidators. Storage relays use this path so a
    /// database echo cannot re-run logical side effects such as mail or instance synchronization.
    /// </summary>
    internal void PublishInvalidation(MeshChangeEvent change)
    {
        if (!_disposed)
            _invalidations.OnNext(change);
    }

    /// <summary>
    /// Subscribes a handler to mesh change events, optionally filtered by change kind.
    /// </summary>
    /// <param name="handler">The callback invoked for each matching change event.</param>
    /// <param name="filter">When set, only events of this kind are delivered; otherwise all events are delivered.</param>
    /// <returns>A disposable that ends the subscription when disposed.</returns>
    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
        => Subscribe(_subject, handler, filter, null);

    IDisposable IMeshInvalidationFeed.Subscribe(
        Action<MeshChangeEvent> handler,
        MeshChangeKind? filter)
        => Subscribe(_invalidations, handler, filter, "invalidation");

    private IDisposable Subscribe(
        Subject<MeshChangeEvent> subject,
        Action<MeshChangeEvent> handler,
        MeshChangeKind? filter,
        string? channel)
    {
        return subject.Subscribe(e =>
        {
            if (filter is not null && e.Kind != filter.Value)
                return;
            // Preserve the logical feed's existing failure semantics: a logical consumer can be
            // part of a post-commit operation whose caller must see its failure. Invalidation is
            // idempotent process-local maintenance, so one bad cache must not starve the others.
            if (channel is null)
            {
                handler(e);
                return;
            }
            try
            {
                handler(e);
            }
            catch (Exception ex)
            {
                // One cache must not prevent the remaining process-local subscribers from seeing
                // this commit, nor tear down the feed for later events.
                logger?.LogError(ex,
                    "Mesh {Channel} feed subscriber failed for {Kind} {Path}; continuing",
                    channel, e.Kind, e.Path);
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storageRelay?.Dispose();
        _subject.OnCompleted();
        _invalidations.OnCompleted();
        _subject.Dispose();
        _invalidations.Dispose();
    }
}
