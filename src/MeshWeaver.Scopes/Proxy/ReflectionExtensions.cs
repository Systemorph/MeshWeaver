using System.Linq.Expressions;
using System.Reflection;

namespace MeshWeaver.Scopes.Proxy
{
    public static class ReflectionExtensions
    {
        public static MethodInfo GetMethod<T, TReturn>(this Expression<Func<T, TReturn>> memberExpression)
        {
            if (memberExpression.Body is MemberExpression memberAccessExpression)
            {
                var member = memberAccessExpression.Member;
                if (member is MethodInfo ret)
                    return ret;
                if (member is PropertyInfo property)
                    return property.GetGetMethod();
            }

            if (memberExpression.Body is MethodCallExpression methodCallExpression)
            {
                return methodCallExpression.Method;
            }

            throw new ArgumentException("Only Properties and Methods are supported.", nameof(memberExpression));
        }
    }
}