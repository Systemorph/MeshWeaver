using System.Reflection;
using Castle.DynamicProxy;
using MeshWeaver.Arithmetics.Aggregation.Implementation;
using MeshWeaver.Collections;
using MeshWeaver.DataCubes;
using MeshWeaver.Reflection;
using MeshWeaver.Scopes.Operations;
using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes.DataCubes
{
    public class IsDataCubeScopeAggregationFunctionProvider : IAggregationFunctionProvider
    {
        public Delegate GetDelegate(Type elementType)
        {
            var scopeIdentityType = elementType.GetGenericArgumentTypes(typeof(IScope<>)).FirstOrDefault();
            if(scopeIdentityType == null)
                return (Delegate)CreateAggregateScopeMethod.MakeGenericMethod(elementType.RepeatOnce().Concat(elementType.GetDataCubeElementType().RepeatOnce()).ToArray()).Invoke(this, null);
            return (Delegate)CreateAggregateScopeWithIdentityMethod.MakeGenericMethod(elementType.RepeatOnce().Concat(elementType.GetDataCubeElementType().RepeatOnce()).Concat(scopeIdentityType.RepeatOnce()).ToArray()).Invoke(this, null);
        }

        // TODO: This is not good here. What about unloading stuff? Could be memory leak. Think how to inject in static context. (2021/05/03, Roland Buergi)
        private static readonly IProxyGenerator ProxyGenerator = new ProxyGenerator();
        private static readonly ProxyGenerationOptions Options = new() { Selector = new InterceptorSelector() };

        private interface IDummy : IDataCube<object>, IScope<object, object>
        {
        }

        private static readonly MethodInfo CreateAggregateScopeMethod = ReflectionHelper.GetMethodGeneric<IsDataCubeScopeAggregationFunctionProvider>(x => x.CreateAggregateScope<IDummy, object>());

        internal Func<IEnumerable<TScope>, TScope> CreateAggregateScope<TScope, TElement>()
            where TScope : class, IDataCube<TElement>, IScope
        {
            var additionalInterfaces = new[] { typeof(IFilterable<TScope>) };
            return scopes =>
                   {
                       var scopesCollection = scopes as ICollection<TScope> ?? scopes.ToArray();
                       return (TScope)ProxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(TScope), additionalInterfaces,
                                                                                       Options, new CachingInterceptor(), new AggregateDataCubeScopeInterceptor<TScope, TElement>(scopesCollection),
                                                                                       new AggregationInterceptor<TScope>(scopesCollection, CreateAggregateScope<TScope, TElement>()), new DelegateToInterfaceDefaultImplementationInterceptor());
                   };
        }
        private static readonly MethodInfo CreateAggregateScopeWithIdentityMethod = ReflectionHelper.GetMethodGeneric<IsDataCubeScopeAggregationFunctionProvider>(x => x.CreateAggregateScopeWithIdentity<IDummy, object, object>());

        internal Func<IEnumerable<TScope>, TScope> CreateAggregateScopeWithIdentity<TScope, TElement, TIdentity>()
            where TScope : class, IDataCube<TElement>, IScope<TIdentity>
        {
            var additionalInterfaces = new[] { typeof(IFilterable<TScope>) };
            return scopes =>
                   {
                       var scopesCollection = scopes as ICollection<TScope> ?? scopes.ToArray();
                       return (TScope)ProxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(TScope), additionalInterfaces,
                                                                                       Options, new CachingInterceptor(), new AggregateDataCubeScopeInterceptor<TScope, TElement>(scopesCollection),
                                                                                       new AggregationInterceptorWithIdentity<TScope, TIdentity>(scopesCollection, CreateAggregateScopeWithIdentity<TScope, TElement, TIdentity>()), new DelegateToInterfaceDefaultImplementationInterceptor());
                   };
        }

        private class AggregateDataCubeScopeInterceptor<TScope, TElement> : ScopeInterceptorBase
            where TScope : IDataCube<TElement>
        {
            private readonly ICollection<IDataCube<TElement>> scopes;
            private static readonly AspectPredicate[] AspectPredicates = { x => x.DeclaringType.IsDataCubeInterface() || x.DeclaringType.IsEnumerableInterface() };

            public override IEnumerable<AspectPredicate> Predicates => AspectPredicates;

            public AggregateDataCubeScopeInterceptor(ICollection<TScope> scopes)
            {
                this.scopes = scopes.Cast<IDataCube<TElement>>().ToArray();
            }

            public override void Intercept(IInvocation invocation)
            {
                if (invocation.Method.DeclaringType.IsEnumerableInterface())
                {
                    var enumerator = GetEnumerator();
                    invocation.ReturnValue = enumerator;
                    return;
                }

                if (invocation.Method.DeclaringType.IsDataCubeInterface())
                {
                    switch (invocation.Method.Name)
                    {
                        case nameof(IDataCube.GetSlices):
                            invocation.ReturnValue = GetSlices(invocation);
                            return;
                        case nameof(IDataCube.GetDimensionDescriptors):
                            invocation.ReturnValue = GetDimensionDescriptors(invocation);
                            return;
                        case nameof(IDataCube.Filter):
                            invocation.ReturnValue =
                                ((IFilterable)invocation.Proxy).Filter(((string, object)[])invocation.Arguments.First());
                            return;
                    }
                }


                invocation.Proceed();
            }

            private object GetEnumerator()
            {
                return scopes.SelectMany(x => x);
            }

            private object GetDimensionDescriptors(IInvocation invocation)
            {
                var isByRow = (bool)invocation.Arguments.First();
                var dimensions = (string[])invocation.Arguments.Skip(1).First();
                return scopes.SelectMany(s => s.GetDimensionDescriptors(isByRow, dimensions)).Distinct();
            }

            private object GetSlices(IInvocation invocation)
            {
                var dimensions = (string[])invocation.Arguments.First();
                return scopes.SelectMany(s => s.GetSlices(dimensions));
            }
        }
    }
}