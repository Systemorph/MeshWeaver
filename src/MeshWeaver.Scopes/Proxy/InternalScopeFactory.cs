using System.Linq.Expressions;
using System.Reflection;
using Castle.DynamicProxy;
using Microsoft.Extensions.Logging;
using MeshWeaver.Collections;
using MeshWeaver.Reflection;

namespace MeshWeaver.Scopes.Proxy
{
    public class InternalScopeFactory(
        ILogger<IScope> logger,
        IScopeInterceptorFactoryRegistry interceptorFactoryRegistry)
        : IInternalScopeFactory
    {
        private readonly ScopeRegistry scopeRegistry = new(logger);
        private readonly IProxyGenerator proxyGenerator = new ProxyGenerator();
        private readonly ProxyGenerationOptions options = new() { Selector = new InterceptorSelector() };
        public ILogger Logger { get; } = logger;


        public IEnumerable<object> CreateScopes(Type tScope, IEnumerable<object> identities, object storage, IEnumerable<IScopeInterceptor> additionalInterceptors, string context, bool registerMain)
        {
            var interceptors = interceptorFactoryRegistry.GetFactories()
                                                                  .SelectMany(f => f.GetInterceptors(tScope, this))
                                                                  .Concat(additionalInterceptors ?? Enumerable.Empty<IScopeInterceptor>())
                                                                  .Cast<IInterceptor>()
                                                                  .ToArray();

            var additionalInterfaces = interceptors.OfType<IHasAdditionalInterfaces>().SelectMany(i => i.GetAdditionalInterfaces(tScope))
                                                   .Except(tScope.GetInterfaces()).ToArray();

            identities ??= IScopeRegistry.SingletonIdentity.RepeatOnce();
            var initialization = GetInitialization(tScope);
            return identities.Select(i =>
                                     {
                                         var scope = proxyGenerator.CreateInterfaceProxyWithoutTarget(tScope, additionalInterfaces, options, interceptors);
                                         scopeRegistry.Register(tScope, scope, i, storage, context, registerMain);
                                         try
                                         {
                                             initialization?.Invoke(scope);
                                         }
                                         catch
                                         {
                                             scopeRegistry.Dispose(scope);
                                             throw;
                                         }
                                         
                                         return scope;
                                     }).ToArray();
        }

        private readonly Dictionary<Type, Action<object>> initializations = new();
        private Action<object> GetInitialization(Type tScope)
        {
            return initializations.GetOrAdd(tScope, CreateInitialization);
        }

        private Action<object> CreateInitialization(Type tScope)
        {
            var methods = tScope.RepeatOnce().Concat(tScope.GetInterfaces())
                                .Select(i =>
                                        {
                                            var att = i.GetCustomAttribute<InitializeScopeAttribute>();
                                            if (att == null)
                                                return null;
                                            return i.GetMethod(att.MethodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        })
                                .Where(x => x != null)
                                .ToArray();
            if (methods.Length == 0)
                return null;

            var prm = Expression.Parameter(typeof(object));
            var scope = Expression.Convert(prm, tScope);
            return Expression.Lambda<Action<object>>(Expression.Block(methods.Select(m => Expression.Call(scope, m))), prm).Compile();
        }

        IScopeRegistry IInternalScopeFactory.ScopeRegistry => scopeRegistry;

        public IEnumerable<object> GetOrCreate(Type tScope, IEnumerable<object> identities, object storage, string context = null, Delegate factory = null)
        {
            var tIdentity = tScope.GetGenericArgumentTypes(typeof(IScope<>))?.FirstOrDefault();
            Func<object, object> validate = tIdentity == null ? 
                                                x => x == IScopeRegistry.SingletonIdentity ? x : throw new ArgumentException($"Singleton scope {tScope.Name} was called with identity {x}")
                                                : x => tIdentity.IsInstanceOfType(x) ? x : throw new ArgumentException($"Wrong identity type {x?.GetType().Name} for Scope {tScope.Name}");
            foreach (var i in identities)
                yield return GetOrCreate(tScope, validate(i), storage, context, factory);
        }

        private readonly object locker = new();

        object GetOrCreate(Type tScope, object identity, object storage, string context, Delegate factory)
        {
            var cached = scopeRegistry.GetScope(tScope, identity, context);
            if(cached != null)
                return cached;

            lock (locker)
            {
                cached = scopeRegistry.GetScope(tScope, identity, context);
                if (cached != null)
                    return cached;
                if (factory != null)
                {
                    var scope = factory.DynamicInvoke();
                    scopeRegistry.Register(tScope, scope, identity, storage, context, true);
                    return scope;
                }
                return CreateScopes(tScope, (identity).RepeatOnce(), storage, null, context, true).First();
            }
        }

    }
}