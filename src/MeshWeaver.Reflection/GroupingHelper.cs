using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshWeaver.Reflection
{
    public static class GroupingHelper
    {
        public static IEnumerable<T> OrderByTypeInheritance<T>(this IEnumerable<T> items, Func<T, Type> typeAccessor)
        {
            return items.OrderBy(x => typeAccessor(x).GetBaseTypes().Count());
        }

        public static IEnumerable<IGrouping<Type, object>> GroupByWithDefaultIfEmpty(this IEnumerable<object> instances)
        {
            return instances.GroupByWithDefaultIfEmpty(instances.GetType().GetEnumerableElementType());
        }

        public static IEnumerable<IGrouping<Type, object>> GroupByWithDefaultIfEmpty(this IEnumerable<object> instances, Type defaultType)
        {
            return instances.DefaultIfEmpty().GroupBy(x => x == null ? defaultType : x.GetType()).OrderByTypeInheritance(x=>x.Key);
        }
    }
}
