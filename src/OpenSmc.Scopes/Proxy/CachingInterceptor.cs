using System.Collections.Concurrent;
using System.Reflection;

namespace OpenSmc.Scopes.Proxy
{
    public class CachingInterceptorFactory : IScopeInterceptorFactory
    {
        public IEnumerable<IScopeInterceptor> GetInterceptors(Type tScope, IInternalScopeFactory scopeFactory)
        { 
            yield return typeof(IMutableScope).IsAssignableFrom(tScope) ? new MutableScopeInterceptor(scopeFactory.ScopeRegistry) : new CachingInterceptor();
        }

    }
    
    public class CachingInterceptor : ScopeInterceptorBase
    {
        protected readonly ConcurrentDictionary<(object instance, MethodInfo property), object> Cache = new();

        // ReSharper disable once InconsistentNaming
        private static readonly AspectPredicate[] predicates = { x => x.IsPropertyGetter() && !ScopeRegistryInterceptor.ScopeInterfaces.Contains(x.DeclaringType) };
        public override IEnumerable<AspectPredicate> Predicates => predicates;

        public override void Intercept(IInvocation invocation)
        {
            GetValue(invocation);
        }


        private ConcurrentDictionary<(object Scope, MethodInfo Method), object> lockers = new();
        protected virtual void GetValue(IInvocation invocation)
        {
            var key = (invocation.Proxy, invocation.Method);
            if (Cache.TryGetValue(key, out var cached))
            {
                invocation.ReturnValue = cached;
                return;
            }

            try
            {
                var locker = lockers.GetOrAdd(key, _ => new object());
                lock (locker)
                {
                    if (Cache.TryGetValue(key, out cached))
                    {
                        invocation.ReturnValue = cached;
                        return;
                    }

                    invocation.Proceed();
                    Cache[key] = invocation.ReturnValue;
                }

            }
            finally
            {
                lockers.Remove(key,out var _);
            }

        }
    }
}