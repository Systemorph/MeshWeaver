using System;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.ServiceProvider;
using OpenSmc.DataSource.Api;

[assembly: OpenSmc.Partition.ModuleSetup]

namespace OpenSmc.Partition;

public class ModuleSetup : Attribute, IModuleRegistry
{
    public void Register(IServiceCollection services)
    {
        services.AddTransient<IPartitionVariable, PartitionVariable>();
    }
}
