using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Default implementation of IDataChangeNotifier using Rx Subject.
/// Acts as a central event bus for data change notifications.
/// </summary>
public class DataChangeNotifier : IDataChangeNotifier, IDisposable
{
    private readonly Subject<DataChangeNotification> _subject = new();
    private bool _disposed;

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
        return _subject.Subscribe(observer);
    }

    /// <summary>
    /// Disposes the notifier and completes the observable sequence.
    /// Tolerates downstream subscribers whose own Subjects have already been
    /// disposed (race during ordered teardown: listener disposes its inner
    /// subjects before the notifier broadcasts OnCompleted).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _subject.OnCompleted(); } catch (ObjectDisposedException) { }
        _subject.Dispose();
    }
}
