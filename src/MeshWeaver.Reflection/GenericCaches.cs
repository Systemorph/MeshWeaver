using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace MeshWeaver.Reflection
{
    public static class GenericCaches
    {
        #region Types

        private static readonly ConcurrentDictionary<Type, IGenericTypeCache> TypeCaches = new ConcurrentDictionary<Type, IGenericTypeCache>();

        public static IGenericTypeCache GetTypeCache(Type genericType)
        {
            if (genericType == null)
                throw new ArgumentNullException(nameof(genericType));

            if (!genericType.IsGenericTypeDefinition)
                genericType = genericType.GetGenericTypeDefinition();

            return TypeCaches.GetOrAdd(genericType, type => new GenericTypeCache(type));
        }

        #endregion

        #region Methods

        private static readonly ConcurrentDictionary<MethodInfo, IGenericMethodCache> MethodCaches = new ConcurrentDictionary<MethodInfo, IGenericMethodCache>();

        public static IGenericMethodCache GetMethodCache(MethodInfo genericMethod)
        {
            if (genericMethod == null)
                throw new ArgumentNullException(nameof(genericMethod));

            if (!genericMethod.IsGenericMethodDefinition)
                genericMethod = genericMethod.GetGenericMethodDefinition();

            return MethodCaches.GetOrAdd(genericMethod, method => new GenericMethodCache(method));
        }

        public static IGenericMethodCache GetMethodCache<T>(Expression<Action<T>> selector)
        {
            if (selector == null)
                throw new ArgumentNullException(nameof(selector));

            var methodInfo = ReflectionHelper.GetMethodGeneric(selector);
            return GetMethodCache(methodInfo);
        }

        public static IGenericMethodCache GetMethodCacheStatic(Expression<Action> selector)
        {
            if (selector == null)
                throw new ArgumentNullException(nameof(selector));

            var methodInfo = ReflectionHelper.GetStaticMethodGeneric(selector);
            return GetMethodCache(methodInfo);
        }

        #endregion
    }

    public interface IGenericMethodCache
    {
        MethodInfo GenericDefinition { get; }
        MethodInfo MakeGenericMethod(params Type[] types);
    }

    public interface IGenericTypeCache
    {
        Type GenericDefinition { get; }
        Type MakeGenericType(params Type[] types);
    }
}