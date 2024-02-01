using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes;
using OpenSmc.DataCubes.Operations;
using OpenSmc.DotNet.Kernel;
using OpenSmc.Scopes.Operations;
using OpenSmc.Scopes.Proxy;
using IModuleInitialization = OpenSmc.ServiceProvider.IModuleInitialization;

[assembly: OpenSmc.Scopes.DataCubes.ModuleSetup]
namespace OpenSmc.Scopes.DataCubes;

public class ModuleSetup : Attribute, IModuleInitialization
{
    public void Initialize(IServiceProvider serviceProvider)
    {
        var scopeInterceptorFactoryRegistry = serviceProvider.GetRequiredService<IScopeInterceptorFactoryRegistry>();
        scopeInterceptorFactoryRegistry.Register(new DataCubeScopeInterceptorFactory());

        InitializeArithmetics();

        var kernel = serviceProvider.GetService<IDotNetKernel>();
        if (kernel != null)
        {
            kernel.AddUsingsStatic(typeof(DataCubeExtensions).FullName);

            kernel.AddUsings(typeof(IDataCube<>).Namespace);
        }

    }

    public static void InitializeArithmetics()
    {
        // it may not be the right place here, but no specific sum function is needed, and scopes have to win over data cubes. This is the project which knows about both data cubes and scopes
        SumFunctionConventionService.Instance.Element(typeof(IsDataCubeSumFunctionProvider)).DependsOn(typeof(IsScopeSumFunctionProvider));

        AggregationFunction.RegisterAggregationProvider(new IsDataCubeScopeAggregationFunctionProvider(),
                                                        (convention, element) =>
                                                        {
                                                            convention.Element(typeof(IsScopeAggregationFunctionProvider)).DependsOn(element);
                                                            convention.Element(typeof(IsDataCubeAggregationFunctionProvider)).DependsOn(element);
                                                            convention.Element(element).DependsOn(typeof(IsValueTypeAggregationFunctionProvider));
                                                            convention.Element(element).Condition().IsFalse(type => type.IsScope() && type.IsDataCube()).Eliminates(element);
                                                        });

        MapOverFields.RegisterMapOverProvider(new IsDataCubeScopeMapOverFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(typeof(IsScopeMapOverFunctionProvider)).DependsOn(element);
                                                  convention.Element(typeof(IsDataCubeMapOverFunctionProvider)).DependsOn(element);
                                                  convention.Element(element).Condition().IsFalse(type => type.IsScope() && type.IsDataCube()).Eliminates(element);
                                              });
    }
}