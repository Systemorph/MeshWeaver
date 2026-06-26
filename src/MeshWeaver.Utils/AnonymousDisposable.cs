#nullable enable
namespace MeshWeaver.Utils;

/// <summary>
/// An <see cref="IDisposable"/> that runs a supplied <see cref="Action"/> when disposed.
/// Useful for turning arbitrary cleanup code into a disposable scope.
/// </summary>
/// <param name="action">The action to invoke on disposal. Must not be <c>null</c>.</param>
public class AnonymousDisposable(Action action) : IDisposable
{
    private readonly Action action = action ?? throw new ArgumentNullException(nameof(action));

    /// <summary>
    /// Invokes the supplied action.
    /// </summary>
    public void Dispose()
    {
        action();
    }
}
