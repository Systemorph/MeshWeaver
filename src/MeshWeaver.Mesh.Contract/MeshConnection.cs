using System.Collections.Concurrent;

namespace MeshWeaver.Mesh;

public record MeshConnection(string AddressType, string Id) : IDisposable
{

    private readonly ConcurrentBag<Action> disposeActions = new();
    public ConnectionStatus Status { get; init; }

    public void WithDisposeAction(Action disposable)
    {
        disposeActions.Add(disposable);
    }

    public void Dispose()
    {
        while (disposeActions.TryTake(out var action))
            action.Invoke();
    }
}

public enum ConnectionStatus
{
    Connected,
    Disconnected
}
