using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Import
{
    public static class MappingHelper
    {
        public static PropertyInfo GetProperty<TSource, TProperty>(this Expression<Func<TSource, TProperty>> selector)
        {
            if (selector == null)
                throw new ArgumentNullException(nameof(selector));
            if (selector.Body == null)
                throw new ArgumentException("Selector can't be null", nameof(selector));

            if (selector.Body is MemberExpression memberExpression && memberExpression.Member is PropertyInfo property)
            {
                //in case if we access property which is declared in base type
                //we receive propertyInfo where ReflectedType is a base type, so it is not one which we expect
                //that's why we get it directly in this case by name, which we have from property with parent type declaration
                return property.ReflectedType == typeof(TSource) ? property : typeof(TSource).GetProperty(property.Name);
            }

            throw new ArgumentException("Selector should access property");
        }
    }
}
