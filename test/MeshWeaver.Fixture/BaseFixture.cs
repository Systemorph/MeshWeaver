using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// A reusable xUnit class/collection fixture that builds the shared service provider once
/// and exposes it to every test in the fixture's scope.
/// </summary>
public class BaseFixture : ServiceSetup, IAsyncLifetime
{
    /// <summary>
    /// Builds the service provider when the fixture is created.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public virtual ValueTask InitializeAsync()
    {
        Initialize();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Tears down the fixture when its scope ends. The base implementation does nothing.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}