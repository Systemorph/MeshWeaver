using Xunit;

namespace MeshWeaver.Fixture;

public class TestBase : ServiceSetup, IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
    }

    public virtual ValueTask InitializeAsync()
    {
        Initialize();
        SetOutputHelper(Output);
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        SetOutputHelper(null);
        return ValueTask.CompletedTask;
    }
}

public class TestBase<TFixture> : IDisposable
    where TFixture : BaseFixture, new()
{
    public TFixture Fixture { get; }

    protected TestBase(TFixture fixture, ITestOutputHelper output)
    {
        fixture.SetOutputHelper(output);
        Fixture = fixture;
    }

    public virtual void Dispose()
    {
        Fixture.SetOutputHelper(null);
        Fixture.Dispose();
    }
}


