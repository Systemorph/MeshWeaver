using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

public class ServiceSetup
{
    public readonly ServiceCollection Services = CreateServiceCollection();
    public readonly List<Action<IServiceProvider>> Initializations = new();
    public IServiceProvider ServiceProvider { get; private set; } = null!;

    protected static ServiceCollection CreateServiceCollection()
    {


        var services = new ServiceCollection();
        services.AddSingleton<TestOutputHelperAccessor>();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddXUnitLogger(); // Add xUnit logger for test output
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
