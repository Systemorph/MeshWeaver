using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.DataCubes;

public static class ModuleSetup 
{
    public static MessageHubConfiguration AddScopesDataCubes(this MessageHubConfiguration configuration)
    {
        return configuration.WithBuildupAction(hub =>
            hub.ServiceProvider.InitializeDataCubesInterceptor());
    }

    public static void InitializeDataCubesInterceptor(this IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<IScopeInterceptorFactoryRegistry>()
            .RegisterBefore<ScopeRegistryInterceptorFactory>(new DataCubeScopeInterceptorFactory());
}