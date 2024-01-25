using System.Reflection;
using OpenSmc.Arithmetics.Aggregation.Implementation;
using OpenSmc.Reflection;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.Operations
{
    public class IsScopeSumFunctionProvider : ISumFunctionProvider
    {
        public Delegate CreateSumFunctionWithResult(Type type)
        {
            return SumMethod.MakeGenericMethod(type).CreateDelegate(typeof(Func<,,>).MakeGenericType(type, type, type));
        }

        public Delegate CreateSumDelegate(Type type) => CreateSumFunctionWithResult(type);

        private static readonly MethodInfo SumMethod = ReflectionHelper.GetStaticMethodGeneric(() => Sum<object>(null, null));
        private static TScope Sum<TScope>(TScope x, TScope y) => x.RepeatOnce().Concat(y.RepeatOnce()).Aggregate();
    }

    public class IsScopeAggregationFunctionProvider : IAggregationFunctionProvider
    {
        public Delegate GetDelegate(Type elementType)
        {
            var identityType = elementType.GetGenericArgumentTypes(typeof(IScope<>))?.FirstOrDefault();
            if(identityType == null)
                return (Delegate)createAggregateScopeMethod.MakeGenericMethod(elementType).Invoke(this, null);
            return (Delegate)createAggregateScopeMethodWithIdentity.MakeGenericMethod(elementType, identityType).Invoke(this, null);
        }

        private readonly IProxyGenerator proxyGenerator = new ProxyGenerator();

        private readonly ProxyGenerationOptions options = new() { Selector = new InterceptorSelector() };

        private readonly MethodInfo createAggregateScopeMethod = typeof(IsScopeAggregationFunctionProvider).GetMethod(nameof(CreateAggregateScope), BindingFlags.Instance | BindingFlags.NonPublic);

        internal Func<IEnumerable<TScope>, TScope> CreateAggregateScope<TScope>()
            where TScope : class, IScope
        {
            var additionalInterfaces = new[] { typeof(IFilterable<TScope>) };
            return scopes =>
                       (TScope)proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(TScope), additionalInterfaces, options,
                                                                                new CachingInterceptor(),
                                                                                new AggregationInterceptor<TScope>(scopes.ToArray(), CreateAggregateScope<TScope>()),
                                                                                new DelegateToInterfaceDefaultImplementationInterceptor()
                                                                               );
        }
        private readonly MethodInfo createAggregateScopeMethodWithIdentity = typeof(IsScopeAggregationFunctionProvider).GetMethod(nameof(CreateAggregateScopeWithIdentity), BindingFlags.Instance | BindingFlags.NonPublic);

        internal Func<IEnumerable<TScope>, TScope> CreateAggregateScopeWithIdentity<TScope, TIdentity>()
            where TScope : class, IScope<TIdentity>
        {
            var additionalInterfaces = new[] { typeof(IFilterable<TScope>) };
            return scopes =>
                       (TScope)proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(TScope), additionalInterfaces, options,
                                                                                new CachingInterceptor(),
                                                                                new AggregationInterceptorWithIdentity<TScope, TIdentity>(scopes.ToArray(), CreateAggregateScopeWithIdentity<TScope, TIdentity>()),
                                                                                new DelegateToInterfaceDefaultImplementationInterceptor()
                                                                               );
        }
    }
}