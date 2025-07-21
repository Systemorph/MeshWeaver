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
            return instances.GroupByWithDefaultIfEmpty(instances.GetType().GetEnumerableElementType() ?? typeof(object));
        }

        public static IEnumerable<IGrouping<Type, object>> GroupByWithDefaultIfEmpty(this IEnumerable<object> instances, Type defaultType)
        {
            return instances.DefaultIfEmpty().Where(x => x != null).GroupBy(x => x!.GetType())
                .Cast<IGrouping<Type, object>>().OrderByTypeInheritance(x=>x.Key);
        }
    }
}
