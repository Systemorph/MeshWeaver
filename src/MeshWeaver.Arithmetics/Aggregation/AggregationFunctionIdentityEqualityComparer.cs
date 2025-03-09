using System.Reflection;
using MeshWeaver.Utils;

namespace MeshWeaver.Arithmetics.Aggregation
{
    internal class AggregationFunctionIdentityEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {

        private static readonly CreatableObjectStore<string, AggregationFunctionIdentityEqualityComparer<T>> Cache = new();

        internal static AggregationFunctionIdentityEqualityComparer<T> Instance(string key, PropertyInfo[] properties)
        {
            return Cache.GetInstance(key, _ => new AggregationFunctionIdentityEqualityComparer<T>(properties));
        }

        private readonly Func<T, T, bool> equalityFunc;
        private readonly Func<T, int> hashCodeFunc;

        private AggregationFunctionIdentityEqualityComparer(PropertyInfo[] props)
        {
            (equalityFunc, hashCodeFunc) = IdentityEqualityComparerHelper.GetHashAndEqualityForType<T>(props);
        }
        
        public bool Equals(T x, T y)
        {
            if (x == null)
                return y == null;
            if (y == null)
                return false;
            return equalityFunc(x, y);
        }

        public int GetHashCode(T obj)
        {
            return hashCodeFunc(obj);
        }
    }
}
