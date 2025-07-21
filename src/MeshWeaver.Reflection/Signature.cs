#nullable enable
using System.Reflection;

namespace MeshWeaver.Reflection;

public sealed class Signature : IEquatable<Signature>
{
    public string? Name { get; }
    public Type[] ParameterTypes { get; }

    public Signature(string? name, params Type[] parameterTypes)
    {
        Name = name;
        ParameterTypes = parameterTypes ?? Type.EmptyTypes;
    }

    public Signature(MemberInfo memberInfo)
            : this(GetName(memberInfo), GetParameterTypes(memberInfo))
    {
    }

    private static string? GetName(MemberInfo memberInfo)
    {
        if (memberInfo == null)
            throw new ArgumentNullException(nameof(memberInfo));

        return memberInfo is ConstructorInfo ? null : memberInfo.Name;
    }

    private static Type[] GetParameterTypes(MemberInfo memberInfo)
    {
        var method = memberInfo as MethodBase;
        if (method != null)
            return method.GetParameters().Select(p => p.ParameterType).ToArray();
        else
            return Type.EmptyTypes;
    }

    #region Formatting

    public override string ToString()
    {
        return String.Format("{0}({1})", Name ?? ".ctor", String.Join<Type>(",", ParameterTypes));
    }

    #endregion Formatting

    #region Equality

    public bool Equals(Signature? other)
    {
        if (ReferenceEquals(null, other))
            return false;
        if (ReferenceEquals(this, other))
            return true;

        if (!string.Equals(Name, other.Name))
            return false;

        return ParameterTypes.SequenceEqual(other.ParameterTypes);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
            return false;
        if (ReferenceEquals(this, obj))
            return true;

        return obj is Signature other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        return new object?[] { Name }.Concat(ParameterTypes).Aggregate(17, (x, y) => x ^ (y?.GetHashCode() ?? 0));
    }

    #endregion Equality
}
