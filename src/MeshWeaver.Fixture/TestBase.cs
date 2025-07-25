using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Base class for tests that automatically logs test method boundaries.
/// All test classes inheriting from this will have their test methods logged.
/// </summary>
[AutoTestLogging]
public class TestBase : ServiceSetup, IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;
    private static ILogger? _logger;

    static TestBase()
    {
        try
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddProvider(new DebugFileLoggerProvider());
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = loggerFactory.CreateLogger("TestBase");
        }
        catch
        {
            _logger = null;
        }
    }

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
    }

    public virtual ValueTask InitializeAsync()
    {
        Initialize();
        SetOutputHelper(Output);
        
        // Log test start with class info (individual test methods will be logged by AutoTestLoggingAttribute)
        var className = GetType().Name;
        _logger?.LogInformation("=== TEST CLASS INIT: {TestClass} ===", className);
        
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        // Log test end with class info
        var className = GetType().Name;
        _logger?.LogInformation("=== TEST CLASS DISPOSE: {TestClass} ===", className);
        
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


