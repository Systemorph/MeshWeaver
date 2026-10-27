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
/// <para><b>The rule: omit a CLR-default value only when the reader would rebuild that same value.</b>
/// For a settable scalar member (primitive, enum, <see cref="decimal"/>, their
/// <see cref="Nullable{T}"/>, or <see cref="string"/>):</para>
/// <list type="bullet">
/// <item>bound to a constructor parameter — the reader passes the parameter's default, so the member
/// is omittable exactly when that default is the CLR default;</item>
/// <item>on a type built by a parameterless constructor — one probe instance is what the reader
/// starts from;</item>
/// <item>on a type built by a parameterized constructor — no probe can prove the initializer ignores
/// the arguments, so the check is made PER INSTANCE at write time: the instance's own constructor
/// arguments (read from its parameter-bound members, which is what the reader will pass) rebuild a
/// fresh instance, and the CLR-default value is omitted only when the fresh instance reads it too.</item>
/// </list>
/// <para>Anything that cannot be established — a probe, rebuild or getter that throws, a constructor
/// parameter with no serialized member — means the member is written. An explicit CLR default reads
/// back identically, so that branch costs bytes, never a value. A member carrying its own
/// <see cref="JsonIgnoreAttribute"/> keeps its author's condition. Never throws from the resolver.</para>
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

        object? probe = null;
        var probed = false;
        Rebuilder? rebuilder = null;
        var rebuilderBuilt = false;
        foreach (var property in candidates)
        {
            var clrDefault = ClrDefault(property.PropertyType);
            if (property.AssociatedParameter is { } parameter)
            {
                var declared = parameter.HasDefaultValue
                    ? Coerce(parameter.DefaultValue, property.PropertyType)
                    : clrDefault;
                if (!Equals(declared, clrDefault))
                    property.ShouldSerialize = static (_, _) => true;
                continue;
            }

            if (typeInfo.CreateObject is not null)
            {
                if (!probed)
                {
                    probe = Probe(typeInfo, logger);
                    probed = true;
                }
                if (probe is null || !TryRead(property, probe, typeInfo.Type, logger, out var value) || !Equals(value, clrDefault))
                    property.ShouldSerialize = static (_, _) => true;
                continue;
            }

            if (!rebuilderBuilt)
            {
                rebuilder = Rebuilder.For(typeInfo, logger);
                rebuilderBuilt = true;
            }
            if (rebuilder is null)
            {
                property.ShouldSerialize = static (_, _) => true;
                continue;
            }
            var get = property.Get!;
            var captured = rebuilder;
            property.ShouldSerialize = (instance, value) =>
                !Equals(value, clrDefault) || !captured.RebuildsTo(instance, get, value);
        }
    }

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(decimal) || underlying == typeof(string);
    }

    private static object? ClrDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

    private static bool TryRead(JsonPropertyInfo property, object instance, Type type, ILogger? logger, out object? value)
    {
        try
        {
            value = property.Get!(instance);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "Reading {Type}.{Member} on a probe instance threw; the member is always written",
                type.FullName, property.Name);
            value = null;
            return false;
        }
    }

    /// <summary>The instance a parameterless deserialization starts from, or null when building it threw.</summary>
    private static object? Probe(JsonTypeInfo typeInfo, ILogger? logger)
    {
        try
        {
            return typeInfo.CreateObject!();
        }
        catch (Exception ex)
        {
            // Not swallowed: a failed probe means "not shown", and every probed member is then
            // always written — the conservative answer. Logged so the type can be found.
            logger?.LogDebug(ex,
                "Could not probe the declared defaults of {Type}; its initialized scalar members are always written",
                typeInfo.Type.FullName);
            return null;
        }
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

    /// <summary>
    /// Rebuilds an instance the way the reader will: the deserialization constructor called with the
    /// instance's own parameter-bound member values. Holds only the most recent (instance, rebuilt)
    /// pair, so the members of one object share one rebuild and nothing is retained beyond it.
    /// </summary>
    private sealed class Rebuilder(ConstructorInfo constructor, Func<object, object?>[] arguments, Type type, ILogger? logger)
    {
        private sealed record Last(object Instance, object? Rebuilt);

        private volatile Last? last;

        public static Rebuilder? For(JsonTypeInfo typeInfo, ILogger? logger)
        {
            if (typeInfo.ConstructorAttributeProvider is not ConstructorInfo constructor)
                return null;
            var parameters = constructor.GetParameters();
            var arguments = new Func<object, object?>[parameters.Length];
            foreach (var property in typeInfo.Properties)
                if (property.AssociatedParameter is { } parameter && property.Get is { } get
                    && parameter.Position >= 0 && parameter.Position < arguments.Length)
                    arguments[parameter.Position] = get;
            for (var i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] is not null)
                    continue;
                logger?.LogDebug(
                    "Constructor parameter {Parameter} of {Type} has no serialized member; its initialized scalar members are always written",
                    parameters[i].Name, typeInfo.Type.FullName);
                return null;
            }
            return new Rebuilder(constructor, arguments, typeInfo.Type, logger);
        }

        /// <summary>Whether the reader, given <paramref name="instance"/>'s constructor arguments, rebuilds <paramref name="value"/>.</summary>
        public bool RebuildsTo(object instance, Func<object, object?> get, object? value)
        {
            var cached = last;
            object? rebuilt;
            if (cached is not null && ReferenceEquals(cached.Instance, instance))
                rebuilt = cached.Rebuilt;
            else
            {
                rebuilt = Rebuild(instance);
                last = new Last(instance, rebuilt);
            }
            if (rebuilt is null)
                return false;
            try
            {
                return Equals(get(rebuilt), value);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Reading a member of a rebuilt {Type} threw; the member is written", type.FullName);
                return false;
            }
        }

        private object? Rebuild(object instance)
        {
            try
            {
                var args = new object?[arguments.Length];
                for (var i = 0; i < args.Length; i++)
                    args[i] = arguments[i](instance);
                return constructor.Invoke(args);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Rebuilding {Type} from its constructor arguments threw; its initialized scalar members are written", type.FullName);
                return null;
            }
        }
    }
}
