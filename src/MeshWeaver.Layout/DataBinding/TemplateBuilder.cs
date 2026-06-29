using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.DataBinding;

/// <summary>
/// Attribute applied to methods that should be substituted during expression-tree template compilation.
/// The template visitor replaces calls to the annotated method with the result of <see cref="Replace"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public abstract class ReplaceMethodInTemplateAttribute : Attribute
{
    /// <summary>
    /// Returns the <see cref="MethodInfo"/> that should replace <paramref name="expression"/> during template building.
    /// </summary>
    /// <param name="expression">The original method found in the expression tree.</param>
    /// <returns>The substitute method to call instead.</returns>
    public abstract MethodInfo Replace(MethodInfo expression);
}

/// <summary>
/// Compiles a typed expression-tree data template into a <see cref="UiControl"/> by replacing
/// property-access nodes with JSON-Pointer binding references.
/// </summary>
public static class TemplateBuilder
{
    /// <summary>
    /// Walks the expression tree <paramref name="layout"/>, replaces property accesses with
    /// <see cref="MeshWeaver.Data.JsonPointerReference"/> bindings, and compiles the result into a
    /// <typeparamref name="TView"/> whose <c>DataContext</c> is set to <paramref name="dataContext"/>.
    /// </summary>
    /// <typeparam name="T">The data type the template binds to.</typeparam>
    /// <typeparam name="TView">The UI control type produced by the template.</typeparam>
    /// <param name="layout">The expression tree template; throws if null.</param>
    /// <param name="dataContext">JSON Pointer string that identifies the data root for the rendered control.</param>
    /// <param name="types">Receives the set of data-bound types discovered while walking the expression.</param>
    /// <returns>The compiled, data-bound UI control.</returns>
    public static TView Build<T, TView>(
        this Expression<Func<T, TView>>? layout,
        string dataContext,
        out IReadOnlyCollection<Type> types
    )
        where TView : UiControl
    {
        if (layout == null)
            throw new ArgumentNullException(nameof(layout));
        var rootParameter = layout.Parameters.First();
        var visitor = new TemplateBuilderVisitor(rootParameter, "");
        var body = visitor.Visit(layout.Body)!;
        var lambda = Expression.Lambda<Func<TView>>(body);
        var ret = lambda.Compile().Invoke();
        types = visitor.DataBoundTypes;
        return ret with { DataContext = dataContext };
    }

    private class TemplateBuilderVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression rootParameter;
        private readonly Expression rootBindingExpression;
        private readonly HashSet<Type> included = new();

        public TemplateBuilderVisitor(ParameterExpression rootParameter, string rootName)
        {
            this.rootParameter = rootParameter;
            bindings.Add(rootParameter, rootParameter.Name ?? string.Empty);
            rootBindingExpression = GetBinding(rootName, rootParameter.Type);
        }

        private static readonly ConstructorInfo BindingConstructor =
            typeof(JsonPointerReference).GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string) }
            )!;
        internal readonly List<Type> DataBoundTypes = new();

        private Expression GetBinding(string path, Type type)
        {
            var binding = Expression.New(BindingConstructor, Expression.Constant(path));
            bindings.Add(binding, path);

            DataBoundTypes.AddRange(GetTypes(type));

            return binding;
        }

        private IEnumerable<Type> GetTypes(Type type)
        {
            if (type.IsPrimitive || type == typeof(string) || !included.Add(type))
                yield break;

            yield return type;
            foreach (var property in type.GetProperties())
            foreach (var t in GetTypes(property.PropertyType))
                yield return t;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == rootParameter)
                return rootBindingExpression;
            return base.VisitParameter(node);
        }

        private readonly Dictionary<Expression, string> bindings = new();

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var obj = Visit(node.Object);
            var args = node.Arguments.Select(Visit).ToArray();
            if (
                node.Method.Name == "get_Item"
                && obj != null
                && bindings.TryGetValue(obj, out var path)
            )
            {
                var slashIfNotEmpty = string.IsNullOrEmpty(path) ? string.Empty : "/";
                var firstArg = args.First();
                if (firstArg is ConstantExpression constExpr && constExpr.Value != null)
                {
                    return GetBinding($"{path}{slashIfNotEmpty}{constExpr.Value}", node.Method.ReturnType);
                }
                throw new InvalidOperationException("Expected constant expression for indexer access");
            }

            var replaceMethodAttribute =
                node.Method.GetCustomAttribute<ReplaceMethodInTemplateAttribute>();

            var method =
                replaceMethodAttribute != null
                    ? replaceMethodAttribute.Replace(node.Method)
                    : node.Method;
            if (
                args.Zip(method.GetParameters(), (a, p) => a != null && p.ParameterType.IsAssignableFrom(a.Type))
                    .Any(x => !x)
            )
                method = TrySubstituteMethod(method, args.Where(a => a != null).Select(a => a!.Type).ToArray());
            return Expression.Call(obj, method, args.Where(a => a != null).ToArray()!);
        }

        private MethodInfo TrySubstituteMethod(MethodInfo method, Type[] types)
        {
            foreach (
                var m in method.ReflectedType!.GetMethods(
                    BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                )
            )
            {
                if (m.Name != method.Name)
                    continue;
                if (
                    m.GetParameters()
                        .Zip(types, (p, t) => p.ParameterType.IsAssignableFrom(t))
                        .All(x => x)
                )
                    return m;
            }

            return method;
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Convert)
            {
                var visited = Visit(node.Operand);
                if (visited == node.Operand)
                    return node;
                return Expression.Convert(visited, node.Type);
            }

            return base.VisitUnary(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            var expr = Visit(node.Expression);

            if (expr != null && bindings.TryGetValue(expr, out var parentPath))
            {
                var separator = string.IsNullOrEmpty(parentPath) ? "" : "/";

                var path = $"{parentPath}{separator}{node.Member.Name.ToCamelCase()}";
                var ret = GetBinding(path, node.Type);
                return ret;
            }
            return base.VisitMember(node);
        }
    }
}
