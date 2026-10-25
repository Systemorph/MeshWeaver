using System.Collections.Immutable;
using System.Reflection;
using System.Text;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Computes the documentation comment ID the C# compiler writes into an assembly's XML
/// documentation file (ECMA-334 annex D, "Processing the documentation file"):
/// <list type="bullet">
/// <item><c>T:Ns.Outer`1.Inner</c> — namespace-qualified, nested types joined with <c>.</c>, a
/// generic type's arity as <c>`n</c>; a constructed type is documented on its definition.</item>
/// <item><c>M:Ns.T.M``1(``0,System.Int32@,System.String[],System.Nullable{System.Int32})</c> —
/// method arity <c>``n</c>; parameters by type with a type's generic parameter as <c>`n</c>, a
/// method's as <c>``n</c>, constructed generics as <c>{…}</c>, arrays <c>[]</c> / <c>[0:,0:]</c>,
/// by-ref <c>@</c>, pointers <c>*</c>; constructors <c>#ctor</c> / <c>#cctor</c>; conversion
/// operators append <c>~ReturnType</c>; an explicit implementation's dots become <c>#</c>.</item>
/// <item><c>P:</c> (with index parameters for an indexer), <c>F:</c>, <c>E:</c> as above.</item>
/// </list>
/// A member of a constructed generic type is documented on the generic definition's member.
/// </summary>
internal static class DocCommentId
{
    /// <summary>The documentation comment ID of <paramref name="member"/>, or <c>null</c> when it has none.</summary>
    /// <param name="member">The type or member.</param>
    /// <returns>The ID.</returns>
    public static string? For(MemberInfo member)
    {
        try
        {
            return Compute(member);
        }
        catch (Exception e) when (e is TypeLoadException or FileNotFoundException or FileLoadException
                                      or BadImageFormatException or NotSupportedException
                                      or InvalidOperationException or ArgumentException)
        {
            // A signature that names a type this process cannot load has no reachable docs; the
            // summary is optional display metadata, so the member reads as undocumented.
            return null;
        }
    }

    private static string? Compute(MemberInfo member)
    {
        if (member is Type type)
        {
            if (type.IsGenericParameter || type.HasElementType)
                return null;
            return "T:" + DefinitionName(type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type);
        }

        if (member.DeclaringType is not { } declaring)
            return null;
        if (declaring.IsConstructedGenericType)
        {
            declaring = declaring.GetGenericTypeDefinition();
            member = declaring.GetMemberWithSameMetadataDefinitionAs(member);
        }
        if (member is MethodInfo { IsConstructedGenericMethod: true } constructed)
            member = constructed.GetGenericMethodDefinition();

        var id = new StringBuilder();
        switch (member)
        {
            case MethodBase method:
                id.Append("M:").Append(DefinitionName(declaring)).Append('.').Append(MemberName(method));
                if (method.IsGenericMethodDefinition)
                    id.Append("``").Append(method.GetGenericArguments().Length);
                AppendParameters(id, method.GetParameters());
                if (method is MethodInfo { Name: "op_Implicit" or "op_Explicit" } conversion)
                    id.Append('~').Append(TypeName(conversion.ReturnType));
                return id.ToString();
            case PropertyInfo property:
                id.Append("P:").Append(DefinitionName(declaring)).Append('.').Append(MemberName(property));
                AppendParameters(id, property.GetIndexParameters());
                return id.ToString();
            case FieldInfo field:
                return "F:" + DefinitionName(declaring) + "." + MemberName(field);
            case EventInfo @event:
                return "E:" + DefinitionName(declaring) + "." + MemberName(@event);
            default:
                return null;
        }
    }

    private static void AppendParameters(StringBuilder id, ParameterInfo[] parameters)
    {
        if (parameters.Length == 0)
            return;
        id.Append('(');
        for (var k = 0; k < parameters.Length; k++)
        {
            if (k > 0)
                id.Append(',');
            id.Append(TypeName(parameters[k].ParameterType));
        }
        id.Append(')');
    }

