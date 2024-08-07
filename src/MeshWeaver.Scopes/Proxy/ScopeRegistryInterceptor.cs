using Castle.DynamicProxy;
using MeshWeaver.Collections;
using MeshWeaver.Reflection;
using MeshWeaver.Scopes.DataCubes;

namespace MeshWeaver.Scopes.Proxy
{
    public class ScopeRegistryInterceptorFactory : IScopeInterceptorFactory
    {
        public IEnumerable<IScopeInterceptor> GetInterceptors(
            Type tScope,
            IInternalScopeFactory scopeFactory
        )
        {
            yield return new ScopeRegistryInterceptor(scopeFactory);
        }
    }

    public class ScopeRegistryInterceptor : ScopeInterceptorBase
    {
        private readonly IScopeRegistry scopeRegistry;
        private readonly IInternalScopeFactory scopeFactory;

        public ScopeRegistryInterceptor(IInternalScopeFactory scopeFactory)
        {
            this.scopeRegistry = scopeFactory.ScopeRegistry;
            this.scopeFactory = scopeFactory;
        }

        public override void Intercept(IInvocation invocation)
        {
            switch (invocation.Method.Name)
            {
                case nameof(IScope.GetScopeType):
                    invocation.ReturnValue = scopeRegistry.GetScopeType(invocation.Proxy);
                    break;
                case ReflectionHelper.GetterPrefix + nameof(IScope<object>.Identity):
                    invocation.ReturnValue = scopeRegistry.GetIdentity(invocation.Proxy);
                    break;
                case nameof(IScopeWithStorage<object>.GetStorage):
                    invocation.ReturnValue = scopeRegistry.GetStorage(invocation.Proxy);
                    break;
                case nameof(IScope.GetScopes):
                    var tScope = invocation.Method.GetGenericArguments().First();
                    var identities = invocation.Arguments[0];
                    var builder = invocation.Arguments[1];
                    invocation.ReturnValue = GetScopesMethod
                        .MakeGenericMethod(tScope)
                        .InvokeAsFunction(this, identities, builder, invocation.Proxy);
                    break;
                case nameof(IScope.GetScope):
                    tScope = invocation.Method.GetGenericArguments().First();
                    var identity = invocation.Arguments[0];
                    if (identity == null)
                    {
                        var tIdentity = tScope
                            .GetGenericArgumentTypes(typeof(IScope<>))
                            ?.FirstOrDefault();
                        if (tIdentity == null)
                            identity = IScopeRegistry.SingletonIdentity;
                        else
                            identity = scopeRegistry.GetIdentity(invocation.Proxy);
                    }
                    builder = invocation.Arguments[1];
                    invocation.ReturnValue = (
                        (IEnumerable<object>)
                            GetScopesMethod
                                .MakeGenericMethod(tScope)
                                .InvokeAsFunction(
                                    this,
                                    identity.RepeatOnce(),
                                    builder,
                                    invocation.Proxy
                                )
                    ).First();
                    break;
                case nameof(IScope.GetContext):
                    invocation.ReturnValue = scopeRegistry.GetContext(invocation.Proxy);
                    break;
                case nameof(IScope.GetGuid):
                    invocation.ReturnValue = scopeRegistry.GetGuid(invocation.Proxy);
                    break;
                case nameof(IDisposable.Dispose):
                    scopeRegistry.Dispose(invocation.Proxy);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        private static IGenericMethodCache GetScopesMethod =
            GenericCaches.GetMethodCache<ScopeRegistryInterceptor>(x =>
                x.GetScopes<object>(null, null, null)
            );

        // ReSharper disable once UnusedMethodReturnValue.Local
        private IList<TScope> GetScopes<TScope>(
            IEnumerable<object> identities,
            Func<ScopeBuilderForScope<TScope>, ScopeBuilderForScope<TScope>> options,
            object scope
        )
        {
            var ctx = scopeRegistry.GetContext(scope);
            var storage = scopeRegistry.GetStorage(scope);
            var builder = new ScopeBuilderForScope<TScope>(scopeFactory, identities, ctx, storage);
            if (options != null)
                builder = options(builder);
            return builder.ToScopes().ToArray();
        }

        internal static readonly HashSet<Type> ScopeInterfaces =
            new() { typeof(IScope<>), typeof(IScopeWithStorage<>) };

        private static readonly AspectPredicate[] SPredicates =

            // ReSharper disable once PossibleNullReferenceException
            {
                x =>
                    x.IsAbstract
                    && (
                        x.DeclaringType == typeof(IScope)
                        || x.DeclaringType == typeof(IDisposable)
                        || x.DeclaringType.IsGenericType
                            && ScopeInterfaces.Contains(x.DeclaringType.GetGenericTypeDefinition())
                    )
            };

        public override IEnumerable<AspectPredicate> Predicates => SPredicates;
    }
}
