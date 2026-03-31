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
