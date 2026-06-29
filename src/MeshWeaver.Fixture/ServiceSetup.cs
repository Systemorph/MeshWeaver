using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Builds and owns the test service provider: configuration, xUnit logging, and DI. Test base
/// classes and fixtures derive from this to share a common service-collection setup.
/// </summary>
public class ServiceSetup
{
    /// <summary>The service collection populated before the provider is built.</summary>
    public readonly ServiceCollection Services = CreateServiceCollection();
    /// <summary>Initialization callbacks invoked against the service provider once it is built.</summary>
    public readonly List<Action<IServiceProvider>> Initializations = new();
    /// <summary>The built service provider; available after <see cref="Initialize()"/> runs.</summary>
    public IServiceProvider ServiceProvider { get; protected set; } = null!;
    /// <summary>The application configuration resolved from the service provider.</summary>
    public IConfiguration Configuration { get; protected set; } = null!;

    /// <summary>
    /// Creates the base service collection with configuration (reload-on-change) and xUnit logging.
    /// </summary>
    /// <returns>The configured <see cref="ServiceCollection"/>.</returns>
    protected static ServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        
        // Add configuration. reloadOnChange: true so flipping a log level in
        // test/appsettings.json takes effect mid-run without restarting the
        // test host (handy for the "tail the log file while the suite is
        // running" hang-hunt workflow).
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        
        services.AddSingleton<TestOutputHelperAccessor>();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddXUnitLogger(); // Add xUnit logger for test output
        });
        services.AddOptions();
        return services;
    }

    /// <summary>
    /// Builds the service provider, resolves <see cref="Configuration"/>, and injects
    /// <c>[Inject]</c>-marked members on this instance.
    /// </summary>
    protected virtual void Initialize()
    {
        BuildServiceProvider();
        Configuration = ServiceProvider.GetRequiredService<IConfiguration>();
        ServiceProvider.Buildup(this);
    }

    /// <summary>
    /// Builds <see cref="ServiceProvider"/> from <see cref="Services"/> and runs the registered
    /// <see cref="Initializations"/> against it.
    /// </summary>
    protected virtual void BuildServiceProvider()
    {
        ServiceProvider = Services.CreateMeshWeaverServiceProvider();

        foreach (var initialize in Initializations)
            initialize(ServiceProvider);
    }

    internal void SetOutputHelper(ITestOutputHelper? output)
    {
        if (output != null)
            ServiceProvider.GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
    }

    /// <summary>
    /// Disposes the service provider so DI singletons (databases, file handles, background
    /// workers) release their resources, then clears the reference.
    /// </summary>
    public virtual void Dispose()
    {
        // Dispose the SP so Autofac's lifetime scope tears down + IDisposable
        // singletons (DBs, file handles, background workers) get to release
        // their resources. The Autofac Reflection.Emit factories themselves
        // remain pinned in the JIT code heap until the AppDomain unloads —
        // that is a separate problem that ShareMeshAcrossTests sidesteps by
        // building the SP once per test class instead of once per [Fact].
        (ServiceProvider as IDisposable)?.Dispose();
        ServiceProvider = null!;
    }
    /// <summary>
    /// Initializes the service provider and wires the given xUnit output helper into logging.
    /// </summary>
    /// <param name="output">The xUnit output helper for the running test.</param>
    protected void Initialize(ITestOutputHelper output)
    {
        Initialize();
        SetOutputHelper(output);
    }
}
