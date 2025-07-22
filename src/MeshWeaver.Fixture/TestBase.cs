using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        
        // Log test start marker to debug logs
        var logger = ServiceProvider?.GetService<ILogger<TestBase>>();
        if (logger != null)
        {
            var testMethod = GetCurrentTestMethodName();
            logger.LogInformation("=== TEST START: {TestMethod} ===", testMethod);
        }
        
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        // Log test end marker to debug logs
        var logger = ServiceProvider?.GetService<ILogger<TestBase>>();
        if (logger != null)
        {
            var testMethod = GetCurrentTestMethodName();
            logger.LogInformation("=== TEST END: {TestMethod} ===", testMethod);
        }
        
        SetOutputHelper(null);
        return ValueTask.CompletedTask;
    }
    
    private string GetCurrentTestMethodName()
    {
        // Get the test method name from the call stack
        var stackTrace = new System.Diagnostics.StackTrace();
        for (int i = 0; i < stackTrace.FrameCount; i++)
        {
            var frame = stackTrace.GetFrame(i);
            var method = frame?.GetMethod();
            if (method != null && method.GetCustomAttributes(typeof(Xunit.FactAttribute), false).Length > 0)
            {
                return $"{method.DeclaringType?.Name}.{method.Name}";
            }
            if (method != null && method.GetCustomAttributes(typeof(Xunit.TheoryAttribute), false).Length > 0)
            {
                return $"{method.DeclaringType?.Name}.{method.Name}";
            }
        }
        return "Unknown Test";
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


