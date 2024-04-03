using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.Scopes.Operations;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes;

public static class ModuleSetup 
{
    public static IServiceCollection RegisterScopesAndArithmetics(this IServiceCollection services)
    {
        InitializeArithmetics();
        return services.RegisterScopes();
    }

    public static IServiceCollection RegisterScopes(this IServiceCollection services)
    {
        services.AddSingleton<IScopeFactory, ScopeFactory>();
        services.AddTransient<IInternalScopeFactory, InternalScopeFactory>();
        services.AddSingleton<IScopeInterceptorFactoryRegistry, ScopeInterceptorFactoryRegistry>();
        return services;
    }

    private static void InitializeArithmetics()
    {
        MapOverFields.RegisterMapOverProviderAfter<IsSupportedValueTypeFunctionProvider>(new IsScopeMapOverFunctionProvider(),
type => type.IsScope());

        SumFunction.RegisterSumProviderBefore<GenericSumFunctionProvider>(new IsScopeSumFunctionProvider(),
x => x.IsScope());

        AggregationFunction.RegisterAggregationProviderAfter<IsValueTypeAggregationFunctionProvider>(new IsScopeAggregationFunctionProvider(),
type => type.IsScope());
    }

}