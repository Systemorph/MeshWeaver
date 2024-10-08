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


public sealed class FactWithWorkItemAttribute : FactAttribute
{
    public string WorkItemId { get; }
    public bool InProgress { get; }

    public FactWithWorkItemAttribute(string workItemId, bool inProgress = false)
    {
        WorkItemId = workItemId;
        InProgress = inProgress;

#if CIRun
        if(InProgress)
        {
            Skip = $"Test is skipped until work item #{workItemId} is resolved";
        }
#endif
    }
}

public sealed class TheoryWithWorkItemAttribute : TheoryAttribute
{
    public string WorkItemId { get; }
    public bool InProgress { get; }

    public TheoryWithWorkItemAttribute(string workItemId, bool inProgress = false)
    {
        WorkItemId = workItemId;
        InProgress = inProgress;

#if CIRun
        if(InProgress)
        {
            Skip = $"Test is skipped until work item #{workItemId} is resolved";
        }
#endif
    }
}