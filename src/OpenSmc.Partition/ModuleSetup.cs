using Microsoft.Extensions.DependencyInjection;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.ServiceProvider;

[assembly: OpenSmc.Partition.ModuleSetup]

namespace OpenSmc.Partition;

public class ModuleSetup : Attribute, IModuleRegistry
{
    public void Register(IServiceCollection services)
    {
        services.AddTransient<IPartitionVariable, PartitionVariable>();
    }
}
