using Xunit;

namespace MeshWeaver.Fixture;

public class BaseFixture : ServiceSetup, IAsyncLifetime
{
    public virtual ValueTask InitializeAsync()
    {
        Initialize();
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}