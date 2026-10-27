using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Keeps an explicitly stored value on the wire when the hub's
/// <see cref="JsonIgnoreCondition.WhenWritingDefault"/> policy would drop it and the READER would
/// re-materialise a DIFFERENT value — the member's declared default.
///
/// <para><b>The defect class.</b> <c>WhenWritingDefault</c> compares against the CLR default
/// (<c>false</c>, <c>0</c>, <c>null</c>), while a reader re-applies the member's DECLARED default — its
/// initializer (<c>public bool Enabled { get; init; } = true</c>) or its constructor parameter's default
/// (<c>record R(bool Enabled = true)</c>). Where the two differ, the CLR-default value is omitted on
/// write and silently comes back as the declared one: an off-switch can be turned on but never off.
/// Measured on a live portal: an <c>ActionHandlerRule</c> committed with <c>"enabled": false</c> read
/// <c>enabled: true</c> after its sync import, re-arming a rule that ships disabled.</para>
///
/// <para><b>The rule.</b> For every settable scalar member (primitive, enum, <see cref="decimal"/>,
/// their <see cref="Nullable{T}"/>, or <see cref="string"/>) whose declared default differs from its
/// CLR default, the member is always written. Nothing else changes: a value that equals the CLR
/// default where the declared default is the CLR default too is still omitted, and a declared-default
/// value was never the CLR default, so it was always written. A member that carries its own
/// <see cref="JsonIgnoreAttribute"/> keeps its author's condition.</para>
/// </summary>
internal static class DeclaredDefaultsSurviveTheWire
{
    /// <summary>
    /// Applies the rule to <paramref name="typeInfo"/> while it is still being configured by the
    /// resolver. Never throws: a type whose declared defaults cannot be probed keeps the hub's
    /// policy unchanged, and the reason is logged.
    /// </summary>
    /// <param name="typeInfo">An object type's metadata, not yet locked.</param>
    /// <param name="logger">Receives the reason a type could not be probed, or null.</param>
    public static void Apply(JsonTypeInfo typeInfo, ILogger? logger)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Type.IsAbstract || typeInfo.Type.IsInterface)
            return;

        List<JsonPropertyInfo>? candidates = null;
        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is null
                || property.ShouldSerialize is not null
                || (property.Set is null && property.AssociatedParameter is null)
                || !IsScalar(property.PropertyType)
                || property.AttributeProvider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) == true)
                continue;
            (candidates ??= []).Add(property);
        }
        if (candidates is null)
            return;

        var probe = Probe(typeInfo, logger);
        foreach (var property in candidates)
        {
            if (!TryDeclaredDefault(property, probe, out var declared))
                continue;
            if (Equals(declared, ClrDefault(property.PropertyType)))
                continue;
            property.ShouldSerialize = static (_, _) => true;
        }
    }

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(decimal) || underlying == typeof(string);
    }

    private static object? ClrDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

    /// <summary>
    /// The value a reader starts from when <paramref name="property"/> is absent: the probe
    /// instance's value, else the constructor parameter's declared default.
    /// </summary>
    private static bool TryDeclaredDefault(JsonPropertyInfo property, object? probe, out object? declared)
    {
        if (probe is not null)
        {
            declared = property.Get!(probe);
            return true;
        }
        if (property.AssociatedParameter is { } parameter)
        {
            declared = parameter.HasDefaultValue
                ? Coerce(parameter.DefaultValue, property.PropertyType)
                : ClrDefault(property.PropertyType);
            return true;
        }
        declared = null;
        return false;
    }

    /// <summary>
    /// An instance built the way the deserializer builds one when no member is present: the
    /// parameterless factory, or the deserialization constructor called with each parameter's
    /// declared default. Null when the type cannot be built that way.
    /// </summary>
    private static object? Probe(JsonTypeInfo typeInfo, ILogger? logger)
    {
        try
        {
            if (typeInfo.CreateObject is { } create)
                return create();
            if (typeInfo.ConstructorAttributeProvider is ConstructorInfo constructor)
                return constructor.Invoke(constructor.GetParameters()
                    .Select(p => p.HasDefaultValue
                        ? Coerce(p.DefaultValue, p.ParameterType)
                        : ClrDefault(p.ParameterType))
                    .ToArray());
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or MemberAccessException
                                       or InvalidOperationException or NotSupportedException)
        {
            // The type is still serialised; only the probe is unavailable, so its members fall back
            // to their constructor parameters' declared defaults (TryDeclaredDefault) and the rest
            // keep the hub's policy. Logged, so a member this leaves exposed can be found.
            logger?.LogDebug(ex,
                "Could not probe the declared defaults of {Type}; members not bound to a constructor parameter keep WhenWritingDefault",
                typeInfo.Type.FullName);
        }
        return null;
    }

    /// <summary>A parameter's default as the member's own type: an enum from its underlying
    /// constant, and <c>= default</c> of a value type (reported as null) as its CLR default.</summary>
    private static object? Coerce(object? value, Type type)
    {
        if (value is null or DBNull)
            return ClrDefault(type);
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum ? Enum.ToObject(underlying, value) : value;
    }
}
