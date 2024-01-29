using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes;
using OpenSmc.DataCubes.Operations;
using OpenSmc.ServiceProvider;

[assembly: ModuleSetup]
namespace OpenSmc.DataCubes;

public class ModuleSetup : Attribute, IModuleInitialization
{
    public void Initialize(IServiceProvider serviceProvider)
    {
        AggregationFunction.RegisterAggregationProvider(new IsDataCubeAggregationFunctionProvider(),
                                                        (convention, element) =>
                                                        {
                                                            convention.Element(typeof(IsClassAggregationFunctionProvider)).DependsOn(element);
                                                            convention.Element(element).DependsOn(typeof(IsValueTypeAggregationFunctionProvider));
                                                            convention.Element(element).Condition().IsFalse(type => type.IsDataCube())
                                                                      .Eliminates(element);
                                                        });

        MapOverFields.RegisterMapOverProvider(new IsDataCubeMapOverFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(typeof(IsEnumerableFunctionProvider)).DependsOn(element);
                                                  convention.Element(element).Condition().IsFalse(type => type.IsDataCube()).Eliminates(element);
                                              });

        SumFunction.RegisterSumProvider(new IsDataCubeSumFunctionProvider(),
                                        (convention, element) =>
                                        {
                                            convention.Element(element).AtBeginning();
                                            convention.Element(element).Condition().IsFalse(t => t.IsDataCube()).Eliminates(element);
                                        });
    }
}