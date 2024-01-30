using OpenSmc.DataCubes;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.DataCubes
{
    public class DataCubeScopeInterceptorFactory : IScopeInterceptorFactory
    {
        //static DataCubeScopeInterceptorFactory()
        //{
        //    AggregationFunctionConventionService.Instance.Element(typeof(IsScopeAggregationFunctionProvider)).DependsOn(typeof(DataCubes.Operations.IsDataCubeAggregationFunctionProvider));
        //    MapOverDelegateConventionService.Instance.Element(typeof(DataCubes.Operations.IsDataCubeMapOverFunctionProvider)).DependsOn(typeof(IsScopeMapOverFunctionProvider));

        //}

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