namespace OpenSmc.Disposables;

public class AnonymousAsyncDisposable : IAsyncDisposable
{
    private readonly Func<Task> action;

    public AnonymousAsyncDisposable(Func<Task> action)
    {
        this.action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public async ValueTask DisposeAsync()
    {
        await action();
    }
}