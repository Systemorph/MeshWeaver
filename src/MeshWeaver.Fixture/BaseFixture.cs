using Xunit;

namespace MeshWeaver.Fixture;

public class BaseFixture : ServiceSetup, IAsyncLifetime
{
    public virtual Task InitializeAsync()
    {
        Initialize();
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}