using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public class MessageHubServiceProviderFactory(
    Func<IServiceProvider, IMessageHub> factory
) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services) => services;

    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        services.AddSingleton(factory);
        return services.CreateMeshWeaverServiceProvider();
    }
}
