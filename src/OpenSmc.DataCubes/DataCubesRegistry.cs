using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes.Operations;

namespace OpenSmc.DataCubes;

public static class DataCubesRegistry
{
    public static void RegisterDataCubeAggregationFunctionProvider()
    {
        AggregationFunction.RegisterAggregationProviderBefore<IsValueTypeAggregationFunctionProvider>(
            new IsDataCubeAggregationFunctionProvider(),
            type => type.IsDataCube()
        );

        MapOverFields.RegisterMapOverProviderBefore<IsSupportedValueTypeFunctionProvider>(
            new IsDataCubeMapOverFunctionProvider(),
            type => type.IsDataCube()
        );

        SumFunction.RegisterSumProviderBefore<GenericSumFunctionProvider>(
            new IsDataCubeSumFunctionProvider(),
            t => t.IsDataCube()
        );
    }
}
