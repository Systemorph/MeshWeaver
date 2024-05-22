using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes;
using OpenSmc.Scopes.Operations;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes;

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
