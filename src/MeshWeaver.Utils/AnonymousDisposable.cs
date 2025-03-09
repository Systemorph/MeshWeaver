namespace MeshWeaver.Disposables;

public class AnonymousDisposable : IDisposable
{
    private readonly Action action;

    public AnonymousDisposable(Action action)
    {
        this.action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose()
    {
        action();
    }
}