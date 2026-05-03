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
            logging.Services.AddSingleton<ILoggerProvider>(serviceProvider => 
                new XUnitFileLoggerProvider(() => FileOutput, serviceProvider));
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




    /// <summary>
    /// When <c>false</c>, <see cref="DisposeAsync"/> does NOT dispose the SP —
    /// the test class is sharing a SP across [Fact]s (see
    /// <c>MonolithMeshTestBase.ShareMeshAcrossTests</c>) and disposing it on the
    /// first [Fact]'s teardown would break every subsequent [Fact] that re-uses
    /// the cached SP. Default <c>true</c> = each [Fact] gets its own SP and
    /// each disposal also disposes that SP.
    /// </summary>
    protected virtual bool DisposeServiceProviderOnTestEnd => true;

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

        // Dispose the SP for non-shared test classes — releases Autofac
        // lifetime scope + IDisposable singletons. Without this, every
        // per-[Fact] SP we built in InitializeAsync stays rooted via the
        // field until garbage collection, and any database/file/network
        // handles registered as singletons never get to run their cleanup
        // code. Shared-SP classes opt out via DisposeServiceProviderOnTestEnd
        // — the SP outlives the test instance and is reclaimed at process exit.
        if (DisposeServiceProviderOnTestEnd)
        {
            Dispose();
        }

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


