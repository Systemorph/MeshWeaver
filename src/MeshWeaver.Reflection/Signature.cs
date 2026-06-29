#nullable enable
using System.Reflection;

namespace MeshWeaver.Reflection;

/// <summary>
/// Represents the identity of a member by its name and parameter types, with value equality.
/// Used to compare and key members (methods, constructors) independently of their declaring type.
/// </summary>
public sealed class Signature : IEquatable<Signature>
{
    /// <summary>
    /// Gets the member name, or <c>null</c> for a constructor (which has no distinct name).
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the ordered parameter types of the member; empty for members without parameters.
    /// </summary>
    public Type[] ParameterTypes { get; }

    /// <summary>
    /// Initializes a new <see cref="Signature"/> from an explicit name and parameter types.
    /// </summary>
    /// <param name="name">The member name, or <c>null</c> for a constructor.</param>
    /// <param name="parameterTypes">The ordered parameter types; treated as empty when <c>null</c>.</param>
    public Signature(string? name, params Type[] parameterTypes)
    {
        Name = name;
        ParameterTypes = parameterTypes ?? Type.EmptyTypes;
    }

    /// <summary>
    /// Initializes a new <see cref="Signature"/> from a reflected member, deriving its name and parameter types.
    /// </summary>
    /// <param name="memberInfo">The member to build the signature from.</param>
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

    /// <summary>
    /// Returns a string of the form <c>Name(Type1,Type2,...)</c>, using <c>.ctor</c> when the name is <c>null</c>.
    /// </summary>
    /// <returns>The formatted signature string.</returns>
    public override string ToString()
    {
        return String.Format("{0}({1})", Name ?? ".ctor", String.Join<Type>(",", ParameterTypes));
    }

    #endregion Formatting

    #region Equality

    /// <summary>
    /// Determines whether this signature equals <paramref name="other"/> by name and parameter-type sequence.
    /// </summary>
    /// <param name="other">The signature to compare with.</param>
    /// <returns><c>true</c> if both have the same name and identical ordered parameter types.</returns>
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

    /// <summary>
    /// Determines whether <paramref name="obj"/> is a <see cref="Signature"/> equal to this one.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><c>true</c> if <paramref name="obj"/> is an equal signature.</returns>
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
            return false;
        if (ReferenceEquals(this, obj))
            return true;

        return obj is Signature other && this.Equals(other);
    }

    /// <summary>
    /// Returns a hash code derived from the name and parameter types, consistent with <c>Equals</c>.
    /// </summary>
    /// <returns>The hash code for this signature.</returns>
    public override int GetHashCode()
    {
        return new object?[] { Name }.Concat(ParameterTypes).Aggregate(17, (x, y) => x ^ (y?.GetHashCode() ?? 0));
    }

    #endregion Equality
}
