using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Hosting;

internal class MessageHubServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        throw new NotImplementedException();
    }
}
