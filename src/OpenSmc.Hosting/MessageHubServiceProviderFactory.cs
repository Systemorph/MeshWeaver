using Microsoft.Extensions.DependencyInjection;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Hosting;

internal class MessageHubServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        var serviceProvider = services.SetupModules();
        return serviceProvider;
    }
}
