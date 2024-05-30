using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Reflection;

public static class RecordExtensions
{
    public static T With<T>(this T obj, params Expression<Func<T, object>>[] propertyExpressions)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var expressions = propertyExpressions.Select(expr =>
        {
            var memberExpr = (MemberExpression)expr.Body;
            var property = (PropertyInfo)memberExpr.Member;
            var value = Expression.Convert(expr.Body, property.PropertyType);
            return Expression.Bind(property, value);
        });

        var memberInit = Expression.MemberInit(Expression.New(typeof(T)), expressions);
        var lambda = Expression.Lambda<Func<T, T>>(memberInit, parameter);
        var compiled = lambda.Compile();
        return compiled(obj);
    }
}
