using System.Reflection;
using Castle.DynamicProxy;

namespace OpenSmc.Scopes.Proxy
{
    public class InterceptorSelector : IInterceptorSelector
    {
        //private readonly Dictionary<(Type, MethodInfo), IInterceptor[]> filterCache = new();
        public IInterceptor[] SelectInterceptors(Type type, MethodInfo method, IInterceptor[] interceptors)
        {
            //if (!filterCache.TryGetValue((type, method), out var ret))
            //ret = filterCache[(type, method)] =
            var ret = interceptors.Cast<IScopeInterceptor>()
                                  .Where(si => si.Predicates.Any(p => p(method))).Cast<IInterceptor>().ToArray();
            return ret;
        }
    }
}