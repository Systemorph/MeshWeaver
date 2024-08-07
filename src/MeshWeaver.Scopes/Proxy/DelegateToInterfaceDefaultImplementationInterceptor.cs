using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using Castle.DynamicProxy;
using MeshWeaver.Reflection;

namespace MeshWeaver.Scopes.Proxy
{
    public class DelegateToInterfaceDefaultImplementationInterceptorFactory : IScopeInterceptorFactory
    {
        public IEnumerable<IScopeInterceptor> GetInterceptors(Type tScope, IInternalScopeFactory scopeFactory)
        {
            yield return new DelegateToInterfaceDefaultImplementationInterceptor();
        }
    }

    public class DelegateToInterfaceDefaultImplementationInterceptor : ScopeInterceptorBase
    {
        // ReSharper disable once InconsistentNaming
        private static readonly AspectPredicate[] predicates = { x => !ScopeRegistryInterceptor.ScopeInterfaces.Contains(x.DeclaringType) };
        public override IEnumerable<AspectPredicate> Predicates => predicates;
        public override void Intercept(IInvocation invocation)
        {
            var method = invocation.Method;
            var proxy = invocation.Proxy;

            //if (invocation.Proxy is IScopeWithApplicability swa)
            //{
            //    var method2 = swa.GetInterface(method).GetMethod(method.Name);
            //    method = swa.GetInterface(method).GetMethod(method.Name) ?? method;
            //}
            if (!method.DeclaringType!.IsInterface || method.IsAbstract)
            {
                if (method.IsPropertyGetter())
                    invocation.ReturnValue = GetDefaultValue(method);
                else
                    invocation.Proceed();
            }
            else
            {
                var mostSpecificOverride = proxy.GetMostSpecificOverride(method);
                invocation.ReturnValue = DefaultImplementationOfInterfacesExtensions.DynamicInvokeNonVirtually(mostSpecificOverride, proxy, invocation.Arguments);
            }
        }
        
        private readonly ConcurrentDictionary<MethodInfo, object> defaultValues = new();

        private object GetDefaultValue(MethodInfo method)
        {
            return defaultValues.GetOrAdd(method, InitializeDefaultValue);
        }

        private object InitializeDefaultValue(MethodInfo method)
        {
            var property = method.DeclaringType!.GetProperty(method.Name.Substring(4, method.Name.Length - 4));
            var defaultAttribute = property?.GetSingleCustomAttribute<DefaultValueAttribute>();
            return defaultAttribute?.Value ?? GetDefaultValue(method.ReturnType);
        }



        private static object GetDefaultValue(Type type)
        {
            if (type == null)
                return null;
            var typeInfo = type.GetTypeInfo();
            return GetDefaultValue(typeInfo);
        }
        private static object GetDefaultValue(TypeInfo typeInfo)
        {
            if (typeInfo == null)
                throw new ArgumentNullException(nameof(typeInfo));
            var type = typeInfo.AsType();
            if (type == typeof(void))
                return null;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Empty:
                case TypeCode.String:
                    return null;
                case TypeCode.Object:
                case TypeCode.DateTime:
                    return typeInfo.IsValueType ? Activator.CreateInstance(type) : null;
                case TypeCode.Boolean:
                case TypeCode.Char:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Activator.CreateInstance(type);
                default:
                    throw new InvalidOperationException("Code supposed to be unreachable.");
            }
        }

    }
}