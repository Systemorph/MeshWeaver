using Microsoft.Extensions.DependencyInjection;
using OpenSmc.ServiceProvider;
using Xunit.Abstractions;

namespace OpenSmc.Fixture;

public class ServiceSetup
{
    public readonly ModulesBuilder Modules = new();
    public readonly ServiceCollection Services = new();
    public readonly List<Action<IServiceProvider>> Initializations = new();
    public IServiceProvider ServiceProvider { get; private set; }

    public ServiceSetup()
    {
        Modules.Add(GetType().Assembly);
        Services.AddLogging(logging => logging.AddXUnitLogger());
        Services.AddOptions();
    }

    public virtual void Initialize()
    {
        BuildServiceProvider();
        ServiceProvider.Buildup(this);
    }

    public virtual void BuildServiceProvider()
    {
        ServiceProvider = Services.SetupModules(Modules);

        foreach (var initialize in Initializations)
            initialize(ServiceProvider);
    }


    public void SetOutputHelper(ITestOutputHelper output)
    {
        if (output != null)
            ServiceProvider.GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
    }

    public void Initialize(ITestOutputHelper output)
    {
        Initialize();
        SetOutputHelper(output);
    }

}