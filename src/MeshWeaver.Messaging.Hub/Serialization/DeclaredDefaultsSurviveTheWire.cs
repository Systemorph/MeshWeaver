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
/// initializer (<c>public bool Enabled { get; init; } = true</c>), its constructor parameter's default
/// (<c>record R(bool Enabled = true)</c>), or an initializer computed FROM the constructor's arguments
/// (<c>MeshNode.MainNode = … Id …</c>). Where the two differ, the CLR-default value is omitted on
/// write and silently comes back as the declared one: an off-switch can be turned on but never off.
/// Measured on a live portal: an <c>ActionHandlerRule</c> committed with <c>"enabled": false</c> read
/// <c>enabled: true</c> after its sync import, re-arming a rule that ships disabled.</para>
///
/// <para><b>The rule: omit only what is PROVEN safe to omit.</b> A settable scalar member (primitive,
/// enum, <see cref="decimal"/>, their <see cref="Nullable{T}"/>, or <see cref="string"/>) keeps the
/// hub's <c>WhenWritingDefault</c> only when its declared default is shown to be the CLR default
/// whatever the constructor was given: for a constructor-parameter member, the parameter's default; for
/// a type built by a parameterless constructor, one probe instance; for a type built by a parameterized
/// constructor, two probes with DIFFERENT arguments that both report the CLR default. Anything that
/// cannot be shown — a probe that throws, a getter that throws, a constructor argument that cannot be
/// varied, two probes that disagree — is always written. Writing an explicit CLR default is
/// always read back identically, so the conservative branch can only cost bytes, never a value.</para>
///
/// <para>A member that carries its own <see cref="JsonIgnoreAttribute"/> keeps its author's
/// condition. Never throws: probing runs inside the resolver, on the path of every serialization.</para>
/// </summary>
internal static class DeclaredDefaultsSurviveTheWire
{
    /// <summary>Applies the rule to <paramref name="typeInfo"/> while the resolver still configures it.</summary>
    /// <param name="typeInfo">An object type's metadata, not yet locked.</param>
    /// <param name="logger">Receives the reason a probe failed (members then fall to "always written"), or null.</param>
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

        // Built lazily: a type whose candidates are all constructor-bound never runs a constructor.
        IReadOnlyList<object>? probes = null;
        var probed = false;
        foreach (var property in candidates)
        {
            bool provenClrDefault;
            if (property.AssociatedParameter is { } parameter)
            {
                provenClrDefault = Equals(
                    parameter.HasDefaultValue ? Coerce(parameter.DefaultValue, property.PropertyType) : ClrDefault(property.PropertyType),
                    ClrDefault(property.PropertyType));
            }
            else
            {
                if (!probed)
                {
                    probes = Probes(typeInfo, logger);
                    probed = true;
                }
                provenClrDefault = probes is not null && ReadsClrDefaultInEvery(property, probes, typeInfo.Type, logger);
            }
            if (!provenClrDefault)
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

    /// <summary>Whether <paramref name="property"/> reads its CLR default on every probe; a throwing getter is "not shown".</summary>
    private static bool ReadsClrDefaultInEvery(JsonPropertyInfo property, IReadOnlyList<object> probes, Type type, ILogger? logger)
    {
        var clrDefault = ClrDefault(property.PropertyType);
        foreach (var probe in probes)
        {
            object? value;
            try
            {
                value = property.Get!(probe);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex,
                    "Reading {Type}.{Member} on a probe instance threw; the member is always written",
                    type.FullName, property.Name);
                return false;
            }
            if (!Equals(value, clrDefault))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Instances built the way the deserializer builds one when no member is present: the
    /// parameterless factory once, or the deserialization constructor twice — with each parameter's
    /// default and with a different value per parameter — so an initializer that reads an argument
    /// shows up as a disagreement. Null when that cannot be done (every probed member is then written).
    /// </summary>
    private static IReadOnlyList<object>? Probes(JsonTypeInfo typeInfo, ILogger? logger)
    {
        try
        {
            if (typeInfo.CreateObject is { } create)
                return [create()];
            if (typeInfo.ConstructorAttributeProvider is not ConstructorInfo constructor)
                return null;
            var parameters = constructor.GetParameters();
            var alternatives = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!TryAlternative(parameters[i].ParameterType, out alternatives[i]))
                {
                    logger?.LogDebug(
                        "Constructor parameter {Parameter} of {Type} cannot be varied; its initialized members are always written",
                        parameters[i].Name, typeInfo.Type.FullName);
                    return null;
                }
            }
            var defaults = parameters
                .Select(p => p.HasDefaultValue ? Coerce(p.DefaultValue, p.ParameterType) : ClrDefault(p.ParameterType))
                .ToArray();
            return [constructor.Invoke(defaults), constructor.Invoke(alternatives)];
        }
        catch (Exception ex)
        {
            // Not swallowed: a failed probe means "not shown", and every probed member of the type is
            // then always written — the conservative answer. Logged so the type can be found.
            logger?.LogDebug(ex,
                "Could not probe the declared defaults of {Type}; its initialized scalar members are always written",
                typeInfo.Type.FullName);
            return null;
        }
    }

    /// <summary>
    /// A value of <paramref name="type"/> that differs from its CLR default — so the second probe's
    /// argument really is different. False when none can be built; the caller then proves nothing.
    /// </summary>
    private static bool TryAlternative(Type type, out object? value)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        value = null;
        if (underlying == typeof(object))
            value = new object();
        else if (underlying.IsArray)
            value = Array.CreateInstance(underlying.GetElementType()!, 0);
        else if (underlying == typeof(Guid))
            value = Guid.NewGuid();
        else if (underlying == typeof(DateTime))
            value = DateTime.UnixEpoch;
        else if (underlying == typeof(DateTimeOffset))
            value = DateTimeOffset.UnixEpoch;
        else if (underlying == typeof(TimeSpan))
            value = TimeSpan.FromSeconds(1);
        else if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableArray<>))
            value = typeof(System.Collections.Immutable.ImmutableArray).GetMethods()
                .Single(m => m.Name == nameof(System.Collections.Immutable.ImmutableArray.Create)
                             && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                .MakeGenericMethod(underlying.GetGenericArguments()[0])
                .Invoke(null, null);
        else if (!underlying.IsValueType && !underlying.IsAbstract && !underlying.IsInterface
                 && underlying.GetConstructor(Type.EmptyTypes) is not null)
            value = Activator.CreateInstance(underlying);
        else if (underlying == typeof(string))
            value = "declared-default-probe";
        else if (underlying == typeof(bool))
            value = true;
        else if (underlying == typeof(char))
            value = 'p';
        else if (underlying.IsEnum)
        {
            var values = Enum.GetValuesAsUnderlyingType(underlying).Cast<object>().ToArray();
            value = Enum.ToObject(underlying, values.FirstOrDefault(v => Convert.ToInt64(v) != 0) ?? 1);
        }
        else if (underlying.IsPrimitive || underlying == typeof(decimal))
            value = Convert.ChangeType(1, underlying);
        return value is not null;
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
