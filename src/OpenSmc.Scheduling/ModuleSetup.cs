using System;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.ServiceProvider;

[assembly: OpenSmc.Scheduling.ModuleSetup]

namespace OpenSmc.Scheduling;

public class ModuleSetup : Attribute, IModuleRegistry
{
    public void Register(IServiceCollection services)
    {
        services.AddTransient<IDataScheduler, DataScheduler>();
    }
}
