using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Reflection;

namespace OpenSmc.Arithmetics.Aggregation
{
    internal static class AggregationDelegatesExtensions
    {
        internal static string GetCacheKey(this IEnumerable<string> identityProperties) => string.Join(",", identityProperties.OrderBy(x => x));
    }

    public static class AggregationDelegates<T>
        where T : class
    {
        private static readonly ConcurrentDictionary<string, Func<IEnumerable<T>, IEnumerable<T>>> AggregateByDelegates = new ConcurrentDictionary<string, Func<IEnumerable<T>, IEnumerable<T>>>();
        private static readonly ConcurrentDictionary<string, Func<IEnumerable<T>, IEnumerable<T>>> AggregateOverDelegates = new ConcurrentDictionary<string, Func<IEnumerable<T>, IEnumerable<T>>>();
        private static readonly ConcurrentDictionary<string, (Func<T, T>, IEqualityComparer<T>)> GroupByDelegateAndEqualityComparer = new ConcurrentDictionary<string, (Func<T, T>, IEqualityComparer<T>)>();


        public static (Func<T, T>, IEqualityComparer<T>) GetGroupByDelegateAndEqualityComparer(List<string> identityProperties , bool isAggregateOver = false)
        {
            identityProperties ??= new List<string>();
            var properties = isAggregateOver?  GetPropertiesOver(identityProperties) : GetPropertiesBy(identityProperties);
            var key = properties.Select(x => x.Name).GetCacheKey();
            return GroupByDelegateAndEqualityComparer.GetOrAdd(key, (CreateGroupBySelector(properties), AggregationFunctionIdentityEqualityComparer<T>.Instance(key, properties)));
        }

   

        public static Func<IEnumerable<T>, IEnumerable<T>> GetAggregateByDelegate(List<string> identityProperties = null)
        {
            identityProperties ??= new List<string>();
            var key = identityProperties.GetCacheKey();
            return AggregateByDelegates.GetOrAdd(key, CreateAggregationDelegate(GetPropertiesBy(identityProperties)));
        }

        private class GroupKeyExpressionKeyVisitor : ExpressionVisitor
        {
            public string Key { get; private set; } = "GroupKey_";

            protected override Expression VisitMember(MemberExpression node)
            {
                var res = base.VisitMember(node);
                Key += node.Member.Name + ".";
                return res;
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                var res = base.VisitParameter(node);
                Key += ",";
                return res;
            }
        }

        public static Func<IEnumerable<T>, IEnumerable<T>> GetAggregateByDelegate<TKey>(Expression<Func<T, TKey>> groupKeySelectorExpression)
        {
            var visitor = new GroupKeyExpressionKeyVisitor();
            visitor.Visit(groupKeySelectorExpression);
            var key = visitor.Key;
            
            var groupKeySelector = groupKeySelectorExpression.Compile();
            return AggregateByDelegates.GetOrAdd(key, CreateAggregationDelegate(groupKeySelector));
        }

        public static Func<IEnumerable<T>, IEnumerable<T>> GetAggregateOverDelegate(List<string> identityProperties = null)
        {
            identityProperties ??= new List<string>();
            var key = identityProperties.GetCacheKey();
            return AggregateOverDelegates.GetOrAdd(key, CreateAggregationDelegate(GetPropertiesOver(identityProperties)));
        }


        private static PropertyInfo[] GetPropertiesBy(List<string> identityProperties)
        {
            if (identityProperties.Count == 0)
                return GetProperties(identityProperties, _ => true);
            return identityProperties.Select(typeof(T).GetProperty).ToArray();
        }

        private static PropertyInfo[] GetPropertiesOver(List<string> identityProperties)
        {
            return GetProperties(identityProperties, x => !x);
        }



        private static PropertyInfo[] GetProperties(List<string> identityProperties, Func<bool, bool> notFunction)
        {
            var properties = typeof(T).GetProperties().Where(x => x.HasAttribute<IdentityPropertyAttribute>() && !x.HasAttribute<AggregateOverAttribute>() && notFunction(identityProperties.Contains(x.Name))).ToArray();
            return properties;
        }

        private static Func<IEnumerable<T>, IEnumerable<T>> CreateAggregationDelegate(PropertyInfo[] properties)
        {
            var key = string.Join(",", properties.Select(p => p.Name).OrderBy(x => x));
            var groupBySelector = CreateGroupBySelector(properties);

            // Clone the aggregate, to keep underlying data unchanged when assigning the key properties
            var identityCopier = properties.GetIdentityPropertiesCopier<T>(key);
            var equalityComparer = AggregationFunctionIdentityEqualityComparer<T>.Instance(key, properties);
            return x => x.GroupByKeySelectorAndExecuteLambda(groupBySelector, g => AggregationFunction.GetAggregationFunction<T, T>()(g), identityCopier, equalityComparer).ToList();
        }

        private static Func<IEnumerable<T>, IEnumerable<T>> CreateAggregationDelegate<TKey>(Func<T, TKey> groupKeySelector)
        {
            return x => x.GroupBy(groupKeySelector).Select(g => g.Aggregate()).ToList();
        }

        /// <summary>
        /// Create a lambda expression similar to
        /// x => new T(){ Prop1 = x.Prop1, Prop2 = x.Prop2, ... }
        /// </summary>
        private static Func<T, T> CreateGroupBySelector(PropertyInfo[] properties)
        {
            var type = typeof(T);
            var x = Expression.Parameter(type);

            var ctr = typeof(T).GetConstructor(Type.EmptyTypes);
            if (ctr == null)
                throw new InvalidOperationException($"Type {typeof(T).Name} must have a default constructor");
            var newType = Expression.New(ctr);

            var propertyAssignments = properties.Select(prop => Expression.Bind(prop, Expression.Property(x, prop)));
            var memberInit = Expression.MemberInit(newType, propertyAssignments);

            return Expression.Lambda<Func<T, T>>(memberInit, x).Compile();
        }
    }
}
