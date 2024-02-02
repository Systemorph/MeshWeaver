using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.Reflection;
using IModuleInitialization = OpenSmc.ServiceProvider.IModuleInitialization;

[assembly: OpenSmc.Arithmetics.ModuleSetup]

namespace OpenSmc.Arithmetics;

public static class ArithmeticsRegistrationExtensions
{
    public static void RegisterProviders()
    {
        ModuleSetup.InitializeMapOver();
        ModuleSetup.InitializeAggregation();
        ModuleSetup.InitializeSum();
    }
}

public class ModuleSetup : Attribute, IModuleInitialization
{
    public void Initialize(IServiceProvider serviceProvider)
    {
        InitializeAggregation();
        InitializeSum();
        InitializeMapOver();
    }

    public static void InitializeMapOver()
    {
        MapOverFields.RegisterMapOverProvider(new IsSupportedValueTypeFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(element).AtBeginning();
                                                  convention.Element(element).Condition().IsFalse(type => type.IsRealType())
                                                            .Eliminates(element);
                                              });
        MapOverFields.RegisterMapOverProvider(new IsDictionaryFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(element).DependsOn(typeof(IsSupportedValueTypeFunctionProvider));
                                                  convention.Element(element).Condition().IsFalse(FormulaFrameworkTypeExtensions.IsDictionary)
                                                            .Eliminates(element);
                                              });
        MapOverFields.RegisterMapOverProvider(new IsClassHasParameterlessConstructorFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(element).DependsOn(typeof(IsDictionaryFunctionProvider));
                                                  convention.Element(element).Condition().IsFalse(type => type.IsClass && type.HasParameterlessConstructor() && !type.ImplementEnumerable())
                                                            .Eliminates(element);
                                              });
        MapOverFields.RegisterMapOverProvider(new IsArrayFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(element).DependsOn(typeof(IsClassHasParameterlessConstructorFunctionProvider));
                                                  convention.Element(element).Condition().IsFalse(type => type.IsArray)
                                                            .Eliminates(element);
                                              });
        MapOverFields.RegisterMapOverProvider(new IsListFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(element).DependsOn(typeof(IsArrayFunctionProvider));
                                                  convention.Element(element).Condition().IsFalse(type => type.IsList())
                                                            .Eliminates(element);
                                              });
        MapOverFields.RegisterMapOverProvider(new IsEnumerableFunctionProvider(),
                                              (convention, element) =>
                                              {
                                                  convention.Element(element).DependsOn(typeof(IsListFunctionProvider));
                                                  convention.Element(element).DependsOn(typeof(IsDictionaryFunctionProvider));
                                                  convention.Element(element).Condition()
                                                            .IsFalse(type => type != typeof(string) && type.IsEnumerable())
                                                            .Eliminates(element);
                                              });
    }

    public static void InitializeAggregation()
    {
        AggregationFunction.RegisterAggregationProvider(new IsValueTypeAggregationFunctionProvider(),
                                                        (convention, element) =>
                                                        {
                                                            convention.Element(element).AtBeginning();
                                                            convention.Element(element).Condition().IsFalse(type => type.IsValueType)
                                                                      .Eliminates(element);
                                                        });

        AggregationFunction.RegisterAggregationProvider(new IsClassAggregationFunctionProvider(),
                                                        (convention, element) =>
                                                        {
                                                            convention.Element(element).DependsOn(typeof(IsValueTypeAggregationFunctionProvider));
                                                            convention.Element(element).Condition().IsFalse(type => type.IsClass)
                                                                      .Eliminates(element);
                                                        });
    }

    public static void InitializeSum()
    {
        SumFunction.RegisterSumProvider(new GenericSumFunctionProvider(), (_, _) => { });
    }
}