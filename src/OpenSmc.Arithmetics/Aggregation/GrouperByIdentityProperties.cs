using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Arithmetics.Aggregation
{
    public static class GrouperByIdentityProperties
    {
        public static IEnumerable<T> GroupByIdentityPropertiesAndExecuteLambda<T>(this IEnumerable<T> data, Func<IEnumerable<T>, T> lambda, Action<T, T> copyIdentityProperties, IEqualityComparer<T> equalityComparer)
            where T : class
        {
            return data.GroupByKeySelectorAndExecuteLambda(x => x, lambda, copyIdentityProperties, equalityComparer);
        }

        public static IEnumerable<T> GroupByKeySelectorAndExecuteLambda<T>(this IEnumerable<T> data, Func<T, T> keySelector, Func<IEnumerable<T>, T> lambda, Action<T, T> copyIdentityProperties, IEqualityComparer<T> equalityComparer)
            where T : class
        {
            return data.GroupBy(keySelector, equalityComparer).Select(x =>
            {
                var ret = lambda(x);
                copyIdentityProperties(x.Key, ret);
                return ret;
            });
        }


        private static readonly CreatableObjectStore<Type, CreatableObjectStore<string, Delegate>> IdentityPropertiesCopiers = new CreatableObjectStore<Type, CreatableObjectStore<string, Delegate>>(t => new CreatableObjectStore<string, Delegate>());

        public static Delegate GetIdentityPropertiesCopier(this PropertyInfo[] props, Type type, string key)
        {
            return IdentityPropertiesCopiers.GetInstance(type).GetInstance(key, _ => CreateIdentityPropertiesCopier(props, type));
        }


        public static Action<T, T> GetIdentityPropertiesCopier<T>(this PropertyInfo[] props, string key)
            where T : class
        {
            return (Action<T, T>)IdentityPropertiesCopiers.GetInstance(typeof(T)).GetInstance(key, _ => CreateIdentityPropertiesCopier(props, typeof(T)));
        }

        private static Delegate CreateIdentityPropertiesCopier(PropertyInfo[] properties, Type type)
        {
            return (Delegate)CreateIdentityPropertiesCopierMethod.MakeGenericMethod(type).InvokeAsFunction(properties);
        }

        private static readonly IGenericMethodCache CreateIdentityPropertiesCopierMethod = GenericCaches.GetMethodCacheStatic(() => CreateIdentityPropertiesCopier<object>(null));
        // ReSharper disable once UnusedMethodReturnValue.Local
        private static Action<T, T> CreateIdentityPropertiesCopier<T>(PropertyInfo[] properties)
        {
            var toCopyFrom = Expression.Parameter(typeof(T));
            var toCopyTo = Expression.Parameter(typeof(T));
            var assignments = Expression.Block(properties.Select(prop => Expression.Assign(Expression.Property(toCopyTo, prop), Expression.Property(toCopyFrom, prop))));
            return Expression.Lambda<Action<T, T>>(assignments, toCopyFrom, toCopyTo).Compile();
        }
    }
}
