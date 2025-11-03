using System.Linq.Expressions;
using MeshWeaver.Domain;

namespace MeshWeaver.Arithmetics.Aggregation
{
    /// <summary>
    /// The <see cref="AggregationExtensions"/> class contains extension methods to Aggregate data grouped by or over a subset of <see cref="IdentityPropertyAttribute">Identity Properties</see>
    /// </summary>
    public static class AggregationExtensions
    {
        /// <summary>
        /// Groups by all <see cref="IdentityPropertyAttribute">Identity Properties</see> specified in <paramref name="propertyNames"/> and aggregates the values in each group
        /// </summary>
        /// <param name="source"></param>
        /// <param name="propertyNames">Subset of <see cref="IdentityPropertyAttribute">Identity Properties</see> used to group by</param>
        /// <returns>For each group the aggregated value with the specified properties set to the key value</returns>
        public static IEnumerable<T> AggregateBy<T>(this IEnumerable<T> source, params string[] propertyNames)
            where T : class
        {
            return AggregationDelegates<T>.GetAggregateByDelegate(propertyNames.ToList())(source);
        }

        /// <summary>
        /// Groups by a lambda specified in <paramref name="groupKeySelector"/> and aggregates the values in each group
        /// </summary>
        /// <param name="source"></param>
        /// <param name="groupKeySelector">A selector for grouping</param>
        public static IEnumerable<T> AggregateBy<T, TKey>(this IEnumerable<T> source, Expression<Func<T, TKey>> groupKeySelector)
            where T : class
        {
            return AggregationDelegates<T>.GetAggregateByDelegate(groupKeySelector)(source);
        }

        public static async IAsyncEnumerable<T> AggregateByAsync<T>(this IAsyncEnumerable<T> source, params string[] propertyNames)
            where T : class
        {
            var (groupBySelector, eq) =
                AggregationDelegates<T>.GetGroupByDelegateAndEqualityComparer(propertyNames.ToList());

            var groups = source.GroupBy(x =>  groupBySelector(x), eq);

            await foreach (var g in groups)
            {
                yield return await g.ToAsyncEnumerable().AggregateAsync((result, item) => SumFunction.Sum(result, item));
            }
        }



        /// <summary>
        /// Groups by all <see cref="IdentityPropertyAttribute">Identity Properties</see> ignoring the specified in <paramref name="propertyNames"/> and aggregates the values in each group
        /// </summary>
        /// <param name="source"></param>
        /// <param name="propertyNames">Subset of <see cref="IdentityPropertyAttribute">Identity Properties</see> used to group by</param>
        /// <returns>For each group the aggregated value with the remaining properties set to the key value</returns>
        public static IEnumerable<T> AggregateOver<T>(this IEnumerable<T> source, params string[] propertyNames)
            where T : class
        {
            return AggregationDelegates<T>.GetAggregateOverDelegate(propertyNames.ToList())(source);
        }

        public static async IAsyncEnumerable<T> AggregateOverAsync<T>(this IAsyncEnumerable<T> source, params string[] propertyNames)
            where T : class
        {
            var (groupBySelector, eq) =
                AggregationDelegates<T>.GetGroupByDelegateAndEqualityComparer(propertyNames.ToList(), true);

            var groups = source.GroupBy(x => groupBySelector(x), eq);

            await foreach (var g in groups)
            {
                yield return await g.ToAsyncEnumerable().AggregateAsync((result, item) => SumFunction.Sum(result, item));
            }
        }

    }
}