    private static string MemberName(MemberInfo member) => member.Name switch
    {
        ".ctor" => "#ctor",
        ".cctor" => "#cctor",
        // Explicit interface implementation: "Ns.IFoo<System.Int32>.Bar" → "Ns#IFoo{System#Int32}#Bar".
        var name => name.Replace('.', '#').Replace('<', '{').Replace('>', '}'),
    };

    /// <summary>A type definition's name as a <c>T:</c> ID spells it: <c>Ns.Outer`1.Inner`2</c>.</summary>
    private static string DefinitionName(Type type)
    {
        if (type.DeclaringType is { } outer && !type.IsGenericParameter)
            return DefinitionName(outer) + "." + type.Name;
        return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
    }

    /// <summary>A type as it appears in a parameter list of an ID.</summary>
    /// <param name="type">The parameter type.</param>
    /// <returns>The encoded name.</returns>
    public static string TypeName(Type type)
    {
        if (type.IsByRef)
            return TypeName(type.GetElementType()!) + "@";
        if (type.IsPointer)
            return TypeName(type.GetElementType()!) + "*";
        if (type.IsArray)
        {
            var element = TypeName(type.GetElementType()!);
            if (type.IsSZArray)
                return element + "[]";
            return element + "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]";
        }
        if (type.IsGenericParameter)
            return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsGenericType)
            return GenericName(type);
        return DefinitionName(type);
    }

    // Ns.Outer{A}.Inner{B,C}: each nesting level carries the arguments it declares itself.
    private static string GenericName(Type type)
    {
        var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        var chain = ImmutableStack<Type>.Empty;
        for (var t = definition; t is not null; t = t.DeclaringType)
            chain = chain.Push(t);

        var name = new StringBuilder();
        var consumed = 0;
        var first = true;
        while (!chain.IsEmpty)
        {
            chain = chain.Pop(out var level);
            if (first)
            {
                if (!string.IsNullOrEmpty(level.Namespace))
                    name.Append(level.Namespace).Append('.');
                first = false;
            }
            else
                name.Append('.');

            var tick = level.Name.IndexOf('`');
            name.Append(tick < 0 ? level.Name : level.Name[..tick]);
            var total = level.IsGenericType ? level.GetGenericArguments().Length : 0;
            if (total > consumed)
            {
                name.Append('{');
                for (var k = consumed; k < total; k++)
                {
                    if (k > consumed)
                        name.Append(',');
                    name.Append(TypeName(arguments[k]));
                }
                name.Append('}');
                consumed = total;
            }
        }
        return name.ToString();
    }

    /// <summary>
    /// The CLR-dotted type name a cref refers to (<c>T:Ns.T</c> → <c>Ns.T</c>; <c>P:Ns.T.P</c> →
    /// <c>Ns.T</c>), with any parameter list removed; <c>null</c> for a malformed cref.
    /// </summary>
    /// <param name="cref">The documentation comment ID.</param>
    /// <returns>The type name.</returns>
    public static string? TypeNameOfCref(string cref)
    {
        if (cref.Length < 3 || cref[1] != ':')
            return null;
        var name = cref[2..];
        var paren = name.IndexOf('(');
        if (paren >= 0)
            name = name[..paren];
        if (cref[0] == 'T')
            return name;
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? null : name[..dot];
    }

    /// <summary>
    /// The type a cref names, if <paramref name="assembly"/> defines it. A cref joins nested types
    /// with <c>.</c> where the CLR uses <c>+</c>, so the trailing dots are tried as nesting.
    /// </summary>
    /// <param name="assembly">The candidate assembly.</param>
    /// <param name="dottedName">The type name from <see cref="TypeNameOfCref"/>.</param>
    /// <returns>The type, or <c>null</c>.</returns>
    public static Type? FindType(Assembly assembly, string dottedName)
    {
        var candidate = dottedName;
        while (true)
        {
            if (assembly.GetType(candidate, throwOnError: false) is { } type)
                return type;
            var dot = candidate.LastIndexOf('.');
            if (dot <= 0)
                return null;
            candidate = candidate[..dot] + "+" + candidate[(dot + 1)..];
        }
    }
}
