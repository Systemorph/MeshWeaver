using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.ServiceProvider;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture;

public class ServiceSetup
{
    public readonly ServiceCollection Services = new();
    public readonly List<Action<IServiceProvider>> Initializations = new();
    public IServiceProvider ServiceProvider { get; private set; }

    public ServiceSetup()
    {
        Services.AddLogging(logging => logging.AddXUnitLogger());
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
