using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes;
using OpenSmc.DataCubes.Operations;
using OpenSmc.Messaging;
using OpenSmc.Scopes.Operations;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.DataCubes;

public static class ModuleSetup 
{
    public static MessageHubConfiguration AddScopesDataCubes(this MessageHubConfiguration configuration)
    {
        InitializeArithmetics();
        return configuration.WithBuildupAction(hub =>
            hub.ServiceProvider.InitializeDataCubesInterceptor());
    }

    public static void InitializeDataCubesInterceptor(this IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<IScopeInterceptorFactoryRegistry>()
            .RegisterBefore<ScopeRegistryInterceptorFactory>(new DataCubeScopeInterceptorFactory());

    public static void InitializeArithmetics()
    {
    }
}