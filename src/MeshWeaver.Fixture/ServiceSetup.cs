using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Xunit;

namespace MeshWeaver.Fixture;

public class ServiceSetup
{
    public readonly ServiceCollection Services = CreateServiceCollection();
    public readonly List<Action<IServiceProvider>> Initializations = new();
    public IServiceProvider ServiceProvider { get; private set; } = null!;

    protected static ServiceCollection CreateServiceCollection()
    {
        // Build configuration
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<TestOutputHelperAccessor>();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(Log.Logger, dispose: true);
        });
        services.AddOptions();
        return services;
    }

    protected virtual void Initialize()
    {
        BuildServiceProvider();
        ServiceProvider.Buildup(this);
    }

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

    public virtual void Dispose() => ServiceProvider = null!;
    protected void Initialize(ITestOutputHelper output)
    {
        Initialize();
        SetOutputHelper(output);
    }
}
