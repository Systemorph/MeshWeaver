using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Base class for tests that uses xUnit output capturing with file logging.
/// All test output is captured to both xUnit test output and log files.
/// </summary>
[AutoTestLogging]
public class TestBase : ServiceSetup, IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;
    protected readonly XUnitFileOutputHelper FileOutput;
    private ILogger? _logger;

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
        FileOutput = new XUnitFileOutputHelper(output, GetType().Name);
        
        // Configure file logging integration
        Services.AddSingleton(FileOutput);
        Services.AddLogging(logging =>
        {
            logging.ClearProviders(); // Remove console and other default providers
            logging.AddProvider(new XUnitFileLoggerProvider(() => FileOutput));
        });
        
    }

    public virtual ValueTask InitializeAsync()
    {
        Initialize();
        SetOutputHelper(FileOutput);
        
        // Register this test instance for cross-class access
        XUnitFileOutputRegistry.Register(this, FileOutput);
        
        // Get logger after service provider is built
        _logger = ServiceProvider.GetService(typeof(ILogger<TestBase>)) as ILogger<TestBase>;
        
        
        // Log test start with class info
        var className = GetType().Name;
        FileOutput.WriteLine($"=== TEST CLASS INIT: {className} ===");
        _logger?.LogInformation("=== TEST CLASS INIT: {TestClass} ===", className);
        
        return ValueTask.CompletedTask;
    }




    public virtual ValueTask DisposeAsync()
    {
        // Log test end with class info
        var className = GetType().Name;
        FileOutput.WriteLine($"=== TEST CLASS DISPOSE: {className} ===");
        _logger?.LogInformation("=== TEST CLASS DISPOSE: {TestClass} ===", className);
        
        
        // Unregister this test instance
        XUnitFileOutputRegistry.Unregister(this);
        
        SetOutputHelper(null);
        FileOutput.Dispose();
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


