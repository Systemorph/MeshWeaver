using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Default implementation of IDataChangeNotifier using Rx Subject.
/// Acts as a central event bus for data change notifications.
///
/// <para>Subscribers see the stream filtered through
/// <see cref="DataChangeNotificationExtensions.DistinctByPathVersion"/>: when the
/// same process both writes a row in-process AND receives the PG LISTEN/NOTIFY
/// echo of that write, both notifications carry the same
/// <c>(Path, Version)</c> and the echo is dropped. Notifications without a
/// version (in-memory adapters, file-system watcher, DELETE) pass through.</para>
/// </summary>
public class DataChangeNotifier : IDataChangeNotifier, IDisposable
{
    private readonly Subject<DataChangeNotification> _subject = new();
    private readonly IObservable<DataChangeNotification> _deduped;
    private bool _disposed;

    public DataChangeNotifier()
    {
        _deduped = _subject.DistinctByPathVersion();
    }

    /// <inheritdoc />
    public void NotifyChange(DataChangeNotification notification)
    {
        if (_disposed)
            return;

        _subject.OnNext(notification);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<DataChangeNotification> observer)
    {
        return _deduped.Subscribe(observer);
    }

    /// <summary>
    /// Disposes the notifier and completes the observable sequence.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subject.OnCompleted();
        _subject.Dispose();
    }
}
