using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.Scopes.Operations;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes;

public static class ModuleSetup 
{
    /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
    public static readonly string VariableName = "Scopes";

    public static IServiceCollection RegisterScopes(this IServiceCollection services)
    {
        services.AddSingleton<IScopeFactory, ScopeFactory>();
        services.AddTransient<IInternalScopeFactory, InternalScopeFactory>();
        services.AddSingleton<IScopeInterceptorFactoryRegistry>(_ => CreateScopeInterceptorFactoryRegistry());
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

    private static IScopeInterceptorFactoryRegistry CreateScopeInterceptorFactoryRegistry()
    {
        var scopeInterceptorFactoryRegistry = new ScopeInterceptorFactoryRegistry();
        scopeInterceptorFactoryRegistry.Register(new ApplicabilityInterceptorFactory())
            .Register(new CachingInterceptorFactory())
            .Register(new DelegateToInterfaceDefaultImplementationInterceptorFactory())
            .Register(new FilterableScopeInterceptorFactory())
            .Register(new ScopeRegistryInterceptorFactory());

        ScopeInterceptorConventionService.Instance.Element(typeof(CachingInterceptor)).AtBeginning();
        ScopeInterceptorConventionService.Instance.Element(typeof(DelegateToInterfaceDefaultImplementationInterceptor)).AtEnd();

        ScopeInterceptorConventionService.Instance.Element(typeof(MutableScopeInterceptor)).AtBeginning();

        ScopeInterceptorConventionService.Instance.Element(typeof(ScopeRegistryInterceptor)).AtBeginning();

        ScopeInterceptorConventionService.Instance.Element(typeof(FilterableScopeInterceptor)).DependsOn(typeof(CachingInterceptor));
        ScopeInterceptorConventionService.Instance.Element(typeof(DelegateToInterfaceDefaultImplementationInterceptor)).DependsOn(typeof(FilterableScopeInterceptor));

        ScopeInterceptorConventionService.Instance.Element(typeof(FilterableScopeInterceptor.FilteringInterceptor)).DependsOn(typeof(CachingInterceptor));
        ScopeInterceptorConventionService.Instance.Element(typeof(DelegateToInterfaceDefaultImplementationInterceptor)).DependsOn(typeof(FilterableScopeInterceptor.FilteringInterceptor));
        return scopeInterceptorFactoryRegistry;
    }
}