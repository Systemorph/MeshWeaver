namespace MeshWeaver.Utils;

public class AnonymousDisposable(Action action) : IDisposable
{
    private readonly Action action = action ?? throw new ArgumentNullException(nameof(action));

    public void Dispose()
    {
        action();
    }
}
