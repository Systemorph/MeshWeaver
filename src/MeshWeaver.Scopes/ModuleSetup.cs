using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.Arithmetics.Aggregation.Implementation;
using MeshWeaver.Arithmetics.MapOver;
using MeshWeaver.DataCubes;
using MeshWeaver.Scopes.Operations;
using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes;

public static class ModuleSetup
{
    public static IServiceCollection AddScopes(this IServiceCollection services)
    {
        InitializeArithmetics();
        DataCubesRegistry.RegisterDataCubeAggregationFunctionProvider();
        services.AddScoped<IScopeFactory, ScopeFactory>();
        services.AddTransient<IInternalScopeFactory, InternalScopeFactory>();
        services.AddScoped<IScopeInterceptorFactoryRegistry, ScopeInterceptorFactoryRegistry>();
        return services;
    }

    private static void InitializeArithmetics()
    {
        MapOverFields.RegisterMapOverProviderAfter<IsSupportedValueTypeFunctionProvider>(
            new IsScopeMapOverFunctionProvider(),
            type => type.IsScope()
        );

        SumFunction.RegisterSumProviderBefore<GenericSumFunctionProvider>(
            new IsScopeSumFunctionProvider(),
            x => x.IsScope()
        );

        AggregationFunction.RegisterAggregationProviderAfter<IsValueTypeAggregationFunctionProvider>(
            new IsScopeAggregationFunctionProvider(),
            type => type.IsScope()
        );
    }
}
