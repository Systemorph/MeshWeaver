using Xunit.Abstractions;
using Xunit;

namespace MeshWeaver.Fixture;

public class TestBase : ServiceSetup, IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
    }

    public virtual Task InitializeAsync()
    {
        Initialize();
        SetOutputHelper(Output);
        return Task.CompletedTask;
    }

    public virtual Task DisposeAsync()
    {
        SetOutputHelper(null);
        return Task.CompletedTask;
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
    }
}


