using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture;

public class ServiceSetup
{
    public readonly ServiceCollection Services = new();
    public readonly List<Action<IServiceProvider>> Initializations = new();
    public IServiceProvider ServiceProvider { get; private set; }

    public ServiceSetup()
    {
        // Build configuration
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Configure logging
        Services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddXUnitLogger();
        });
        Services.AddOptions();
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

    internal void SetOutputHelper(ITestOutputHelper output)
    {
        if (output != null)
            ServiceProvider.GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
    }

    protected void Initialize(ITestOutputHelper output)
    {
        Initialize();
        SetOutputHelper(output);
    }
}
