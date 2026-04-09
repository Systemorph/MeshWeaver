using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

/// <summary>
/// In-process implementation of <see cref="IMeshChangeFeed"/>.
/// Uses Rx Subject for local pub/sub. Suitable for monolith mode.
/// In Orleans, <c>OrleansMeshChangeFeed</c> wraps this and adds
/// BroadcastChannel for cross-silo propagation.
/// </summary>
public class InProcessMeshChangeFeed : IMeshChangeFeed, IDisposable
{
    private readonly Subject<MeshChangeEvent> _subject = new();
    private bool _disposed;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
