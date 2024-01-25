using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Collections;
using OpenSmc.Reflection;
using OpenSmc.Scopes.Proxy;

namespace OpenSmc.Scopes.Operations
{
    public class AggregationInterceptorWithIdentity<TScope, TIdentity> : AggregationInterceptor<TScope>
        where TScope : class, IScope<TIdentity>
    {
        public AggregationInterceptorWithIdentity(ICollection<TScope> scopes, Func<IEnumerable<TScope>, TScope> aggregationFunction)
            : base(scopes, aggregationFunction)
        {
        }

        public override void Intercept(IInvocation invocation)
        {
            switch (invocation.Method.Name)
            {
                case ReflectionHelper.GetterPrefix + nameof(IScope<object>.Identity):
                    var distinctIdentities = Scopes.Select(s => s.Identity).Distinct().ToList();
                    if (distinctIdentities.Count == 1)
                        invocation.ReturnValue = distinctIdentities[0];
                    else
                    {
                        if (distinctIdentities.Count > 1)
                        {
                            var identityAggregationBehaviour = typeof(TIdentity).GetCustomAttribute<IdentityAggregationBehaviourAttribute>();

                            if (identityAggregationBehaviour is { Behaviour: IdentityAggregationBehaviour.Aggregate })
                            {
                                invocation.ReturnValue = distinctIdentities.Aggregate();
                                return;
                            }
                        }
                        invocation.ReturnValue = default(TIdentity);
                    }
                    return;
                default:
                    base.Intercept(invocation);
                    return;
            }
        }
    }

    public class AggregationInterceptor<TScope> : ScopeInterceptorBase
        where TScope : class, IScope
    {
        protected readonly ICollection<TScope> Scopes;
        private readonly Func<IEnumerable<TScope>, TScope> aggregationFunction;
        private readonly Guid id = Guid.NewGuid();

        public AggregationInterceptor(ICollection<TScope> scopes, Func<IEnumerable<TScope>, TScope> aggregationFunction)
        {
            this.Scopes = scopes;
            this.aggregationFunction = aggregationFunction;
        }


        public override void Intercept(IInvocation invocation)
        {
            if (invocation.Method.DeclaringType.IsScopeInterface())
            {
                switch (invocation.Method.Name)
                {
                    case nameof(IScope.GetScopeType):
                        invocation.ReturnValue = typeof(TScope);
                        return;
                    case nameof(IScope.GetGuid):
                        invocation.ReturnValue = id;
                        return;
                    default:
                        // TODO: Should we throw here? Which identity to return? (2021/05/21, Roland Buergi)
                        //invocation.Proceed();
                        return;
                }
            }

            if (invocation.Method.DeclaringType.IsFilterableInterface())
            {
                var filter = ((string filter, object value)[])invocation.Arguments.First();
                invocation.ReturnValue = aggregationFunction(Scopes.Cast<IFilterable>().Select(s => s.Filter(filter)).Cast<TScope>());
                return;
            }

            if (exludeFromAggregation.GetInstance(invocation.Method, ExcludeFromAggregation))
            {
                invocation.Proceed();
                return;
            }

            var methodReturnType = invocation.Method.ReturnType;
            var del = accessors.GetInstance(invocation.Method, x => CreateAccessor(x));
            invocation.ReturnValue = aggregateMethod.MakeGenericMethod(methodReturnType)
                                                    .InvokeAsFunction(this, del, invocation.Arguments);
        }

        // TODO: This is a pretty random list. Probably it would be best to find if AggregationFunction can map it. (2021/05/26, Roland Buergi)
        private static readonly HashSet<Type> ExcludedTypes = new() { typeof(string), typeof(bool), typeof(DateTime) };

        private bool ExcludeFromAggregation(MethodInfo method)
        {
            var propertyName = method.Name[4..];
            var property = method.DeclaringType.GetProperty(propertyName);
            if (property == null)
                return true;
            // TODO: Think of these rules (2021/05/26, Roland Buergi)
            if (ExcludedTypes.Contains(property.PropertyType))
                return true;
            return property.HasAttribute<NotAggregatedAttribute>();
        }

        private Delegate CreateAccessor(MethodInfo methodInfo)
        {
            var prm = Expression.Parameter(typeof(TScope));
            var args = Expression.Parameter(typeof(object[]));
            return Expression.Lambda(
                                     Expression.Call(prm, methodInfo,
                                                     methodInfo.GetParameters().Select((p, i) =>
                                                                                           Expression.Convert(Expression.ArrayIndex(Expression.Constant(i)), p.ParameterType))
                                                               .Cast<Expression>()
                                                               .ToArray()),
                                     prm, args).Compile();
        }

        private readonly CreatableObjectStore<MethodInfo, Delegate> accessors = new();
        private readonly CreatableObjectStore<MethodInfo, bool> exludeFromAggregation = new();

        private readonly IGenericMethodCache aggregateMethod =
            GenericCaches.GetMethodCache<AggregationInterceptor<TScope>>(i => i.Aggregate<object>(null, null));

        // ReSharper disable once UnusedMethodReturnValue.Local
        private T Aggregate<T>(Func<TScope, object[], T> methodDelegate, object[] arguments)
        {
            return AggregationFunction.GetAggregationFunction<T>().Invoke(Scopes.Select(s => methodDelegate(s, arguments)));
        }
    }
}