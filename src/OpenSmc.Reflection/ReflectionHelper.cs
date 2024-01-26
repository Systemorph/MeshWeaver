using System.Linq.Expressions;
using System.Reflection;

namespace OpenSmc.Reflection;

public static class ReflectionHelper
{
    public static Signature GetSignature(this MemberInfo member)
    {
        if (member == null)
            throw new ArgumentNullException(nameof(member));

        return new Signature(member);
    }

    /// <summary>
    /// Gets the <see cref="MethodInfo"/> of the static method defined in the selector expression
    /// </summary>
    /// <param name="selector">An expression which selects a static method</param>
    /// <returns>The <see cref="MethodInfo"/> of the selected static method</returns>
    /// <example>
    /// The usage is similar to <see cref="GetMethod{T}"/>
    /// </example>
    /// <remarks>
    /// The <see cref="GetMethod{T}"/> method has the great advantage to use expressions instead of hardcoded string values which 
    /// allow better support for compiler checks, renaming and refactoring.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown, if the selector expression does not evaluate to a static method call</exception>
    public static MethodInfo GetStaticMethod(Expression<Action> selector)
    {
        return GetMethodInner(selector);
    }

    private static MethodInfo GetMethodInner<TDelegate>(Expression<TDelegate> selector, Type type = null, bool generic = false)
    {
        var body = selector.Body;
        var expression = body as MethodCallExpression;
        if (expression == null)
            throw new ArgumentException("Method selector expected");

        var member = expression.Method;
        if (type != null && member.DeclaringType != type && !member.DeclaringType.IsStatic())
        {
            var flags = BindingFlags.Default;
            flags |= member.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
            flags |= member.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic;

            if (generic)
                member = member.GetGenericMethodDefinition();

            var @params = member.GetSignature().ParameterTypes;
            return GetMethodWorkaround(type, member.Name, flags, @params);
        }

        return generic
            ? member.GetGenericMethodDefinition()
            : member;
    }

    private static MethodInfo GetMethodWorkaround(Type type, string name, BindingFlags bindingFlags, Type[] parameterTypes)
    {
        if (!type.IsInterface)
            return type.GetMethod(name, bindingFlags, null, parameterTypes, null);

        var q = from p in EnumerableEx.Return(type).Union(type.GetInterfaces())
                select p.GetMethod(name, bindingFlags, null, parameterTypes, null);

        return q.FirstOrDefault(p => p != null);
    }
}
