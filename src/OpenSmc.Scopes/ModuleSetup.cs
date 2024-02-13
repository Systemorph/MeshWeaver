using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.Scopes.Operations;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes;

public static class ModuleSetup 
{
    public static IServiceCollection RegisterScopes(this IServiceCollection services)
    {
        services.AddSingleton<IScopeFactory, ScopeFactory>();
        services.AddTransient<IInternalScopeFactory, InternalScopeFactory>();
        services.AddSingleton<IScopeInterceptorFactoryRegistry, ScopeInterceptorFactoryRegistry>();
        return services;
    }

    public static void Initialize(IServiceProvider serviceProvider)
    {

        InitializeArithmetics();


    }

    private static void InitializeArithmetics()
    {
        MapOverFields.RegisterMapOverProvider(new IsScopeMapOverFunctionProvider(),
            (convention, element) =>
            {
                convention.Element(typeof(IsArrayFunctionProvider)).DependsOn(element);
                convention.Element(element).AtBeginning();
                convention.Element(element).Condition().IsFalse(type => type.IsScope())
                    .Eliminates(element);
            });

        SumFunction.RegisterSumProvider(new IsScopeSumFunctionProvider(),
            (convention, element) =>
            {
                convention.Element(element).AtBeginning();
                convention.Element(element).Condition().IsFalse(x => x.IsScope()).Eliminates(element);
            });

        AggregationFunction.RegisterAggregationProvider(new IsScopeAggregationFunctionProvider(),
            (convention, element) =>
            {
                convention.Element(typeof(IsClassAggregationFunctionProvider)).DependsOn(element);
                convention.Element(element).DependsOn(typeof(IsValueTypeAggregationFunctionProvider));
                convention.Element(element).Condition().IsFalse(type => type.IsScope())
                    .Eliminates(element);
            });
    }

}