namespace OpenSmc.Disposables;

/// <summary>
/// Thread unsafe collection of disposable objects. Please use it carefully.
/// </summary>
public class CompositeAsyncDisposable : IAsyncDisposable
{
    private bool isDisposed;

    private readonly Stack<object> disposables = new();

    public void Add(Action dispose)
    {
        Add(new AnonymousDisposable(dispose));
    }

    public void Add(IDisposable disposable)
    {
        if (disposable == null)
        {
            throw new ArgumentNullException(nameof(disposable));
        }

        if (isDisposed)
        {
            throw new InvalidOperationException("Already disposed");
        }

        disposables.Push(disposable);
    }

    public void Add(Func<Task> dispose)
    {
        Add(new AnonymousAsyncDisposable(dispose));
    }

    public void Add(IAsyncDisposable disposable)
    {
        if (disposable == null)
        {
            throw new ArgumentNullException(nameof(disposable));
        }

        if (isDisposed)
        {
            throw new InvalidOperationException("Already disposed");
        }

        disposables.Push(disposable);
    }

    public static CompositeAsyncDisposable operator +(CompositeAsyncDisposable container, IAsyncDisposable disposable)
    {
        container.Add(disposable);
        return container;
    }

    public static CompositeAsyncDisposable operator +(CompositeAsyncDisposable container, IDisposable disposable)
    {
        container.Add(disposable);
        return container;
    }

    public static CompositeAsyncDisposable operator +(CompositeAsyncDisposable container, Action dispose)
    {
        container.disposables.Push(new AnonymousDisposable(dispose));
        return container;
    }
    public static CompositeAsyncDisposable operator +(CompositeAsyncDisposable container, Func<Task> dispose)
    {
        container.disposables.Push(new AnonymousAsyncDisposable(dispose));
        return container;
    }

    public async ValueTask DisposeAsync()
    {
        isDisposed = true;

        while (disposables.Any())
        {
            var disposable = disposables.Pop();
            if (disposable is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                ((IDisposable)disposable).Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }
}