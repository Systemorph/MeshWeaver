using System.Linq.Expressions;
using System.Reflection;
using Castle.DynamicProxy;
using MeshWeaver.Arithmetics;
using MeshWeaver.Arithmetics.MapOver;
using MeshWeaver.Collections;
using MeshWeaver.DataCubes;
using MeshWeaver.Reflection;
using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes.Operations
{
    public class IsScopeMapOverFunctionProvider : IMapOverFunctionProvider
    {
        private readonly MapOverProxyGenerator proxyGenerator = new();

        public Delegate GetDelegate(Type type, ArithmeticOperation method) => proxyGenerator.CreateMapOverDelegateForScope(type, method);

        public class MapOverProxyGenerator
        {
            private readonly IProxyGenerator proxyGenerator = new ProxyGenerator();
            private readonly ProxyGenerationOptions options = new() { Selector = new InterceptorSelector() };

            private static readonly MethodInfo MapOverScopeInnerMethod =
                ReflectionHelper.GetMethodGeneric<MapOverProxyGenerator>(x =>
                                                                             x.MapOverScopeInner<object>(null, ArithmeticOperation.Plus, 0));

            public Delegate CreateMapOverDelegateForScope(Type type, ArithmeticOperation method)
            {
                var factor = Expression.Parameter(typeof(double));
                var container = Expression.Parameter(type);
                var mapOverMethod = MapOverScopeInnerMethod.MakeGenericMethod(type);
                return Expression
                       .Lambda(
                               Expression.Call(Expression.Constant(this), mapOverMethod, container,
                                               Expression.Constant(method), factor), factor, container).Compile();
            }

            internal TScope MapOverScopeInner<TScope>(TScope scope, ArithmeticOperation method, double scalar)
                where TScope : class
            {
                var mapOverInterceptor = GetMapOverInterceptor(scope, method, scalar);
                return CreateProxy<TScope>(mapOverInterceptor);
            }

            public TScope CreateProxy<TScope>(params IScopeInterceptor[] interceptors)
                where TScope : class
            {
                var additionalInterfacesToProxy = new[] { typeof(IFilterable<TScope>) };
                return (TScope)proxyGenerator.CreateInterfaceProxyWithoutTarget(typeof(TScope),
                                                                                additionalInterfacesToProxy,
                                                                                options,
                                                                                new CachingInterceptor().RepeatOnce()
                                                                                                        .Concat(interceptors)
                                                                                                        .Concat(new DelegateToInterfaceDefaultImplementationInterceptor().RepeatOnce())
                                                                                                        .Cast<IInterceptor>().ToArray());
            }

            public MapOverInterceptor<TScope> GetMapOverInterceptor<TScope>(TScope scope, ArithmeticOperation method, double scalar)
                where TScope : class
            {
                var mapOverInterceptor = new MapOverInterceptor<TScope>(scope, method, scalar, this);
                return mapOverInterceptor;
            }
        }

        public class MapOverInterceptor<TScope> : ScopeInterceptorBase
            where TScope : class
        {
            private readonly TScope scope;
            private readonly ArithmeticOperation arithmeticOperation;
            private readonly double scalar;
            private readonly MapOverProxyGenerator generator;

            public MapOverInterceptor(TScope scope, ArithmeticOperation arithmeticOperation, double scalar,
                                      MapOverProxyGenerator generator)
            {
                this.scope = scope;
                this.arithmeticOperation = arithmeticOperation;
                this.scalar = scalar;
                this.generator = generator;
            }


            public override void Intercept(IInvocation invocation)
            {
                if (invocation.Method.DeclaringType.IsFilterableInterface())
                {
                    var filterArgs = ((string filter, object value)[])invocation.Arguments.First();
                    invocation.ReturnValue =
                        generator.MapOverScopeInner(((IFilterable<TScope>)scope).Filter(filterArgs), arithmeticOperation, scalar);
                    return;
                }

                var del = accessors.GetInstance(invocation.Method, CreateAccessor);
                var property = invocation.Method.DeclaringType.GetProperty(invocation.Method.Name[4..]);
                if (property == null || property.DeclaringType.IsScopeInterface() || HasNoArithmeticsAttribute(property))
                {
                    invocation.ReturnValue = del(scope, invocation.Arguments);
                    return;
                }

                if (property.HasAttribute<DirectEvaluationAttribute>())
                {
                    invocation.Proceed();
                    return;
                }

                var methodReturnType = invocation.Method.ReturnType;
                invocation.ReturnValue = MapOverMethod.MakeGenericMethod(methodReturnType)
                                                      .InvokeAsFunction(this, del, invocation.Arguments);
            }

            private bool HasNoArithmeticsAttribute(PropertyInfo property)
            {
                var attribute = property.GetCustomAttribute<NoArithmeticsAttribute>();
                return attribute != null && (attribute.Operations & arithmeticOperation) == arithmeticOperation;
            }

            private Func<TScope, object[], object> CreateAccessor(MethodInfo methodInfo)
            {
                var prm = Expression.Parameter(typeof(TScope));
                var args = Expression.Parameter(typeof(object[]));
                return Expression.Lambda<Func<TScope, object[], object>>(
                                                                         Expression.Convert(Expression.Call(prm, methodInfo,
                                                                                                            methodInfo.GetParameters().Select((p, i) =>
                                                                                                                                                  Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), p.ParameterType))
                                                                                                                      .Cast<Expression>()
                                                                                                                      .ToArray()), typeof(object)),
                                                                         prm, args).Compile();
            }

            private readonly CreatableObjectStore<MethodInfo, Func<TScope, object[], object>> accessors =
                new();

            private static readonly IGenericMethodCache MapOverMethod =
                GenericCaches.GetMethodCache<MapOverInterceptor<TScope>>(i => i.MapOver<object>(null, null));

            // ReSharper disable once UnusedMethodReturnValue.Local
            private T MapOver<T>(Func<TScope, object[], object> methodDelegate, object[] arguments)
            {
                var underlying = (T)methodDelegate.Invoke(scope, arguments);
                return MapOverFields.GetMapOver<T>(arithmeticOperation).Invoke(scalar, underlying);
            }
        }
    }
}