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
    /// <summary>The raw xUnit output helper for the running test.</summary>
    protected readonly ITestOutputHelper Output;
    /// <summary>The file-and-xUnit output helper that captures test output to both sinks.</summary>
    protected readonly XUnitFileOutputHelper FileOutput;
    private ILogger? _logger;

    /// <summary>
    /// Initializes a new test instance, creating the file output helper and wiring file logging.
    /// </summary>
    /// <param name="output">The xUnit output helper for the running test.</param>
    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
        FileOutput = new XUnitFileOutputHelper(output, GetType().Name);

        // Configure file logging integration. Log-level filters are bound
        // from appsettings.json in ServiceSetup (see ServiceSetup.CreateServiceCollection
        // → logging.AddConfiguration("Logging")). Flip the shared
        // test/appsettings.json's "Logging:LogLevel:MeshWeaver" to "Debug"
        // for hang-hunt runs.
        Services.AddSingleton(FileOutput);
        Services.AddLogging(logging =>
        {
            logging.ClearProviders(); // Remove default providers
            logging.Services.AddSingleton<ILoggerProvider>(serviceProvider =>
                new XUnitFileLoggerProvider(() => FileOutput, serviceProvider));
        });
        
    }

    /// <summary>
    /// Builds the service provider, registers this test for cross-class output access, and logs
    /// the test-class start marker.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
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
    /// Logs the test-class dispose marker, unregisters this test, and disposes the file output helper.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
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

        // NOTE: Earlier version called Dispose() here to release the Autofac
        // SP at end of every [Fact]. That broke 40+ tests across PathResolution,
        // Hosting.Monolith, FutuRe, Graph etc. — too many test classes hold
        // singleton handles that are read AFTER DisposeAsync (e.g. via test
        // fixture DI patterns or hub-disposal callbacks). Reverting until a
        // safer mechanism (per-class collectible AssemblyLoadContext, or
        // explicit opt-in flag) is in place.

        return ValueTask.CompletedTask;
    }

    

}

/// <summary>
/// Base class for tests that share a <typeparamref name="TFixture"/> across the test class,
/// routing each test's output through the shared fixture.
/// </summary>
/// <typeparam name="TFixture">The shared fixture type.</typeparam>
public class TestBase<TFixture> : IDisposable
    where TFixture : BaseFixture, new()
{
    /// <summary>The shared fixture for the test class.</summary>
    public TFixture Fixture { get; }

    /// <summary>
    /// Initializes a new test instance bound to the shared fixture and wires the test's output helper.
    /// </summary>
    /// <param name="fixture">The shared fixture instance.</param>
    /// <param name="output">The xUnit output helper for the running test.</param>
    protected TestBase(TFixture fixture, ITestOutputHelper output)
    {
        fixture.SetOutputHelper(output);
        Fixture = fixture;
    }

    /// <summary>
    /// Detaches this test's output helper from the shared fixture and disposes the fixture.
    /// </summary>
    public virtual void Dispose()
    {
        Fixture.SetOutputHelper(null);
        Fixture.Dispose();
    }
}


