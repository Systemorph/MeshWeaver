using System.Reflection;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.Operations
{
    public class FilterableScopeInterceptorFactory : IScopeInterceptorFactory
    {
        private readonly CreatableObjectStore<Type, Dictionary<MethodInfo, Dictionary<string, FilterPart[]>>>
            filterParts = new(CreateFilterParts);


        public IEnumerable<IScopeInterceptor> GetInterceptors(Type tScope, IInternalScopeFactory scopeFactory)
        {
            var dict = filterParts.GetInstance(tScope);
            if (dict.Count > 0 || tScope.IsFilterable())
                yield return new FilterableScopeInterceptor(dict, scopeFactory);
        }

        private static Dictionary<MethodInfo, Dictionary<string, FilterPart[]>> CreateFilterParts(Type @interface)
        {
            return @interface.RepeatOnce().Concat(@interface.GetInterfaces()).SelectMany(GetFilterParts).GroupBy(x => x.Method)
                             .ToDictionary(x => x.Key, x => x.GroupBy(y => y.Name).ToDictionary(y => y.Key, y => y.ToArray()));
        }

        private static IEnumerable<FilterPart> GetFilterParts(Type @interface)

        {
            var methods = @interface.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                    .Where(m => typeof(FilterBuilder).IsAssignableFrom(m.ReturnType) &&
                                                m.GetParameters().Length == 1 &&
                                                typeof(FilterBuilder).IsAssignableFrom(m.GetParameters()[0].ParameterType));
            var builder = new FilterBuilder();
            return methods.SelectMany(m => ((FilterBuilder)m.Invoke(null, new object[] { builder })).Parts);
        }
    }

    public class FilterableScopeInterceptor : ScopeInterceptorBase, IHasAdditionalInterfaces
    {
        private readonly IInternalScopeFactory scopeFactory;
        protected static readonly AspectPredicate[] AspectPredicates = { x => x.DeclaringType.IsFilterableInterface() };
        public override IEnumerable<AspectPredicate> Predicates => AspectPredicates;

        private readonly Dictionary<MethodInfo, Dictionary<string, FilterPart[]>> filterParts;

        public FilterableScopeInterceptor(Dictionary<MethodInfo, Dictionary<string, FilterPart[]>> filterParts, IInternalScopeFactory scopeFactory)
        {
            this.filterParts = filterParts;
            this.scopeFactory = scopeFactory;
        }

        public IEnumerable<Type> GetAdditionalInterfaces(Type tScope) => new[] { typeof(IFilterable<>).MakeGenericType(tScope) };

        public override void Intercept(IInvocation invocation)
        {
            HandleIFilterableFilterMethod(invocation);
        }

        protected void HandleIFilterableFilterMethod(IInvocation invocation)
        {
            var filter = invocation.Arguments.FirstOrDefault() as (string filter, object value)[];
            if (filter == null || filter.Length == 0)
            {
                invocation.ReturnValue = invocation.Proxy;
                return;
            }

            var interceptor = new FilteringInterceptor(filterParts, invocation.Proxy, filter);
            var scopeType = scopeFactory.ScopeRegistry.GetScopeType(invocation.Proxy);
            var identity = scopeFactory.ScopeRegistry.GetIdentity(invocation.Proxy);
            var storage = scopeFactory.ScopeRegistry.GetStorage(invocation.Proxy);

            var filteredScope = scopeFactory.CreateScopes(scopeType, identity.RepeatOnce(), storage,
                                                          interceptor.RepeatOnce(), null, false)
                                            .First();
            invocation.ReturnValue = filteredScope;
        }


        internal class FilteringInterceptor : ScopeInterceptorBase
        {
            private readonly Dictionary<MethodInfo, Dictionary<string, FilterPart[]>> filterParts;
            private readonly object scope;
            private readonly (string filter, object value)[] filter;

            public FilteringInterceptor(Dictionary<MethodInfo, Dictionary<string, FilterPart[]>> filterParts, object scope, (string filter, object value)[] filter)
            {
                this.filterParts = filterParts;
                this.scope = scope;
                this.filter = filter;
            }

            public override void Intercept(IInvocation invocation)
            {
                HandleFiltering(invocation);
            }


            public override IEnumerable<AspectPredicate> Predicates
            {
                get
                {
                    yield return x =>
                                     x.ReturnType.IsFilterable() || x.ReturnType.IsScope() || filterParts.ContainsKey(x);
                }
            }

            private void HandleFiltering(IInvocation invocation)
            {
                object ret;
                if (scope != null)
                    ret = invocation.ReturnValue = invocation.Method.Invoke(scope, invocation.Arguments);
                else
                {
                    invocation.Proceed();
                    ret = invocation.ReturnValue;
                }

                if (ret == null)
                {
                    return;
                }

                if (ret is IFilterable filterable)
                {
                    invocation.ReturnValue =
                        filterable.Filter(filter);
                    return;
                }

                // must be configured here:
                var partsByName = filterParts[invocation.Method];
                foreach (var filterExpression in filter)
                {
                    if (partsByName.TryGetValue(filterExpression.filter, out var parts))
                        foreach (var part in parts)
                            invocation.ReturnValue = part.Filter(invocation.ReturnValue, filterExpression.value);
                }
            }
        }
    }
}