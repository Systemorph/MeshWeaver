using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.Arithmetics.MapOver;
using OpenSmc.DataCubes;
using OpenSmc.DataCubes.Operations;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.DataCubes
{
    public class DataCubeScopeInterceptorFactory : IScopeInterceptorFactory
    {
        static DataCubeScopeInterceptorFactory()
        {
            AggregationFunction.RegisterAggregationProviderBefore<IsDataCubeAggregationFunctionProvider>(
                new IsDataCubeScopeAggregationFunctionProvider(),
                type => type.IsScope() && type.IsDataCube());

            MapOverFields.RegisterMapOverProviderBefore<IsDataCubeMapOverFunctionProvider>(new IsDataCubeScopeMapOverFunctionProvider(),
                type => type.IsDataCube());

        }

        public IEnumerable<IScopeInterceptor> GetInterceptors(Type scopeType, IInternalScopeFactory scopeFactory)
        {
            var dataCubeElement = scopeType.GetDataCubeElementType();
            var identityType = scopeType.GetIdentityType();
            if (dataCubeElement != null)
            {
                var interceptorType = typeof(DataCubeScopeInterceptor<,,>).MakeGenericType(scopeType, dataCubeElement, identityType);
                yield return (IScopeInterceptor)Activator.CreateInstance(interceptorType, scopeFactory.ScopeRegistry);
            }
        }
    }
}