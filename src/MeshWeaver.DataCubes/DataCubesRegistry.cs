using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.Arithmetics.Aggregation.Implementation;
using MeshWeaver.Arithmetics.MapOver;
using MeshWeaver.DataCubes.Operations;

namespace MeshWeaver.DataCubes;

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
