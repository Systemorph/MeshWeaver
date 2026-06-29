using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshWeaver.Reflection
{
    /// <summary>
    /// Provides helper extension methods for ordering and grouping sequences by the runtime type of their elements.
    /// </summary>
    public static class GroupingHelper
    {
        /// <summary>
        /// Orders the <paramref name="items"/> so that elements whose type is closer to the root of the
        /// inheritance hierarchy (fewer base types) come first.
        /// </summary>
        /// <typeparam name="T">The element type of the sequence.</typeparam>
        /// <param name="items">The sequence to order.</param>
        /// <param name="typeAccessor">A function selecting the <see cref="Type"/> used to compute inheritance depth for each element.</param>
        /// <returns>The items ordered ascending by their inheritance depth.</returns>
        public static IEnumerable<T> OrderByTypeInheritance<T>(this IEnumerable<T> items, Func<T, Type> typeAccessor)
        {
            return items.OrderBy(x => typeAccessor(x).GetBaseTypes().Count());
        }

        /// <summary>
        /// Groups the <paramref name="instances"/> by their runtime type, inferring the default group type from the
        /// sequence's element type when the sequence is empty.
        /// </summary>
        /// <param name="instances">The instances to group.</param>
        /// <returns>The groupings keyed by runtime type, ordered by inheritance depth.</returns>
        public static IEnumerable<IGrouping<Type, object>> GroupByWithDefaultIfEmpty(this IEnumerable<object> instances)
        {
            return instances.GroupByWithDefaultIfEmpty(instances.GetType().GetEnumerableElementType() ?? typeof(object));
        }

        /// <summary>
        /// Groups the <paramref name="instances"/> by their runtime type, using <paramref name="defaultType"/> as the
        /// fallback group type when the sequence is empty.
        /// </summary>
        /// <param name="instances">The instances to group.</param>
        /// <param name="defaultType">The type to use as the group key when the sequence contains no elements.</param>
        /// <returns>The groupings keyed by runtime type, ordered by inheritance depth.</returns>
        public static IEnumerable<IGrouping<Type, object>> GroupByWithDefaultIfEmpty(this IEnumerable<object> instances, Type defaultType)
        {
            return instances.DefaultIfEmpty().Where(x => x != null).GroupBy(x => x!.GetType())
                .Cast<IGrouping<Type, object>>().OrderByTypeInheritance(x=>x.Key);
        }
    }
}
