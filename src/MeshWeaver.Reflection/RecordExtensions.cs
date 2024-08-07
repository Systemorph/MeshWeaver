using System.Linq.Expressions;
using System.Reflection;

namespace MeshWeaver.Reflection;


/// <summary>
/// I'm not sure yet this is a good way of doing things. It allows to modify record types
/// In a generic way. Currently, we're using Json Deserialize for the job, it will always
/// create new.
/// </summary>
internal static class RecordExtensions
{
    internal static T With<T>(this T obj, params Expression<Func<T, object>>[] propertyExpressions)
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
