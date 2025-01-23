﻿using System.Linq.Expressions;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.DataBinding;

[AttributeUsage(AttributeTargets.Method)]
public abstract class ReplaceMethodInTemplateAttribute : Attribute
{
    public abstract MethodInfo Replace(MethodInfo expression);
}

public static class TemplateBuilder
{
    public static TView Build<T, TView>(
        this Expression<Func<T, TView>> layout,
        string dataContext,
        out IReadOnlyCollection<Type> types
    )
        where TView : UiControl
    {
        var rootParameter = layout.Parameters.First();
        var visitor = new TemplateBuilderVisitor(rootParameter, "");
        var body = visitor.Visit(layout.Body);
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
            bindings.Add(rootParameter, rootParameter.Name);
            rootBindingExpression = GetBinding(rootName, rootParameter.Type);
        }

        private static readonly ConstructorInfo BindingConstructor =
            typeof(JsonPointerReference).GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string) }
            );
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
                return GetBinding($"{path}{slashIfNotEmpty}{args.First()}", node.Method.ReturnType);
            }

            var replaceMethodAttribute =
                node.Method.GetCustomAttribute<ReplaceMethodInTemplateAttribute>();

            var method =
                replaceMethodAttribute != null
                    ? replaceMethodAttribute.Replace(node.Method)
                    : node.Method;
            if (
                args.Zip(method.GetParameters(), (a, p) => p.ParameterType.IsAssignableFrom(a.Type))
                    .Any(x => !x)
            )
                method = TrySubstituteMethod(method, args.Select(a => a.Type).ToArray());
            return Expression.Call(obj, method, args);
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
