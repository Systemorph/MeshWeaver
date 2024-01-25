// TODO V10: Follow up (25.01.2024, Alexander Yolokhov)

// using System.ComponentModel;
// using System.ComponentModel.DataAnnotations;
// using OpenSmc.Arithmetics.Aggregation;
// using OpenSmc.Arithmetics.Aggregation.Implementation;
// using OpenSmc.Arithmetics.MapOver;
// using OpenSmc.Scopes;
// using OpenSmc.Scopes.Operations;
// using OpenSmc.Scopes.Proxy;
// using IModuleInitialization = Systemorph.ServiceProvider.IModuleInitialization;
//
// [assembly: ModuleSetup]
// namespace OpenSmc.Scopes;
//
// public class ModuleSetup : Attribute, IModuleInitialization, IModuleRegistry
// {
//     /// <remarks>this must not be a constant in order for module ordering to work properly</remarks>
//     public static readonly string VariableName = "Scopes";
//
//     public void Register(IServiceCollection services)
//     {
//         services.AddSingleton<IScopeFactory, ScopeFactory>();
//         services.AddTransient<IInternalScopeFactory, InternalScopeFactory>();
//         services.AddSingleton<IScopeInterceptorFactoryRegistry, ScopeInterceptorFactoryRegistry>();
//     }
//
//     public void Initialize(IServiceProvider serviceProvider)
//     {
//         InitializeScopeInterceptors(serviceProvider);
//
//         InitializeArithmetics();
//
//         var scopeFactory = serviceProvider.GetRequiredService<IScopeFactory>();
//
//         var kernel = serviceProvider.GetService<IDotNetKernel>();
//         if (kernel != null)
//         {
//             kernel.AddUsings(typeof(ApplicabilityBuilder).Namespace,
//                 typeof(DimensionAttribute).Namespace,
//                 typeof(RangeAttribute).Namespace,
//                 typeof(IOrdered).Namespace,
//                 typeof(INamed).Namespace,
//                 typeof(IHierarchicalDimension).Namespace,
//                 typeof(DefaultValueAttribute).Namespace,
//                 typeof(NotVisibleAttribute).Namespace);
//         }
//
//         var sessionContext = serviceProvider.GetService<ISessionContext>();
//         if (sessionContext != null)
//         {
//             sessionContext.SetVariable(VariableName, scopeFactory, typeof(IScopeFactory));
//         }
//     }
//
//     private static void InitializeArithmetics()
//     {
//         MapOverFields.RegisterMapOverProvider(new IsScopeMapOverFunctionProvider(),
//             (convention, element) =>
//             {
//                 convention.Element(typeof(IsArrayFunctionProvider)).DependsOn(element);
//                 convention.Element(element).AtBeginning();
//                 convention.Element(element).Condition().IsFalse(type => type.IsScope())
//                     .Eliminates(element);
//             });
//
//         SumFunction.RegisterSumProvider(new IsScopeSumFunctionProvider(),
//             (convention, element) =>
//             {
//                 convention.Element(element).AtBeginning();
//                 convention.Element(element).Condition().IsFalse(x => x.IsScope()).Eliminates(element);
//             });
//
//         AggregationFunction.RegisterAggregationProvider(new IsScopeAggregationFunctionProvider(),
//             (convention, element) =>
//             {
//                 convention.Element(typeof(IsClassAggregationFunctionProvider)).DependsOn(element);
//                 convention.Element(element).DependsOn(typeof(IsValueTypeAggregationFunctionProvider));
//                 convention.Element(element).Condition().IsFalse(type => type.IsScope())
//                     .Eliminates(element);
//             });
//     }
//
//     private static void InitializeScopeInterceptors(IServiceProvider serviceProvider)
//     {
//         var scopeInterceptorFactoryRegistry = serviceProvider.GetRequiredService<IScopeInterceptorFactoryRegistry>();
//         scopeInterceptorFactoryRegistry.Register(new ApplicabilityInterceptorFactory())
//             .Register(new CachingInterceptorFactory())
//             .Register(new DelegateToInterfaceDefaultImplementationInterceptorFactory())
//             .Register(new FilterableScopeInterceptorFactory())
//             .Register(new ScopeRegistryInterceptorFactory());
//
//         ScopeInterceptorConventionService.Instance.Element(typeof(CachingInterceptor)).AtBeginning();
//         ScopeInterceptorConventionService.Instance.Element(typeof(DelegateToInterfaceDefaultImplementationInterceptor)).AtEnd();
//
//         ScopeInterceptorConventionService.Instance.Element(typeof(MutableScopeInterceptor)).AtBeginning();
//
//         ScopeInterceptorConventionService.Instance.Element(typeof(ScopeRegistryInterceptor)).AtBeginning();
//
//         ScopeInterceptorConventionService.Instance.Element(typeof(FilterableScopeInterceptor)).DependsOn(typeof(CachingInterceptor));
//         ScopeInterceptorConventionService.Instance.Element(typeof(DelegateToInterfaceDefaultImplementationInterceptor)).DependsOn(typeof(FilterableScopeInterceptor));
//
//         ScopeInterceptorConventionService.Instance.Element(typeof(FilterableScopeInterceptor.FilteringInterceptor)).DependsOn(typeof(CachingInterceptor));
//         ScopeInterceptorConventionService.Instance.Element(typeof(DelegateToInterfaceDefaultImplementationInterceptor)).DependsOn(typeof(FilterableScopeInterceptor.FilteringInterceptor));
//     }
// }